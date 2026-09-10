using System.IO;
using System.Linq;
using AntarusPoFinder.Core.Services;
using AntarusPoFinder.Tests.TestHelpers;
using Xunit;

namespace AntarusPoFinder.Tests;

/// <summary>Служебные папки в папке КОНТРОЛЛЕРА — след старой раскладки.
///
/// Жалоба владельца (09.09.2026): «в папке контроллера лежат рудиментарные папки Инструкция, HMI
/// и т.п., хотя должны быть папки с версиями и папка ОПЦ». Эти четыре папки заводились каждому
/// контроллеру безусловно, независимо от того, нужны ли они.
///
/// Осторожность важнее полноты: заводить перестали, но убираем только ПУСТЫЕ. В непустых лежат
/// настоящие документы, на которые ссылаются версии в режиме совместимости и коллеги со старым
/// клиентом — снести их значило бы забрать у людей файлы.
///
/// ⚠️ «Инструкция» сюда НЕ входит и остаётся штатной: в ней живёт заглушка, на которую ведёт QR с
/// наклейки. Убрать её значило бы сломать инструкции у версий, чьи документы не разложены по
/// папкам версий, — что я и сделал с первого захода, поймав это полным прогоном.</summary>
public class LegacyControllerFoldersTests
{
    private static string CtrlDir(string root) =>
        Path.Combine(root, "ПО", "НГР", "КНС", "SMH4");

    [Fact]
    public void EmptyLegacyFolders_AreRemoved()
    {
        using var m = new TwoMachines();
        var root = m.Root.Path;
        var ctrl = CtrlDir(root);

        foreach (var name in new[] { "Карта ВВ", "Карта Modbus", "HMI" })
            Directory.CreateDirectory(Path.Combine(ctrl, name));

        m.HierA.EnsureStructure(root);

        foreach (var name in new[] { "Карта ВВ", "Карта Modbus", "HMI" })
            Assert.False(Directory.Exists(Path.Combine(ctrl, name)), $"пустая «{name}» должна была исчезнуть");
    }

    [Fact]
    public void LegacyFoldersWithDocuments_AreKept()
    {
        using var m = new TwoMachines();
        var root = m.Root.Path;
        var ctrl = CtrlDir(root);

        var withDoc = Path.Combine(ctrl, "Карта Modbus");
        Directory.CreateDirectory(withDoc);
        File.WriteAllText(Path.Combine(withDoc, "карта.pdf"), "документ");

        var withSubfolder = Path.Combine(ctrl, "HMI");
        Directory.CreateDirectory(Path.Combine(withSubfolder, "проект"));

        m.HierA.EnsureStructure(root);

        Assert.True(File.Exists(Path.Combine(withDoc, "карта.pdf")), "документ не должен пропасть");
        Assert.True(Directory.Exists(Path.Combine(withSubfolder, "проект")), "вложенная папка не должна пропасть");
    }

    /// <summary>Заводить их заново тоже перестали: иначе уборка и создание боролись бы друг с другом
    /// при каждой перестройке.</summary>
    [Fact]
    public void StructureRebuild_DoesNotRecreateThem()
    {
        using var m = new TwoMachines();
        var root = m.Root.Path;

        m.HierA.EnsureStructure(root);
        m.HierA.EnsureStructure(root);

        var ctrl = CtrlDir(root);
        foreach (var name in new[] { "Карта ВВ", "Карта Modbus", "HMI" })
            Assert.False(Directory.Exists(Path.Combine(ctrl, name)), $"«{name}» не должна заводиться заново");
    }

    /// <summary>Папки версий и «ОПЦ» — то, что в папке контроллера и должно лежать.</summary>
    [Fact]
    public void VersionsAndOpc_StillBelongThere()
    {
        using var m = new TwoMachines();
        var root = m.Root.Path;
        m.HierA.EnsureStructure(root);

        var ctrl = CtrlDir(root);
        Assert.True(Directory.Exists(ctrl), "папка контроллера должна существовать");
        Assert.True(Directory.Exists(Path.Combine(ctrl, "ОПЦ")), "«ОПЦ» должна остаться");
        Assert.True(Directory.Exists(Path.Combine(ctrl, "Инструкция")), "«Инструкция» остаётся штатной — в ней заглушка для QR");
    }
}
