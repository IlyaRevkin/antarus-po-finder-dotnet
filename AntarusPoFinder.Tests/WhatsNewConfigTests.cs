using AntarusPoFinder.App;
using AntarusPoFinder.Core.Data;
using AntarusPoFinder.Core.Services;
using AntarusPoFinder.Tests.TestHelpers;
using Xunit;

namespace AntarusPoFinder.Tests;

/// <summary>Окно «Что нового», показываемое после автообновления приложения (см.
/// MainWindowViewModel.CheckWhatsNewAsync). Покрывает две части:
/// • ConfigService.LastWhatsNewShownVersion — хранение/умолчание отдельного ключа
///   "last_whatsnew_shown_version" (по умолчанию пусто на свежей установке — см. ConfigService.Defaults);
/// • AppUpdateService.ShouldShowWhatsNew — чистое решение "показывать/не показывать" для трёх случаев
///   из постановки задачи (пустой ключ, версия отличается, версия совпадает), проверено уже отдельно
///   в AppUpdateServiceTests — здесь дополнительно проверяется связка с реальным ConfigService,
///   а не только сырыми строками.</summary>
public class WhatsNewConfigTests
{
    [Fact]
    public void LastWhatsNewShownVersion_DefaultsToEmpty_OnAFreshInstall()
    {
        using var dbFile = new TempDb();
        using var db = new Database(dbFile.Path);
        var cfg = new ConfigService(db);

        Assert.Equal("", cfg.LastWhatsNewShownVersion());
        // Пустой ключ — сигнал "ещё не показывали" — ShouldShowWhatsNew обязан отказать в показе,
        // а не решить, будто версия "0" уже когда-то была последней показанной.
        Assert.False(AppUpdateService.ShouldShowWhatsNew(cfg.LastWhatsNewShownVersion(), AppUpdateService.CurrentVersionText));
    }

    [Fact]
    public void LastWhatsNewShownVersion_RoundTripsAndSurvivesRestart()
    {
        using var dbFile = new TempDb();
        using var db = new Database(dbFile.Path);
        var cfg = new ConfigService(db);

        cfg.SetLastWhatsNewShownVersion("1.4.0");
        Assert.Equal("1.4.0", cfg.LastWhatsNewShownVersion());

        // "Перезапуск" — новый ConfigService поверх той же БД, как при следующем запуске приложения.
        var cfgAfterRestart = new ConfigService(db);
        Assert.Equal("1.4.0", cfgAfterRestart.LastWhatsNewShownVersion());
    }

    [Fact]
    public void ShouldShowWhatsNew_AfterRecordingOlderVersion_DetectsUpgrade()
    {
        // Сквозной сценарий: машина запомнила версию 1.4.0 (записана на предыдущем запуске), сейчас
        // приложение автообновилось до 1.5.0 — окно должно показаться.
        using var dbFile = new TempDb();
        using var db = new Database(dbFile.Path);
        var cfg = new ConfigService(db);
        cfg.SetLastWhatsNewShownVersion("1.4.0");

        Assert.True(AppUpdateService.ShouldShowWhatsNew(cfg.LastWhatsNewShownVersion(), "1.5.0"));
    }

    [Fact]
    public void ShouldShowWhatsNew_AfterRecordingSameVersion_DoesNotShowAgain()
    {
        using var dbFile = new TempDb();
        using var db = new Database(dbFile.Path);
        var cfg = new ConfigService(db);
        cfg.SetLastWhatsNewShownVersion("1.5.0");

        Assert.False(AppUpdateService.ShouldShowWhatsNew(cfg.LastWhatsNewShownVersion(), "1.5.0"));
    }
}
