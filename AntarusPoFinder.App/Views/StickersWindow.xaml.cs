using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Input;
using AntarusPoFinder.App.Services;
using AntarusPoFinder.Core.Services;

namespace AntarusPoFinder.App.Views;

/// <summary>Наклейки — маленькое окно вместо целой страницы. Просьба была ровно такая: «чтобы была
/// кнопка, чтобы не искать… и она ради такой мелочи весь функционал не занимала». Поэтому здесь нет
/// ни своей таблицы в базе, ни синхронизации, ни модерации: список файлов из общей папки (см.
/// <see cref="StickerTemplates"/>), печать и открытие. Новый шаблон появляется у всех сразу после
/// того, как его положили в папку.</summary>
public partial class StickersWindow : Window
{
    private readonly AppServices _services;
    private readonly IAppHost _host;
    private string? _folder;

    private sealed class Row
    {
        public string Path { get; init; } = "";
        public string Name => System.IO.Path.GetFileName(Path);

        /// <summary>Подпапка внутри папки наклеек — «Раздел» в таблице; пусто для файлов, лежащих
        /// прямо в корне.</summary>
        public string Folder { get; init; } = "";
        public string Changed { get; init; } = "";
    }

    public StickersWindow(AppServices services, IAppHost host)
    {
        InitializeComponent();
        _services = services;
        _host = host;
        Refresh();
    }

    private void Refresh()
    {
        _folder = StickerTemplates.FolderFor(_services.Cfg.RootPath(), _services.Cfg.StickersFolder());
        var files = StickerTemplates.List(_folder);

        var rows = new List<Row>();
        foreach (var f in files)
        {
            var sub = _folder is not null ? LabelLinkBuilder.RelativeTo(_folder, f) : null;
            rows.Add(new Row
            {
                Path = f,
                Folder = sub is not null ? (Path.GetDirectoryName(sub) ?? "") : "",
                Changed = SafeChanged(f),
            });
        }
        FilesGrid.ItemsSource = rows;

        FolderText.Text = _folder is null
            ? "Диск не настроен — папку наклеек показать неоткуда (Настройки → Печать)."
            : rows.Count > 0
                ? $"Папка: {_folder}"
                : $"Папка пуста или недоступна: {_folder}\nПоложите в неё файлы шаблонов — они появятся здесь и у коллег.";
    }

    private static string SafeChanged(string path)
    {
        try { return File.GetLastWriteTime(path).ToString("dd.MM.yyyy"); }
        catch (Exception) { return ""; }
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

    private void Open_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedPath() is { } path) PrintableDocActions.Open(path);
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        if (_folder is null) return;
        try { Directory.CreateDirectory(_folder); } catch (Exception) { /* сеть недоступна — покажем как есть */ }
        PrintableDocActions.Open(_folder);
    }

    private void Refresh_Click(object sender, RoutedEventArgs e) => Refresh();

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    public static void ShowFor(Window? owner, AppServices services, IAppHost host)
    {
        var dlg = new StickersWindow(services, host) { Owner = owner };
        dlg.ShowDialog();
    }
}
