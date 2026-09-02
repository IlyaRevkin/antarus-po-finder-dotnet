using System;
using System.Linq;
using AntarusPoFinder.App;
using Xunit;

namespace AntarusPoFinder.Tests;

/// <summary>Цветовые схемы. Главное здесь — не «цвет красивый», а то, что на КАЖДОМ акценте читается
/// белый текст: подпись на цветной кнопке везде берётся из TextOnAccentBrush, он белый, и второй
/// логики выбора цвета текста нет. Стоит добавить в набор светлый цвет — и надписи на кнопках
/// поплывут разом по всему приложению, молча. Поэтому контраст проверяется числом, а не на глаз:
/// 4.5:1 — порог WCAG AA, с которого мелкий текст уверенно читается.</summary>
public class AccentPaletteTests
{
    private static double Luminance(string hex)
    {
        var h = hex.TrimStart('#');
        double Ch(int i)
        {
            var v = Convert.ToInt32(h.Substring(i, 2), 16) / 255.0;
            return v <= 0.03928 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4);
        }
        return 0.2126 * Ch(0) + 0.7152 * Ch(2) + 0.0722 * Ch(4);
    }

    private static double ContrastWithWhite(string hex) => 1.05 / (Luminance(hex) + 0.05);

    [Fact]
    public void EveryAccent_KeepsWhiteTextReadable()
    {
        foreach (var p in AccentPalette.All)
        {
            foreach (var (hex, where) in new[]
                     {
                         (p.LightAccent, "светлая тема"),
                         (p.DarkAccent, "тёмная тема"),
                     })
            {
                var contrast = ContrastWithWhite(hex);
                Assert.True(contrast >= 4.5,
                    $"{p.DisplayName} ({where}, {hex}): контраст белого {contrast:F2} < 4.5 — подпись на кнопке будет плыть");
            }
        }
    }

    /// <summary>Наведённый цвет обязан отличаться от основного, иначе кнопка перестаёт отзываться на
    /// мышь — визуально ничего не происходит, и это выглядит как «залипла».</summary>
    [Fact]
    public void HoverDiffersFromAccent()
    {
        foreach (var p in AccentPalette.All)
        {
            Assert.NotEqual(p.LightAccent, p.LightHover);
            Assert.NotEqual(p.DarkAccent, p.DarkHover);
        }
    }

    [Fact]
    public void Ids_AreUnique_AndDefaultExists()
    {
        var ids = AccentPalette.All.Select(p => p.Id).ToArray();
        Assert.Equal(ids.Length, ids.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Contains(ids, id => id == AccentPalette.DefaultId);
    }

    /// <summary>Незнакомый или пустой идентификатор — это НЕ повод падать: настройка приезжает из
    /// общего конфига и с чужой машины, где набор цветов может быть новее. Возвращаем цвет по
    /// умолчанию, чтобы программа открылась, а не встала на старте.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("несуществующий")]
    public void UnknownId_FallsBackToDefault(string? id)
        => Assert.Equal(AccentPalette.DefaultId, AccentPalette.Find(id).Id);

    [Fact]
    public void Find_IsCaseInsensitive()
        => Assert.Equal("green", AccentPalette.Find("GREEN").Id);
}
