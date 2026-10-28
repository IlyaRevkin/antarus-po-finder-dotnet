using AntarusPoFinder.App;
using System.Windows.Media;
using Xunit;

namespace AntarusPoFinder.Tests;

/// <summary>Ползунок прозрачности двигает стекло, но не ломает его.
///
/// Настройка оформления — то место, где легко согласиться на «ну подвигаем числа и посмотрим».
/// Смотреть приходится на живом экране, а проверять можно здесь: соотношение поверхностей
/// (сайдбар плотнее карточки, карточка плотнее поля) — это и есть глубина, ради которой тема
/// сделана, и оно обязано пережить любое положение ручки.</summary>
public class GlassSurfaceTests
{
    private static Color Card => Color.FromArgb(0xA6, 0x1A, 0x23, 0x34);
    private static Color Sidebar => Color.FromArgb(0xCC, 0x12, 0x1A, 0x24);
    private static Color Input => Color.FromArgb(0x99, 0x10, 0x17, 0x22);

    [Fact]
    public void На_значении_по_умолчанию_тема_не_меняется()
    {
        // Человек, не трогавший настройку, обязан видеть ровно то, что нарисовано в Glass.xaml.
        Assert.Equal(Card, GlassSurface.Scale(Card, GlassSurface.Default));
        Assert.Equal(Sidebar, GlassSurface.Scale(Sidebar, GlassSurface.Default));
    }

    [Theory]
    [InlineData(GlassSurface.Min)]
    [InlineData(50)]
    [InlineData(GlassSurface.Default)]
    [InlineData(80)]
    [InlineData(GlassSurface.Max)]
    public void Порядок_плотности_поверхностей_сохраняется(int percent)
    {
        var sidebar = GlassSurface.Scale(Sidebar, percent).A;
        var card = GlassSurface.Scale(Card, percent).A;
        var input = GlassSurface.Scale(Input, percent).A;
        Assert.True(sidebar >= card, $"сайдбар должен быть не прозрачнее карточки ({sidebar} < {card})");
        Assert.True(card >= input, $"карточка должна быть не прозрачнее поля ({card} < {input})");
    }

    [Fact]
    public void Значение_вне_границ_прижимается_а_не_выворачивается()
    {
        // Значение может приехать из конфига, который правили руками: 0 там означал бы невидимый
        // интерфейс, 1000 — переполнение байта альфы и внезапно ПРОЗРАЧНУЮ поверхность.
        Assert.Equal(GlassSurface.Scale(Card, GlassSurface.Min), GlassSurface.Scale(Card, -40));
        Assert.Equal(GlassSurface.Scale(Card, GlassSurface.Max), GlassSurface.Scale(Card, 1000));
        Assert.True(GlassSurface.Scale(Card, 1000).A > GlassSurface.Scale(Card, GlassSurface.Min).A);
    }

    [Fact]
    public void Оттенок_темы_остаётся_её_собственным()
    {
        var scaled = GlassSurface.Scale(Card, GlassSurface.Min);
        Assert.Equal(Card.R, scaled.R);
        Assert.Equal(Card.G, scaled.G);
        Assert.Equal(Card.B, scaled.B);
    }

    [Fact]
    public void Блик_светлее_поверхности_но_не_прозрачнее_её()
    {
        var brush = Assert.IsType<LinearGradientBrush>(GlassSurface.Surface(Card, GlassSurface.Default, specular: true));
        var top = brush.GradientStops[0].Color;
        var body = brush.GradientStops[^1].Color;

        Assert.True(top.R > body.R && top.G > body.G && top.B > body.B, "верх обязан ловить свет");
        // Блик — это свет НА стекле: добавь он прозрачности, сквозь верх карточки стали бы видны
        // чужие панели, и вместо толщины получилась бы дыра.
        Assert.Equal(body.A, top.A);
    }

    [Fact]
    public void Мелкие_поверхности_остаются_ровными()
    {
        // Градиент на плашке тега читается не как стекло, а как грязь.
        Assert.False(GlassSurface.HasSpecular("TagBgBrush"));
        Assert.IsType<SolidColorBrush>(GlassSurface.Surface(Card, GlassSurface.Default, specular: false));
    }
}
