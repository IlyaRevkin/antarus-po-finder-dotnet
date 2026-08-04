using System;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace AntarusPoFinder.App.Views;

/// <summary>Встроенный просмотрщик картинки для пасхалки на номере версии — своё окно, а не системный
/// просмотрщик: работает без файловых ассоциаций и всегда открывается одинаково. Картинка
/// масштабируется под размер окна (Stretch=Uniform), закрывается крестиком или Esc. Загрузка обёрнута
/// в try/catch: битый/неверный файл не должен ронять приложение — тогда окно просто не открывается.</summary>
public partial class PhotoViewerWindow : Window
{
    private PhotoViewerWindow(ImageSource image)
    {
        InitializeComponent();
        Photo.Source = image;
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    /// <summary>Показывает картинку из <paramref name="path"/> модальным окном. Возвращает true, если
    /// окно удалось открыть; false — если файла нет или он не читается как изображение (тихо, без
    /// сообщений об ошибке).</summary>
    public static bool TryShow(Window? owner, string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        try
        {
            if (!File.Exists(path)) return false;

            var bmp = new BitmapImage();
            bmp.BeginInit();
            // OnLoad — прочитать файл сразу и не держать его открытым: иначе картинка на сетевом диске
            // осталась бы заблокированной, пока открыто окно.
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.UriSource = new Uri(path);
            bmp.EndInit();
            bmp.Freeze();

            new PhotoViewerWindow(bmp) { Owner = owner }.ShowDialog();
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }
}
