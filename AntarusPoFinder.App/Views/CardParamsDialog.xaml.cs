using System.Diagnostics;
using System.IO;
using System.Windows;
using AntarusPoFinder.Core.Domain;
using AntarusPoFinder.Core.Services;

namespace AntarusPoFinder.App.Views;

/// <summary>Параметры ПЧ/УПП этого шкафа — то, что открывается кнопкой «Параметры» на карточке
/// версии.
///
/// <b>Почему окно переделано.</b> Тикет наладчика от 18.11.2026: «Непонятно как добавлять новые
/// параметры для ПЧ к текущим прошивкам, в редакторе нет подходящего пункта. Нужно переходить на
/// новую систему параметров, либо добавить понятную клавишу добавления файла TXT». Он был прав:
/// <list type="bullet">
/// <item>окно показывало ТОЛЬКО старые файлы param_files — таблицы, заведённые в новой системе, с
///       карточки видно не было вовсе;</item>
/// <item>добавить что-либо отсюда было нельзя, а совет «загрузите в разделе Параметры» вёл к
///       кнопке, которой там больше нет (карточка загрузки скрыта с переходом на таблицы).</item>
/// </list>
///
/// Теперь в списке и файлы, и таблицы (их собирает <see cref="SubtypeParams"/>), а добавление
/// живёт здесь же — там, где человек уже стоит, разбираясь с этой прошивкой. Заведённая отсюда
/// таблица сама привязывается к шкафу карточки.</summary>
public partial class CardParamsDialog : Window
{
    private readonly AppServices _services;
    private readonly IAppHost _host;
    private readonly int? _subtypeId;

    /// <summary>Строка списка — либо старый файл параметров, либо таблица. Один список на оба вида
    /// намеренно: человеку нужны «параметры этого шкафа», а не два списка, из которых надо
    /// догадаться, в каком смотреть (на этом уже обожглись в разделе «Параметры»).</summary>
    private sealed class Item
    {
        public ParamFile? File { get; init; }
        public ParamTable? Table { get; init; }

        /// <summary>Папка файла, приведённая к корню ЭТОЙ машины. В базе disk_path лежит
        /// абсолютным, с буквой диска той машины, которая файл заливала, и через общий конфиг эта
        /// буква разъезжается по всем. У коллеги с диском под другой буквой (или подключённым по
        /// UNC) прошивки открывались, а параметры — нет: прошивки проходят через
        /// FirmwarePathLocalizer, а параметры шли по сырому пути из базы.</summary>
        public string Folder { get; init; } = "";

        public bool IsTable => Table is not null;

        public string Display => Table is not null
            ? $"Таблица: {Table.Name}" + (Table.Manufacturer.Length > 0 ? $" [{Table.Manufacturer}]" : "")
            : $"{File!.Filename} [{File.Manufacturer}]";
    }

    public CardParamsDialog(AppServices services, IAppHost host, int? subtypeId, string? subtypeLabel = null)
    {
        InitializeComponent();
        _services = services;
        _host = host;
        _subtypeId = subtypeId;

        HintLabel.Text = string.IsNullOrWhiteSpace(subtypeLabel)
            ? "Параметры ПЧ и УПП этого шкафа."
            : $"Параметры ПЧ и УПП: {subtypeLabel}.";
        Reload();
    }

    private void Reload()
    {
        var found = SubtypeParams.For(_services.Db, _subtypeId);
        var items = new List<Item>();
        foreach (var table in found.Tables) items.Add(new Item { Table = table });
        foreach (var file in found.Files)
            items.Add(new Item
            {
                File = file,
                Folder = FirmwarePathLocalizer.Localize(file.DiskPath, _services.Cfg.RootPath()),
            });

        FilesList.ItemsSource = items;
        if (items.Count > 0) FilesList.SelectedIndex = 0;
        // Пусто — не тупик: кнопки добавления стоят тут же, и текст говорит именно про них, а не
        // отсылает в раздел, где нужной кнопки нет.
        if (items.Count == 0)
            HintLabel.Text = "Для этого шкафа параметров пока нет. Заведите таблицу — из накопленного "
                             + "txt или пустую, — она сразу привяжется к этому шкафу.";
        RefreshButtons();
    }

    private Item? Current => FilesList.SelectedItem as Item;

    /// <summary>Кнопки файла гаснут на таблице, и наоборот: таблица не лежит файлом на диске, её
    /// нельзя ни «открыть папку», ни положить в протокол. Гасить, а не прятать — ряд кнопок,
    /// меняющий состав от строки к строке, читается как мигание.</summary>
    private void RefreshButtons()
    {
        var item = Current;
        var isFile = item is { IsTable: false };
        OpenBtn.IsEnabled = item is not null;
        FolderBtn.IsEnabled = isFile;
        ProtocolBtn.IsEnabled = isFile;
        OpenBtn.Content = item is { IsTable: true } ? "Открыть таблицу" : "Открыть";
    }

    private void FilesList_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e) =>
        RefreshButtons();

    private Item? Selected()
    {
        if (Current is { } item) return item;
        AppMessageBox.Show("Выберите строку.", "Параметры", MessageBoxButton.OK, MessageBoxImage.Information);
        return null;
    }

    /// <summary>Двойной клик открывает строку — ровно то же, что кнопка «Открыть». Клик по пустому
    /// месту списка (ниже строк) игнорируем: SelectedItem там остаётся от прошлого выделения, и
    /// открывать по нему файл — не то, что человек имел в виду.</summary>
    private void FilesList_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        var source = e.OriginalSource as DependencyObject;
        while (source is not null && source is not System.Windows.Controls.ListBoxItem)
            source = System.Windows.Media.VisualTreeHelper.GetParent(source);
        if (source is null) return;
        OpenSelected();
    }

    private void Open_Click(object sender, RoutedEventArgs e) => OpenSelected();

    private void OpenSelected()
    {
        var item = Selected();
        if (item is null) return;

        if (item.Table is { } table)
        {
            OpenTableWindow(table);
            return;
        }

        var full = Path.Combine(item.Folder, item.File!.Filename);
        if (File.Exists(full)) Process.Start(new ProcessStartInfo(full) { UseShellExecute = true });
        else if (Directory.Exists(item.Folder)) Process.Start(new ProcessStartInfo(item.Folder) { UseShellExecute = true });
        else AppMessageBox.Show($"Файл не найден:\n{full}", "Параметры", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    /// <summary>Таблица при файле открывается в своём, файловом окне — там у неё видна привязка к
    /// шкафам; свободная открывается окном свободных таблиц. Тот же выбор, что в разделе
    /// «Параметры» (ParamsView.OpenSelectedTable), и по той же причине.</summary>
    private void OpenTableWindow(ParamTable table)
    {
        var standalone = table.DiskPath.Trim().Length == 0 && table.Filename.Trim().Length == 0;
        new ParamTableWindow(_services, _host,
            standalone ? "" : table.DiskPath,
            standalone ? "" : table.Filename,
            standalone ? "" : table.Filename,
            table.Id, forSubtypeId: _subtypeId) { Owner = this }.ShowDialog();
        Reload();
    }

    /// <summary>«Добавить из файла TXT…» — то, о чём просил тикет, дословно. Окно таблиц сразу
    /// спрашивает файл: показывать после нажатия пустой список и ещё одну кнопку с тем же
    /// названием незачем.</summary>
    private void AddFromText_Click(object sender, RoutedEventArgs e) => AddTable(startImport: true);

    private void AddEmpty_Click(object sender, RoutedEventArgs e) => AddTable(startImport: false);

    private void AddTable(bool startImport)
    {
        if (!ParamTableEditing.CanEdit(_services.Cfg.CurrentRole()))
        {
            AppMessageBox.Show(
                "Заводить и править таблицы параметров может программист или администратор. "
                + "Смотреть и отбирать строки можно в любой роли.",
                "Параметры", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        // Свободная таблица (без файла) — то, как параметры заводятся теперь: «не зависим от
        // файлов». Шкаф передаётся окну, чтобы заведённая таблица привязалась к нему сама.
        new ParamTableWindow(_services, _host, "", "", "",
            forSubtypeId: _subtypeId, startImport: startImport) { Owner = this }.ShowDialog();
        Reload();
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        var item = Selected();
        if (item?.File is null) return;
        if (Directory.Exists(item.Folder)) Process.Start(new ProcessStartInfo(item.Folder) { UseShellExecute = true });
        else AppMessageBox.Show($"Папка не найдена:\n{item.Folder}", "Параметры", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private void ToProtocol_Click(object sender, RoutedEventArgs e)
    {
        var item = Selected();
        if (item?.File is null) return;
        var full = Path.Combine(item.Folder, item.File.Filename);
        if (!File.Exists(full))
        {
            AppMessageBox.Show($"Файл не найден:\n{full}", "Параметры", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var proto = _services.Cfg.Get("inspection_folder");
        if (string.IsNullOrEmpty(proto))
        {
            var dlg = new Microsoft.Win32.OpenFolderDialog { Title = "Выберите папку осмотра" };
            if (dlg.ShowDialog() != true) return;
            proto = dlg.FolderName;
            _services.Cfg.SetInspectionFolder(proto);
        }

        // InspectionDrop, не File.Copy напрямую: файл параметров мог годами лежать на сервере, и
        // его старая дата изменения перенеслась бы на копию — тогда автоочистка папки осмотра (по
        // возрасту файла) снесла бы его почти сразу после переноса. Хелпер ставит дату «сейчас».
        InspectionDrop.CopyInto(proto, full, System.DateTime.Now);
        // Не модальное окно с «ОК»: переносить файлы в протокол приходится подряд, и подтверждение
        // уже случившегося не должно требовать отдельного нажатия (см. FlashNote). Плашка уходит
        // сама — по щелчку в любом месте, при уходе фокуса с окна, при его сдвиге или по времени.
        FlashNote.Show(this, $"Скопировано в протокол: {item.File.Filename}");
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
