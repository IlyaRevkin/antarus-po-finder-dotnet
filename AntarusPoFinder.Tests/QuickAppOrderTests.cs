using System.Collections.Generic;
using System.IO;
using System.Linq;
using AntarusPoFinder.App;
using Xunit;

namespace AntarusPoFinder.Tests;

/// <summary>Перетаскивание ярлыков в «Быстрый доступ»: куда встаёт перенесённая строка.
///
/// Проверяется тестом, а не глазами, по одной причине: промах на единицу при переносе вниз выглядит
/// как «встало через одного» и замечается только тем, кто специально присматривается. Порядок в этом
/// списке — это порядок кнопок на экране, то есть то, ради чего человек и полез их двигать.</summary>
public class QuickAppOrderTests
{
    private static List<string> Items() => new() { "A", "B", "C", "D" };

    [Fact]
    public void Вниз_строка_встаёт_под_ту_на_которую_бросили()
    {
        var items = Items();
        QuickAppOrder.Move(items, 0, 2);          // A бросили на C
        Assert.Equal(new[] { "B", "C", "A", "D" }, items);
    }

    [Fact]
    public void Вверх_строка_занимает_место_цели()
    {
        var items = Items();
        QuickAppOrder.Move(items, 3, 1);          // D бросили на B
        Assert.Equal(new[] { "A", "D", "B", "C" }, items);
    }

    [Fact]
    public void В_самый_конец_и_в_самое_начало()
    {
        var items = Items();
        QuickAppOrder.Move(items, 0, 3);
        Assert.Equal(new[] { "B", "C", "D", "A" }, items);
        QuickAppOrder.Move(items, 3, 0);
        Assert.Equal(new[] { "A", "B", "C", "D" }, items);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(2, 2)]
    [InlineData(-1, 2)]
    [InlineData(9, 1)]
    public void Бессмысленный_перенос_ничего_не_портит(int from, int to)
    {
        var items = Items();
        QuickAppOrder.Move(items, from, to);
        Assert.Equal(new[] { "A", "B", "C", "D" }, items);
    }

    [Fact]
    public void Индекс_цели_за_пределами_списка_прижимается_к_концу()
    {
        var items = Items();
        QuickAppOrder.Move(items, 0, 99);
        Assert.Equal(new[] { "B", "C", "D", "A" }, items);
    }

    [Fact]
    public void Принимаем_только_то_что_существует_на_диске()
    {
        var file = Path.Combine(Path.GetTempPath(), "quick-" + System.Guid.NewGuid().ToString("N") + ".exe");
        File.WriteAllText(file, "");
        try
        {
            var ok = QuickAppOrder.Acceptable(new[] { file, "", "   ", @"C:\нет\такого\файла.exe" }).ToList();
            Assert.Equal(new[] { file }, ok);
            // Папку принимаем наравне с программой: наладчики кладут в быстрый доступ рабочие каталоги.
            Assert.Contains(Path.GetTempPath(), QuickAppOrder.Acceptable(new[] { Path.GetTempPath() }));
        }
        finally { File.Delete(file); }
    }

    [Fact]
    public void Имя_по_умолчанию_берётся_из_пути()
    {
        Assert.Equal("CodesysV23", QuickAppOrder.NameFor(@"C:\Program Files\Codesys\CodesysV23.exe"));
        Assert.Equal("Проекты", QuickAppOrder.NameFor(@"Z:\Software\Проекты\"));
        Assert.Equal("", QuickAppOrder.NameFor("  "));
    }
}
