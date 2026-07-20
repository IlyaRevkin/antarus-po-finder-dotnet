using System;
using System.Windows;

namespace AntarusPoFinder.App;

public static class ThemeManager
{
    public static event Action<string>? ThemeChanged;

    public static string Current { get; private set; } = "light";

    public static void Apply(string themeName)
    {
        Current = themeName;

        var uri = new Uri($"/AntarusPoFinder.App;component/Themes/{(themeName == "dark" ? "Dark" : "Light")}.xaml", UriKind.Relative);
        var dict = new ResourceDictionary { Source = uri };

        var merged = Application.Current.Resources.MergedDictionaries;
        // The theme dictionary is always merged dictionary index 0 (see App.xaml).
        merged[0] = dict;

        bool dark = themeName == "dark";
        foreach (Window window in Application.Current.Windows)
            DarkTitleBar.Apply(window, dark);

        ThemeChanged?.Invoke(themeName);
    }
}
