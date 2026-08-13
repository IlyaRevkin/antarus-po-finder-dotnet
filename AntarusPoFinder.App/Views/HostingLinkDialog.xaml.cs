using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Windows;
using AntarusPoFinder.Core.Services;

namespace AntarusPoFinder.App.Views;

/// <summary>Правка ссылки на документ вручную — из списка «Хранилища», по строке.
///
/// Просьба Ильи от 13.08.2026: «возможность подправить ссылку вручную». Адрес на хостинге не хранится
/// нигде отдельно: он ПОВТОРЯЕТ путь документа на диске (см. <see cref="LabelLinkBuilder"/> и
/// <see cref="S3Settings.KeyFor"/>) — тот же путь уходит и в QR на наклейке. Поэтому «поправить
/// ссылку» здесь означает «сказать, какой файл считать документом этой версии», а не вписать адрес
/// строкой: адрес, разошедшийся с тем, что лежит на диске, повёл бы наклейку в пустоту — ровно то,
/// от чего эта страница и заводилась.
///
/// Второе — и главное — назначение диалога: РАЗВЕСТИ общий документ. Прошивка, привязанная к
/// нескольким подтипам шкафа (<see cref="FirmwareSubtypeLinkService"/>), лежит на диске один раз, в
/// папке основного подтипа, и документ у всех записей общий: у «ПЖ / FD / SMH5» адрес ведёт в
/// «ПЖ / 2.0 / SMH5», и приложенное руководство оказывается общим на оба шкафа. Здесь такую запись
/// можно отметить одну и дать ей собственный документ.</summary>
public partial class HostingLinkDialog : Window
{
    /// <summary>Одна запись fw_versions, которой принадлежит документ. Правится та, что отмечена.</summary>
    public sealed class Target : INotifyPropertyChanged
    {
        private bool _selected;

        public required int Id { get; init; }
        public required string Label { get; init; }

        public bool Selected
        {
            get => _selected;
            set { _selected = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Selected))); }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
    }

    private readonly ObservableCollection<Target> _targets = new();
    private readonly S3Settings _settings;
    private readonly string _diskRoot;

    /// <summary>Что применить: путь к документу (пустая строка — «убрать ссылку») и записи, к которым
    /// он относится. Читается вызывающим после <see cref="Window.ShowDialog"/> = true.</summary>
    public string ChosenPath { get; private set; } = "";

    public IReadOnlyList<int> ChosenIds { get; private set; } = Array.Empty<int>();

    public HostingLinkDialog(HostingItem item, IReadOnlyList<(int Id, string Label)> targets,
        S3Settings settings, string diskRoot)
    {
        _settings = settings;
        _diskRoot = diskRoot;
        InitializeComponent();

        HeaderText.Text = $"{item.Where} · версия {item.VersionRaw}\n" +
                          (string.IsNullOrEmpty(item.SourcePath)
                              ? "Документа на диске сейчас нет."
                              : $"Сейчас: {item.SourcePath}");

        PathInput.Text = item.SourcePath;

        foreach (var (id, label) in targets)
            _targets.Add(new Target { Id = id, Label = label, Selected = true });
        TargetsList.ItemsSource = _targets;

        var shared = _targets.Count > 1;
        TargetsHeader.Text = shared
            ? "Этот документ общий: одна и та же прошивка привязана к нескольким подтипам шкафа, и файл " +
              "у них один на всех. Отметьте те записи, к которым относится выбранный документ, — снятая " +
              "галочка означает «у этой версии документ остаётся прежним»."
            : "Документ относится к этой версии.";
        TargetsList.Visibility = shared ? Visibility.Visible : Visibility.Collapsed;

        UpdatePreview();
    }

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Выбрать документ инструкции",
            Filter = "Документы (*.pdf;*.doc;*.docx)|*.pdf;*.doc;*.docx|Все файлы (*.*)|*.*",
        };
        try
        {
            var current = PathInput.Text.Trim();
            if (current.Length > 0 && File.Exists(current))
                dialog.InitialDirectory = Path.GetDirectoryName(current);
            else if (_diskRoot.Length > 0 && Directory.Exists(_diskRoot))
                dialog.InitialDirectory = _diskRoot;
        }
        catch (Exception) { /* недоступная папка — просто откроем диалог как есть */ }

        if (dialog.ShowDialog(this) == true) PathInput.Text = dialog.FileName;
    }

    private void Clear_Click(object sender, RoutedEventArgs e) => PathInput.Text = "";

    private void Path_Changed(object sender, RoutedEventArgs e) => UpdatePreview();

    /// <summary>Каким станет адрес. Считается ровно тем же кодом, что и настоящий адрес выкладки, —
    /// иначе предпросмотр обещал бы одно, а на хостинг ушло бы другое.</summary>
    private void UpdatePreview()
    {
        var path = PathInput.Text.Trim();
        if (path.Length == 0)
        {
            PreviewText.Text = "Ссылка на файл будет убрана: документ снова будут искать в папке " +
                               "«Инструкция» этой версии, а адрес считать от него.";
            return;
        }

        var exists = SafeExists(path);
        var relative = LabelLinkBuilder.RelativeTo(_diskRoot, path);
        if (relative is null)
        {
            PreviewText.Text = "Этот файл лежит ВНЕ диска прошивок, поэтому адреса на хостинге у него не " +
                               "будет — и QR-коду на наклейке вести станет некуда. Так делать можно только " +
                               "осознанно." + (exists ? "" : " Вдобавок файла по этому пути нет.");
            return;
        }

        var key = _settings.KeyFor(InstructionPublisher.AsPublishedName(relative));
        PreviewText.Text = (exists ? "" : "Файла по этому пути сейчас нет. ") +
                           $"Адрес станет: {S3Client.PublicUrl(_settings, key)}";
    }

    private static bool SafeExists(string path)
    {
        try { return File.Exists(path) || Directory.Exists(path); }
        catch (Exception) { return false; }
    }

    private void Apply_Click(object sender, RoutedEventArgs e)
    {
        var ids = _targets.Where(t => t.Selected).Select(t => t.Id).ToList();
        if (ids.Count == 0)
        {
            AppMessageBox.Show("Отметьте хотя бы одну версию — иначе менять нечего.", "Документ и ссылка",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        ChosenPath = PathInput.Text.Trim();
        ChosenIds = ids;
        DialogResult = true;
    }
}
