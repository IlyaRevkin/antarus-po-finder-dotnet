using AntarusPoFinder.Core.Data;
using AntarusPoFinder.Core.Services;
using AntarusPoFinder.Tests.TestHelpers;
using Xunit;

namespace AntarusPoFinder.Tests;

/// <summary>Автообновление приложения (Настройки → Общие → «Устанавливать найденные обновления
/// автоматически при запуске», см. ConfigService.AppAutoUpdate).
///
/// Реальный инцидент, из-за которого дефолт поменялся: пользователь три раунда подряд смотрел на
/// давно исправленные баги, потому что сидел на сборке многодневной давности. Весь транспорт
/// обновления при этом был полностью исправен — релиз виден в GitHub Releases, .sha256 сходится,
/// установка проходит (это покрыто AppUpdateServiceTests). Не работала именно доставка: при
/// выключенном по умолчанию автообновлении единственной дорогой к новой версии оставалась плашка
/// с кнопкой «Установить», а наладчик в Настройки не заходит и плашку пропускает.
///
/// Ключевой момент, который здесь и проверяется: дефолт действует ТОЛЬКО на установки, где ключ
/// ни разу не сохраняли. Если кто-то осознанно выключил автообновление у себя, в settings лежит
/// явное "false", и смена дефолта не имеет права его перебить.</summary>
public class AppAutoUpdateDefaultTests
{
    [Fact]
    public void AppAutoUpdate_DefaultsToTrue_OnAFreshInstall()
    {
        using var dbFile = new TempDb();
        using var db = new Database(dbFile.Path);
        var cfg = new ConfigService(db);

        Assert.True(cfg.AppAutoUpdate());
    }

    [Fact]
    public void AppAutoUpdate_ExplicitFalse_IsNotOverriddenByTheNewDefault()
    {
        // Ровно та причина, по которой смена дефолта здесь сделана без разового сброса уже
        // сохранённых значений (в отличие от app_start_minimized, см.
        // Database.ResetAppStartMinimizedDefaultOnce): выключил у себя — значит выключено.
        using var dbFile = new TempDb();
        using var db = new Database(dbFile.Path);
        new ConfigService(db).SetAppAutoUpdate(false);

        var cfgAfterRestart = new ConfigService(db);
        Assert.False(cfgAfterRestart.AppAutoUpdate());
    }

    [Fact]
    public void AppAutoUpdate_RoundTripsAndSurvivesRestart()
    {
        using var dbFile = new TempDb();
        using var db = new Database(dbFile.Path);
        var cfg = new ConfigService(db);

        cfg.SetAppAutoUpdate(false);
        Assert.False(cfg.AppAutoUpdate());

        cfg.SetAppAutoUpdate(true);
        Assert.True(new ConfigService(db).AppAutoUpdate());
    }
}
