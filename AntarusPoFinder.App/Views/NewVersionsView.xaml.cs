using System.Windows;
using System.Windows.Controls;
using AntarusPoFinder.App.Services;
using AntarusPoFinder.Core.Domain;

namespace AntarusPoFinder.App.Views;

public partial class NewVersionsView : UserControl
{
    private readonly AppServices _services;
    private readonly IAppHost _host;

    /// <summary>Строка таблицы — ОДНА ПРОШИВКА целиком, а не одна запись в базе. У прошивки,
    /// заведённой на несколько подтипов шкафа, записей несколько (см. Core/Services/ModerationQueue),
    /// и до сих пор очередь перечисляла их по одной: у наладчика, заливающего сразу на пять
    /// подтипов, список распухал до «20+» там, где у соседа было «2–3».</summary>
    private class RecentRow
    {
        public ModerationQueueItem Item { get; init; } = null!;
        public FwVersionRecord Record => Item.Primary;
        public string GroupName => Record.GroupName;
        /// <summary>Подтипы, которых касается прошивка, — перечислением. Это не украшение: по
        /// кнопке «В релиз» из модерации выйдут ВСЕ эти записи разом, и человек должен видеть
        /// объём действия до нажатия, а не узнавать о нём после.</summary>
        public string SubtypeName => Item.SubtypesDisplay;
        public string SubtypesTooltip => Item.IsMulti
            ? $"Одна прошивка на {Item.SubtypeNames.Count} {ModerationQueueItem.Plural(Item.SubtypeNames.Count)}: {Item.SubtypesFull}."
              + $"\nЗаписей в базе — {Item.RecordCount}; «В релиз» выпустит их все сразу."
            : Item.SubtypesFull;
        public string CtrlName => Record.CtrlName;
        public string VersionRaw => Record.VersionRaw;
        /// <summary>Кто залил версию. Прочерк вместо пустоты: у прошивок, заведённых до появления
        /// поля, автора нет, и пустая ячейка читалась бы как «данные не загрузились».</summary>
        public string AuthorName => string.IsNullOrWhiteSpace(Record.AuthorName) ? "—" : Record.AuthorName;
        /// <summary>Исходное название — то, как сборку зовёт программист; по нему её и ищут.</summary>
        public string SourceName => string.IsNullOrWhiteSpace(Record.SourceName) ? "—" : Record.SourceName;
        /// <summary>Описание В ОДНУ СТРОКУ.
        ///
        /// Программист пишет «что изменилось» в несколько абзацев — это правильно и так и задумано.
        /// Но в таблице перенос строки растягивал строку таблицы на всю высоту текста: у одной
        /// версии строка в три сантиметра, у соседней в сантиметр, и список переставал читаться
        /// («если описание длинное у файла на несколько строк то он растягивается поублюдски в
        /// высоту»). TextWrapping="NoWrap" от этого не спасает: он запрещает ПЕРЕНОС, а явные
        /// переводы строки всё равно остаются переводами строки.
        ///
        /// Абзацы склеиваются через « · », а не выбрасываются: в таблице нужен обзор, и видеть,
        /// что там было несколько пунктов, полезно. Полный текст — в карточке версии.</summary>
        public string Description => OneLine(Record.Description);

        private static string OneLine(string? text)
        {
            if (string.IsNullOrWhiteSpace(text)) return "";
            var parts = text.Split(NewLines, StringSplitOptions.RemoveEmptyEntries)
                .Select(p => p.Trim())
                .Where(p => p.Length > 0);
            return string.Join(" · ", parts);
        }

        /// <summary>Возврат каретки и перевод строки. Заданы кодами, а не символьными
        /// литералами: файл правится и скриптами тоже, а там обратный слэш легко теряется.</summary>
        private static readonly char[] NewLines = { (char)13, (char)10 };
        public string TagsDisplay => string.IsNullOrWhiteSpace(Record.Tags) ? "—" : Record.Tags;
        public string DateOnly => Record.UploadDate.Length >= 10 ? Record.UploadDate[..10] : Record.UploadDate;
    }

    public NewVersionsView(AppServices services, IAppHost host)
    {
        InitializeComponent();
        _services = services;
        _host = host;
        Loaded += (_, _) => LoadData();
    }

    public void RefreshIfActive() => LoadData();

    /// <summary>Откатанные и заменённые более свежей версией сюда больше не приезжают вовсе — их
    /// отсекает сам запрос (Database.GetUnreleasedFwVersionsWithNames): размечать теги у версии,
    /// которую уже не поставят, незачем, а список они забивали.</summary>
    private void LoadData()
    {
        var rows = _services.Db.GetModerationQueue();
        RecentGrid.ItemsSource = rows.Select(v => new RecentRow { Item = v }).ToList();
        // Снимок очереди — раз в сутки, молча (см. ModerationDiagnosticsReporter): причину
        // расхождения «у кого-то 2–3, у меня 20+» иначе не установить, а просить людей переписывать
        // цифры руками — верный способ не узнать ничего.
        Services.ModerationDiagnosticsReporter.ReportOnce(_services, rows.Count);
    }

    private void EditTagsButton_Click(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is RecentRow row) EditTags(row);
    }

    /// <summary>«В релиз» прямо из списка, без открытия карточки.
    ///
    /// Вывод из модерации и доставка коллегам идут тем же путём, что и из карточки (см. Release ниже):
    /// второй способ выпускать версию неизбежно разошёлся бы с первым — и решение, принятое одной
    /// кнопкой, у коллег не появлялось бы.
    ///
    /// Подтверждение остаётся: это действие видно всей конторе и отменяется не одним нажатием,
    /// а кнопка теперь стоит в строке списка, где промахнуться проще, чем в открытом окне.</summary>
    private void ReleaseButton_Click(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is not RecentRow row) return;

        var title = TitleOf(row.Item);
        // Сколько записей выпускается — в самом вопросе, а не мелким шрифтом где-то рядом: действие
        // задевает несколько подтипов шкафа сразу, и узнать об этом человек должен ДО нажатия.
        var scope = row.Item.IsMulti
            ? $"\n\nПрошивка заведена на {row.Item.SubtypeNames.Count} "
              + $"{ModerationQueueItem.Plural(row.Item.SubtypeNames.Count)}: {row.Item.SubtypesFull}."
              + $"\nИз модерации выйдут все записи этой прошивки ({row.Item.RecordCount})."
            : "";
        if (AppMessageBox.Show($"Вывести из модерации и сделать релизной?\n\n{title}{scope}",
                "Модерация прошивок", MessageBoxButton.YesNo, MessageBoxImage.Question,
                MessageBoxResult.Yes) != MessageBoxResult.Yes)
            return;

        Release(row.Item, title);
        LoadData();
    }

    private void RecentGrid_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (DataGridClickGuard.IsOverDataRow(e) && RecentGrid.SelectedItem is RecentRow row) EditTags(row);
    }

    /// <summary>Как прошивка называется в заголовках и сообщениях. Подтип берётся из ГЛАВНОЙ записи
    /// (оригинала), а не перечислением: в заголовке окна правки нужен короткий опознавательный
    /// знак, полный состав группы человек видит в самой таблице и в вопросе о выпуске.</summary>
    private static string TitleOf(ModerationQueueItem item)
    {
        var v = item.Primary;
        return $"{v.GroupName} {v.SubtypeName} {v.CtrlName} {v.VersionRaw}";
    }

    private void EditTags(RecentRow row)
    {
        var v = row.Record;
        var title = TitleOf(row.Item);
        var dlg = new EditFirmwareDialog(_services, v, title, _host) { Owner = Window.GetWindow(this) };
        if (dlg.ShowDialog() != true) return;

        EditFirmwareDialog.ApplyResult(dlg, _services, _host, v.Id!.Value);

        var release = AppMessageBox.Show(
            "Вывести версию из модерации и сделать релизной?",
            "Модерация прошивок", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.Yes) == MessageBoxResult.Yes;
        // Вместе со всеми записями-копиями этой же прошивки под другими подтипами — иначе подтип,
        // отмеченный только что в этом же диалоге, вернул бы версию в модерацию.
        var delivered = false;
        if (release) delivered = Release(row.Item, title);

        // Названием прошивки, а не голым номером версии: по «2.0.0042.0003» невозможно понять, к
        // чему относится сообщение (тикет коллеги — «не номер прошивки, а название её»).
        _host.ShowStatus(release
            ? $"Версия выведена из модерации: {title}" + (delivered ? " (отправлено коллегам)" : "")
            : $"Теги обновлены: {title}", category: NotificationCategory.FirmwareAndParams);
        LoadData();
    }

    /// <summary>Единственное место, где версия выходит из модерации. Оба пути — и кнопка в строке,
    /// и вопрос после правки карточки — ходят сюда: иначе они разошлись бы, и решение, принятое
    /// одним из них, у коллег не появлялось бы.
    ///
    /// Вместе со всеми записями-копиями этой же прошивки под другими подтипами — иначе подтип,
    /// отмеченный только что, вернул бы версию в модерацию.
    ///
    /// Узкий канал доставки (ConfigSyncService.PushFirmwareAndModerationOnly) обязателен: страница
    /// модерации доступна и наладчику, а полный экспорт — только администратору.</summary>
    private bool Release(ModerationQueueItem item, string title)
    {
        // ВСЯ группа, а не выбранная строка: наполовину выпущенная прошивка хуже невыпущенной —
        // в поиске она уже видна, а в очереди висят её же записи под другими подтипами, и
        // следующий человек не понимает, выпускать их или нет.
        _services.Db.ReleaseModerationGroup(item.Ids);
        var delivered = ConfigSyncService.RecordAndPushModeration(_services,
            _services.Db.GetModerationGroupDeliveryIds(item.Ids), _services.CurrentUserName);

        _host.ShowStatus($"Версия выведена из модерации: {title}"
            + (delivered ? " (отправлено коллегам)" : ""),
            category: NotificationCategory.FirmwareAndParams);
        return delivered;
    }
}
