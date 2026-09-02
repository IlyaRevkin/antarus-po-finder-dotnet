using System.Collections.Generic;
using System.Windows.Media;

namespace AntarusPoFinder.App;

/// <summary>Цветовые схемы приложения. Тема (светлая/тёмная) и ЦВЕТ — разные вещи: цвет меняет
/// только акцент, а фон, текст и рамки остаются от базовой темы. Поэтому палитра здесь задаёт три
/// кисти, а не весь набор: перекрашивать тему целиком ради одной кнопки незачем.
///
/// Каждый акцент задан парой значений — для светлой и тёмной темы отдельно. Один и тот же цвет на
/// белом и на почти чёрном читается по-разному: на светлом фоне нужен насыщенный, на тёмном —
/// приглушённый, иначе он выжигает глаза.
///
/// ⚠️ Текст на акценте везде белый (TextOnAccentBrush). Поэтому каждый акцент подобран так, чтобы
/// контраст белого к нему был не ниже 4.5:1 — порога, с которого мелкий текст читается уверенно.
/// Из-за этого в наборе нет светло-оранжевого и жёлтого: на них белый текст «плывёт», а заводить
/// ради двух цветов вторую логику выбора цвета текста — плодить случаи, где его забудут.</summary>
public sealed record AccentPalette(
    string Id,
    string DisplayName,
    string LightAccent,
    string LightHover,
    string LightSelectedBg,
    string DarkAccent,
    string DarkHover,
    string DarkSelectedBg)
{
    public const string DefaultId = "blue";

    /// <summary>Синий — исходный цвет приложения; он идёт первым и остаётся значением по умолчанию,
    /// чтобы у тех, кто цвет не выбирал, ничего не поменялось.</summary>
    public static readonly IReadOnlyList<AccentPalette> All = new[]
    {
        new AccentPalette("blue",   "Синий",      "#1E66F5", "#3B7CF7", "#C4D4F7", "#2A66D9", "#2252AE", "#35406A"),
        new AccentPalette("green",  "Зелёный",    "#1F7A4C", "#279060", "#C3E3D2", "#2C8159", "#215F42", "#2C4A3A"),
        new AccentPalette("purple", "Фиолетовый", "#6B3FC9", "#7E52DC", "#D9CCF5", "#6F49C4", "#553594", "#3E3560"),
        new AccentPalette("teal",   "Бирюзовый",  "#0F7A80", "#149098", "#C2E1E4", "#137F86", "#0D6167", "#26454A"),
        new AccentPalette("crimson","Малиновый",  "#C2255C", "#D93B72", "#F5CCDA", "#B32E60", "#8C2249", "#4A2A38"),
    };

    public static AccentPalette Find(string? id)
    {
        foreach (var p in All)
            if (string.Equals(p.Id, id, System.StringComparison.OrdinalIgnoreCase)) return p;
        return All[0];
    }

    public Color Accent(bool dark) => Parse(dark ? DarkAccent : LightAccent);

    public Color Hover(bool dark) => Parse(dark ? DarkHover : LightHover);

    public Color SelectedBg(bool dark) => Parse(dark ? DarkSelectedBg : LightSelectedBg);

    private static Color Parse(string hex) => (Color)ColorConverter.ConvertFromString(hex);
}
