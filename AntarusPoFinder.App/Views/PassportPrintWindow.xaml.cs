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

/// <summary>«Сформировать паспорт»: вписать название шкафа в бланк, посмотреть, что получилось, и
/// напечатать.
///
/// Порядок работы задан Ильёй дословно: «я в настройках где-то загружу шаблон… И потом для работы я
/// буду нажимать кнопку "сформировать паспорт" — будет окно со строкой ввода названия шкафа,
/// программа подставит название в загруженный ранее шаблон, даст проверить предпросмотром файла, и
/// кнопки для редактирования или, если всё ок, для печати». Отсюда всё устройство окна: два поля,
/// одна кнопка, и только после неё — готовый лист с «Редактировать» и «Печать». Прежний вид (таблица
/// бланков с шестью равнозначными кнопками, среди которых «Печать» и «Посмотреть» стояли рядом ещё
/// до того, как что-либо сформировано) на эту просьбу не отвечал.
///
/// Точка входа в окно ОДНА — «Сформировать паспорт» в секции «ДЛЯ НАЛАДЧИКА» бокового меню; вторая,
/// контекстная, живёт на карточке найденного бланка в поиске (там название шкафа уже известно из
/// запроса). Раньше то же окно открывалось ещё из «Осмотра», «Паспортов шкафов» и Настроек, и
/// назывались все эти кнопки по-разному — отсюда и жалоба «по паспортам почему-то 2 кнопки, а не так
/// как я просил».
///
/// Три вещи, которые тут важнее всего и легко потерять:
///   • правится всегда КОПИЯ бланка во временной папке — общий бланк на диске не меняется никогда;
///   • длинное название не должно ломать вёрстку (см. <see cref="DocxNameFit"/>);
///   • печать идёт с двух сторон с переворотом относительно короткого края (см.
///     <see cref="DuplexPrinting"/>) — иначе настройки каждый раз приходилось выставлять руками.</summary>
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

    /// <summary>Готовый лист: заполненная копия бланка и собранный из неё PDF. null — ещё не
    /// формировали или сформированное устарело (поменяли название/бланк).</summary>
    private Built? _built;

    private bool _loaded;

    /// <summary>Подпапка в %TEMP% под заполненные копии — своя, чтобы «Паспорт ПЖ ПИ.pdf» бланка не
    /// затирал одноимённый PDF паспорта шкафа (см. PassportsView.PdfTempFolder).</summary>
    private const string TempFolder = @"AntarusPassport\Бланки";

    private sealed class Row
    {
        /// <summary>Запись в базе; null — файл просто лежит в общей папке, записи о нём нет.</summary>
        public PassportTemplate? Record { get; init; }

        public string Name { get; init; } = "";
        public string Tags { get; init; } = "";

        /// <summary>Исходник Word — единственное, во что можно подставить название.</summary>
        public string? Docx { get; init; }

        /// <summary>Что печатать, если подставлять не во что (готовый PDF или иной формат).</summary>
        public string? AnyFile { get; init; }

        public string? Folder { get; init; }

        public string FileDisplay => Path.GetFileName(Docx ?? AnyFile ?? "") is { Length: > 0 } n ? n : "файл не найден";

        /// <summary>Строка в списке бланков: название и имя файла — по одному названию бывает не
        /// видно, тот ли это бланк, особенно когда рядом лежат docx и готовый PDF.</summary>
        public string Display => $"{Name}  ·  {FileDisplay}";
    }

    /// <param name="Row">Из какого бланка сделан лист.</param>
    /// <param name="Name">Название шкафа, которое подставили (пусто — печатаем бланк как есть).</param>
    /// <param name="Document">Заполненный документ Word; null — бланк был не Word.</param>
    /// <param name="Printable">Что открывать и печатать: PDF, а если собрать его нечем — сам документ.</param>
    private sealed record Built(Row Row, string Name, string? Document, string Printable);

    public PassportPrintWindow(AppServices services, IAppHost host, string? preselect = null, string? prefillName = null)
    {
        InitializeComponent();
        _services = services;
        _host = host;
        _preselect = string.IsNullOrWhiteSpace(preselect) ? null : preselect;
        NameInput.Text = (prefillName ?? "").Trim();
        Refresh();
        _loaded = true;
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
        TemplateCombo.ItemsSource = _rows;

        // Бланк, выбранный в прошлый раз, — уже выбран: чаще всего печатают один и тот же. Если окно
        // открыли с карточки конкретного бланка, выбирается он.
        var wanted = _preselect ?? _services.Cfg.PassportTemplateLast();
        TemplateCombo.SelectedItem =
            _rows.FirstOrDefault(r => r.Name.Equals(wanted, StringComparison.OrdinalIgnoreCase))
            ?? (_rows.Count == 1 ? _rows[0] : null);

        ShowHint();
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

    private void ShowHint()
    {
        var placeholder = _services.Cfg.PassportNamePlaceholder();
        HintText.Text = _folder is null
            ? "Диск не настроен — бланки брать неоткуда (Настройки → Печать)."
            : _rows.Count == 0
                ? $"В папке бланков пусто: {_folder}\nПоложите туда шаблон паспорта — он появится и у коллег."
                : $"В бланке название встаёт вместо метки {placeholder}. Печать — с двух сторон, разворот относительно короткого края.";
    }

    private Row? Selected()
    {
        if (TemplateCombo.SelectedItem is Row row) return row;
        AppMessageBox.Show(_rows.Count == 0
                ? "Бланков пока нет. Положите шаблон в общую папку (Настройки → Печать → «Открыть папку») или загрузите его на странице «Паспорта шкафов» как типовой."
                : "Выберите бланк.",
            "Сформировать паспорт", MessageBoxButton.OK, MessageBoxImage.Information);
        return null;
    }

    // ── Ввод ──────────────────────────────────────────────────────────────────

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void NameInput_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { e.Handled = true; _ = GenerateAsync(); }
    }

    /// <summary>Название или бланк поменяли — готовый лист больше не про них. Прятать блок надёжнее,
    /// чем оставить: иначе «Печать» отправила бы на бумагу прошлый шкаф.</summary>
    private void NameInput_Changed(object sender, System.Windows.Controls.TextChangedEventArgs e) => Invalidate();

    private void Template_Changed(object sender, System.Windows.Controls.SelectionChangedEventArgs e) => Invalidate();

    private void Invalidate()
    {
        if (!_loaded) return;
        _built = null;
        ResultPanel.Visibility = Visibility.Collapsed;
    }

    // ── Формирование ──────────────────────────────────────────────────────────

    private void Generate_Click(object sender, RoutedEventArgs e) => _ = GenerateAsync();

    /// <summary>Подставить название, собрать лист и показать его. Предпросмотр открывается сразу —
    /// просили именно «дать проверить»: увидеть готовый лист до печати, а не после.</summary>
    private async Task GenerateAsync()
    {
        if (await BuildAsync() is not { } built) return;

        _built = built;
        _services.Cfg.SetPassportTemplateLast(built.Row.Name);

        ResultText.Text = built.Document is null
            ? $"Готово: {built.Row.Name} — бланк не в формате Word, название вписывается ручкой.\nФайл: {Path.GetFileName(built.Printable)}"
            : built.Printable.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase)
                ? $"Готово: {built.Row.Name}{(built.Name.Length > 0 ? $" — {built.Name}" : "")}\nФайл: {Path.GetFileName(built.Printable)}"
                : $"Готово: {built.Row.Name}{(built.Name.Length > 0 ? $" — {built.Name}" : "")}\n" +
                  "PDF собрать нечем (нет Word или LibreOffice) — откроется документ Word, печатать придётся из него.";
        ResultPanel.Visibility = Visibility.Visible;

        PrintableDocActions.Open(built.Printable);
    }

    private void Preview_Click(object sender, RoutedEventArgs e)
    {
        if (_built is { } built) PrintableDocActions.Open(built.Printable);
    }

    /// <summary>«Редактировать» открывает ЗАПОЛНЕННУЮ КОПИЮ, а не сам бланк: правки нужны этому
    /// конкретному паспорту (дописать исполнение, поправить строку), а общий бланк на диске должен
    /// остаться каким был. Правится копия — значит перед печатью PDF надо пересобрать, этим
    /// занимается <see cref="PrintAsync"/>.</summary>
    private void Edit_Click(object sender, RoutedEventArgs e)
    {
        if (_built is not { } built) return;
        if (built.Document is null)
        {
            AppMessageBox.Show("Этот бланк — не документ Word, править его здесь нечем.",
                "Сформировать паспорт", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        PrintableDocActions.Open(built.Document);
        _host.ShowStatus("Паспорт открыт для правки — после сохранения нажмите «Печать», лист пересоберётся");
    }

    private void Print_Click(object sender, RoutedEventArgs e) => _ = PrintAsync();

    private async Task PrintAsync()
    {
        if (_built is not { } built) return;

        // Документ правили после того, как собрали PDF («Редактировать» → сохранили) — печатать
        // старый PDF значило бы молча выбросить правку.
        if (built.Document is not null && IsStale(built.Document, built.Printable))
        {
            var pdf = await ToPdfAsync(built.Document);
            if (pdf is not null) _built = built = built with { Printable = pdf };
        }

        var outcome = _services.Cfg.PassportDuplexShortEdge()
            ? DuplexPrinting.PrintTwoSidedShortEdge(built.Printable)
            : PrintAsIs(built.Printable);

        _host.ShowStatus(outcome.DuplexApplied
            ? $"Паспорт отправлен на печать (двусторонняя, разворот по короткому краю): {built.Row.Name}"
            : $"Паспорт отправлен на печать: {built.Row.Name} — двустороннюю печать выставить не удалось, проверьте настройки принтера");
    }

    private static DuplexPrintOutcome PrintAsIs(string path)
    {
        PrintableDocActions.Print(path);
        return new DuplexPrintOutcome(null, false);
    }

    private static bool IsStale(string document, string printable)
    {
        try
        {
            return !printable.Equals(document, StringComparison.OrdinalIgnoreCase)
                   && File.Exists(document) && File.Exists(printable)
                   && File.GetLastWriteTimeUtc(document) > File.GetLastWriteTimeUtc(printable);
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>Собирает лист: копия бланка с подставленным названием плюс PDF из неё. null — дальше
    /// идти незачем, о причине уже сказано.</summary>
    private async Task<Built?> BuildAsync()
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
                AppMessageBox.Show($"Файл бланка не найден:\n{row.Folder}", "Сформировать паспорт",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return null;
            }
            var reply = AppMessageBox.Show(
                $"Бланк «{row.Name}» — не документ Word, подставить в него название нельзя.\n\nВзять его как есть?",
                "Сформировать паспорт", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.Yes);
            return reply == MessageBoxResult.Yes ? new Built(row, "", null, row.AnyFile) : null;
        }

        if (name.Length == 0)
        {
            var reply = AppMessageBox.Show(
                $"Название шкафа не введено — в бланке останется метка {placeholder}.\n\nВсё равно продолжить?",
                "Сформировать паспорт", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No);
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
            AppMessageBox.Show($"Не удалось подготовить документ:\n{ex.Message}", "Сформировать паспорт",
                MessageBoxButton.OK, MessageBoxImage.Error);
            return null;
        }

        // Метки в бланке нет — сказать об этом СРАЗУ. Иначе на бумагу ушёл бы лист, в котором
        // название шкафа не подставлено, и это заметили бы уже у шкафа.
        if (replacements == 0 && name.Length > 0)
        {
            var reply = AppMessageBox.Show(
                $"В бланке «{row.Name}» не нашлось метки {placeholder} — подставлять название некуда.\n\n" +
                "Впишите метку в шаблон (Настройки → Печать → «Открыть папку») или поменяйте её там же.\n\n" +
                "Взять бланк как есть?",
                "Сформировать паспорт", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
            if (reply != MessageBoxResult.Yes) return null;
        }

        // PDF нужен ровно затем же, зачем инструкции и паспорту шкафа: печать через ассоциацию Word
        // открывает редактор и ждёт человека, а PDF уходит на принтер сразу. Нет конвертера —
        // остаёмся с документом Word: показать и напечатать его вручную всё равно можно.
        var pdf = await ToPdfAsync(filled);
        return new Built(row, name, filled, pdf ?? filled);
    }

    private async Task<string?> ToPdfAsync(string docx)
    {
        using (_host.BeginBusy("Готовим паспорт — открывается Word/LibreOffice, это может занять несколько секунд…"))
            return await Task.Run(() => DocxToPdfConverter.Convert(docx, Path.ChangeExtension(docx, ".pdf")));
    }

    /// <param name="preselect">Название бланка, который надо выбрать сразу — когда окно открывают с
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
