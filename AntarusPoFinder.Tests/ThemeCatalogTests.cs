using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using AntarusPoFinder.App;
using AntarusPoFinder.Core.Domain;
using Xunit;

namespace AntarusPoFinder.Tests;

/// <summary>ТЕМЫ ОФОРМЛЕНИЯ: НАБОР КЛЮЧЕЙ ОБЯЗАН СОВПАДАТЬ.
///
/// Просьба Ильи (бета 2.0): «добавь темы типа матиреал дизайн, ликвид Гласс и тп».
///
/// Почему это тест, а не «посмотрим глазами». Тема — это словарь ресурсов, который подменяется
/// целиком. Стили ссылаются на цвета ПО ИМЕНИ, и недостающий ключ даёт не «цвет по умолчанию», а
/// исключение при разборе разметки — то есть окно, которое не открывается вовсе. Увидеть это можно
/// только переключившись на тему, а переключаются на неё редко: ровно тот случай, когда поломка
/// живёт неделями и вылезает у чужого человека.</summary>
public class ThemeCatalogTests
{
    private static string ThemesDir => Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory, "..", "..", "..", "..", "AntarusPoFinder.App", "Themes"));

    private static HashSet<string> KeysOf(string dictionary)
    {
        var doc = XDocument.Load(Path.Combine(ThemesDir, dictionary + ".xaml"));
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        return doc.Descendants()
            // ФОРМА тоже считается ключом темы, не только цвет. Скругления и отступы
            // (CardCorner/NavCorner/ButtonCorner/ControlCorner/CardPadding/CardShadowOpacity) стили
            // читают через DynamicResource, и тема, где такой ключ забыли, НЕ падает: WPF молча
            // подставляет ноль, и экран выходит с прямыми углами вместо скруглённых. Заметно это
            // только глазами и только на той теме, которую забыли открыть.
            .Where(e => e.Name.LocalName is "Color" or "SolidColorBrush"
                                         or "CornerRadius" or "Thickness" or "Double")
            .Select(e => e.Attribute(x + "Key")?.Value)
            .Where(k => !string.IsNullOrEmpty(k))
            .Select(k => k!)
            .ToHashSet(StringComparer.Ordinal);
    }

    [Fact]
    public void Каждая_тема_объявляет_тот_же_набор_ключей_что_и_светлая()
    {
        var expected = KeysOf("Light");
        Assert.NotEmpty(expected);

        foreach (var theme in ThemeCatalog.All.Where(t => t.Dictionary != "Light"))
        {
            var actual = KeysOf(theme.Dictionary);
            var missing = expected.Except(actual).OrderBy(k => k).ToList();
            Assert.True(missing.Count == 0,
                $"в теме «{theme.Label}» нет ключей: {string.Join(", ", missing)} — "
                + "окно, где они используются, просто не откроется");
        }
    }

    /// <summary>ВСПЛЫВАЮЩИЕ ОКНА НЕПРОЗРАЧНЫ — ВО ВСЕХ ТЕМАХ.
    ///
    /// «Стекло» делает карточки полупрозрачными намеренно: так видна глубина. Но выпадающий список,
    /// подсказка и всплывающее меню висят НАД окном, и сквозь них виден рабочий стол — читать такой
    /// список невозможно. Предупреждение Ильи 21.10.2026 дословно: «в стекле проверь чтобы
    /// выпадающие списки не смотря на стекло не сливались с фоном за ними».
    ///
    /// Поэтому цвет этих поверхностей обязан быть полностью непрозрачным: либо шесть цифр без
    /// альфы, либо восемь, начинающихся с FF. Проверяется по словарю, а не глазами: прозрачность
    /// здесь ломается одной цифрой и замечается только на чужой машине с открытым списком.</summary>
    [Fact]
    public void Поверхность_всплывающих_окон_непрозрачна_в_каждой_теме()
    {
        foreach (var theme in ThemeCatalog.All)
        {
            var doc = XDocument.Load(Path.Combine(ThemesDir, theme.Dictionary + ".xaml"));
            XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";

            var colour = doc.Descendants()
                .FirstOrDefault(e => e.Name.LocalName == "Color" && e.Attribute(x + "Key")?.Value == "PopupBgColor")
                ?.Value.Trim();

            Assert.True(colour is not null, $"в теме «{theme.Label}» нет PopupBgColor");

            var hex = colour!.TrimStart('#');
            Assert.True(hex.Length == 6 || hex.StartsWith("FF", StringComparison.OrdinalIgnoreCase),
                $"в теме «{theme.Label}» всплывающие окна полупрозрачны ({colour}) — сквозь список будет виден рабочий стол");
        }
    }

    /// <summary>Словарь должен существовать на диске и собираться в ресурсы: опечатка в имени файла
    /// каталога даёт исключение при переключении темы, а не «тема не нашлась».</summary>
    [Fact]
    public void Файл_каждой_темы_существует()
    {
        foreach (var theme in ThemeCatalog.All)
            Assert.True(File.Exists(Path.Combine(ThemesDir, theme.Dictionary + ".xaml")),
                $"нет файла темы «{theme.Label}»: {theme.Dictionary}.xaml");
    }

    /// <summary>Имя темы хранится в настройках у каждого человека — менять его нельзя, иначе у всех
    /// молча слетит выбор. Тест фиксирует существующие имена.</summary>
    [Fact]
    public void Имена_сохранённых_тем_не_меняются()
    {
        Assert.Contains(ThemeCatalog.All, t => t.Id == "light" && !t.IsDark);
        Assert.Contains(ThemeCatalog.All, t => t.Id == "dark" && t.IsDark);
    }

    /// <summary>Неизвестное имя — светлая тема, как и раньше («всё, что не dark, считалось светлым»).
    /// Настройка могла приехать с машины, где стоит бета с темой, которой здесь ещё нет.</summary>
    [Fact]
    public void Неизвестная_тема_разворачивается_в_светлую()
    {
        Assert.Equal("light", ThemeCatalog.Resolve("нет-такой-темы").Id);
        Assert.Equal("light", ThemeCatalog.Resolve(null).Id);
    }

    /// <summary>Все четыре темы предлагаются человеку, и одинаково в обеих сборках.
    ///
    /// До 2.2 «Material» и «Стекло» были закрыты признаком AppTheme.Beta, пока их обкатывали; с
    /// выпуском 2.2.0.0 закрытых тем не осталось ни одной. Проверяется и то, что отбор больше не
    /// зависит от конфигурации сборки: разъехавшийся список тем означал бы, что настройка,
    /// приехавшая с чужой машины через общий конфиг, разворачивается в разное.</summary>
    [Fact]
    public void Все_темы_предлагаются_и_одинаково_в_обеих_сборках()
    {
        var available = ThemeCatalog.Available.Select(t => t.Id).ToList();

        Assert.Equal(new[] { "light", "dark", "material", "glass" }, available);
        Assert.DoesNotContain(ThemeCatalog.All, t => t.Beta);
    }
}
