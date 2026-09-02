using System;
using System.Linq;
using AntarusPoFinder.App.Services;
using AntarusPoFinder.App.ViewModels;
using AntarusPoFinder.Core.Data;
using AntarusPoFinder.Core.Domain;
using AntarusPoFinder.Core.Services;
using AntarusPoFinder.Tests.TestHelpers;
using Xunit;

namespace AntarusPoFinder.Tests;

/// <summary>Тикет kiselyov.a: «Настроить правильность работы уведомлений, практически всегда
/// уведомления остаются пустыми. Оставить счётчик уведомлений при наличии новых и убирать из
/// счётчика количество ТОЛЬКО прочитанные. Сохранять историю уведомлений, сделать возможность
/// удалять каждое уведомление поштучно».
///
/// «Пустыми» окно оказывалось не потому, что терялся текст: текст был на месте. Прежнее окно при
/// ЗАКРЫТИИ метило прочитанным ВЕСЬ список, а список по умолчанию показывал только непрочитанное, —
/// значит второе и любое следующее открытие колокольчика давало пустое окно. Вдобавок история жила
/// только в памяти и умирала вместе с процессом. Воспроизведено живым прогоном
/// scratchpad/live/notifications_run.py: первое открытие — 2 записи, второе — 0.
///
/// Здесь закреплено то, что проверяется без поднятия WPF: хранение, схлопывание повторов, счётчик,
/// поштучное удаление.</summary>
public class NotificationReadStateTests
{
    private static NotificationCenter Center(Database db) => new(db, new ConfigService(db));

    /// <summary>Главная защита от возврата бага: прочитанное ОСТАЁТСЯ в истории.</summary>
    [Fact]
    public void MarkingEverythingRead_DoesNotEmptyTheHistory()
    {
        using var dbFile = new TempDb();
        using var db = new Database(dbFile.Path);
        var center = Center(db);

        center.Add("Структура диска создана: 701 папок", NotificationCategory.Sync);
        center.Add("Проверка обновлений не удалась", NotificationCategory.AppUpdates);

        center.MarkAllRead();

        Assert.Equal(2, center.History.Count);
        Assert.All(center.History, e => Assert.False(string.IsNullOrWhiteSpace(e.Text)));
        Assert.Equal(0, center.UnreadCount);
    }

    /// <summary>Счётчик убавляется РОВНО на прочитанном — по одной записи, а не всем списком по
    /// факту открытия окна.</summary>
    [Fact]
    public void UnreadCount_DropsOnlyForEntriesActuallyMarkedRead()
    {
        using var dbFile = new TempDb();
        using var db = new Database(dbFile.Path);
        var center = Center(db);

        center.Add("первое", NotificationCategory.General);
        center.Add("второе", NotificationCategory.General);
        center.Add("третье", NotificationCategory.General);
        Assert.Equal(3, center.UnreadCount);

        center.MarkRead(center.History.First(e => e.Text == "второе"));

        Assert.Equal(2, center.UnreadCount);
        Assert.Equal(3, center.History.Count);
    }

    /// <summary>Категория, исключённая из счётчика, в истории остаётся, но на колокольчик не
    /// влияет — и переключение настройки пересчитывает счётчик сразу.</summary>
    [Fact]
    public void MutedCategory_StaysInHistoryButIsNotCounted()
    {
        using var dbFile = new TempDb();
        using var db = new Database(dbFile.Path);
        var cfg = new ConfigService(db);
        var center = new NotificationCenter(db, cfg);

        center.Add("Путь сохранён", NotificationCategory.Sync);
        Assert.Equal(1, center.UnreadCount);

        cfg.SetNotificationCategoryCountedUnread(NotificationCategory.Sync, false);
        center.Refresh();

        Assert.Equal(0, center.UnreadCount);
        Assert.Single(center.History);
    }

    /// <summary>История переживает перезапуск: тикет «Сохранять историю уведомлений». Признак
    /// «прочитано» переживает тоже — иначе после каждого запуска счётчик оживал бы целиком.</summary>
    [Fact]
    public void History_SurvivesRestart_WithTextCategoryAndReadFlag()
    {
        using var dbFile = new TempDb();
        using (var db = new Database(dbFile.Path))
        {
            var center = Center(db);
            center.Add("Обновлено прошивок: 3", NotificationCategory.FirmwareAndParams);
            center.Add("Не удалось принять конфиг", NotificationCategory.Sync);
            center.MarkRead(center.History.First(e => e.Text == "Обновлено прошивок: 3"));
        }

        using var reopened = new Database(dbFile.Path);
        var after = Center(reopened);

        Assert.Equal(2, after.History.Count);
        var fw = after.History.First(e => e.Text == "Обновлено прошивок: 3");
        Assert.Equal(NotificationCategory.FirmwareAndParams, fw.Category);
        Assert.True(fw.IsRead);
        Assert.Equal(1, after.UnreadCount);
    }

    /// <summary>Поштучное удаление — и оно тоже переживает перезапуск (удалили в базе, а не только
    /// в списке на экране).</summary>
    [Fact]
    public void DeleteOne_RemovesExactlyThatEntry_AndItDoesNotComeBack()
    {
        using var dbFile = new TempDb();
        using (var db = new Database(dbFile.Path))
        {
            var center = Center(db);
            center.Add("оставить", NotificationCategory.General);
            center.Add("убрать", NotificationCategory.General);

            center.Delete(center.History.First(e => e.Text == "убрать"));

            Assert.Single(center.History);
            Assert.Equal("оставить", center.History[0].Text);
        }

        using var reopened = new Database(dbFile.Path);
        var after = Center(reopened);
        Assert.Single(after.History);
        Assert.Equal("оставить", after.History[0].Text);
    }

    [Fact]
    public void Clear_EmptiesHistoryAndCounter()
    {
        using var dbFile = new TempDb();
        using var db = new Database(dbFile.Path);
        var center = Center(db);
        center.Add("что-то", NotificationCategory.General);

        center.Clear();

        Assert.Empty(center.History);
        Assert.Equal(0, center.UnreadCount);
        Assert.Empty(db.GetNotifications());
    }

    /// <summary>Повтор поднимает запись наверх, растит счётчик повторов и СНИМАЕТ «прочитано»:
    /// ошибка, случившаяся снова, — это новая информация, и залипать в прочитанном она не должна.</summary>
    [Fact]
    public void Repeat_BubblesUp_AndUnmarksRead()
    {
        using var dbFile = new TempDb();
        using var db = new Database(dbFile.Path);
        var center = Center(db);

        center.Add("Не удалось принять конфиг с диска", NotificationCategory.Sync);
        center.Add("свежее", NotificationCategory.General);
        center.MarkAllRead();
        Assert.Equal(0, center.UnreadCount);

        center.Add("Не удалось принять конфиг с диска", NotificationCategory.Sync);

        Assert.Equal(2, center.History.Count);
        Assert.Equal("Не удалось принять конфиг с диска", center.History[0].Text);
        Assert.Equal(2, center.History[0].Repeats);
        Assert.False(center.History[0].IsRead);
        Assert.Equal(1, center.UnreadCount);
        Assert.Contains("×2", center.History[0].DisplayText);
    }

    /// <summary>Пометка «новое» (точка и полужирный в списке) живёт отдельно от «прочитано»:
    /// «прочитано» гаснет в тот же миг, когда строку показали, и по нему пометку не нарисовать —
    /// она пропала бы за один кадр. Снимает её только повторное открытие окна или «Всё прочитано».</summary>
    [Fact]
    public void IsNew_SurvivesMarkRead_ButNotMarkAllRead()
    {
        using var dbFile = new TempDb();
        using var db = new Database(dbFile.Path);
        var center = Center(db);

        var entry = center.Add("свежее", NotificationCategory.General);
        Assert.True(entry.IsNew);

        center.MarkRead(entry);
        Assert.True(entry.IsNew);
        Assert.True(entry.IsRead);

        center.MarkAllRead();
        Assert.False(entry.IsNew);

        // Пришло снова — снова новость, и пометка возвращается вместе со снятым «прочитано».
        center.Add("свежее", NotificationCategory.General);
        Assert.True(entry.IsNew);
        Assert.False(entry.IsRead);
    }

    /// <summary>Свежая запись непрочитана — иначе счётчик не вырос бы никогда.</summary>
    [Fact]
    public void NewEntryIsUnreadByDefault()
    {
        using var dbFile = new TempDb();
        using var db = new Database(dbFile.Path);

        var entry = Center(db).Add("что угодно", NotificationCategory.General);

        Assert.False(entry.IsRead);
        Assert.Equal("что угодно", entry.DisplayText);
    }

    /// <summary>Обрезка по лимиту выкидывает самое старое, а не самое новое.</summary>
    [Fact]
    public void Trim_KeepsNewest()
    {
        using var dbFile = new TempDb();
        using var db = new Database(dbFile.Path);
        var start = new DateTime(2026, 9, 1, 8, 0, 0);

        for (var i = 0; i < 10; i++)
            db.SaveNotification($"сообщение {i}", NotificationCategory.General, start.AddMinutes(i), limit: 4);

        var kept = db.GetNotifications();
        Assert.Equal(4, kept.Count);
        Assert.Equal("сообщение 9", kept[0].Text);
        Assert.DoesNotContain(kept, k => k.Text == "сообщение 5");
    }

    /// <summary>Всё, что попало в историю, показывается с непустым текстом — та самая «пустота» из
    /// тикета не должна возникнуть и на уровне модели строки.</summary>
    [Fact]
    public void EveryLoadedEntry_HasTextAndCategoryLabel()
    {
        using var dbFile = new TempDb();
        using var db = new Database(dbFile.Path);
        var center = Center(db);
        foreach (var (category, _) in NotificationCategoryInfo.All)
            center.Add($"сообщение категории {category}", category);

        using var reopened = new Database(dbFile.Path);
        foreach (var entry in Center(reopened).History)
        {
            Assert.False(string.IsNullOrWhiteSpace(entry.DisplayText));
            Assert.False(string.IsNullOrWhiteSpace(entry.CategoryLabel));
            Assert.False(string.IsNullOrWhiteSpace(entry.WhenLabel));
        }
    }
}

/// <summary>Тикет: «добавить более подробную информацию, какие теги и куда добавились». Фраза
/// собиралась внутри диалога модерации и никому больше не была доступна — у файлов параметров
/// сообщение оставалось безликим «Теги обновлены: имя файла».</summary>
public class TagChangeTextTests
{
    [Fact]
    public void ListsAddedAndRemoved()
    {
        var text = TagChangeText.Describe(
            new[] { "черновик", "насос" },
            new[] { "насос", "жокей", "2 насоса" });

        Assert.Equal("теги добавлены: 2 насоса, жокей; убраны: черновик", text);
    }

    [Fact]
    public void OnlyAdded_OmitsRemovedPart()
    {
        Assert.Equal("теги добавлены: жокей", TagChangeText.Describe(new string[0], new[] { "жокей" }));
    }

    [Fact]
    public void NoChange_GivesEmptyString()
    {
        // Пустая строка, а не «теги » — вызывающий по ней решает, показывать ли вообще что-нибудь.
        Assert.Equal("", TagChangeText.Describe(new[] { "насос" }, new[] { "насос" }));
        Assert.Equal("", TagChangeText.Describe(new string[0], new string[0]));
    }

    [Fact]
    public void CaseOnlyDifferenceIsNotAChange()
    {
        // «ПИ» и «пи» — один тег: набор тегов сравнивается без учёта регистра.
        Assert.Equal("", TagChangeText.Describe(new[] { "ПИ" }, new[] { "пи" }));
    }
}
