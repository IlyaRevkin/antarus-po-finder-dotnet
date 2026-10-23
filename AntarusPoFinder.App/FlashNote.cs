using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace AntarusPoFinder.App;

/// <summary>Короткое сообщение «получилось» поверх окна — вместо модального окошка с кнопкой «ОК».
///
/// Просьба дословно: «плашка об удачном переносе, её закрыть можно только нажав ок, давай сделаем
/// что просто кликом в другой области или смещением активного окна она закрылась». Модальное окно
/// здесь и правда лишнее: ничего не спрашивают, ответа не ждут, а руку приходится вести к кнопке и
/// нажимать — на каждый перенос файла.
///
/// Закрывается сама: по щелчку в любом месте (в том числе по той же кнопке — тогда просто покажется
/// заново), при уходе фокуса с окна, при его перемещении или изменении размера и по времени. Поэтому
/// плашка НЕ годится для вопросов и предупреждений, на которые человек обязан ответить, — только для
/// подтверждения уже случившегося. Для всего остального остаётся AppMessageBox.</summary>
public static class FlashNote
{
    /// <summary>Сколько плашка висит, если её ничем не потревожили. Четырёх секунд хватает прочитать
    /// имя файла и не настолько много, чтобы она висела над следующим действием.</summary>
    private static readonly TimeSpan Linger = TimeSpan.FromSeconds(4);

    public static void Show(Window owner, string text)
    {
        if (owner.Content is not UIElement anchor) return;

        var border = new Border
        {
            Padding = new Thickness(14, 10, 14, 10),
            CornerRadius = new CornerRadius(8),
            BorderThickness = new Thickness(1),
            MaxWidth = 420,
        };
        // Цвета — только из темы: hex в коде так же запрещён, как в XAML, иначе в «Стекле» и
        // «Материале» плашка окажется чужеродной.
        border.SetResourceReference(Border.BackgroundProperty, "PopupBgBrush");
        border.SetResourceReference(Border.BorderBrushProperty, "PopupBorderBrush");

        var label = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap };
        label.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
        border.Child = label;

        var popup = new Popup
        {
            Child = border,
            AllowsTransparency = true,
            // StaysOpen=false закрывает плашку по щелчку МИМО неё — это и есть «кликом в другой
            // области». Щелчок по самой плашке тоже закрывает (обработчик ниже): отдельного способа
            // «убрать это сообщение» человеку искать не нужно.
            StaysOpen = false,
            PlacementTarget = anchor,
            Placement = PlacementMode.Center,
            PopupAnimation = PopupAnimation.Fade,
        };

        // Чуть ниже середины окна: по центру плашка накрывает то самое место, о котором сообщает
        // (список файлов, таблицу), и человек на секунду теряет строку, с которой работал.
        popup.VerticalOffset = Math.Max(0, anchor.RenderSize.Height / 2 - 60);

        var timer = new DispatcherTimer { Interval = Linger };

        void Close()
        {
            timer.Stop();
            popup.IsOpen = false;
            owner.Deactivated -= OnGone;
            owner.LocationChanged -= OnGone;
            owner.SizeChanged -= OnSize;
            owner.PreviewMouseDown -= OnClick;
        }

        void OnGone(object? s, EventArgs e) => Close();
        void OnSize(object? s, SizeChangedEventArgs e) => Close();
        void OnClick(object? s, MouseButtonEventArgs e) => Close();

        timer.Tick += (_, _) => Close();
        border.MouseDown += (_, _) => Close();
        owner.Deactivated += OnGone;
        owner.LocationChanged += OnGone;
        owner.SizeChanged += OnSize;
        owner.PreviewMouseDown += OnClick;

        popup.IsOpen = true;
        timer.Start();
    }
}
