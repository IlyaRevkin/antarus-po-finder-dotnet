using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace AntarusPoFinder.App;

/// <summary>НАСТОЯЩЕЕ РАЗМЫТИЕ ЗА ОКНОМ — то, чего не хватало теме «Стекло».
///
/// Замечание Ильи 16.10.2026, дословно: «Стекло нихер не стекло». Справедливо: я поменял только
/// палитру и полупрозрачность панелей, а стекло — это не цвет, а РАЗМЫТИЕ того, что за окном.
/// Нарисовать его средствами приложения нельзя в принципе: содержимое за своим окном недоступно.
/// Делает это система, через DWM.
///
/// Windows 11 (сборка 22621+) умеет это одним атрибутом окна: DWMWA_SYSTEMBACKDROP_TYPE со
/// значением Acrylic — прозрачное размытие обоев и окон под нами, то самое «матовое стекло»; Mica —
/// спокойнее, подмешивает только обои. На Windows 10 атрибута нет, вызов просто вернёт ошибку, и
/// тема останется такой, какой была, — тёмной и полупрозрачной, без размытия. Проверять версию
/// заранее незачем: отказ DWM здесь и есть проверка.
///
/// ⚠️ Обязательное условие: фон самого окна должен быть ПРОЗРАЧНЫМ, иначе размытие окажется под
/// сплошной заливкой и его никто не увидит. Поэтому Apply сам приводит фон окна к прозрачному, а
/// Clear возвращает кисть темы.</summary>
public static class WindowBackdrop
{
    private const int DWMWA_SYSTEMBACKDROP_TYPE = 38;

    private enum BackdropType
    {
        Auto = 0,
        None = 1,
        Mica = 2,
        Acrylic = 3,
        MicaAlt = 4,
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    /// <summary>Включить размытие за окном. Возвращает false, если система не умеет (Windows 10 и
    /// старее) — вызывающий на это не обязан реагировать, тема и без размытия остаётся рабочей.</summary>
    public static bool Apply(Window window, bool acrylic = true)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero) return false;

        var value = (int)(acrylic ? BackdropType.Acrylic : BackdropType.Mica);
        if (DwmSetWindowAttribute(hwnd, DWMWA_SYSTEMBACKDROP_TYPE, ref value, sizeof(int)) != 0)
            return false;

        // Без прозрачного фона окна размытие просто не видно: оно рисуется ПОД содержимым.
        window.Background = Brushes.Transparent;
        return true;
    }

    /// <summary>Выключить размытие и вернуть обычный фон темы. Зовётся при переключении на тему без
    /// стекла: оставленный прозрачным фон дал бы дыру в окне.</summary>
    public static void Clear(Window window)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd != IntPtr.Zero)
        {
            var none = (int)BackdropType.None;
            DwmSetWindowAttribute(hwnd, DWMWA_SYSTEMBACKDROP_TYPE, ref none, sizeof(int));
        }

        window.SetResourceReference(Window.BackgroundProperty, "BgBrush");
    }
}
