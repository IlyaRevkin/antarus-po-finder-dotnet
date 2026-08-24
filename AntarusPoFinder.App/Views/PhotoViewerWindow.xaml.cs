using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using AntarusPoFinder.Core.Services;

namespace AntarusPoFinder.App.Views;

/// <summary>Встроенный просмотрщик для пасхалки на номере версии — своё окно, а не системный
/// просмотрщик: работает без файловых ассоциаций и всегда открывается одинаково.
///
/// Показывает не один файл, а ВСЮ общую папку лентой: что положил коллега, видно всем, листается
/// стрелками. Раньше окно открывало ровно одну запомненную запись, и у каждой машины она была своя.
///
/// Умеет три вида содержимого (см. <see cref="EasterEggPhoto.MediaKind"/>):
/// <list type="bullet">
/// <item><b>картинка</b> — один кадр в Image;</item>
/// <item><b>GIF</b> — кадры перелистываются вручную по таймеру. WPF анимировать GIF не умеет вовсе:
///       BitmapImage показывает только первый кадр, из-за чего «гифка» выглядела мёртвой картинкой;</item>
/// <item><b>видео</b> — MediaElement (кодеки берутся системные), с зацикливанием.</item>
/// </list>
///
/// Загрузка обёрнута в try/catch: битый/неверный файл не должен ронять приложение — такой файл
/// просто пропускается и лента едет к следующему.</summary>
public partial class PhotoViewerWindow : Window
{
    /// <summary>Кадры GIF и задержки к ним. Держим готовыми (Freeze) — перелистывание не должно
    /// упираться в декодирование на каждый тик.</summary>
    private readonly List<(ImageSource Frame, TimeSpan Delay)> _frames = new();

    private DispatcherTimer? _gifTimer;
    private int _frameIndex;

    /// <summary>Лента файлов и место в ней. Порядок задаёт вызывающий (EasterEggPhoto.List) — он же
    /// одинаков на всех машинах.</summary>
    private IReadOnlyList<string> _files = Array.Empty<string>();
    private int _index;

    /// <summary>Показываемый сейчас файл — видео (его надо запустить, когда окно появится).</summary>
    private bool _pendingVideo;

    private PhotoViewerWindow()
    {
        InitializeComponent();
        // Play() до показа окна WPF игнорирует — запускаем, когда элемент уже в дереве. Подписка
        // одна на всё время жизни окна: при перелистывании Loaded больше не сработает.
        Loaded += (_, _) => StartVideoIfPending();
        Closed += (_, _) => StopEverything();
    }


    private void Prev_Click(object sender, RoutedEventArgs e) => Step(-1);

    private void Next_Click(object sender, RoutedEventArgs e) => Step(+1);

    /// <summary>Стрелки и пробел листают ленту. Esc закрывает штатно (кнопка IsCancel).</summary>
    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Left:
            case Key.PageUp:
                Step(-1);
                e.Handled = true;
                break;
            case Key.Right:
            case Key.PageDown:
            case Key.Space:
                Step(+1);
                e.Handled = true;
                break;
            // Escape закрывал окно через IsCancel у собственного крестика; крестик убран как
            // дубликат системного, и закрытие по Escape теперь живёт здесь.
            case Key.Escape:
                Close();
                e.Handled = true;
                break;
        }
    }

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

    /// <summary>Кодека для этого файла в системе нет. Молчим: в ленте это просто «не показалось»,
    /// листать остальное по-прежнему можно. Один файл в ленте — закрываемся, показывать нечего.</summary>
    private void Video_MediaFailed(object sender, ExceptionRoutedEventArgs e)
    {
        if (_files.Count <= 1) Close();
        else Step(+1);
    }

    private void StopEverything()
    {
        _gifTimer?.Stop();
        _gifTimer = null;
        _frames.Clear();
        _frameIndex = 0;
        _pendingVideo = false;
        try { Video.Stop(); Video.Source = null; }
        catch (Exception)
        {
            // MediaElement бросает, если проигрывание уже сорвалось (кодека нет, файл исчез с шары).
            // Это остановка перед показом следующего файла или перед закрытием окна — вываливать
            // ошибку прошлого файла человеку, который уже листает дальше, незачем.
        }
    }

    /// <summary>Показывает ленту <paramref name="files"/>, начиная с <paramref name="startIndex"/>.
    /// Возвращает true, если окно удалось открыть; false — если показать нечего (пустая лента или ни
    /// один файл не читается). Тихо, без сообщений об ошибке.</summary>
    public static bool TryShow(Window? owner, IReadOnlyList<string>? files, int startIndex = 0)
    {
        if (files is null || files.Count == 0) return false;
        try
        {
            var window = new PhotoViewerWindow { Owner = owner, _files = files };
            var start = startIndex >= 0 && startIndex < files.Count ? startIndex : 0;

            // Ни один файл не открылся — окно не показываем вовсе.
            if (!window.ShowFrom(start, +1)) return false;

            window.ShowDialog();
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>Один файл — частный случай ленты из одного элемента.</summary>
    public static bool TryShow(Window? owner, string? path) =>
        string.IsNullOrWhiteSpace(path) ? false : TryShow(owner, new[] { path! });

    /// <summary>Перелистнуть на <paramref name="step"/> с учётом закольцовки; битые файлы по дороге
    /// пропускаются. Показать не удалось ничего — оставляем на экране то, что было.</summary>
    private void Step(int step)
    {
        if (_files.Count < 2) return;
        ShowFrom(Wrap(_index + step), step);
    }

    /// <summary>Ищет ближайший показываемый файл начиная с <paramref name="index"/> в сторону
    /// <paramref name="step"/>. false — не открылся ни один.</summary>
    private bool ShowFrom(int index, int step)
    {
        for (var tried = 0; tried < _files.Count; tried++)
        {
            var candidate = Wrap(index + step * tried);
            if (!Load(_files[candidate])) continue;

            _index = candidate;
            UpdateChrome();
            return true;
        }
        return false;
    }

    private int Wrap(int index)
    {
        var count = _files.Count;
        return ((index % count) + count) % count;
    }

    /// <summary>Готовит окно к следующему файлу и показывает его. Всё состояние предыдущего (кадры
    /// гифки, таймер, проигрыватель) снимается ЗДЕСЬ — иначе гифка продолжала бы перелистываться
    /// поверх следующей картинки, а звук видео играть после перелистывания.</summary>
    private bool Load(string path)
    {
        StopEverything();
        Photo.Source = null;
        Photo.Visibility = Visibility.Collapsed;
        Video.Visibility = Visibility.Collapsed;

        try
        {
            if (!File.Exists(path)) return false;

            return EasterEggPhoto.KindOf(path) switch
            {
                EasterEggPhoto.MediaKind.Video => ShowVideo(path),
                EasterEggPhoto.MediaKind.AnimatedImage => ShowAnimation(path) || ShowStill(path),
                EasterEggPhoto.MediaKind.Image => ShowStill(path),
                // Незнакомое расширение — не приговор: файл мог прийти без него или с чужим. Пробуем
                // прочитать как картинку, и только если и это не вышло, сдаёмся.
                _ => ShowStill(path),
            };
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>Счётчик и стрелки. Лента из одного файла — ни того, ни другого: листать нечего.</summary>
    private void UpdateChrome()
    {
        var many = _files.Count > 1;
        var visibility = many ? Visibility.Visible : Visibility.Collapsed;
        PrevButton.Visibility = visibility;
        NextButton.Visibility = visibility;
        Counter.Visibility = visibility;
        if (many) Counter.Text = $"{_index + 1} / {_files.Count}";
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
            // Таймер мог пережить перелистывание на другой файл — тогда кадров уже нет.
            if (_frames.Count == 0) return;
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
            _pendingVideo = true;
            StartVideoIfPending();
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>Запускает проигрывание, когда окно уже показано. До показа Play() бесполезен, поэтому
    /// первый файл ждёт Loaded, а перелистнутые запускаются сразу.</summary>
    private void StartVideoIfPending()
    {
        if (!_pendingVideo || !IsLoaded) return;
        _pendingVideo = false;
        try { Video.Play(); }
        catch (Exception)
        {
            // Нет кодека, битый файл — MediaElement сообщит об этом сам своим MediaFailed, там же и
            // рисуется сообщение. Второе окно с ошибкой поверх ленты просмотра только мешало бы.
        }
    }
}
