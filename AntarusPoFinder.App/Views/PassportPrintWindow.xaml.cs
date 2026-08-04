using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using AntarusPoFinder.App.Services;
using AntarusPoFinder.Core.Domain;
using AntarusPoFinder.Core.Services;

namespace AntarusPoFinder.App.Views;

/// <summary>Печать типового паспорта: выбрать бланк, вписать название шкафа — программа подставит
/// его в документ вместо метки и отправит на принтер.
///
/// Зачем отдельно от страницы «Паспорта шкафов»: часть бланков не ложится ни на один тип и подтип
/// (НКУ, Щит СПЛ, ШР) — их печатают под конкретный шкаф, вписывая название руками, и искать их через
/// иерархию шкафов негде. Окно, а не страница, и по той же причине, что «Наклейки»: это действие на
/// полминуты, ради него не стоит занимать раздел меню наравне с Поиском.
///
/// Печатается всегда КОПИЯ во временной папке — общий бланк на диске не меняется никогда.</summary>
public partial class PassportPrintWindow : Window
{
    private readonly AppServices _services;
    private readonly IAppHost _host;
    private string? _folder;
    private List<Row> _rows = new();

    /// <summary>Бланк, который надо выделить сразу: окно открыли не «вообще», а с карточки
    /// конкретного бланка в поиске. Перевешивает «выбранный в прошлый раз» — там привычка, здесь
    /// прямое указание оператора.</summary>
    private readonly string? _preselect;

    /// <summary>Подпапка в %TEMP% под заполненные копии — своя, чтобы «Паспорт ПЖ ПИ.pdf» бланка не
    /// затирал одноимённый PDF паспорта шкафа (см. PassportsView.PdfTempFolder).</summary>
    private const string TempFolder = @"AntarusPassport\Бланки";

    private sealed class Row
    {
        /// <summary>Запись в базе; null — файл просто лежит в общей папке, записи о нём нет.</summary>
        public PassportTemplate? Record { get; init; }

        public string Name { get; init; } = "";
        public string Tags { get; init; } = "";
        public string TagsDisplay => string.IsNullOrWhiteSpace(Tags) ? "—" : Tags;

        /// <summary>Исходник Word — единственное, во что можно подставить название.</summary>
        public string? Docx { get; init; }

        /// <summary>Что печатать, если подставлять не во что (готовый PDF или иной формат).</summary>
        public string? AnyFile { get; init; }

        public string? Folder { get; init; }

        public string FileDisplay => Path.GetFileName(Docx ?? AnyFile ?? "") is { Length: > 0 } n ? n : "файл не найден";
    }

    public PassportPrintWindow(AppServices services, IAppHost host, string? preselect = null, string? prefillName = null)
    {
        InitializeComponent();
        _services = services;
        _host = host;
        _preselect = string.IsNullOrWhiteSpace(preselect) ? null : preselect;
        NameInput.Text = (prefillName ?? "").Trim();
        Refresh();
        Loaded += (_, _) =>
        {
            NameInput.Focus();
            // Подставленное название выделяем целиком: это предположение программы, и заменить его
            // должно быть не дороже, чем принять — одна клавиша вместо стирания чужого текста.
            NameInput.SelectAll();
        };
    }

    // ── Список бланков ────────────────────────────────────────────────────────

    private void Refresh()
    {
        _folder = PassportService.TemplatesFolder(_services.Cfg.RootPath(), _services.Cfg.PassportTemplatesFolder());
        var root = _services.Cfg.RootPath();

        var rows = new List<Row>();
        foreach (var record in _services.Db.GetGeneralPassports())
        {
            var doc = PassportService.ResolveDoc(record, root, _folder);
            rows.Add(new Row
            {
                Record = record,
                Name = record.Name,
                Tags = record.Tags,
                Docx = doc.Docx,
                AnyFile = doc.Newest ?? doc.Pdf,
                Folder = doc.Folder ?? FirmwarePathLocalizer.Localize(record.DiskPath, root),
            });
        }

        // Файлы, положенные в общую папку руками, мимо загрузки — они видны здесь наравне с
        // загруженными (папка ведёт себя как папка наклеек: положил файл — он появился у всех).
        // Только верхний уровень: всё, что лежит в подпапках, — это папки самих загруженных бланков
        // с их PDF и прежними редакциями, и показывать их вторым списком значило бы двоить каждый
        // бланк.
        foreach (var file in LooseFiles(_folder))
            rows.Add(new Row
            {
                Name = Path.GetFileNameWithoutExtension(file),
                Docx = DocxTemplateFiller.IsSupported(file) ? file : null,
                AnyFile = file,
                Folder = Path.GetDirectoryName(file),
            });

        _rows = rows.OrderBy(r => r.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        ApplyFilter();

        var placeholder = _services.Cfg.PassportNamePlaceholder();
        HintText.Text = _folder is null
            ? "Диск не настроен — папку бланков показать неоткуда (Настройки → Печать)."
            : $"Папка бланков: {_folder}\nВ бланке подставляется метка {placeholder} (меняется в Настройки → Печать).";
    }

    private static List<string> LooseFiles(string? folder)
    {
        if (string.IsNullOrWhiteSpace(folder)) return new List<string>();
        try
        {
            return Directory.EnumerateFiles(folder, "*", SearchOption.TopDirectoryOnly)
                .Where(f => !DocFileResolver.IsShortcut(f))
                .ToList();
        }
        catch (Exception)
        {
            // Шара отвалилась — окно из-за этого падать не должно, покажем то, что знаем из базы.
            return new List<string>();
        }
    }

    private void ApplyFilter()
    {
        // Что выбрано сейчас — отбор сужает список, но не должен переставлять выбор оператора: он мог
        // выделить бланк, а потом дописать пару букв в отборе, чтобы до него было ближе.
        var keep = TemplatesGrid.SelectedItem as Row;
        var text = FilterInput.Text.Trim();
        var shown = text.Length == 0
            ? _rows
            : _rows.Where(r =>
                r.Name.Contains(text, StringComparison.CurrentCultureIgnoreCase) ||
                r.Tags.Contains(text, StringComparison.CurrentCultureIgnoreCase) ||
                r.FileDisplay.Contains(text, StringComparison.CurrentCultureIgnoreCase)).ToList();

        TemplatesGrid.ItemsSource = shown;
        CountLabel.Text = $"Бланков: {shown.Count}";

        // Бланк, выбранный в прошлый раз, — уже выделен: чаще всего печатают один и тот же. Если окно
        // открыли с карточки конкретного бланка, выделяется он.
        var wanted = _preselect ?? _services.Cfg.PassportTemplateLast();
        TemplatesGrid.SelectedItem =
            (keep is not null && shown.Contains(keep) ? keep : null)
            ?? shown.FirstOrDefault(r => r.Name.Equals(wanted, StringComparison.OrdinalIgnoreCase))
            ?? (shown.Count == 1 ? shown[0] : null);
    }

    private void Filter_Changed(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        // TextChanged прилетает и при разборе XAML — до того, как конструктор успел раздать поля.
        if (_services is not null) ApplyFilter();
    }

    private void Refresh_Click(object sender, RoutedEventArgs e) => Refresh();

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private Row? Selected()
    {
        if (TemplatesGrid.SelectedItem is Row row) return row;
        AppMessageBox.Show(_rows.Count == 0
                ? "Бланков пока нет. Положите шаблон в общую папку или загрузите его на странице «Паспорта шкафов» как типовой."
                : "Выберите бланк в списке.",
            "Паспорт по шаблону", MessageBoxButton.OK, MessageBoxImage.Information);
        return null;
    }

    // ── Действия ──────────────────────────────────────────────────────────────

    private void TemplatesGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        // Двойной клик ОТКРЫВАЕТ бланк, а не печатает: случайно отправить лист на принтер по клику
        // в списке нельзя (то же правило, что в «Наклейках»).
        if (DataGridClickGuard.IsOverDataRow(e) && TemplatesGrid.SelectedItem is Row row) OpenTemplate(row);
    }

    private void NameInput_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { e.Handled = true; _ = PrintAsync(); }
    }

    private void Print_Click(object sender, RoutedEventArgs e) => _ = PrintAsync();

    private void Preview_Click(object sender, RoutedEventArgs e) => _ = PreviewAsync();

    private void OpenTemplate_Click(object sender, RoutedEventArgs e)
    {
        if (Selected() is { } row) OpenTemplate(row);
    }

    private static void OpenTemplate(Row row)
    {
        var path = row.Docx ?? row.AnyFile;
        if (path is null || !File.Exists(path))
        {
            AppMessageBox.Show($"Файл бланка не найден:\n{row.Folder}", "Паспорт по шаблону",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        PrintableDocActions.Open(path);
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        if (_folder is null)
        {
            AppMessageBox.Show("Сетевой диск не настроен — открывать нечего.", "Паспорт по шаблону",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        try { Directory.CreateDirectory(_folder); } catch (Exception) { /* сеть недоступна — покажем как есть */ }
        PrintableDocActions.Open(_folder);
    }

    /// <summary>Печать: заполненная копия → PDF → принтер. PDF нужен ровно затем же, зачем инструкции
    /// и паспорту шкафа — печать через ассоциацию Word открывает редактор и ждёт человека, а PDF
    /// уходит на принтер сразу.</summary>
    private async Task PrintAsync()
    {
        if (await BuildAsync(wantPdf: true) is not { } built) return;
        PrintableDocActions.Print(built.Path);
        _services.Cfg.SetPassportTemplateLast(built.Row.Name);
        _host.ShowStatus(built.Name.Length > 0
            ? $"Паспорт отправлен на печать: {built.Row.Name} — {built.Name}"
            : $"Паспорт отправлен на печать: {built.Row.Name}");
    }

    /// <summary>«Посмотреть» — тот же заполненный документ, но открывается, а не печатается: увидеть,
    /// что подставилось, и при желании дописать что-то от руки перед печатью.</summary>
    private async Task PreviewAsync()
    {
        if (await BuildAsync(wantPdf: false) is not { } built) return;
        PrintableDocActions.Open(built.Path);
        _services.Cfg.SetPassportTemplateLast(built.Row.Name);
    }

    private sealed record Built(Row Row, string Name, string Path);

    /// <summary>Общая часть печати и просмотра: подставить название в копию бланка и, если нужно,
    /// собрать из неё PDF. null — дальше идти незачем, о причине уже сказано.</summary>
    private async Task<Built?> BuildAsync(bool wantPdf)
    {
        if (Selected() is not { } row) return null;

        var name = NameInput.Text.Trim();
        var placeholder = _services.Cfg.PassportNamePlaceholder();

        // Бланк не в формате Word (готовый PDF, старый .doc) — подставлять некуда. Это не ошибка:
        // такой бланк печатают как есть, а название вписывают ручкой.
        if (row.Docx is null)
        {
            if (row.AnyFile is null || !File.Exists(row.AnyFile))
            {
                AppMessageBox.Show($"Файл бланка не найден:\n{row.Folder}", "Паспорт по шаблону",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return null;
            }
            var reply = AppMessageBox.Show(
                $"Бланк «{row.Name}» — не документ Word, подставить в него название нельзя.\n\n" +
                (wantPdf ? "Напечатать как есть?" : "Открыть как есть?"),
                "Паспорт по шаблону", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.Yes);
            return reply == MessageBoxResult.Yes ? new Built(row, "", row.AnyFile) : null;
        }

        if (name.Length == 0)
        {
            var reply = AppMessageBox.Show(
                $"Название шкафа не введено — в бланке останется метка {placeholder}.\n\nВсё равно продолжить?",
                "Паспорт по шаблону", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No);
            if (reply != MessageBoxResult.Yes) { NameInput.Focus(); return null; }
        }

        string filled;
        int replacements;
        try
        {
            filled = Path.Combine(Path.GetTempPath(), TempFolder,
                PassportService.FolderName(name.Length > 0 ? $"{row.Name} — {name}" : row.Name) + ".docx");
            replacements = await Task.Run(() => DocxTemplateFiller.Fill(row.Docx, filled, placeholder, name));
        }
        catch (Exception ex)
        {
            AppMessageBox.Show($"Не удалось подготовить документ:\n{ex.Message}", "Паспорт по шаблону",
                MessageBoxButton.OK, MessageBoxImage.Error);
            return null;
        }

        // Метки в бланке нет — сказать об этом ДО печати. Иначе на бумагу ушёл бы лист, в котором
        // название шкафа не подставлено и это заметили бы уже у шкафа.
        if (replacements == 0 && name.Length > 0)
        {
            var reply = AppMessageBox.Show(
                $"В бланке «{row.Name}» не нашлось метки {placeholder} — подставлять название некуда.\n\n" +
                "Впишите метку в шаблон (кнопка «Открыть бланк») или поменяйте её в Настройки → Печать.\n\n" +
                (wantPdf ? "Напечатать бланк как есть?" : "Открыть бланк как есть?"),
                "Паспорт по шаблону", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
            if (reply != MessageBoxResult.Yes) return null;
        }

        if (!wantPdf) return new Built(row, name, filled);

        string? pdf;
        using (_host.BeginBusy("Готовим паспорт к печати — открывается Word/LibreOffice, это может занять несколько секунд…"))
            pdf = await Task.Run(() => DocxToPdfConverter.Convert(filled, Path.ChangeExtension(filled, ".pdf")));

        if (pdf is not null) return new Built(row, name, pdf);

        // Конвертера на машине нет — печатать нечем, но заполненный документ уже готов: предлагаем
        // открыть его и напечатать из Word руками, а не оставляем оператора ни с чем.
        var openInstead = AppMessageBox.Show(
            "Не удалось собрать PDF из документа Word.\n\nДля автоматической печати нужен установленный " +
            "Microsoft Word или LibreOffice.\n\nОткрыть заполненный документ, чтобы напечатать вручную?",
            "Паспорт по шаблону", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.Yes);
        if (openInstead == MessageBoxResult.Yes) PrintableDocActions.Open(filled);
        return null;
    }

    /// <param name="preselect">Название бланка, который надо выделить сразу — когда окно открывают с
    /// карточки конкретного бланка. null — обычное открытие «вообще».</param>
    /// <param name="prefillName">Название шкафа, уже вписанное в поле — из поискового запроса
    /// (см. PassportService.CabinetNameFromQuery). null/пусто — оператор впишет сам.</param>
    public static void ShowFor(Window? owner, AppServices services, IAppHost host, string? preselect = null,
        string? prefillName = null)
    {
        var dlg = new PassportPrintWindow(services, host, preselect, prefillName) { Owner = owner };
        dlg.ShowDialog();
    }
}
