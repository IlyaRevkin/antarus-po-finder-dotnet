using System.Windows;
using System.Windows.Media;

namespace AntarusPoFinder.App;

public static class ThemeManager
{
    public static event Action<string>? ThemeChanged;

    public static string Current { get; private set; } = "light";

    /// <summary>Выбранный цвет акцента. Отдельно от темы: светлая/тёмная и цвет — независимые
    /// настройки, их сочетаний десяток, и держать десяток файлов тем ради этого не нужно.</summary>
    public static string CurrentAccent { get; private set; } = AccentPalette.DefaultId;

    public static void Apply(string themeName) => Apply(themeName, CurrentAccent);

    public static void Apply(string themeName, string accentId)
    {
        Current = themeName;
        CurrentAccent = accentId;

        var uri = new Uri($"/AntarusPoFinder.App;component/Themes/{(themeName == "dark" ? "Dark" : "Light")}.xaml", UriKind.Relative);
        var dict = new ResourceDictionary { Source = uri };

        var merged = Application.Current.Resources.MergedDictionaries;
        // The theme dictionary is always merged dictionary index 0 (see App.xaml).
        merged[0] = dict;

        bool dark = themeName == "dark";

        // Акцент кладём поверх темы, прямо в ресурсы приложения. Так надёжнее, чем ещё одним
        // словарём в MergedDictionaries: тот пришлось бы держать строго после Styles.xaml и следить
        // за порядком вечно. Ключ верхнего уровня перекрывает всё, что merged'ится под ним.
        // Работает это потому, что стили ссылаются на акцент только через DynamicResource (проверено:
        // StaticResource на акцентные кисти в Styles.xaml нет ни одного) — StaticResource разрешился
        // бы один раз при загрузке темы и на смену цвета уже не отозвался.
        var palette = AccentPalette.Find(accentId);
        var res = Application.Current.Resources;
        res["AccentBrush"] = new SolidColorBrush(palette.Accent(dark));
        res["AccentHoverBrush"] = new SolidColorBrush(palette.Hover(dark));
        res["ListSelectedBgBrush"] = new SolidColorBrush(palette.SelectedBg(dark));

        foreach (Window window in Application.Current.Windows)
            DarkTitleBar.Apply(window, dark);

        ThemeChanged?.Invoke(themeName);
    }
}
