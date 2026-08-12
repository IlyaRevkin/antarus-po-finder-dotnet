using System.Linq;
using AntarusPoFinder.App.Services;
using AntarusPoFinder.Core.Data;
using AntarusPoFinder.Core.Services;
using AntarusPoFinder.Tests.TestHelpers;
using Xunit;

namespace AntarusPoFinder.Tests;

/// <summary>Адреса хранилища и диска инструкций (s3_endpoint/s3_bucket/s3_region/
/// instruction_base_url — см. ConfigService.PresetKeys). Жалоба Ильи: «не синхронизируется настройка
/// ссылки для хранилища, а также ссылка инструкции, плюс оно должно быть предустановлено по
/// умолчанию».
///
/// Разобранных причин было три, и все три покрыты здесь:
/// <list type="number">
/// <item>у instruction_base_url предустановки не было вовсе — поле оставалось пустым на каждой
/// машине, и в QR уходил сетевой путь, который с телефона не открыть;</item>
/// <item>умолчание из кода живёт только на своей машине: в общий конфиг уезжает ТАБЛИЦА settings
/// (ConfigSyncService.PrepareExport), поэтому адрес, ни разу не сохранённый руками, до соседей не
/// доезжал — отсюда разовая миграция, которая делает его обычной строкой настройки;</item>
/// <item>а пустая строка, уехавшая с машины, где адрес не настраивали, затирала адрес у всех
/// остальных — теперь она не применяется (ShouldApplySetting).</item>
/// </list></summary>
public class HostingAddressPresetTests
{
    [Fact]
    public void FreshInstall_HasCompanyAddressesPreset()
    {
        using var dbFile = new TempDb();
        using var db = new Database(dbFile.Path);
        var cfg = new ConfigService(db);

        Assert.Equal(ConfigService.DefaultS3Endpoint, cfg.S3Endpoint());
        Assert.Equal(ConfigService.DefaultS3Bucket, cfg.S3Bucket());
        Assert.Equal(ConfigService.DefaultS3Region, cfg.S3Region());
        Assert.Equal(ConfigService.DefaultInstructionBaseUrl, cfg.InstructionBaseUrl());
    }

    /// <summary>Главное, ради чего заведена миграция: адрес обязан ЛЕЖАТЬ в settings, а не
    /// подставляться из кода, иначе он не попадёт в общий конфиг и не доедет до соседа.</summary>
    [Fact]
    public void Migration_PutsPresetsIntoTheSettingsTable_SoTheyTravelInTheSharedConfig()
    {
        using var dbFile = new TempDb();
        using var db = new Database(dbFile.Path);

        var stored = db.GetAllSettings();
        foreach (var (key, value) in ConfigService.PresetDefaults)
        {
            Assert.True(stored.ContainsKey(key), $"{key} должен физически лежать в settings");
            Assert.Equal(value, stored[key]);
        }
    }

    /// <summary>Установленная копия, где адреса когда-то стёрли в пустоту (или их там не было):
    /// миграция заполняет их предустановкой при первом открытии базы новой версией.</summary>
    [Fact]
    public void Migration_FillsBlankRowsInAnAlreadyInstalledDatabase()
    {
        using var dbFile = new TempDb();
        using (var db = new Database(dbFile.Path))
        {
            // Симулируем старую базу: адреса пустые, отметки о миграции нет.
            foreach (var (key, _) in ConfigService.PresetDefaults)
                db.SetSetting(key, "");
            db.SetSetting("migration_hosting_addresses_seeded", "");
        }

        using var reopened = new Database(dbFile.Path);
        Assert.Equal(ConfigService.DefaultInstructionBaseUrl, reopened.GetSetting("instruction_base_url"));
        Assert.Equal(ConfigService.DefaultS3Endpoint, reopened.GetSetting("s3_endpoint"));
    }

    /// <summary>Осознанно сменённый адрес миграция не перебивает — разовый флаг ровно для этого
    /// (тот же приём, что у AddNewDefaultManufacturersOnce).</summary>
    [Fact]
    public void Migration_NeverOverwritesAnAddressSomebodyChangedOnPurpose()
    {
        using var dbFile = new TempDb();
        using (var db = new Database(dbFile.Path))
            new ConfigService(db).SetInstructionBaseUrl("https://files.example.local/po");

        using var reopened = new Database(dbFile.Path);
        Assert.Equal("https://files.example.local/po", new ConfigService(reopened).InstructionBaseUrl());
    }

    /// <summary>Стёртое поле не гасит адрес навсегда: пустая строка у этих ключей означает «не
    /// настроено» и читается как предустановка.</summary>
    [Fact]
    public void BlankValue_ReadsBackAsThePreset()
    {
        using var dbFile = new TempDb();
        using var db = new Database(dbFile.Path);
        var cfg = new ConfigService(db);

        cfg.SetInstructionBaseUrl("");
        cfg.SetS3Endpoint("");

        Assert.Equal(ConfigService.DefaultInstructionBaseUrl, cfg.InstructionBaseUrl());
        Assert.Equal(ConfigService.DefaultS3Endpoint, cfg.S3Endpoint());
    }

    /// <summary>Пустой префикс — законное значение («раскладка бакета совпадает с раскладкой диска»),
    /// и в PresetKeys его сознательно нет: залечивать здесь нечего.</summary>
    [Fact]
    public void EmptyPrefix_StaysEmpty_ItIsAValidValue()
    {
        using var dbFile = new TempDb();
        using var db = new Database(dbFile.Path);
        var cfg = new ConfigService(db);

        cfg.SetS3Prefix("");
        Assert.Equal("", cfg.S3Prefix());
    }

    [Fact]
    public void ChangedAddress_SyncsToTheOtherMachine()
    {
        using var m = new TwoMachines();
        m.SetSharedRoot();

        m.CfgA.SetInstructionBaseUrl("https://files.example.local/po");
        m.CfgA.SetS3Bucket("amperus-2");

        ConfigSyncService.Export(m.SvcA, m.Root.Path, "profileA");
        var update = ConfigSyncService.CheckForUpdate(m.SvcB, out var error);
        Assert.Null(error);
        Assert.NotNull(update);
        ConfigSyncService.Apply(m.SvcB, update!.ConfigPath, m.Root.Path);

        Assert.Equal("https://files.example.local/po", m.CfgB.InstructionBaseUrl());
        Assert.Equal("amperus-2", m.CfgB.S3Bucket());
    }

    /// <summary>Та самая поломка «не синхронизируется»: машина, где адрес стёрли, уносила пустую
    /// строку в общий конфиг и гасила адрес у всех. Теперь пустое значение этих ключей не
    /// применяется — как и удаления справочников, которые синхронизация тоже не переносит.</summary>
    [Fact]
    public void BlankAddressFromAnotherMachine_DoesNotWipeALocallyConfiguredOne()
    {
        using var m = new TwoMachines();
        m.SetSharedRoot();

        m.CfgB.SetInstructionBaseUrl("https://files.example.local/po");
        m.CfgA.SetInstructionBaseUrl(""); // на A адрес стёрли

        ConfigSyncService.Export(m.SvcA, m.Root.Path, "profileA");
        var update = ConfigSyncService.CheckForUpdate(m.SvcB, out _);
        Assert.NotNull(update);
        ConfigSyncService.Apply(m.SvcB, update!.ConfigPath, m.Root.Path);

        Assert.Equal("https://files.example.local/po", m.CfgB.InstructionBaseUrl());
    }

    /// <summary>Проверка применения и подсчёт «что изменилось» обязаны совпадать: иначе плашка приёма
    /// вечно обещала бы изменение, которого применение не делает (см. ShouldApplySetting).</summary>
    [Fact]
    public void BlankAddress_IsNotCountedAsAPendingChangeEither()
    {
        using var m = new TwoMachines();
        m.SetSharedRoot();

        m.CfgB.SetInstructionBaseUrl("https://files.example.local/po");
        m.CfgA.SetInstructionBaseUrl("");

        ConfigSyncService.Export(m.SvcA, m.Root.Path, "profileA");
        var update = ConfigSyncService.CheckForUpdate(m.SvcB, out _);
        Assert.NotNull(update);

        ConfigSyncService.Apply(m.SvcB, update!.ConfigPath, m.Root.Path);
        // После применения свежих изменений не остаётся: пустой адрес не считается изменением, а
        // всё остальное уже применено.
        ConfigSyncService.Export(m.SvcA, m.Root.Path, "profileA");
        var again = ConfigSyncService.CheckForUpdate(m.SvcB, out _);
        Assert.Equal(0, again?.SettingsChanged ?? 0);
    }

    /// <summary>Ключи предустановок обязаны синхронизироваться: держать их в SkipSettingsKeys значило
    /// бы вернуть исходную жалобу («настроил у себя — у соседа пусто»).</summary>
    [Fact]
    public void PresetKeys_AreNotInSkipSettingsKeys()
    {
        var skip = ConfigSyncSkipKeys.Read();
        foreach (var key in ConfigService.PresetKeys)
            Assert.DoesNotContain(key, skip);
    }
}
