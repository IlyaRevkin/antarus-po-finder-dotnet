using System;
using System.IO;
using System.Linq;
using AntarusPoFinder.Core.Services;
using Xunit;

namespace AntarusPoFinder.Tests;

/// <summary>Перезаливка инструкции не стирает прежнюю редакцию.
///
/// До этого копирование шло с overwrite, и прошлый документ исчезал молча. Инструкция — то, что
/// правят: «дописали пункт про насос» и «переписали половину» выглядят одинаково, пока не с чем
/// сравнить, а сравнивать было уже не с чем.</summary>
public class InstructionArchiveTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "instr-" + Guid.NewGuid().ToString("N"));

    private string Folder(string name)
    {
        var path = Path.Combine(_root, name);
        Directory.CreateDirectory(path);
        return path;
    }

    private string SourceFile(string name, string text)
    {
        var path = Path.Combine(Folder("src"), name);
        File.WriteAllText(path, text);
        return path;
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { /* временная папка */ }
    }

    [Fact]
    public void Прежняя_редакция_уезжает_в_архив_со_слепком_времени()
    {
        var target = Folder("Инструкция");
        var warnings = new System.Collections.Generic.List<string>();

        var first = SourceFile("инструкция старая.pdf", "редакция один");
        InstructionStorage.Copy(first, target, _root, warnings, "2.1.0004.0002");

        var second = SourceFile("инструкция новая.pdf", "редакция два — переписана целиком");
        var placed = InstructionStorage.Copy(second, target, _root, warnings, "2.1.0004.0002");

        Assert.Equal("редакция два — переписана целиком", File.ReadAllText(placed.StoredPath));

        var archive = Path.Combine(target, InstructionStorage.PreviousFolderName);
        Assert.True(Directory.Exists(archive), "папка прежних редакций должна появиться");
        var kept = Directory.GetFiles(archive).Single();
        Assert.Equal("редакция один", File.ReadAllText(kept));
        // Имя несёт отметку времени, иначе вторая перезаливка затёрла бы первую в самом архиве.
        Assert.Matches(@"_\d{8}-\d{4}", Path.GetFileNameWithoutExtension(kept));
    }

    [Fact]
    public void Тот_же_файл_повторно_не_плодит_копий()
    {
        var target = Folder("Инструкция");
        var warnings = new System.Collections.Generic.List<string>();

        var first = SourceFile("инструкция.pdf", "один и тот же документ");
        InstructionStorage.Copy(first, target, _root, warnings, "2.1.0004.0002");

        // Ровно тот же документ, просто положенный в другую папку: дата другая, содержимое то же.
        var again = SourceFile("инструкция (копия).pdf", "один и тот же документ");
        InstructionStorage.Copy(again, target, _root, warnings, "2.1.0004.0002");

        var archive = Path.Combine(target, InstructionStorage.PreviousFolderName);
        Assert.False(Directory.Exists(archive) && Directory.GetFiles(archive).Length > 0,
            "повторная загрузка того же документа не должна оставлять архивных копий");
    }
}
