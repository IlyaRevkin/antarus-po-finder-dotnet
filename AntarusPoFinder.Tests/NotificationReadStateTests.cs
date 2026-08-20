using System;
using System.Collections.Generic;
using AntarusPoFinder.App.Services;
using AntarusPoFinder.App.ViewModels;
using AntarusPoFinder.Core.Domain;
using Xunit;

namespace AntarusPoFinder.Tests;

/// <summary>Тикет: «авто скрытие уведомления после его просмотра». История уведомлений не
/// очищалась никогда — кроме кнопки «Очистить» и обрезки по лимиту, — и на второй день оператор
/// снова разбирал вчерашнее, чтобы найти сегодняшнее.
///
/// Здесь проверяется правило, которое можно проверить без поднятия WPF: повтор уже показанного
/// сообщения снимает признак «прочитано». Иначе залипшая фоновая ошибка, один раз прочитанная,
/// больше никогда бы не всплыла — а она как раз и есть то, что нужно увидеть.</summary>
public class NotificationReadStateTests
{
    private static NotificationEntry Entry(string text, bool isRead = false) =>
        new(text, DateTime.Now, NotificationCategory.General) { IsRead = isRead };

    [Fact]
    public void Repeat_UnmarksRead()
    {
        var history = new List<NotificationEntry>
        {
            Entry("свежее"),
            Entry("Не удалось принять конфиг с диска", isRead: true),
        };

        var collapsed = NotificationHistoryOps.CollapseRepeat(
            history, "Не удалось принять конфиг с диска", reopen: null, DateTime.Now);

        Assert.True(collapsed);
        // Поднялась наверх, счётчик вырос, признак «прочитано» снят.
        Assert.Equal("Не удалось принять конфиг с диска", history[0].Text);
        Assert.Equal(2, history[0].Repeats);
        Assert.False(history[0].IsRead);
    }

    [Fact]
    public void NewEntryIsUnreadByDefault()
    {
        Assert.False(Entry("что угодно").IsRead);
    }

    [Fact]
    public void MarkingReadKeepsEverythingElse()
    {
        var before = Entry("Теги обновлены") with { Repeats = 3, ReopenIsModal = true };
        var after = before with { IsRead = true };

        Assert.True(after.IsRead);
        Assert.Equal(before.Text, after.Text);
        Assert.Equal(3, after.Repeats);
        Assert.True(after.ReopenIsModal);
        Assert.Equal("Теги обновлены  ×3", after.DisplayText);
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
