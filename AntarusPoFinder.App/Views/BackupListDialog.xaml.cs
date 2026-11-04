using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using AntarusPoFinder.Core.Services;

namespace AntarusPoFinder.App.Views;

/// <summary>«ПОКАЗАТЬ КОПИИ» — всё, что первая версия резервного копирования даёт по части
/// восстановления, и это сделано намеренно.
///
/// Соблазн был сделать «откатить одной кнопкой». Против него ровно один, но решающий довод: откат
/// кладёт базу ПОВЕРХ работающей, и если выбрана не та строка (копия позавчерашняя, копия другой
/// машины, копия до вчерашней загрузки десяти прошивок), то отменить это нечем — рабочее состояние
/// уже стёрто тем, чем его пытались спасти. Нажать такую кнопку человек может в единственный день,
/// когда у него всё горит и думать некогда; ровно тогда цена ошибки максимальна.
///
/// Поэтому здесь честный минимум: список (когда, сколько, где лежит) и «распаковать рядом» —
/// выбранная копия раскладывается в отдельную папку, папка открывается, и дальше человек смотрит
/// глазами и решает сам. Порядок восстановления руками лежит в самом архиве (manifest.json,
/// см. BackupArchive.RestoreHint) и подробно — в docs/backups.md.</summary>
public partial class BackupListDialog : Window
{
    private readonly AppServices _services;
    private readonly string _machine = Environment.MachineName;
    private readonly BackupService _backups = new();

    public BackupListDialog(AppServices services)
    {
        InitializeComponent();
        _services = services;
        Loaded += async (_, _) => await LoadAsync();
    }

    private async System.Threading.Tasks.Task LoadAsync()
    {
        var folder = _services.Cfg.BackupFolder();
        var local = BackupService.ListFolder(folder, _machine);

        // Папку показываем сразу, не дожидаясь сети: бакет может молчать минутами, а копии в папке
        // лежат здесь и сейчас — и чаще всего спрашивают именно про них.
        var rows = new List<BackupEntry>(local);
        CopiesGrid.ItemsSource = rows;
        WhereText.Text = $"Папка: {BackupService.MachineFolder(folder, _machine)}";
        StatusText.Text = local.Count == 0
            ? "Копий пока нет. Первая снимется автоматически в течение суток после включения — или сразу, кнопкой «Сделать копию сейчас» на странице «Сетевые диски»."
            : $"Копий в папке: {local.Count}.";

        var s3 = _services.Cfg.S3();
        if (!_services.Cfg.BackupToS3() || !s3.CanPublish) return;

        StatusText.Text += " Смотрю в хранилище…";
        var cloud = await _backups.ListS3Async(s3, _machine);
        rows.AddRange(cloud);
        CopiesGrid.ItemsSource = null;
        CopiesGrid.ItemsSource = rows.OrderByDescending(r => r.TakenUtc).ToList();
        StatusText.Text = $"Копий в папке: {local.Count}; в хранилище: {cloud.Count} (все зашифрованы).";
    }

    private BackupEntry? Selected => CopiesGrid.SelectedItem as BackupEntry;

    private void CopiesGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e) => Extract();

    private void Extract_Click(object sender, RoutedEventArgs e) => Extract();

    private async void Extract()
    {
        var entry = Selected;
        if (entry is null)
        {
            StatusText.Text = "Сначала выберите копию в списке.";
            return;
        }

        var file = entry.Location;
        if (entry.InCloud)
        {
            StatusText.Text = "Скачиваю копию из хранилища…";
            var (downloaded, error) = await _backups.DownloadAsync(_services.Cfg.S3(), entry.S3Key!);
            if (downloaded is null)
            {
                StatusText.Text = "Не удалось скачать копию: " + error;
                return;
            }
            file = downloaded;
        }

        var result = BackupService.ExtractBeside(file, _services.Cfg.BackupPassword());
        if (!result.Ok)
        {
            StatusText.Text = "Распаковать не удалось: " + result.Error;
            AppMessageBox.Show(result.Error ?? "Неизвестная ошибка", "Резервная копия",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        StatusText.Text = "Копия распакована: " + result.Folder;
        OpenInExplorer(result.Folder!);

        // Говорим прямо, что дальше делать: распакованная папка сама по себе ничего не исправляет,
        // и человек, нажавший «распаковать», вправе думать, что уже восстановил.
        AppMessageBox.Show(
            "Копия распакована в отдельную папку — в программе при этом ничего не изменилось.\n\n" +
            "Внутри:\n" +
            $"• {BackupArchive.DbEntryName} — база этого компьютера на момент копии;\n" +
            $"• {BackupArchive.ConfigEntryName} — общий конфиг с сетевого диска (если был доступен);\n" +
            $"• {BackupArchive.ManifestEntryName} — что это за копия и порядок восстановления руками.\n\n" +
            "Восстановление делается вручную и при закрытой программе — сперва посмотрите, то ли это, что нужно.",
            "Распаковано", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        var dir = BackupService.MachineFolder(_services.Cfg.BackupFolder(), _machine);
        Directory.CreateDirectory(dir); // копий может ещё не быть — показать пустую папку честнее, чем отказать
        OpenInExplorer(dir);
    }

    private void OpenLog_Click(object sender, RoutedEventArgs e)
    {
        if (!File.Exists(BackupService.LogPath))
        {
            StatusText.Text = "Журнал пока пуст — копий ещё не снималось.";
            return;
        }
        OpenInExplorer(BackupService.LogPath, select: true);
    }

    private static void OpenInExplorer(string path, bool select = false)
    {
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe",
                select ? $"/select,\"{path}\"" : $"\"{path}\"") { UseShellExecute = true });
        }
        catch { /* проводника нет или путь исчез — молча, окно и так уже открыто */ }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
