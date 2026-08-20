using System.Linq;
using AntarusPoFinder.Core.Data;
using AntarusPoFinder.Core.Domain;
using AntarusPoFinder.Tests.TestHelpers;
using Xunit;

namespace AntarusPoFinder.Tests;

/// <summary>Тикет: «Создаются дубликаты файлов параметров. Пример: x2 XL Chint».
///
/// Причина оказалась не в самой заливке, а в ключе, по которому ищется уже существующая запись:
/// param_files.filename и .manufacturer объявлены без COLLATE NOCASE, то есть сравнивались
/// двоично. Файловая система Windows регистр не различает — оператор перезаливает тот же файл,
/// назвав его «Chint XL.par» вместо «chint xl.par», на диске это ОДИН файл, а в базе поиск
/// промахивается и заводит вторую живую строку. В списке появляется дубль.
///
/// Свёрнуть регистр средствами SQLite нельзя: NOCASE берёт только ASCII, а имена бывают
/// кириллическими. Поэтому сравнение переехало в .NET — см. Database.FileKey.</summary>
public class ParamFileCaseDuplicateTests
{
    private static (int SubtypeId, string Folder) Target(Database db)
    {
        var group = db.GetAllEquipmentGroups().First();
        var subtype = db.GetSubtypesForGroup(group.Id!.Value).First();
        return (subtype.Id!.Value, @"Z:\Software\Параметры\НГР\КНС\Chint");
    }

    private static int Add(Database db, int subtypeId, string manufacturer, string filename, string folder)
        => db.AddParamFile(new ParamFile
        {
            SubtypeId = subtypeId,
            Manufacturer = manufacturer,
            Filename = filename,
            DiskPath = folder,
            UploadDate = "2026-08-12",
        });

    [Fact]
    public void FindLiveParamFile_IgnoresCaseOfNameAndManufacturer()
    {
        using var dbFile = new TempDb();
        using var db = new Database(dbFile.Path);
        var (subtypeId, folder) = Target(db);

        var id = Add(db, subtypeId, "Chint", "x2 XL Chint.par", folder);

        // Ровно тот сценарий из тикета: тот же файл, другое написание.
        var found = db.FindLiveParamFile(subtypeId, "CHINT", "X2 XL CHINT.PAR");
        Assert.NotNull(found);
        Assert.Equal(id, found!.Id);

        // И наоборот, и с лишними пробелами по краям.
        Assert.NotNull(db.FindLiveParamFile(subtypeId, "chint", "  x2 xl chint.par "));
    }

    [Fact]
    public void FindLiveParamFile_FoldsCyrillicCaseToo()
    {
        using var dbFile = new TempDb();
        using var db = new Database(dbFile.Path);
        var (subtypeId, folder) = Target(db);

        var id = Add(db, subtypeId, "Веспер", "Параметры X2.par", folder);

        // Именно здесь COLLATE NOCASE был бы бесполезен — кириллицу он не сворачивает.
        var found = db.FindLiveParamFile(subtypeId, "ВЕСПЕР", "ПАРАМЕТРЫ X2.PAR");
        Assert.NotNull(found);
        Assert.Equal(id, found!.Id);
    }

    [Fact]
    public void FindLiveParamFile_StillSeparatesDifferentFiles()
    {
        using var dbFile = new TempDb();
        using var db = new Database(dbFile.Path);
        var (subtypeId, folder) = Target(db);

        Add(db, subtypeId, "Chint", "x2 XL Chint.par", folder);

        // Свёртка регистра не должна склеивать РАЗНЫЕ файлы и разных производителей.
        Assert.Null(db.FindLiveParamFile(subtypeId, "Chint", "x2 XS Chint.par"));
        Assert.Null(db.FindLiveParamFile(subtypeId, "Веспер", "x2 XL Chint.par"));
    }

    [Fact]
    public void GetParamFilesSharingFile_IgnoresCaseAndTrailingSlash()
    {
        using var dbFile = new TempDb();
        using var db = new Database(dbFile.Path);
        var group = db.GetAllEquipmentGroups().First();
        var subtypes = db.GetSubtypesForGroup(group.Id!.Value).Take(2).ToList();
        Assert.Equal(2, subtypes.Count);

        const string folder = @"Z:\Software\Параметры\НГР\КНС\Chint";
        Add(db, subtypes[0].Id!.Value, "Chint", "x2 XL Chint.par", folder);
        Add(db, subtypes[1].Id!.Value, "Chint", "x2 XL Chint.par", folder);

        // Один физический файл, две записи — по одной на подтип. Написание пути роли не играет.
        var shared = db.GetParamFilesSharingFile(folder.ToUpperInvariant() + @"\", "X2 XL CHINT.PAR");
        Assert.Equal(2, shared.Count);
    }

    [Fact]
    public void FileKey_CollapsesSpacesAndCase()
    {
        Assert.Equal(Database.FileKey("x2   XL  Chint.par"), Database.FileKey("X2 xl chint.par"));
        Assert.Equal(Database.FileKey(@"Z:\Папка\"), Database.FileKey(@"z:\папка"));
        Assert.Equal("", Database.FileKey("   "));
        Assert.Equal("", Database.FileKey(null));
        Assert.NotEqual(Database.FileKey("насос.par"), Database.FileKey("насосы.par"));
    }
}
