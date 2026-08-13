using System;
using System.IO;
using System.Linq;
using AntarusPoFinder.Core.Domain;
using AntarusPoFinder.Core.Services;
using AntarusPoFinder.Tests.TestHelpers;
using Xunit;

namespace AntarusPoFinder.Tests;

/// <summary>Раскладка файлов версии (docs/hierarchy-rework-plan.md, этапы 4–5). Главное требование
/// плана к обоим этапам — <b>режим совместимости</b>: релиз ставится всем ДО того, как кто-нибудь
/// запустит перестройку диска, и обе раскладки живут рядом сколько угодно долго. Поэтому почти
/// каждый тест здесь парный: «версия ещё не переехала» и «версия уже переехала».</summary>
public class VersionAndOpcLayoutTests
{
    private static string Touch(string folder, string name)
    {
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, name);
        File.WriteAllText(path, "x");
        return path;
    }

    // ── Этап 4: «Прошивка\» внутри версии ────────────────────────────────────

    /// <summary>Признак «версия перестроена» — существование её «Прошивка\», и ничто другое. Пустая
    /// папка версии и недоступный путь считаются старой раскладкой: не подтвердили — работаем
    /// по-прежнему, это всегда безопасное направление.</summary>
    [Fact]
    public void IsNewLayout_TrueOnlyWhenFirmwareFolderExists()
    {
        using var root = new TempRoot();
        var dir = Path.Combine(root.Path, "1.0.0004.0003");
        Touch(dir, "1.0.0004.0003.psl");

        Assert.False(VersionLayout.IsNewLayout(dir));
        Assert.False(VersionLayout.IsNewLayout(null));
        Assert.False(VersionLayout.IsNewLayout(""));

        Directory.CreateDirectory(VersionLayout.FirmwareFolder(dir));
        Assert.True(VersionLayout.IsNewLayout(dir));
    }

    /// <summary>Файлы прошивки ищутся в ОБЕИХ папках, а не в одной: прогон перестройки, прерванный
    /// обрывом шары, оставляет часть файлов наверху, и потерять их нельзя.</summary>
    [Fact]
    public void FirmwareFolders_SearchesBothPlacesAfterPartialMigration()
    {
        using var root = new TempRoot();
        var dir = Path.Combine(root.Path, "1.0.0004.0003");
        Touch(dir, "остался_наверху.bin");
        Touch(VersionLayout.FirmwareFolder(dir), "1.0.0004.0003.psl");

        var folders = VersionLayout.FirmwareFolders(dir);

        Assert.Equal(new[] { VersionLayout.FirmwareFolder(dir), dir }, folders);
        // Старая раскладка — только сама папка версии, лишнего обращения к несуществующей папке нет.
        var old = Path.Combine(root.Path, "1.0.0004.0004");
        Directory.CreateDirectory(old);
        Assert.Equal(new[] { old }, VersionLayout.FirmwareFolders(old));
    }

    [Fact]
    public void IsServiceFile_ChangelogAndShortcutsStayOutOfFirmwareFolder()
    {
        Assert.True(VersionLayout.IsServiceFile(Path.Combine("x", ChangelogFile.FileName)));
        Assert.True(VersionLayout.IsServiceFile(Path.Combine("x", "инструкция.pdf.lnk")));
        Assert.False(VersionLayout.IsServiceFile(Path.Combine("x", "1.0.0004.0003.psl")));
    }

    /// <summary>Документы читаются из своей папки версии, только если там ЕСТЬ файлы. Перестройка не
    /// копирует документы контроллера в каждую версию (это удвоило бы диск), поэтому у переехавшей
    /// версии своя «Инструкция» обычно пуста — и без проверки на файлы инструкция контроллера у неё
    /// бы «пропала».</summary>
    [Fact]
    public void SlotBestReadFolder_PrefersOwnFolderOnlyWhenItHasFiles()
    {
        using var root = new TempRoot();
        var ctrl = Path.Combine(root.Path, "SMH5");
        var dir = Path.Combine(ctrl, "1.0.0004.0003");
        var shared = Path.Combine(ctrl, HierarchyFolders.Instructions);
        Touch(shared, "общая.pdf");
        Directory.CreateDirectory(VersionLayout.SlotFolder(dir, HierarchyFolders.Instructions));

        // Своя папка есть, но пуста — читаем общую.
        Assert.Equal(shared, VersionLayout.SlotBestReadFolder(dir, ctrl, HierarchyFolders.Instructions));

        // Положили документ именно к этой версии — теперь она главнее общей.
        Touch(VersionLayout.SlotFolder(dir, HierarchyFolders.Instructions), "своя.pdf");
        Assert.Equal(VersionLayout.SlotFolder(dir, HierarchyFolders.Instructions),
            VersionLayout.SlotBestReadFolder(dir, ctrl, HierarchyFolders.Instructions));
    }

    /// <summary>Ярлык документом не считается и здесь: папка с одним лишь .lnk (третий диск оставляет
    /// на первом ровно это) не должна перебивать общую папку контроллера с настоящим файлом.</summary>
    [Fact]
    public void SlotBestReadFolder_ShortcutIsNotADocument()
    {
        using var root = new TempRoot();
        var ctrl = Path.Combine(root.Path, "SMH5");
        var dir = Path.Combine(ctrl, "1.0.0004.0003");
        Touch(Path.Combine(ctrl, HierarchyFolders.Instructions), "общая.pdf");
        Touch(VersionLayout.SlotFolder(dir, HierarchyFolders.Instructions), "уехала.pdf.lnk");

        Assert.Equal(Path.Combine(ctrl, HierarchyFolders.Instructions),
            VersionLayout.SlotBestReadFolder(dir, ctrl, HierarchyFolders.Instructions));
    }

    /// <summary>Служебный мусор не делает папку «непустой». Это второе лицо жалобы «сделал ссылку на
    /// Thumbs.db вместо заглушки»: там мусор притворялся ДОКУМЕНТОМ, здесь он притворяется поводом
    /// выбрать ПАПКУ. Проводник заводит Thumbs.db в любой папке, куда заглянули за эскизами, — и
    /// пустая «Инструкция» версии от одного такого файла перебивала общую папку контроллера с
    /// настоящим документом, после чего программа честно сообщала «инструкции нет».</summary>
    [Fact]
    public void SlotBestReadFolder_JunkDoesNotMakeAFolderLookOccupied()
    {
        using var root = new TempRoot();
        var ctrl = Path.Combine(root.Path, "SMH5");
        var dir = Path.Combine(ctrl, "1.0.0004.0003");
        var shared = Path.Combine(ctrl, HierarchyFolders.Instructions);
        Touch(shared, "общая.pdf");
        Touch(VersionLayout.SlotFolder(dir, HierarchyFolders.Instructions), "Thumbs.db");

        Assert.Equal(shared, VersionLayout.SlotBestReadFolder(dir, ctrl, HierarchyFolders.Instructions));

        // А настоящий документ рядом с мусором папку по-прежнему выигрывает.
        Touch(VersionLayout.SlotFolder(dir, HierarchyFolders.Instructions), "своя.pdf");
        Assert.Equal(VersionLayout.SlotFolder(dir, HierarchyFolders.Instructions),
            VersionLayout.SlotBestReadFolder(dir, ctrl, HierarchyFolders.Instructions));
    }

    /// <summary>Писать внутрь версии можно только после её перестройки: положив документ в ещё не
    /// переехавшую версию, мы спрятали бы его от всех коллег со старым клиентом.</summary>
    [Fact]
    public void SlotWriteFolder_GoesInsideVersionOnlyAfterMigration()
    {
        using var root = new TempRoot();
        var ctrl = Path.Combine(root.Path, "SMH5");
        var dir = Path.Combine(ctrl, "1.0.0004.0003");
        Directory.CreateDirectory(dir);

        Assert.Equal(Path.Combine(ctrl, HierarchyFolders.Instructions),
            VersionLayout.SlotWriteFolder(dir, ctrl, HierarchyFolders.Instructions));
        Assert.Equal(dir, VersionLayout.FirmwareWriteFolder(dir));

        Directory.CreateDirectory(VersionLayout.FirmwareFolder(dir));
        Assert.Equal(VersionLayout.SlotFolder(dir, HierarchyFolders.Instructions),
            VersionLayout.SlotWriteFolder(dir, ctrl, HierarchyFolders.Instructions));
        Assert.Equal(VersionLayout.FirmwareFolder(dir), VersionLayout.FirmwareWriteFolder(dir));
    }

    /// <summary>Папка контроллера по папке версии: у обычной — родитель, у ОПЦ внутри контроллера —
    /// дед, у ОПЦ прежней раскладки контроллера над ней нет вовсе.</summary>
    [Fact]
    public void ControllerFolderOf_HandlesBothOpcLayouts()
    {
        using var root = new TempRoot();
        var subtype = Path.Combine(root.Path, "ПО", "ПЖ", "2.0");
        var ctrl = Path.Combine(subtype, "SMH5");
        Directory.CreateDirectory(Path.Combine(ctrl, HierarchyFolders.Instructions));

        var ordinary = Path.Combine(ctrl, "1.0.0004.0003");
        Directory.CreateDirectory(ordinary);
        Assert.Equal(ctrl, VersionLayout.ControllerFolderOf(ordinary));

        var newOpc = Path.Combine(OpcLayout.ControllerOpcFolder(ctrl), "01312");
        Directory.CreateDirectory(newOpc);
        Assert.Equal(ctrl, VersionLayout.ControllerFolderOf(newOpc));

        var legacyOpc = Path.Combine(OpcLayout.SubtypeOpcFolder(subtype), "3.0.005.0777");
        Directory.CreateDirectory(legacyOpc);
        Assert.Null(VersionLayout.ControllerFolderOf(legacyOpc));
    }

    // ── Этап 5: имя папки ОПЦ ────────────────────────────────────────────────

    [Theory]
    [InlineData("01312", "00042", "01312_SN00042")]
    [InlineData("01312", "", "01312")]
    [InlineData("", "00042", "SN00042")]
    [InlineData("", "", "3.0.005.0777")]   // ни заявки, ни SN — остаётся строка версии
    public void OpcFolderName_ReadsLikeTheFilename(string request, string sn, string expected)
    {
        Assert.Equal(expected, OpcLayout.FolderName(request, sn, "3.0.005.0777"));
    }

    /// <summary>Разбор имени папки обратим — иначе досмотр диска не восстановил бы заявку и SN у
    /// папки, заведённой на машине коллеги (в CHANGELOG.md их нет).</summary>
    [Fact]
    public void OpcParseFolderName_IsInverseOfFolderName()
    {
        Assert.Equal(("01312", "00042"), OpcLayout.ParseFolderName("01312_SN00042"));
        Assert.Equal(("01312", ""), OpcLayout.ParseFolderName("01312"));
        Assert.Equal(("", "00042"), OpcLayout.ParseFolderName("SN00042"));
        // Строка версии — это не заявка: иначе «3.0.005.0777» приехало бы номером заявки «3».
        Assert.Equal(("", ""), OpcLayout.ParseFolderName("3.0.005.0777"));
        Assert.Equal(("", ""), OpcLayout.ParseFolderName(null));
    }

    /// <summary>Номер версии переехавшей ОПЦ-папки берётся из CHANGELOG.md — и это единственный
    /// источник, поэтому мигратор обязан дописать журнал ДО переименования.</summary>
    [Fact]
    public void OpcResolveVersion_FallsBackFromFolderNameToChangelogToFilename()
    {
        using var root = new TempRoot();

        // 1. Прежняя раскладка: номер прямо в имени папки.
        var byName = Path.Combine(root.Path, "3.0.005.0777");
        Directory.CreateDirectory(byName);
        Assert.Equal("3.0.005.0777", OpcLayout.ResolveVersion(byName)?.Raw);

        // 2. Переехавшая папка с журналом.
        var byChangelog = Path.Combine(root.Path, "01312_SN00042");
        Directory.CreateDirectory(byChangelog);
        ChangelogFile.Write(byChangelog, FwVersionNumber.Parse("3.0.005.0778")!, new[] { "УПП" }, "правки", Array.Empty<string>());
        Assert.Equal("3.0.005.0778", OpcLayout.ResolveVersion(byChangelog)?.Raw);

        // 3. Журнала нет — остаётся имя файла прошивки, в т.ч. внутри «Прошивка\» (этап 4).
        var byFile = Path.Combine(root.Path, "01313");
        Touch(VersionLayout.FirmwareFolder(byFile), "3.0.005.0779_(01313)_SN00042.psl");
        Assert.Equal("3.0.005.0779", OpcLayout.ResolveVersion(byFile)?.Raw);

        // 4. Ни одного источника — null, а не выдуманная версия.
        var nothing = Path.Combine(root.Path, "01314");
        Touch(nothing, "какой-то_файл.bin");
        Assert.Null(OpcLayout.ResolveVersion(nothing));
    }

    /// <summary>Поиск переехавшей папки — основа локальной починки disk_path на машинах, которые
    /// перестройку не запускали (этап 5 — единственный переезд, меняющий путь).</summary>
    [Fact]
    public void FindMigratedFolder_MatchesByVersionFromChangelog()
    {
        using var root = new TempRoot();
        var ctrl = Path.Combine(root.Path, "SMH5");
        var moved = Path.Combine(OpcLayout.ControllerOpcFolder(ctrl), "01312_SN00042");
        Directory.CreateDirectory(moved);
        ChangelogFile.Write(moved, FwVersionNumber.Parse("3.0.005.0778")!, new[] { "УПП" }, "правки", Array.Empty<string>());

        Assert.Equal(moved, OpcLayout.FindMigratedFolder(ctrl, "3.0.005.0778"));
        Assert.Null(OpcLayout.FindMigratedFolder(ctrl, "3.0.005.0999"));
        // Папки «ОПЦ» у контроллера нет вовсе — не падаем.
        Assert.Null(OpcLayout.FindMigratedFolder(Path.Combine(root.Path, "SMH4"), "3.0.005.0778"));
    }
}
