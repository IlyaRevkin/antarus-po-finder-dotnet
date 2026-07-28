using System;
using System.Linq;
using AntarusPoFinder.Core.Data;
using AntarusPoFinder.Core.Services;
using AntarusPoFinder.Tests.TestHelpers;
using Xunit;

namespace AntarusPoFinder.Tests;

/// <summary>Постоянный журнал «что менялось по версиям программы» (ConfigService.AppChangelogHistory):
/// наполняется при обновлении, читается окном истории изменений в любой момент потом. Проверяем дедуп
/// по версии, порядок (новые сверху), сохранность между перезапусками и предел длины.</summary>
public class AppChangelogHistoryTests
{
    [Fact]
    public void AddAppChangelogEntry_DedupsByVersion_NewestFirst_AndPersists()
    {
        using var dbFile = new TempDb();
        using var db = new Database(dbFile.Path);
        var cfg = new ConfigService(db);

        Assert.Empty(cfg.AppChangelogHistory());

        cfg.AddAppChangelogEntry("1.0.0", "первые заметки", new DateTime(2026, 1, 1));
        cfg.AddAppChangelogEntry("1.1.0", "вторые заметки", new DateTime(2026, 2, 1));
        // Повторный показ той же версии не плодит дубль, а обновляет запись и поднимает её наверх.
        cfg.AddAppChangelogEntry("1.0.0", "переписанные заметки", new DateTime(2026, 3, 1));
        // Пустая версия игнорируется.
        cfg.AddAppChangelogEntry("", "мусор", new DateTime(2026, 3, 2));

        // Читаем свежим экземпляром — журнал реально сохранён в настройках, а не только в памяти.
        var history = new ConfigService(db).AppChangelogHistory();
        Assert.Equal(2, history.Count);
        Assert.Equal("1.0.0", history[0].Version);
        Assert.Equal("переписанные заметки", history[0].Notes);
        Assert.Equal("1.1.0", history[1].Version);
    }

    [Fact]
    public void AddAppChangelogEntry_CapsHistoryLength()
    {
        using var dbFile = new TempDb();
        using var db = new Database(dbFile.Path);
        var cfg = new ConfigService(db);

        // Больше предела (50) — старые версии подрезаются, новейшие остаются.
        for (var i = 1; i <= 60; i++)
            cfg.AddAppChangelogEntry($"2.0.{i}", $"заметки {i}", new DateTime(2026, 1, 1).AddDays(i));

        var history = cfg.AppChangelogHistory();
        Assert.Equal(50, history.Count);
        Assert.Equal("2.0.60", history[0].Version);
    }
}
