using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using AntarusPoFinder.Core.Data;
using AntarusPoFinder.Core.Infrastructure;
using AntarusPoFinder.Core.Services;
using Xunit;

namespace AntarusPoFinder.Tests;

/// <summary>РЕЗЕРВНЫЕ КОПИИ. До этой функции копий у программы не было вообще, поэтому проверяется
/// не «как оно выглядит», а три вещи, из-за которых копия может оказаться бесполезной ровно в тот
/// день, когда понадобится:
///   • база снимается ПРАВИЛЬНЫМ способом (VACUUM INTO) и открывается как база;
///   • зашифрованная паролем копия не читается ни без пароля, ни чужим паролем, и внутри неё не
///     видно открытого текста — бакет публичен на чтение;
///   • уборка старого никогда не выносит последнюю копию и не трогает чужие файлы.</summary>
public class BackupTests
{
    private sealed class Temp : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "antarus-backup-test-" + Guid.NewGuid().ToString("N"));

        public Temp() => Directory.CreateDirectory(Path);

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); } catch { }
        }
    }

    // ── Снимок базы ───────────────────────────────────────────────────────────────────────────

    /// <summary>Главный тест всей затеи: копия, снятая у ОТКРЫТОЙ базы в режиме WAL, открывается и
    /// содержит данные, записанные перед самым снятием. Файловое копирование .db здесь бы и
    /// провалилось — последняя транзакция в этот момент лежит в .db-wal.</summary>
    [Fact]
    public void BackupTo_LiveWalDatabase_ProducesReadableCopyWithLatestWrites()
    {
        using var temp = new Temp();
        var dbPath = Path.Combine(temp.Path, "po_finder.db");
        var copyPath = Path.Combine(temp.Path, "copy.db");

        using (var db = new Database(dbPath))
        {
            db.SetSetting("backup_probe", "значение-перед-копией");
            db.BackupTo(copyPath);
        }

        Assert.True(File.Exists(copyPath));
        using var restored = new Database(copyPath);
        Assert.Equal("значение-перед-копией", restored.GetSetting("backup_probe"));
    }

    /// <summary>VACUUM INTO отказывается писать поверх существующего файла — и это ровно то
    /// поведение, которое нужно: затереть вчерашнюю копию сегодняшней неудачей нельзя.</summary>
    [Fact]
    public void BackupTo_ExistingTarget_Throws()
    {
        using var temp = new Temp();
        var copyPath = Path.Combine(temp.Path, "copy.db");
        File.WriteAllText(copyPath, "уже лежит");

        using var db = new Database(Path.Combine(temp.Path, "po_finder.db"));
        Assert.ThrowsAny<Exception>(() => db.BackupTo(copyPath));
        Assert.Equal("уже лежит", File.ReadAllText(copyPath));
    }

    // ── Шифрование паролем ────────────────────────────────────────────────────────────────────

    [Fact]
    public void EncryptWithPassword_RoundTrips()
    {
        var plain = Encoding.UTF8.GetBytes("содержимое архива с базой");

        var sealed_ = ConfigFileCrypto.EncryptWithPassword(plain, "пароль Ильи");
        var back = ConfigFileCrypto.TryDecryptWithPassword(sealed_, "пароль Ильи");

        Assert.Equal(plain, back);
    }

    [Fact]
    public void EncryptWithPassword_WrongPassword_ReturnsNull()
    {
        var sealed_ = ConfigFileCrypto.EncryptWithPassword(Encoding.UTF8.GetBytes("тайна"), "верный");

        Assert.Null(ConfigFileCrypto.TryDecryptWithPassword(sealed_, "неверный"));
        Assert.Null(ConfigFileCrypto.TryDecryptWithPassword(sealed_, ""));
    }

    /// <summary>Ключ, лежащий внутри программы, к этим копиям отношения не имеет — иначе весь смысл
    /// пароля терялся бы (бакет читает кто угодно, exe у него тоже есть).</summary>
    [Fact]
    public void EncryptWithPassword_IsNotReadableByTheBuiltInKey()
    {
        var sealed_ = ConfigFileCrypto.EncryptWithPassword(Encoding.UTF8.GetBytes("тайна"), "пароль");

        Assert.Null(ConfigFileCrypto.TryDecrypt(sealed_));
    }

    [Fact]
    public void EncryptWithPassword_DoesNotLeakPlaintext()
    {
        var plain = Encoding.UTF8.GetBytes("ОЧЕНЬ-СЕКРЕТНАЯ-СТРОКА");

        var sealed_ = ConfigFileCrypto.EncryptWithPassword(plain, "пароль");

        Assert.DoesNotContain("ОЧЕНЬ-СЕКРЕТНАЯ-СТРОКА", Encoding.UTF8.GetString(sealed_));
        Assert.True(ConfigFileCrypto.IsPasswordEncrypted(sealed_));
        Assert.False(ConfigFileCrypto.IsPasswordEncrypted(ConfigFileCrypto.Encrypt("обычный конфиг")));
    }

    [Fact]
    public void EncryptWithPassword_EmptyPassword_Throws() =>
        Assert.Throws<ArgumentException>(() => ConfigFileCrypto.EncryptWithPassword(new byte[] { 1 }, ""));

    /// <summary>Случайная соль: два шифрования одного и того же одним паролем не совпадают побайтно.
    /// Без этого одинаковые пароли на разных машинах давали бы одинаковые ключи.</summary>
    [Fact]
    public void EncryptWithPassword_UsesRandomSalt()
    {
        var plain = Encoding.UTF8.GetBytes("одно и то же");

        var a = ConfigFileCrypto.EncryptWithPassword(plain, "пароль");
        var b = ConfigFileCrypto.EncryptWithPassword(plain, "пароль");

        Assert.NotEqual(a, b);
        Assert.Equal(plain, ConfigFileCrypto.TryDecryptWithPassword(b, "пароль"));
    }

    // ── Имена и уборка ────────────────────────────────────────────────────────────────────────

    [Fact]
    public void FileName_AndParseTaken_AgreeWithEachOther()
    {
        var taken = new DateTime(2026, 11, 4, 3, 15, 0, DateTimeKind.Utc);

        Assert.Equal("po-finder-20261104_031500.zip", BackupArchive.FileName(taken, encrypted: false));
        Assert.Equal("po-finder-20261104_031500.apofbak", BackupArchive.FileName(taken, encrypted: true));
        Assert.Equal(taken, BackupArchive.ParseTaken(BackupArchive.FileName(taken, encrypted: false)));
        Assert.Equal(taken, BackupArchive.ParseTaken("backup/ilia-pc/" + BackupArchive.FileName(taken, encrypted: true)));
    }

    [Theory]
    [InlineData("какой-то-файл.zip")]
    [InlineData("po-finder-не-дата.zip")]
    [InlineData("po-finder-20261104_031500.txt")]
    [InlineData("отчёт.docx")]
    public void ParseTaken_ForeignNames_ReturnsNull(string name) =>
        Assert.Null(BackupArchive.ParseTaken(name));

    [Fact]
    public void ObjectKey_PutsMachineInPathWithoutCyrillic()
    {
        var key = BackupArchive.ObjectKey("ILIA-PC", new DateTime(2026, 11, 4, 3, 15, 0, DateTimeKind.Utc));

        Assert.Equal("backup/ilia-pc/po-finder-20261104_031500.apofbak", key);
    }

    /// <summary>Имя компьютера целиком кириллическое — обычное дело в конторе. Такое имя чистится в
    /// пустоту, и если бы все они стали общим «unknown», машины складывали бы копии в одну папку и
    /// выносили бы копии друг друга при уборке старого. Отпечаток у каждой свой — и один и тот же
    /// при каждом запуске.</summary>
    [Fact]
    public void MachineKey_CyrillicNames_StayDistinctAndStable()
    {
        var a = BackupArchive.MachineKey("КОМП-ИЛЬИ");
        var b = BackupArchive.MachineKey("КОМП-ПЕТРА");

        Assert.NotEqual(a, b);
        Assert.NotEqual("unknown", a);
        Assert.Equal(a, BackupArchive.MachineKey("КОМП-ИЛЬИ"));
        Assert.DoesNotContain(a, ch => ch > '\u007f');
        Assert.Equal("unknown", BackupArchive.MachineKey(""));
    }

    [Fact]
    public void ToPrune_DropsOnlyWhatIsOlderThanKeepDays()
    {
        var now = new DateTime(2026, 11, 4, 0, 0, 0, DateTimeKind.Utc);
        var names = new[]
        {
            BackupArchive.FileName(now.AddDays(-1), false),
            BackupArchive.FileName(now.AddDays(-29), false),
            BackupArchive.FileName(now.AddDays(-31), false),
            BackupArchive.FileName(now.AddDays(-400), false),
        };

        var prune = BackupArchive.ToPrune(names, keepDays: 30, now);

        Assert.Equal(2, prune.Count);
        Assert.Contains(BackupArchive.FileName(now.AddDays(-31), false), prune);
        Assert.Contains(BackupArchive.FileName(now.AddDays(-400), false), prune);
    }

    /// <summary>Машину не включали год — остаться совсем без копии хуже, чем держать одну старую.</summary>
    [Fact]
    public void ToPrune_NeverRemovesTheNewestCopy()
    {
        var now = new DateTime(2026, 11, 4, 0, 0, 0, DateTimeKind.Utc);
        var names = new[]
        {
            BackupArchive.FileName(now.AddDays(-300), false),
            BackupArchive.FileName(now.AddDays(-400), false),
        };

        var prune = BackupArchive.ToPrune(names, keepDays: 30, now);

        Assert.Single(prune);
        Assert.Equal(BackupArchive.FileName(now.AddDays(-400), false), prune[0]);
    }

    [Fact]
    public void ToPrune_LeavesForeignFilesAlone()
    {
        var now = new DateTime(2026, 11, 4, 0, 0, 0, DateTimeKind.Utc);
        var names = new[]
        {
            "не трогать.zip", "заметки.txt",
            BackupArchive.FileName(now.AddDays(-1), false),
            BackupArchive.FileName(now.AddDays(-99), false),
        };

        var prune = BackupArchive.ToPrune(names, keepDays: 30, now);

        Assert.Single(prune);
        Assert.DoesNotContain("не трогать.zip", prune);
    }

    [Fact]
    public void ToPrune_KeepDaysZero_MeansNeverClean()
    {
        var now = new DateTime(2026, 11, 4, 0, 0, 0, DateTimeKind.Utc);
        var names = new[] { BackupArchive.FileName(now.AddDays(-5000), false), BackupArchive.FileName(now, false) };

        Assert.Empty(BackupArchive.ToPrune(names, keepDays: 0, now));
    }

    // ── Копия целиком: от базы до распакованной папки ─────────────────────────────────────────

    /// <summary>Сквозной прогон того единственного сценария, ради которого всё написано: сняли
    /// копию в папку → нашли её в списке → распаковали рядом → внутри лежит открывающаяся база и
    /// общий конфиг.</summary>
    [Fact]
    public async System.Threading.Tasks.Task RunAsync_ToFolder_ProducesArchiveThatExtractsBack()
    {
        using var temp = new Temp();
        var dbPath = Path.Combine(temp.Path, "po_finder.db");
        var folder = Path.Combine(temp.Path, "Копии");
        var sharedConfig = Path.Combine(temp.Path, "po_finder_config.json");
        File.WriteAllBytes(sharedConfig, ConfigFileCrypto.Encrypt("{\"root_path\":\"Z:\\\\Software\"}"));

        using var db = new Database(dbPath);
        db.SetSetting("backup_probe", "живое значение");

        var targets = new BackupTargets(folder, ToS3: false, Password: "", KeepDays: 30,
            S3: new S3Settings("", "", "", "", "", "", "", false));
        var outcome = await new BackupService().RunAsync(db, "TEST-PC", sharedConfig, targets);

        Assert.True(outcome.Ok, outcome.Error);
        Assert.NotNull(outcome.FolderFile);

        var listed = BackupService.ListFolder(folder, "TEST-PC");
        Assert.Single(listed);
        Assert.False(listed[0].Encrypted);

        var extracted = BackupService.ExtractBeside(outcome.FolderFile!, password: "");
        Assert.True(extracted.Ok, extracted.Error);

        var restoredDb = Path.Combine(extracted.Folder!, BackupArchive.DbEntryName);
        using (var restored = new Database(restoredDb))
            Assert.Equal("живое значение", restored.GetSetting("backup_probe"));

        Assert.True(File.Exists(Path.Combine(extracted.Folder!, BackupArchive.ConfigEntryName)));
        var manifest = File.ReadAllText(Path.Combine(extracted.Folder!, BackupArchive.ManifestEntryName));
        Assert.Contains("TEST-PC", manifest);
        Assert.Contains("howToRestore", manifest);
    }

    /// <summary>Недоступная шара — обычное дело (ноутбук вне офиса). Копия базы без конфига куда
    /// лучше, чем отсутствие копии.</summary>
    [Fact]
    public async System.Threading.Tasks.Task RunAsync_WithoutSharedConfig_StillBacksUpTheDatabase()
    {
        using var temp = new Temp();
        var folder = Path.Combine(temp.Path, "Копии");
        using var db = new Database(Path.Combine(temp.Path, "po_finder.db"));

        var targets = new BackupTargets(folder, false, "", 30, new S3Settings("", "", "", "", "", "", "", false));
        var outcome = await new BackupService().RunAsync(db, "TEST-PC", @"Z:\нет такого\config.json", targets);

        Assert.True(outcome.Ok, outcome.Error);
        using var zip = ZipFile.OpenRead(outcome.FolderFile!);
        Assert.Contains(zip.Entries, e => e.Name == BackupArchive.DbEntryName);
        Assert.DoesNotContain(zip.Entries, e => e.Name == BackupArchive.ConfigEntryName);
    }

    /// <summary>Выкладка в бакет без пароля НЕ ВКЛЮЧАЕТСЯ — бакет публичен на чтение. Это штатное
    /// «ещё не настроено», а не ошибка, поэтому именно свойство, а не исключение.</summary>
    [Fact]
    public void CanUploadToS3_RequiresPassword()
    {
        var live = new S3Settings("https://s3.twcstorage.ru", "amperus", "ru-1", "", "AK", "SK", "https://fs", true);

        Assert.False(new BackupTargets("", ToS3: true, Password: "", KeepDays: 30, S3: live).CanUploadToS3);
        Assert.True(new BackupTargets("", ToS3: true, Password: "пароль", KeepDays: 30, S3: live).CanUploadToS3);
        Assert.False(new BackupTargets("", ToS3: false, Password: "пароль", KeepDays: 30, S3: live).CanUploadToS3);
    }

    [Fact]
    public void ExtractBeside_EncryptedCopyWithWrongPassword_ExplainsInsteadOfFailingAsCorrupt()
    {
        using var temp = new Temp();
        var file = Path.Combine(temp.Path, BackupArchive.FileName(DateTime.UtcNow, encrypted: true));
        File.WriteAllBytes(file, ConfigFileCrypto.EncryptWithPassword(new byte[] { 1, 2, 3 }, "верный"));

        var result = BackupService.ExtractBeside(file, "неверный");

        Assert.False(result.Ok);
        Assert.Contains("паролем", result.Error);
    }
}
