using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using AntarusPoFinder.App.ViewModels;
using AntarusPoFinder.Core.Data;
using AntarusPoFinder.Core.Domain;
using Xunit;

namespace AntarusPoFinder.Tests;

/// <summary>Живая жалоба: «стала висеть ошибка an item same key has already been added. Key: ПИ, за
/// рабочий день она под 500 уведомлений наспамила».
///
/// Причина — расхождение того, как сворачивают регистр SQLite и .NET. Первичный ключ
/// flat_list_state — (kind, name) с COLLATE NOCASE, и по этому ключу код строил словарь с
/// StringComparer.OrdinalIgnoreCase: раз база не даёт двух имён, различающихся только регистром, то
/// и ключи уникальны. Но NOCASE в SQLite сворачивает ТОЛЬКО латиницу — «ПИ» и «пи» для базы разные
/// строки и лежат рядом законно, а для .NET это один ключ. Приём конфига падал на построении
/// словаря, и падал на каждом тике.
///
/// Второй тест — про сам спам: даже правильная ошибка не должна плодить сотни строк в истории.</summary>
public class CyrillicCaseFlatListTests
{
    private static string NewTempDb() => Path.Combine(Path.GetTempPath(), $"antarus_cyrcase_{Guid.NewGuid():N}.db");

    private static void Cleanup(params string[] dbPaths)
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (var db in dbPaths)
            foreach (var f in new[] { db, db + "-wal", db + "-shm" })
                if (File.Exists(f)) File.Delete(f);
    }

    [Fact]
    public void SqliteNocase_DoesNotFoldCyrillic_SoBothSpellingsCoexist()
    {
        // Предпосылка всего бага. Если это когда-нибудь перестанет быть правдой (сменился провайдер,
        // включили ICU) — тест ниже потеряет смысл, и об этом надо узнать отсюда, а не из падения.
        var path = NewTempDb();
        try
        {
            using var db = new Database(path);
            db.AddTag("ПИ");
            db.AddTag("пи");
            db.AddTag("ABC");
            db.AddTag("abc");

            var tags = db.GetAllTags();
            Assert.Contains("ПИ", tags);
            Assert.Contains("пи", tags);
            // Латиница сворачивается как и ожидается — второе написание в таблицу не попадает.
            Assert.Single(tags.Where(t => t.Equals("ABC", StringComparison.OrdinalIgnoreCase)));
        }
        finally { Cleanup(path); }
    }

    [Fact]
    public void Import_WithTwoCyrillicSpellingsOfOneName_DoesNotThrow()
    {
        var pathA = NewTempDb();
        var pathB = NewTempDb();
        try
        {
            using var dbA = new Database(pathA);
            using var dbB = new Database(pathB);

            // Оба написания живут на принимающей машине — ровно то состояние, в котором приём падал.
            dbB.AddTag("ПИ");
            dbB.AddTag("пи");
            dbA.AddTag("что-нибудь ещё");

            var exported = dbA.ExportHierarchyData();

            var preview = dbB.PreviewImportHierarchyData(exported);
            Assert.NotNull(preview);

            var counts = dbB.ImportHierarchyData(exported);
            Assert.NotNull(counts);

            // Ни одно из написаний приём не потерял: удалять их он не собирался, а значит и не должен.
            var tagsB = dbB.GetAllTags();
            Assert.Contains("ПИ", tagsB);
            Assert.Contains("пи", tagsB);
        }
        finally { Cleanup(pathA, pathB); }
    }

    [Fact]
    public void Import_CyrillicCaseTwins_KeepsTheFresherDecision()
    {
        // Из двух написаний одного имени берётся более свежее решение, а не то, что прочиталось
        // последним: иначе давнее «добавлен» могло бы перебить только что сделанное удаление.
        var pathA = NewTempDb();
        var pathB = NewTempDb();
        try
        {
            using var dbA = new Database(pathA);
            using var dbB = new Database(pathB);

            dbB.AddTag("пи");   // старое написание
            dbB.AddTag("ПИ");   // новое, более свежая отметка
            dbB.DeleteTag("ПИ"); // и его же сняли — это самое свежее событие по имени «ПИ»

            // A ничего про этот тег не знает и присылает свой снимок.
            var counts = dbB.ImportHierarchyData(dbA.ExportHierarchyData());
            Assert.NotNull(counts);

            // Снятое не воскресло из-за второго написания.
            Assert.DoesNotContain("ПИ", dbB.GetAllTags());
        }
        finally { Cleanup(pathA, pathB); }
    }

    [Fact]
    public void RepeatedNotification_CollapsesInsteadOfPilingUp()
    {
        // Залипшая фоновая ошибка чередуется с обычными сообщениями синхронизации, поэтому проверка
        // «совпадает ли с самой верхней записью» её не ловила и каждый тик заводил новую строку.
        var history = new ObservableCollection<NotificationEntry>();
        var now = new DateTime(2026, 8, 11, 9, 0, 0);
        const string error = "Сбой синхронизации конфига: что-то пошло не так";

        for (var tick = 0; tick < 50; tick++)
        {
            Record(history, error, now.AddMinutes(tick * 2));
            Record(history, $"Применён конфиг с диска (изменений: {tick})", now.AddMinutes(tick * 2 + 1));
        }

        Assert.Single(history.Where(e => e.Text == error));
        Assert.Equal(50, history.First(e => e.Text == error).Repeats);
        Assert.Contains("×50", history.First(e => e.Text == error).DisplayText);
        // 50 разных сообщений об успехе + одна свёрнутая ошибка.
        Assert.Equal(51, history.Count);
    }

    [Fact]
    public void FirstNotification_IsNotMarkedAsRepeat()
    {
        var history = new ObservableCollection<NotificationEntry>();
        Record(history, "однократное сообщение", DateTime.Now);

        var entry = Assert.Single(history);
        Assert.Equal(1, entry.Repeats);
        Assert.Equal("однократное сообщение", entry.DisplayText);
    }

    /// <summary>То же, что делает MainWindowViewModel.AddNotification, но без поднятия WPF.</summary>
    private static void Record(IList<NotificationEntry> history, string text, DateTime when)
    {
        if (NotificationHistoryOps.CollapseRepeat(history, text, reopen: null, when)) return;
        history.Insert(0, new NotificationEntry(text, when, NotificationCategory.Sync));
    }
}
