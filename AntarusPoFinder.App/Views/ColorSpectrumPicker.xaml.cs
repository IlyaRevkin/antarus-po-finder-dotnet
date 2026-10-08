using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace AntarusPoFinder.App.Views;

/// <summary>Выбор цвета спектром — радужная полоса оттенка и квадрат «бледнее/темнее» под ней.
///
/// Заведён по просьбе: «сделай выбор цвета не только кодом, но и спектром». Поле с кодом никуда не
/// делось и остаётся главным способом попасть в ТОЧНЫЙ цвет (фирменный, из макета), а спектр — для
/// того, чего кодом не сделать: подобрать оттенок на глаз, видя его целиком.
///
/// Оттенок хранится ОТДЕЛЬНО от цвета, а не вычисляется из него каждый раз. У серого оттенка нет
/// (см. ColorSpectrum.ToHsv), поэтому, уведи человек насыщенность в ноль, маркер на радуге прыгнул бы
/// в красный — и вернуть прежний оттенок мышью стало бы невозможно. То же и с чёрным.</summary>
public partial class ColorSpectrumPicker : System.Windows.Controls.UserControl
{
    private double _hue;
    private double _saturation;
    private double _value = 1;
    private bool _updating;

    public ColorSpectrumPicker()
    {
        InitializeComponent();
        Loaded += (_, _) => Redraw();
        SizeChanged += (_, _) => Redraw();
        Redraw();
    }

    /// <summary>Цвет менялся мышью. Отдельное событие, а не только свойство: настройки применяют
    /// цвет сразу, и им важно отличить «человек ведёт мышь» от «нам задали цвет извне».</summary>
    public event EventHandler<Color>? ColorPicked;

    public static readonly DependencyProperty SelectedColorProperty =
        DependencyProperty.Register(nameof(SelectedColor), typeof(Color), typeof(ColorSpectrumPicker),
            new FrameworkPropertyMetadata(Colors.Black,
                FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnSelectedColorChanged));

    public Color SelectedColor
    {
        get => (Color)GetValue(SelectedColorProperty);
        set => SetValue(SelectedColorProperty, value);
    }

    private static void OnSelectedColorChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not ColorSpectrumPicker picker || picker._updating) return;
        picker.AdoptColor((Color)e.NewValue);
    }

    /// <summary>Принять цвет, пришедший снаружи (поле кода, образец палитры, загрузка настроек).
    /// Оттенок серого цвета сохраняем прежний — ровно по причине из описания класса.</summary>
    private void AdoptColor(Color c)
    {
        var (h, s, v) = ColorSpectrum.ToHsv(c);
        if (s > 0) _hue = h;
        _saturation = s;
        _value = v;
        Redraw();
    }

    private void Apply()
    {
        var color = ColorSpectrum.FromHsv(_hue, _saturation, _value);
        _updating = true;
        SelectedColor = color;
        _updating = false;
        Redraw();
        ColorPicked?.Invoke(this, color);
    }

    private void Redraw()
    {
        // Подложка квадрата — чистый оттенок: насыщенность и яркость накладывают градиенты поверх.
        HueLayer.Fill = new SolidColorBrush(ColorSpectrum.FromHsv(_hue, 1, 1));

        var p = ColorSpectrum.SvToPoint(_saturation, _value, SvArea.ActualWidth, SvArea.ActualHeight);
        SvThumb.Margin = new Thickness(p.X - SvThumb.Width / 2, p.Y - SvThumb.Height / 2, 0, 0);

        var x = ColorSpectrum.HueToX(_hue, HueArea.ActualWidth);
        HueThumb.Margin = new Thickness(Math.Max(0, x - HueThumb.Width / 2), 0, 0, 0);
    }

    // ── Квадрат ───────────────────────────────────────────────────────────────

    private void Sv_MouseDown(object sender, MouseButtonEventArgs e)
    {
        SvArea.CaptureMouse();
        PickSv(e.GetPosition(SvArea));
    }

    private void Sv_MouseMove(object sender, MouseEventArgs e)
    {
        // Мышь захвачена — значит человек ведёт её, не отпуская: цвет должен меняться непрерывно,
        // иначе подбирать оттенок пришлось бы щелчками.
        if (e.LeftButton == MouseButtonState.Pressed && SvArea.IsMouseCaptured) PickSv(e.GetPosition(SvArea));
    }

    private void Sv_MouseUp(object sender, MouseButtonEventArgs e) => SvArea.ReleaseMouseCapture();

    private void PickSv(Point p)
    {
        (_saturation, _value) = ColorSpectrum.PointToSv(p, SvArea.ActualWidth, SvArea.ActualHeight);
        Apply();
    }

    // ── Радуга ────────────────────────────────────────────────────────────────

    private void Hue_MouseDown(object sender, MouseButtonEventArgs e)
    {
        HueArea.CaptureMouse();
        PickHue(e.GetPosition(HueArea).X);
    }

    private void Hue_MouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed && HueArea.IsMouseCaptured) PickHue(e.GetPosition(HueArea).X);
    }

    private void Hue_MouseUp(object sender, MouseButtonEventArgs e) => HueArea.ReleaseMouseCapture();

    private void PickHue(double x)
    {
        _hue = ColorSpectrum.PointToHue(x, HueArea.ActualWidth);
        // Выбирать оттенок на чёрном или полностью бледном квадрате бессмысленно — цвет не
        // изменится, и человек решит, что полоса не работает. Поднимаем до видимого минимума.
        if (_value <= 0.05) _value = 1;
        if (_saturation <= 0.05) _saturation = 1;
        Apply();
    }
}
