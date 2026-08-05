using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using AntarusPoFinder.Core.Services;

namespace AntarusPoFinder.App.Views;

/// <summary>Встроенный просмотрщик для пасхалки на номере версии — своё окно, а не системный
/// просмотрщик: работает без файловых ассоциаций и всегда открывается одинаково.
///
/// Умеет три вида содержимого (см. <see cref="EasterEggPhoto.MediaKind"/>):
/// <list type="bullet">
/// <item><b>картинка</b> — один кадр в Image;</item>
/// <item><b>GIF</b> — кадры перелистываются вручную по таймеру. WPF анимировать GIF не умеет вовсе:
///       BitmapImage показывает только первый кадр, из-за чего «гифка» выглядела мёртвой картинкой;</item>
/// <item><b>видео</b> — MediaElement (кодеки берутся системные), с зацикливанием.</item>
/// </list>
///
/// Загрузка обёрнута в try/catch: битый/неверный файл не должен ронять приложение — тогда окно просто
/// не открывается, и вызывающий предлагает выбрать другой.</summary>
public partial class PhotoViewerWindow : Window
{
    /// <summary>Кадры GIF и задержки к ним. Держим готовыми (Freeze) — перелистывание не должно
    /// упираться в декодирование на каждый тик.</summary>
    private readonly List<(ImageSource Frame, TimeSpan Delay)> _frames = new();

    private DispatcherTimer? _gifTimer;
    private int _frameIndex;

    private PhotoViewerWindow()
    {
        InitializeComponent();
        Closed += (_, _) => StopEverything();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    /// <summary>Видео зациклено: пасхалка — это «посмотреть», а не «включить один раз и смотреть на
    /// чёрный прямоугольник».</summary>
    private void Video_MediaEnded(object sender, RoutedEventArgs e)
    {
        try
        {
            Video.Position = TimeSpan.Zero;
            Video.Play();
        }
        catch (Exception)
        {
            // Проигрыватель отвалился — просто перестаём крутить, окно остаётся открытым.
        }
    }

    /// <summary>Кодека для этого файла в системе нет. Молчим и закрываемся: пасхалка не то место, где
    /// показывают диагностику мультимедиа.</summary>
    private void Video_MediaFailed(object sender, ExceptionRoutedEventArgs e) => Close();

    private void StopEverything()
    {
        _gifTimer?.Stop();
        _gifTimer = null;
        try { Video.Stop(); Video.Source = null; }
        catch (Exception) { }
    }

    /// <summary>Показывает файл из <paramref name="path"/> модальным окном. Возвращает true, если окно
    /// удалось открыть; false — если файла нет, расширение незнакомое или содержимое не читается
    /// (тихо, без сообщений об ошибке).</summary>
    public static bool TryShow(Window? owner, string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        try
        {
            if (!File.Exists(path)) return false;

            var window = new PhotoViewerWindow { Owner = owner };
            var kind = EasterEggPhoto.KindOf(path);

            var ready = kind switch
            {
                EasterEggPhoto.MediaKind.Video => window.ShowVideo(path),
                EasterEggPhoto.MediaKind.AnimatedImage => window.ShowAnimation(path) || window.ShowStill(path),
                EasterEggPhoto.MediaKind.Image => window.ShowStill(path),
                // Незнакомое расширение — не приговор: файл мог прийти без него или с чужим. Пробуем
                // прочитать как картинку, и только если и это не вышло, сдаёмся.
                _ => window.ShowStill(path),
            };
            if (!ready) return false;

            window.ShowDialog();
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private bool ShowStill(string path)
    {
        try
        {
            Photo.Source = LoadFrozen(path);
            Photo.Visibility = Visibility.Visible;
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>Читает файл ЦЕЛИКОМ в память (OnLoad) и не держит его открытым: иначе картинка на
    /// сетевом диске оставалась бы заблокированной, пока открыто окно.</summary>
    private static BitmapImage LoadFrozen(string path)
    {
        var bmp = new BitmapImage();
        bmp.BeginInit();
        bmp.CacheOption = BitmapCacheOption.OnLoad;
        bmp.UriSource = new Uri(path);
        bmp.EndInit();
        bmp.Freeze();
        return bmp;
    }

    /// <summary>Раскладывает GIF на кадры и запускает их перелистывание. false — файл однокадровый или
    /// не разобрался; вызывающий тогда покажет его обычной картинкой.</summary>
    private bool ShowAnimation(string path)
    {
        try
        {
            // Файл читается в память и закрывается сразу — как и у неподвижной картинки.
            var bytes = File.ReadAllBytes(path);
            using var stream = new MemoryStream(bytes);
            var decoder = new GifBitmapDecoder(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            if (decoder.Frames.Count < 2) return false;

            foreach (var frame in decoder.Frames)
            {
                frame.Freeze();
                _frames.Add((frame, DelayOf(frame)));
            }
        }
        catch (Exception)
        {
            _frames.Clear();
            return false;
        }

        Photo.Visibility = Visibility.Visible;
        _frameIndex = 0;
        Photo.Source = _frames[0].Frame;

        _gifTimer = new DispatcherTimer(DispatcherPriority.Render) { Interval = _frames[0].Delay };
        _gifTimer.Tick += (_, _) =>
        {
            _frameIndex = (_frameIndex + 1) % _frames.Count;
            Photo.Source = _frames[_frameIndex].Frame;
            _gifTimer!.Interval = _frames[_frameIndex].Delay;
        };
        _gifTimer.Start();
        return true;
    }

    /// <summary>Задержка кадра из метаданных GIF (в сотых долях секунды). Нули и отсутствующее
    /// значение приводим к 100 мс — так же поступают браузеры: кадр «без задержки» на деле означает
    /// «как можно быстрее», и буквальные 0 мс сожгли бы процессор.</summary>
    private static TimeSpan DelayOf(BitmapFrame frame)
    {
        const int defaultMs = 100;
        try
        {
            if (frame.Metadata is BitmapMetadata meta &&
                meta.GetQuery("/grctlext/Delay") is ushort hundredths && hundredths > 1)
                return TimeSpan.FromMilliseconds(hundredths * 10);
        }
        catch (Exception)
        {
            // У кадра нет расширения управления графикой — берём значение по умолчанию.
        }
        return TimeSpan.FromMilliseconds(defaultMs);
    }

    private bool ShowVideo(string path)
    {
        try
        {
            Video.Visibility = Visibility.Visible;
            Video.Source = new Uri(path);
            // Play() до показа окна WPF игнорирует — запускаем, когда элемент уже в дереве.
            Loaded += (_, _) =>
            {
                try { Video.Play(); }
                catch (Exception) { Close(); }
            };
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }
}
