using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using AntarusPoFinder.Core.Services;

namespace AntarusPoFinder.App.Views;

/// <summary>Что оператор выбрал в папке: какой файл является прошивкой и что ещё взять рядом с ней.
/// MainFile пуст только у отменённого диалога — «ОК» без отмеченной прошивки недоступен.</summary>
public record FolderPickResult(string MainFile, List<string> ExtraFiles);

/// <summary>«Что взять из папки» — окно, заменившее собой выбор одного файла (PickFileDialog) там,
/// где источником прошивки выбрали ПАПКУ.
///
/// Разница принципиальная, и ради неё окно и появилось. PickFileDialog спрашивал, каким файлом
/// ОТКРЫВАЕТСЯ прошивка, а копировалась при этом папка целиком и под своими именами: «выбираю файл,
/// он всё равно оставшиеся файлы в папке тоже тянет, сам файл не переименовывает». Здесь ответ
/// определяет, что реально ляжет на диск: отмеченная прошивка — в «Прошивка» под именем версии,
/// отмеченные «взять» — рядом, с сохранением вложенности, остальное не едет вовсе.
///
/// Умолчание — «только прошивка»: единственный файл с родным расширением отмечен и как прошивка, и
/// как взятый, прочее снято. Служебный мусор (Thumbs.db и подобное) отмечен быть не может даже
/// кнопкой «Взять всё» — см. JunkFiles.</summary>
public partial class FolderContentsDialog : Window
{
    /// <summary>Строка таблицы. INotifyPropertyChanged нужен из-за колонки «Прошивка»: она ведёт
    /// себя как переключатель, и снятие отметки у соседней строки должно доехать до её галочки.</summary>
    private sealed class Row : INotifyPropertyChanged
    {
        private bool _isMain;
        private bool _take;

        public Row(FolderFileEntry entry)
        {
            Entry = entry;
        }

        public FolderFileEntry Entry { get; }
        public string RelativePath => Entry.RelativePath;
        public string SizeLabel => FolderUploadPick.SizeLabel(Entry.Size);
        public bool IsJunk => Entry.IsJunk;

        public string Note => Entry.IsJunk
            ? JunkFiles.Reason(Entry.RelativePath) ?? "служебный файл"
            : Entry.LooksLikeFirmware ? "похоже на файл прошивки" : "";

        public bool IsMain
        {
            get => _isMain;
            set
            {
                if (_isMain == value) return;
                _isMain = value;
                OnPropertyChanged();
                // Сама прошивка всегда едет — отдельная отметка «взять» у неё была бы ловушкой:
                // снял её, и загрузка ушла бы без единственного файла, ради которого затевалась.
                if (value) Take = true;
            }
        }

        public bool Take
        {
            get => _take;
            set
            {
                if (_take == value) return;
                _take = value;
                OnPropertyChanged();
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        private void OnPropertyChanged([CallerMemberName] string? name = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    private readonly List<Row> _rows;
    private bool _switchingMain;

    public FolderPickResult Result { get; private set; } = new("", new List<string>());

    public FolderContentsDialog(string folder, IReadOnlyCollection<string> knownExtensions, string label,
        bool takeAllByDefault = false)
    {
        InitializeComponent();
        LabelText.Text = label;
        FolderText.Text = folder;

        _rows = FolderUploadPick.List(folder, knownExtensions).Select(e => new Row(e)).ToList();
        var main = FolderUploadPick.DefaultMain(_rows.Select(r => r.Entry));
        foreach (var row in _rows)
        {
            if (takeAllByDefault && !row.IsJunk) row.Take = true;
            if (string.Equals(row.RelativePath, main, StringComparison.OrdinalIgnoreCase)) row.IsMain = true;
        }

        FilesGrid.ItemsSource = _rows;
        FilesGrid.SelectedItem = _rows.FirstOrDefault(r => r.IsMain) ?? _rows.FirstOrDefault();
        UpdateSummary();
    }

    private Row? MainRow => _rows.FirstOrDefault(r => r.IsMain);

    /// <summary>Колонка «Прошивка» — переключатель: отметив новую строку, снимаем прежнюю. Флаг
    /// нужен, чтобы снятие не вернулось сюда же вторым событием и не сняло только что поставленную.</summary>
    private void Main_Checked(object sender, RoutedEventArgs e)
    {
        if (_switchingMain) return;
        if (sender is CheckBox { DataContext: Row picked }) SetMain(picked);
    }

    /// <summary>Одна отметка «прошивка» на всю папку. Снятие у остальных строк идёт под флагом:
    /// правка привязанного свойства вернётся сюда же событием Checked/Unchecked галочки, и без флага
    /// проход снимал бы отметку, которую только что поставил.</summary>
    private void SetMain(Row picked)
    {
        _switchingMain = true;
        try
        {
            foreach (var row in _rows) row.IsMain = ReferenceEquals(row, picked);
        }
        finally { _switchingMain = false; }

        UpdateSummary();
    }

    private void FilesGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (FilesGrid.SelectedItem is Row row && !row.IsJunk) SetMain(row);
    }

    private void OnlyFirmware_Click(object sender, RoutedEventArgs e)
    {
        foreach (var row in _rows) row.Take = row.IsMain;
        UpdateSummary();
    }

    private void TakeAll_Click(object sender, RoutedEventArgs e)
    {
        foreach (var row in _rows) row.Take = !row.IsJunk;
        UpdateSummary();
    }

    private void UpdateSummary()
    {
        var main = MainRow;
        var extras = _rows.Count(r => r.Take && !r.IsMain);
        OkButton.IsEnabled = main is not null;

        SummaryText.Text = main is null
            ? _rows.Count == 0
                ? "В папке нет файлов — выбирать нечего."
                : "Отметьте файл прошивки в колонке «Прошивка»."
            : $"Прошивка: {main.RelativePath} → ляжет в «{VersionLayout.FirmwareFolderName}» под именем версии." +
              (extras > 0 ? $"  Рядом поедет файлов: {extras}." : "  Больше ничего из папки не поедет.");
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        var main = MainRow;
        if (main is null) return;
        Result = new FolderPickResult(main.RelativePath,
            _rows.Where(r => r.Take && !r.IsMain).Select(r => r.RelativePath).ToList());
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        Result = new FolderPickResult("", new List<string>());
        DialogResult = false;
    }

    /// <summary>null — оператор отменил выбор (вызывающий отменяет и саму загрузку папки) либо в
    /// папке не оказалось ни одного файла.</summary>
    public static FolderPickResult? Pick(Window? owner, string folder, IReadOnlyCollection<string> knownExtensions,
        bool takeAllByDefault, string label)
    {
        if (!Directory.Exists(folder) || ExecutableHintResolver.ListRelativeFiles(folder).Count == 0) return null;

        var dlg = new FolderContentsDialog(folder, knownExtensions, label, takeAllByDefault) { Owner = owner };
        return dlg.ShowDialog() == true ? dlg.Result : null;
    }
}
