using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Input;
using AntarusPoFinder.App.Services;
using AntarusPoFinder.Core.Services;

namespace AntarusPoFinder.App.Views;

/// <summary>Наклейки — маленькое окно вместо целой страницы. Просьба была ровно такая: «чтобы была
/// кнопка, чтобы не искать… и она ради такой мелочи весь функционал не занимала». Поэтому здесь нет
/// ни своей таблицы в базе, ни модерации: список файлов из общей папки (см.
/// <see cref="StickerTemplates"/>), печать, открытие и загрузка нового шаблона.
///
/// <b>Синхронизация тут «бесплатная», но с одним условием.</b> Сама папка живёт на общем диске
/// (<c>Конфиг\Наклейки</c>), поэтому положенный файл видят все сразу — отдельной синхронизации ему
/// не нужно. Условие — чтобы у всех сходился ПУТЬ к ней: настройка хранится хвостом от корня диска,
/// а не буквой конкретной машины (см. SharedFolderPath.ToPortable). Пока обзор папки записывал туда
/// «Z:\Конфиг\Наклейки», у коллеги с той же шарой под «Y:» наклеек не было вовсе.</summary>
public partial class StickersWindow : Window
{
    private readonly AppServices _services;
    private readonly IAppHost _host;
    private string? _folder;

    private sealed class Row
    {
        public string Path { get; init; } = "";
        public string Name => System.IO.Path.GetFileName(Path);
    }

    public StickersWindow(AppServices services, IAppHost host)
    {
        InitializeComponent();
        _services = services;
        _host = host;
        // Кнопки «Обновить» больше нет: список перечитывается при каждом возврате в окно — этого
        // достаточно и для «коллега только что положил шаблон», и после своей загрузки.
        Activated += (_, _) => Refresh();
        Refresh();
    }

    private void Refresh()
    {
        _folder = StickerTemplates.FolderFor(_services.Cfg.RootPath(), _services.Cfg.StickersFolder());
        var files = StickerTemplates.List(_folder);

        var selected = (FilesGrid.SelectedItem as Row)?.Path;
        var rows = files.Select(f => new Row { Path = f }).ToList();
        FilesGrid.ItemsSource = rows;
        if (selected is not null)
            FilesGrid.SelectedItem = rows.FirstOrDefault(r => r.Path.Equals(selected, StringComparison.OrdinalIgnoreCase));

        FolderText.Text = _folder is null
            ? "Диск не настроен — папку наклеек показать неоткуда (Настройки → Печать)."
            : rows.Count > 0
                ? $"Папка: {_folder}"
                : $"Папка пуста или недоступна: {_folder}\nНажмите «Загрузить…» — шаблон появится здесь и у коллег.";
    }

    private string? SelectedPath()
    {
        if (FilesGrid.SelectedItem is Row row) return row.Path;
        AppMessageBox.Show("Выберите наклейку в списке.", "Наклейки", MessageBoxButton.OK, MessageBoxImage.Information);
        return null;
    }

    private void Print_Click(object sender, RoutedEventArgs e) => PrintSelected();

    /// <summary>Двойной клик ОТКРЫВАЕТ шаблон, а не печатает: там же, где в приложении открываются
    /// файлы параметров, паспортов и прошивок. Печать — кнопка: отправить лист на принтер случайным
    /// двойным кликом по списку нельзя.</summary>
    private void FilesGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (DataGridClickGuard.IsOverDataRow(e) && FilesGrid.SelectedItem is Row row)
            PrintableDocActions.Open(row.Path);
    }

    /// <summary>Печать через ассоциацию файла (verb «print») — тем же способом, что печатается PDF
    /// инструкции и паспорта. Своего рендера у наклейки нет и не нужно: шаблон уже свёрстан под
    /// печать тем, кто его сделал.</summary>
    private void PrintSelected()
    {
        if (SelectedPath() is not { } path) return;
        if (!File.Exists(path))
        {
            AppMessageBox.Show($"Файл не найден:\n{path}", "Наклейки", MessageBoxButton.OK, MessageBoxImage.Warning);
            Refresh();
            return;
        }
        PrintableDocActions.Print(path);
        _host.ShowStatus($"Наклейка отправлена на печать: {Path.GetFileName(path)}");
    }

    /// <summary>Загрузка шаблона — обычное копирование в общую папку, без записи в базу: у наклеек
    /// её и нет.</summary>
    private void Upload_Click(object sender, RoutedEventArgs e)
    {
        if (_folder is null)
        {
            AppMessageBox.Show("Сетевой диск не настроен — класть шаблон некуда.", "Наклейки",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Выберите файлы шаблонов наклеек",
            Multiselect = true,
            Filter = "Документы и картинки (*.pdf;*.docx;*.doc;*.png;*.jpg)|*.pdf;*.docx;*.doc;*.png;*.jpg|Все файлы (*.*)|*.*",
        };
        if (dlg.ShowDialog() != true) return;

        var target = _folder;

        var copied = 0;
        var errors = new List<string>();
        foreach (var source in dlg.FileNames)
        {
            try
            {
                Directory.CreateDirectory(target);
                var dst = Path.Combine(target, Path.GetFileName(source));
                if (File.Exists(dst))
                {
                    var answer = AppMessageBox.Show(
                        $"«{Path.GetFileName(source)}» в этой папке уже есть. Заменить?",
                        "Наклейки", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No);
                    if (answer != MessageBoxResult.Yes) continue;
                }
                File.Copy(source, dst, overwrite: true);
                copied++;
            }
            catch (Exception ex)
            {
                errors.Add($"{Path.GetFileName(source)}: {ex.Message}");
            }
        }

        Refresh();
        if (errors.Count > 0)
            AppMessageBox.Show(string.Join("\n", errors), "Наклейки", MessageBoxButton.OK, MessageBoxImage.Warning);
        if (copied > 0)
            _host.ShowStatus(copied == 1
                ? "Шаблон наклейки загружен — он уже виден коллегам"
                : $"Загружено шаблонов: {copied} — они уже видны коллегам");
    }

    /// <summary>Удаление файла из общей папки — на случай «загрузил не то». С подтверждением: файл
    /// уедет у всех сразу, отменить это нельзя. Записи в базе нет, поэтому удаляется именно сам файл.</summary>
    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedPath() is not { } path) return;
        var name = Path.GetFileName(path);
        var reply = AppMessageBox.Show(
            $"Удалить файл «{name}» из общей папки наклеек?\nОн исчезнет у всех машин. Отменить нельзя.",
            "Удалить наклейку", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
        if (reply != MessageBoxResult.Yes) return;

        try
        {
            File.Delete(path);
            _host.ShowStatus($"Наклейка удалена: {name}");
        }
        catch (Exception ex)
        {
            AppMessageBox.Show($"Не удалось удалить файл:\n{ex.Message}", "Наклейки",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        Refresh();
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        if (_folder is null) return;
        try { Directory.CreateDirectory(_folder); } catch (Exception) { /* сеть недоступна — покажем как есть */ }
        PrintableDocActions.Open(_folder);
    }

    public static void ShowFor(Window? owner, AppServices services, IAppHost host)
    {
        var dlg = new StickersWindow(services, host) { Owner = owner };
        dlg.ShowDialog();
    }
}
