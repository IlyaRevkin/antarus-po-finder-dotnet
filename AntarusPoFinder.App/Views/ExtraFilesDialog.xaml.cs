using System.Collections.Generic;
using System.Collections.Specialized;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using AntarusPoFinder.App.Services;
using AntarusPoFinder.Core.Domain;

namespace AntarusPoFinder.App.Views;

/// <summary>Доп. материалы одной версии (см. <see cref="FwAttachment"/>) — со ВЫБОРОМ ДЕЙСТВИЯ, а не
/// только «открыть». Раньше клик по пункту карточки открывал файл программой по умолчанию, и для
/// документов это верно, а для прошивки ПЛК поставщика — нет: её нужно ПОЛОЖИТЬ в среду разработки
/// контроллера, перетащив файл мышью. Открывать её нечем (среда не ассоциирована с расширением), а
/// дотянуться до файла было неоткуда — путь на диске карточка не показывала вовсе.
///
/// Поэтому здесь три пути к одному и тому же файлу: «Открыть» (как было), «Показать в папке»
/// (проводник с выделенным файлом — оттуда и перетаскивают) и перетаскивание прямо из списка, минуя
/// проводник. Окно показывается и для единственного файла: выбор действия нужен и ему, а «один файл
/// открывается сразу» как раз и было той самой поломкой.</summary>
public partial class ExtraFilesDialog : Window
{
    /// <summary>Строка списка. Путь уже приведён к корню ЭТОЙ машины (FirmwarePathLocalizer) —
    /// окно про сетевые диски ничего не знает и знать не должно.</summary>
    public class Row
    {
        public string Path { get; init; } = "";
        public string Title { get; init; } = "";
        public string Subtitle { get; init; } = "";
        public bool Exists { get; init; }

        /// <summary>Файл этого вида по двойному щелчку показывается в папке, а не открывается —
        /// см. <see cref="PrefersReveal"/>.</summary>
        public bool RevealByDefault { get; init; }
    }

    private const double DragThreshold = 6;
    private Point _mouseDownAt;
    private bool _dragCandidate;

    public ExtraFilesDialog(string header, IEnumerable<Row> rows)
    {
        InitializeComponent();
        HeaderText.Text = header;
        FilesList.ItemsSource = rows.ToList();
        FilesList.SelectedIndex = 0;
        RefreshActions();
    }

    private Row? Selected => FilesList.SelectedItem as Row;

    private void List_SelectionChanged(object sender, SelectionChangedEventArgs e) => RefreshActions();

    private void RefreshActions()
    {
        var row = Selected;
        OpenButton.IsEnabled = row is { Exists: true };
        RevealButton.IsEnabled = row is not null;
        // Кнопка по умолчанию — своя у каждого вида файла: у прошивки ПЛК это «Показать в папке»
        // (её перетаскивают), у документа — «Открыть». Так Enter делает то, что нужно этому файлу.
        var reveal = row is null || row.RevealByDefault || !row.Exists;
        OpenButton.IsDefault = !reveal;
        RevealButton.IsDefault = reveal;
        HintText.Text = row is null
            ? ""
            : row.Exists
                ? "Файл можно перетащить мышью прямо из списка — например, в программу контроллера."
                : "Файла нет на диске: возможно, его убрали мимо программы. «Показать в папке» откроет папку, где он должен лежать.";
    }

    private void List_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        var row = Selected;
        if (row is null) return;
        if (row.RevealByDefault || !row.Exists) PrintableDocActions.Reveal(row.Path);
        else OpenFile(row);
    }

    private void Open_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is { } row) OpenFile(row);
    }

    private void Reveal_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is { } row) PrintableDocActions.Reveal(row.Path);
    }

    private void Close_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    /// <summary>Расширения, при которых «Открыть» сначала спрашивает. Доп. материалы лежат на общем
    /// сетевом диске, куда пишут все, — а «Открыть» это запуск через оболочку Windows: подложенный
    /// туда .exe/.bat запустился бы по нажатию кнопки, ничего об этом не сказав. Для настоящей
    /// программы-утилиты от поставщика (такое бывает) ответ «да» остаётся в одном щелчке.</summary>
    private static readonly HashSet<string> ExecutableExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".exe", ".bat", ".cmd", ".com", ".msi", ".ps1", ".vbs", ".js", ".jse", ".wsf", ".scr", ".lnk", ".reg",
    };

    private void OpenFile(Row row)
    {
        if (!row.Exists)
        {
            AppMessageBox.Show($"Файл не найден на диске:\n{row.Path}", "Доп. материалы",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var ext = System.IO.Path.GetExtension(row.Path);
        if (ExecutableExtensions.Contains(ext))
        {
            var reply = AppMessageBox.Show(
                $"Это исполняемый файл ({ext}), открыть его — значит запустить программу:\n{row.Path}\n\nЗапустить?",
                "Доп. материалы", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
            if (reply != MessageBoxResult.Yes) return;
        }

        PrintableDocActions.Open(row.Path);
    }

    // ── Перетаскивание файла из списка ────────────────────────────────────────
    // Стандартный для WPF порог: сам факт нажатия перетаскиванием не считается, иначе обычный выбор
    // строки мышью превращался бы в drag и список нельзя было бы просто пролистать.

    private void List_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _mouseDownAt = e.GetPosition(null);
        _dragCandidate = true;
    }

    private void List_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_dragCandidate || e.LeftButton != MouseButtonState.Pressed) return;

        var now = e.GetPosition(null);
        if (Math.Abs(now.X - _mouseDownAt.X) < DragThreshold && Math.Abs(now.Y - _mouseDownAt.Y) < DragThreshold) return;

        _dragCandidate = false;
        if (Selected is not { Exists: true } row) return;

        // FileDrop — тот же формат, которым файлы отдаёт проводник, поэтому принимающая программа не
        // видит разницы между «перетащили из проводника» и «перетащили отсюда».
        var data = new DataObject();
        data.SetFileDropList(new StringCollection { row.Path });
        try { DragDrop.DoDragDrop(FilesList, data, DragDropEffects.Copy); }
        catch (Exception)
        {
            // Перетаскивание сорвалось (принимающая программа закрылась посреди операции) — это не
            // повод показывать окно с ошибкой: файл на месте, действие можно повторить.
        }
    }

    /// <summary>Показывать этот файл в папке, а не открывать: прошивку ПЛК поставщика кладут в среду
    /// разработки контроллера перетаскиванием, а не «открывают». Определяется по виду из справочника
    /// (он же может быть переименован людьми — поэтому по вхождению слова, а не точным равенством), и
    /// вторым признаком по расширению: файлы прошивок открывать нечем в любом случае.</summary>
    public static bool PrefersReveal(FwAttachment a)
    {
        if (a.Kind.Contains("прошивк", StringComparison.OrdinalIgnoreCase)
            || a.Kind.Contains("ПЛК", StringComparison.OrdinalIgnoreCase))
            return true;

        var ext = System.IO.Path.GetExtension(a.Filename);
        return FirmwareLikeExtensions.Contains(ext);
    }

    private static readonly HashSet<string> FirmwareLikeExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".lfs", ".psl", ".hex", ".bin", ".s19", ".mot", ".fw", ".plc", ".pro", ".prj", ".zip", ".rar", ".7z",
    };

    /// <summary>Показать окно для готового списка вложений. Пути приводит вызывающий (он знает корень
    /// диска этой машины), проверка существования — здесь: она одинакова для всех строк.</summary>
    public static void Show(Window? owner, string header, IEnumerable<(FwAttachment Attachment, string Path)> files)
    {
        var rows = files.Select(f => new Row
        {
            Path = f.Path,
            Title = Label(f.Attachment),
            Subtitle = f.Path,
            Exists = File.Exists(f.Path),
            RevealByDefault = PrefersReveal(f.Attachment),
        });

        var dlg = new ExtraFilesDialog(header, rows) { Owner = owner };
        dlg.ShowDialog();
    }

    /// <summary>Вид — имя файла — комментарий: именно вид и комментарий объясняют, какой из файлов
    /// сейчас нужен (см. FwAttachment).</summary>
    public static string Label(FwAttachment a)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(a.Kind)) parts.Add(a.Kind);
        parts.Add(a.Filename);
        if (!string.IsNullOrWhiteSpace(a.Comment)) parts.Add(a.Comment);
        return string.Join(" — ", parts);
    }
}
