using System.IO;
using AntarusPoFinder.Core.Services;
using AntarusPoFinder.Tests.TestHelpers;
using Xunit;

namespace AntarusPoFinder.Tests;

/// <summary>Проект панели формата «папка» (.fsprj у FStudio): выбран один файл, а забрать надо всю
/// папку. Пока этого не знали, проект уезжал на диск одним переименованным файлом и открывался
/// пустым — «модель HMI не соответствует текущему программному обеспечению» — и у нас, и у коллег,
/// хотя исходная папка у программиста открывалась нормально.</summary>
public class HmiProjectFormatTests
{
    private static string Touch(string folder, string name)
    {
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, name);
        File.WriteAllText(path, "x");
        return path;
    }

    [Fact]
    public void FolderProjectFile_OnlyForFormatsThatLiveInAFolder()
    {
        Assert.True(HmiProjectFormat.IsFolderProjectFile(@"C:\x\panel.fsprj"));
        Assert.True(HmiProjectFormat.IsFolderProjectFile(@"C:\x\panel.FSPRJ"));
        // Одиночные форматы панели копируются файлом, как копировались всегда.
        Assert.False(HmiProjectFormat.IsFolderProjectFile(@"C:\x\panel.dpj"));
        Assert.False(HmiProjectFormat.IsFolderProjectFile(null));
    }

    [Fact]
    public void ProjectFolderOf_FsprjInItsOwnFolder_ReturnsFolder()
    {
        using var root = new TempRoot();
        var project = Path.Combine(root.Path, "Проект панели");
        var entry = Touch(project, "panel.fsprj");
        Touch(Path.Combine(project, "Driver"), "lib.dll");

        Assert.Equal(project, HmiProjectFormat.ProjectFolderOf(entry));
    }

    [Fact]
    public void ProjectFolderOf_FsprjNextToPlcFirmware_ReturnsNull_DoesNotDragTheWholeVersionFolder()
    {
        using var root = new TempRoot();
        // Кто-то положил проект панели прямо в папку версии. Утащив её целиком в «HMI\», мы
        // продублировали бы туда и прошивку, и всё остальное содержимое версии.
        var version = Path.Combine(root.Path, "2.1.041");
        var entry = Touch(version, "panel.fsprj");
        Touch(version, "прошивка.lfs");

        Assert.Null(HmiProjectFormat.ProjectFolderOf(entry));
    }

    [Fact]
    public void SelectionWarning_OnlyForTheCaseThatSilentlyProducesAnEmptyProject()
    {
        using var root = new TempRoot();
        var version = Path.Combine(root.Path, "2.1.041");
        var doomed = Touch(version, "panel.fsprj");
        Touch(version, "прошивка.lfs");
        // Такой выбор скопируется одним файлом — и панель откроется пустой, поэтому предупреждаем.
        Assert.NotNull(HmiProjectFormat.SelectionWarning(doomed));

        // А эти три — нормальные: своя папка проекта, однофайловый формат, папка целиком.
        var own = Path.Combine(root.Path, "Проект панели");
        Assert.Null(HmiProjectFormat.SelectionWarning(Touch(own, "panel.fsprj")));
        Assert.Null(HmiProjectFormat.SelectionWarning(Touch(version, "panel.dpj")));
        Assert.Null(HmiProjectFormat.SelectionWarning(own));
        Assert.Null(HmiProjectFormat.SelectionWarning(null));
    }

    [Fact]
    public void ProjectFolderOf_SingleFileFormat_ReturnsNull()
    {
        using var root = new TempRoot();
        var entry = Touch(Path.Combine(root.Path, "Проект"), "panel.dpj");
        Assert.Null(HmiProjectFormat.ProjectFolderOf(entry));
        Assert.Null(HmiProjectFormat.ProjectFolderOf(Path.Combine(root.Path, "нет такого.fsprj")));
    }

    [Fact]
    public void LooksStripped_LoneFsprj_IsStripped()
    {
        using var root = new TempRoot();
        var stored = Touch(Path.Combine(root.Path, "HMI"), "2.1.041_hmi.fsprj");
        Assert.True(HmiProjectFormat.LooksStrippedOfCompanions(stored));
    }

    [Fact]
    public void LooksStripped_InSharedHmiFolder_NeighbourVersionsAreNotCompanions()
    {
        using var root = new TempRoot();
        // Общая папка «HMI» контроллера — ровно то место, куда старый код складывал обрубки. Рядом
        // лежат и такой же обрубок соседней версии, и версия, загруженная уже целиком папкой.
        var hmi = Path.Combine(root.Path, "HMI");
        var stored = Touch(hmi, "2.1.041_hmi.fsprj");
        Touch(hmi, "2.1.040_hmi.fsprj");
        Touch(Path.Combine(hmi, "2.1.039_hmi"), "panel.fsprj");

        Assert.True(HmiProjectFormat.LooksStrippedOfCompanions(stored));
    }

    [Fact]
    public void LooksStripped_ProjectWithItsCompanions_IsFine()
    {
        using var root = new TempRoot();
        var project = Path.Combine(root.Path, "2.1.041_hmi");
        var entry = Touch(project, "panel.fsprj");
        Touch(Path.Combine(project, "Driver"), "lib.dll");
        Assert.False(HmiProjectFormat.LooksStrippedOfCompanions(entry));

        // Окружением считается и просто файл рядом — не только подпапка.
        var flat = Path.Combine(root.Path, "2.1.042_hmi");
        var flatEntry = Touch(flat, "panel.fsprj");
        Touch(flat, "model.bin");
        Assert.False(HmiProjectFormat.LooksStrippedOfCompanions(flatEntry));
    }

    [Fact]
    public void LooksStripped_SingleFileFormat_NeverComplains()
    {
        using var root = new TempRoot();
        // .dpj без соседей — это нормальный, полностью рабочий проект.
        var stored = Touch(Path.Combine(root.Path, "HMI"), "2.1.041_hmi.dpj");
        Assert.False(HmiProjectFormat.LooksStrippedOfCompanions(stored));
    }

    [Fact]
    public void CopyHmiProject_FsprjFileSelected_CopiesWholeFolder_AndKeepsInnerNames()
    {
        using var root = new TempRoot();
        var project = Path.Combine(root.Path, "Проект панели");
        var entry = Touch(project, "panel.fsprj");
        Touch(Path.Combine(project, "Driver"), "lib.dll");
        var hmiRoot = Path.Combine(root.Path, "HMI");

        var stored = FirmwareAttachmentsService.CopyHmiProject(hmiRoot, "2.1.041", entry);

        Assert.Equal(Path.Combine(hmiRoot, "2.1.041_hmi"), stored);
        // Имя точки входа НЕ меняется: уникальность даёт имя папки, а переименование ломало проект.
        Assert.True(File.Exists(Path.Combine(stored, "panel.fsprj")));
        Assert.True(File.Exists(Path.Combine(stored, "Driver", "lib.dll")));
        Assert.False(HmiProjectFormat.LooksStrippedOfCompanions(Path.Combine(stored, "panel.fsprj")));
    }

    [Fact]
    public void CopyHmiProject_SingleFileFormat_StillCopiedAsOneRenamedFile()
    {
        using var root = new TempRoot();
        var entry = Touch(Path.Combine(root.Path, "Проект"), "panel.dpj");
        var hmiRoot = Path.Combine(root.Path, "HMI");

        var stored = FirmwareAttachmentsService.CopyHmiProject(hmiRoot, "2.1.041", entry);

        Assert.Equal(Path.Combine(hmiRoot, "2.1.041_hmi.dpj"), stored);
        Assert.True(File.Exists(stored));
    }

    [Fact]
    public void CopyHmiProject_AlreadyStoredProjectSelectedAgain_IsNoOp_NotFileBusy()
    {
        using var root = new TempRoot();
        var hmiRoot = Path.Combine(root.Path, "HMI");
        var stored = Path.Combine(hmiRoot, "2.1.041_hmi");
        var entry = Touch(stored, "panel.fsprj");
        Touch(Path.Combine(stored, "Driver"), "lib.dll");

        // Диалог модерации открывается прямо в папке, где проект уже лежит, — выбрать его повторно
        // проще простого. Копирование папки в саму себя падало «файл занят другим процессом».
        var again = FirmwareAttachmentsService.CopyHmiProject(hmiRoot, "2.1.041", entry, replaceExisting: true);

        Assert.Equal(stored, again);
        Assert.True(File.Exists(Path.Combine(stored, "panel.fsprj")));
        Assert.True(File.Exists(Path.Combine(stored, "Driver", "lib.dll")));
    }

    [Fact]
    public void CopyHmiProject_SameSingleFileSelectedAgain_IsNoOp()
    {
        using var root = new TempRoot();
        var hmiRoot = Path.Combine(root.Path, "HMI");
        var stored = Touch(hmiRoot, "2.1.041_hmi.dpj");

        var again = FirmwareAttachmentsService.CopyHmiProject(hmiRoot, "2.1.041", stored, replaceExisting: true);

        Assert.Equal(stored, again);
        Assert.True(File.Exists(stored));
    }

    [Fact]
    public void CopyHmiProject_SourceInsideDestination_Refuses_InsteadOfWipingIt()
    {
        using var root = new TempRoot();
        var hmiRoot = Path.Combine(root.Path, "HMI");
        // Проект лежит ВНУТРИ папки назначения: снос назначения перед копированием унёс бы источник.
        var entry = Touch(Path.Combine(hmiRoot, "2.1.041_hmi", "Вложенный проект"), "panel.fsprj");
        Touch(Path.Combine(hmiRoot, "2.1.041_hmi", "Вложенный проект"), "model.bin");

        Assert.Throws<IOException>(() =>
            FirmwareAttachmentsService.CopyHmiProject(hmiRoot, "2.1.041", entry, replaceExisting: true));
        Assert.True(File.Exists(entry));
    }

    [Fact]
    public void HmiExtensions_KnowFsprj_SoAutoDetectAndButtonCaptionSeeIt()
    {
        using var root = new TempRoot();
        var version = Path.Combine(root.Path, "2.1.041");
        var panel = Touch(version, "panel.fsprj");

        // До этого .fsprj не был известен ни автодетекту, ни подписи кнопки — хотя выбрать его в
        // модерации фильтр диалога предлагал.
        Assert.Equal(panel, HmiOpenResolver.Resolve(new HmiOpenSources { FilteredFolders = new[] { version } }));
        Assert.Equal(".fsprj", HmiOpenResolver.ResolveExtension(new HmiOpenSources { FilteredFolders = new[] { version } }));
    }
}
