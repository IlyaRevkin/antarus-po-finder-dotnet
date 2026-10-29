using System.Windows;
using System.Windows.Media;

namespace AntarusPoFinder.App;

public static class ThemeManager
{
    public static event Action<string>? ThemeChanged;

    public static string Current { get; private set; } = "light";

    /// <summary>Выбранный цвет — кодом вида #1E66F5. Отдельно от темы: тема отвечает за фон и текст
    /// страницы, цвет — только за акцент.</summary>
    public static string CurrentAccent { get; private set; } = AccentPalette.DefaultHex;

    /// <summary>Насколько просвечивают поверхности темы «Стекло» (см. GlassSurface). Живёт здесь, а
    /// не читается из конфига прямо в Apply: тему применяет и самоснимок, и тесты, которым конфиг
    /// пользователя не нужен вовсе. Значение ставит тот, кто знает про настройки, — главное окно при
    /// запуске и страница настроек при движении ползунка.</summary>
    public static int GlassOpacity { get; set; } = GlassSurface.Default;

    public static void Apply(string themeName) => Apply(themeName, CurrentAccent);

    /// <summary>Пересчитать ТОЛЬКО поверхности стекла под текущее значение ползунка.
    ///
    /// Отдельно от <see cref="Apply"/> потому, что тот перекладывает словарь темы, пересчитывает
    /// акцент и переставляет размытие у каждого окна. На каждое движение ручки это читается как
    /// моргание всем окном — ровно то, что названо кривой регулировкой. Здесь меняются четыре
    /// кисти в ресурсах приложения, а разметка подхватывает их сама: она ссылается на них через
    /// DynamicResource.</summary>
    public static void RefreshGlass(ResourceDictionary? themeDictionary = null)
    {
        var dict = themeDictionary ?? Application.Current.Resources.MergedDictionaries[0];
        var res = Application.Current.Resources;
        foreach (var key in GlassSurface.Keys)
        {
            if (dict[key] is SolidColorBrush brush)
                res[key] = GlassSurface.Surface(brush.Color, GlassOpacity, GlassSurface.HasSpecular(key));
        }
    }

    public static void Apply(string themeName, string accentId)
    {
        Current = themeName;
        CurrentAccent = accentId;

        // Какой словарь подменять и считать ли тему тёмной — знает ThemeCatalog, а не этот метод:
        // тем стало больше двух, и развилка «dark или не dark» в каждой точке превращалась в место,
        // где забывают поправить одну ветку.
        var theme = ThemeCatalog.Resolve(themeName);
        var uri = new Uri($"/AntarusPoFinder.App;component/Themes/{theme.Dictionary}.xaml", UriKind.Relative);
        var dict = new ResourceDictionary { Source = uri };

        var merged = Application.Current.Resources.MergedDictionaries;
        // The theme dictionary is always merged dictionary index 0 (see App.xaml).
        merged[0] = dict;

        bool dark = theme.IsDark;

        // Акцент кладём поверх темы, прямо в ресурсы приложения. Так надёжнее, чем ещё одним
        // словарём в MergedDictionaries: тот пришлось бы держать строго после Styles.xaml и следить
        // за порядком вечно. Ключ верхнего уровня перекрывает всё, что merged'ится под ним.
        // Работает это потому, что стили ссылаются на акцент только через DynamicResource (проверено:
        // StaticResource на акцентные кисти в Styles.xaml нет ни одного) — StaticResource разрешился
        // бы один раз при загрузке темы и на смену цвета уже не отозвался.
        var accent = AccentPalette.Parse(AccentPalette.NormalizeStored(accentId));
        var res = Application.Current.Resources;
        res["AccentBrush"] = new SolidColorBrush(accent);
        res["AccentHoverBrush"] = new SolidColorBrush(AccentPalette.Hover(accent, dark));
        res["ListSelectedBgBrush"] = new SolidColorBrush(AccentPalette.SelectedBackground(accent, dark));
        // Цвет надписи НА акценте считается от самого акцента, а не берётся из темы. Иначе выбор
        // цвета пришлось бы ограничивать тёмными: на светлом акценте белая подпись пропадает.
        res["TextOnAccentBrush"] = new SolidColorBrush(AccentPalette.TextOn(accent));

        // МЯГКИЕ ПОВЕРХНОСТИ считаются ОТ АКЦЕНТА, а не берутся из словаря темы.
        //
        // В «Материале» и «Стекле» вторичная кнопка и выбранный пункт меню заливаются оттенком
        // акцента (tonal surface у Material 3, подсвеченная строка у Apple). Если бы этот оттенок
        // лежал в теме числом, он разошёлся бы с акцентом, который человек выбрал себе сам:
        // сиреневая плашка под синей кнопкой — ровно то, что на снимке и получилось.
        // Подпись на такой заливке — обычного цвета текста: заливка светлая, и «белое на акценте»
        // на ней пропадает.
        if (theme.Tonal)
        {
            res["ButtonSoftBrush"] = new SolidColorBrush(AccentPalette.SelectedBackground(accent, dark));
            res["NavActiveBrush"] = new SolidColorBrush(AccentPalette.SelectedBackground(accent, dark));
            if (dict["TextBrush"] is Brush text)
            {
                res["ButtonSoftTextBrush"] = text;
                res["NavActiveTextBrush"] = text;
            }
        }
        else
        {
            // Светлая и тёмная: ключи возвращаются к значениям своей темы — иначе мягкие
            // поверхности «залипли» бы после переключения с беты на обычную тему.
            res.Remove("ButtonSoftBrush");
            res.Remove("NavActiveBrush");
            res.Remove("ButtonSoftTextBrush");
            res.Remove("NavActiveTextBrush");
        }

        // Прозрачность поверхностей — поверх словаря темы, тем же приёмом, что и акцент: ключ в
        // ресурсах приложения перекрывает значение темы, а Remove возвращает тему к себе самой.
        // Поэтому у тем без стекла ползунок не оставляет следов — на этом уже обжигались «залипшие»
        // мягкие поверхности после переключения темы.
        if (theme.Backdrop) RefreshGlass(dict);
        else foreach (var key in GlassSurface.Keys) res.Remove(key);

        foreach (Window window in Application.Current.Windows)
        {
            DarkTitleBar.Apply(window, dark);
            // Стекло — это размытие за окном, и включает его система. Тема без размытия обязана его
            // снять: оставленный прозрачным фон окна выглядел бы дырой.
            if (theme.Backdrop) WindowBackdrop.Apply(window);
            else WindowBackdrop.Clear(window);
        }

        ThemeChanged?.Invoke(themeName);
    }
}
