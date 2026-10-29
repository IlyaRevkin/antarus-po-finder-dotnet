using System.Linq;
using AntarusPoFinder.Core.Data;
using AntarusPoFinder.Core.Domain;
using AntarusPoFinder.Core.Services;
using AntarusPoFinder.Tests.TestHelpers;
using Xunit;

namespace AntarusPoFinder.Tests;

/// <summary>Таблица QR-кодов прошивок (fw_qr_codes): заводится, перестраивается и чистится вслед за
/// базой, и всё это БЕЗ сети и без обхода диска — ради чего она и заведена (см.
/// <see cref="FirmwareQrCodes"/>). Проверяется именно поведение прохода, а не отрисовка кода:
/// картинку рисует App (QrArt), и у неё свои тесты с настоящим декодером.</summary>
public class FirmwareQrCodesTests
{
    private const string Root = @"Z:\ПО";
    private const string BaseUrl = "https://fs.example.ru";

    private static FirmwareQrSweep.Options Opts(string baseUrl = BaseUrl, string root = Root,
        TranslitMap? translit = null) =>
        new(baseUrl, root, translit ?? TranslitMap.Empty, "AMPERUS");

    private static (Database Db, TempDb File) NewDb()
    {
        var file = new TempDb();
        return (new Database(file.Path), file);
    }

    /// <summary>Заводит активную версию прошивки с указанным путём инструкции и возвращает её id.</summary>
    private static int SeedVersion(Database db, string instructionsPath, int sw = 1)
    {
        var group = db.GetAllEquipmentGroups().First(g => g.Name == "НГР");
        var subtypeName = $"QR-{Guid.NewGuid():N}"[..10];
        var subtypeId = db.UpsertEquipmentSubtype(new EquipmentSubType
        {
            GroupId = group.Id!.Value, Name = subtypeName, Prefix = 91, FolderName = subtypeName, SortOrder = 999,
        });
        var mod = db.GetAllModifications().First(m => m.ControllerName == "PIXEL" && m.DisplayName == "PIXEL-2511");

        db.AddFwVersion(new FwVersionRecord
        {
            SubtypeId = subtypeId, ControllerId = mod.ControllerId,
            EqPrefix = group.Prefix, SubPrefix = 91, HwVersion = mod.HwVersion, SwVersion = sw,
            DtStr = "20260101_0000", VersionRaw = $"1.1.{sw}.1.20260101_0000",
            Filename = "firmware.psl", DiskPath = "", InstructionsPath = instructionsPath,
            Description = "qr test", Status = "active",
        });

        return db.GetFwQrSubjects().Single(s => s.InstructionPath == instructionsPath).FwVersionId;
    }

    [Fact]
    public void Sweep_NewVersion_GetsWebLinkWithoutTouchingDisk()
    {
        var (db, file) = NewDb();
        using var _f = file; using var _db = db;

        // Путь заведомо несуществующий: проход НЕ ходит на диск, и это главное его свойство —
        // наладчик вне офиса должен получить свой код ровно так же, как в офисе.
        var id = SeedVersion(db, @"Z:\ПО\НГР\КНС\SMH5\1.0\Инструкция\инструкция.pdf");

        var result = FirmwareQrSweep.Run(db, Opts());

        Assert.Equal(1, result.Added);
        var row = db.GetFwQrCode(id);
        Assert.NotNull(row);
        Assert.Equal("url", row!.Kind);
        // Сегменты переводятся в латиницу тем же справочником, каким файл кладётся на хостинг
        // (TranslitMap/Transliteration) — сверяем форму адреса, а не таблицу перевода: у неё свои тесты.
        Assert.StartsWith("https://fs.example.ru/", row.Payload);
        Assert.Contains("/SMH5/1.0/", row.Payload);
        Assert.EndsWith(".pdf", row.Payload);
        Assert.DoesNotContain("Инструкция", row.Payload);
    }

    [Fact]
    public void Sweep_SecondRunWithSameInputs_WritesNothing()
    {
        var (db, file) = NewDb();
        using var _f = file; using var _db = db;
        SeedVersion(db, @"Z:\ПО\НГР\КНС\SMH5\1.0\Инструкция\и.pdf");

        FirmwareQrSweep.Run(db, Opts());
        var second = FirmwareQrSweep.Run(db, Opts());

        // Холостой проход — обязательное свойство: он крутится каждые десять минут, и если бы на
        // совпавшем отпечатке строка всё равно переписывалась, это был бы бесконечный поток записей
        // в базу на ровном месте.
        Assert.Equal(0, second.Added);
        Assert.Equal(0, second.Updated);
        Assert.Equal(1, second.Unchanged);
    }

    [Fact]
    public void Sweep_BaseUrlChanged_RebuildsLink()
    {
        var (db, file) = NewDb();
        using var _f = file; using var _db = db;
        var id = SeedVersion(db, @"Z:\ПО\НГР\КНС\SMH5\1.0\Инструкция\и.pdf");

        FirmwareQrSweep.Run(db, Opts());
        var after = FirmwareQrSweep.Run(db, Opts(baseUrl: "https://new.example.ru"));

        Assert.Equal(1, after.Updated);
        Assert.StartsWith("https://new.example.ru/", db.GetFwQrCode(id)!.Payload);
    }

    [Fact]
    public void Sweep_TranslitOverrideChanged_RebuildsLink()
    {
        var (db, file) = NewDb();
        using var _f = file; using var _db = db;
        var id = SeedVersion(db, @"Z:\ПО\НГР\КНС\SMH5\1.0\Инструкция\и.pdf");

        FirmwareQrSweep.Run(db, Opts());
        // Справочник написаний — единственный из четырёх источников, который не строка, а структура;
        // именно ради него у TranslitMap появилась Signature.
        var translit = TranslitMap.FromPairs(new[] { new KeyValuePair<string, string>("Инструкция", "manual") });
        var after = FirmwareQrSweep.Run(db, Opts(translit: translit));

        Assert.Equal(1, after.Updated);
        Assert.Contains("/manual/", db.GetFwQrCode(id)!.Payload);
    }

    [Fact]
    public void Sweep_VersionArchived_DropsItsCode()
    {
        var (db, file) = NewDb();
        using var _f = file; using var _db = db;
        var id = SeedVersion(db, @"Z:\ПО\НГР\КНС\SMH5\1.0\Инструкция\и.pdf");
        FirmwareQrSweep.Run(db, Opts());

        db.ArchiveFwVersion(id);
        var after = FirmwareQrSweep.Run(db, Opts());

        Assert.Equal(1, after.Removed);
        Assert.Null(db.GetFwQrCode(id));
    }

    [Fact]
    public void Sweep_DoesNotOverwriteLinkBuiltFromResolvedDocument()
    {
        var (db, file) = NewDb();
        using var _f = file; using var _db = db;

        // У версии записана ПАПКА инструкции, а окно наклейки, увидев живой диск, нашло в ней
        // конкретный файл и записало ссылку на него. Фоновый проход про этот файл ничего не знает —
        // и не должен затирать точную ссылку своей приблизительной (см. FirmwareQrCodes.Inputs).
        var folder = @"Z:\ПО\НГР\КНС\SMH5\1.0\Инструкция";
        var id = SeedVersion(db, folder);
        FirmwareQrSweep.Ensure(db, id, syncId: "", sourcePath: folder + @"\инструкция_2.1.pdf",
            instructionPath: folder, options: Opts());
        var precise = db.GetFwQrCode(id)!.Payload;

        FirmwareQrSweep.Run(db, Opts());

        Assert.Equal(precise, db.GetFwQrCode(id)!.Payload);
        Assert.EndsWith("_2.1.pdf", precise);
    }

    [Fact]
    public void Sweep_InstructionRelinked_RebuildsEvenPreciseLink()
    {
        var (db, file) = NewDb();
        using var _f = file; using var _db = db;

        var folder = @"Z:\ПО\НГР\КНС\SMH5\1.0\Инструкция";
        var id = SeedVersion(db, folder);
        FirmwareQrSweep.Ensure(db, id, syncId: "", sourcePath: folder + @"\старая.pdf",
            instructionPath: folder, options: Opts());

        // Документ перепривязали к версии — уступка «не затирать точную ссылку» не должна
        // превратиться в вечную жизнь ссылки на снятый документ.
        db.UpdateFwVersionAttachments(id, instructionsPath: @"Z:\ПО\НГР\КНС\SMH5\1.0\Инструкция2");
        var before = db.GetFwQrCode(id)!.Payload;
        var after = FirmwareQrSweep.Run(db, Opts());

        Assert.Equal(1, after.Updated);
        Assert.NotEqual(before, db.GetFwQrCode(id)!.Payload);
        Assert.EndsWith("2", db.GetFwQrCode(id)!.Payload);
    }

    [Fact]
    public void Build_NoWebAddress_FallsBackToPath()
    {
        var payload = FirmwareQrCodes.Build(
            FirmwareQrCodes.Inputs.FromRecord(@"Z:\ПО\НГР\и.pdf", baseUrl: "", diskRoot: Root, translit: ""),
            TranslitMap.Empty);

        Assert.Equal(FirmwareQrCodes.PayloadKind.Path, payload.Kind);
        Assert.Equal(@"Z:\ПО\НГР\и.pdf", payload.Content);
    }

    [Fact]
    public void Build_WordSource_LinksToPublishedPdf()
    {
        // Инструкция в Word уезжает на хостинг собранным PDF — наклейка, ведущая на .docx, вела бы
        // в пустоту (см. InstructionPublisher.AsPublishedName).
        var payload = FirmwareQrCodes.Build(
            FirmwareQrCodes.Inputs.FromRecord(@"Z:\ПО\НГР\и.docx", BaseUrl, Root, ""), TranslitMap.Empty);

        Assert.EndsWith(".pdf", payload.Content);
        Assert.DoesNotContain(".docx", payload.Content);
    }

    [Fact]
    public void Fingerprint_IgnoresSlashStyleAndCase()
    {
        // Пути с диска приходят в разном виде; расхождение отпечатка на пустом месте означало бы
        // перестройку всей таблицы на каждом проходе.
        var a = FirmwareQrCodes.Fingerprint(FirmwareQrCodes.Inputs.FromRecord(@"Z:\ПО\НГР\и.pdf", BaseUrl, Root, ""));
        var b = FirmwareQrCodes.Fingerprint(FirmwareQrCodes.Inputs.FromRecord(@"z:/ПО/НГР/и.pdf/", BaseUrl, Root, ""));
        Assert.Equal(a, b);
    }
}
