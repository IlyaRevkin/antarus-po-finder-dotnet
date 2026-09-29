using System.Windows;
using System.Windows.Controls;
using AntarusPoFinder.App.Services;
using AntarusPoFinder.Core.Domain;

namespace AntarusPoFinder.App.Views;

public partial class NewVersionsView : UserControl
{
    private readonly AppServices _services;
    private readonly IAppHost _host;

    private class RecentRow
    {
        public FwVersionRecord Record { get; init; } = null!;
        public string GroupName => Record.GroupName;
        public string SubtypeName => Record.SubtypeName;
        public string CtrlName => Record.CtrlName;
        public string VersionRaw => Record.VersionRaw;
        public string Description => Record.Description;
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
    private void LoadData() =>
        RecentGrid.ItemsSource = _services.Db.GetUnreleasedFwVersionsWithNames()
            .Select(v => new RecentRow { Record = v }).ToList();

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

        var v = row.Record;
        var title = $"{v.GroupName} {v.SubtypeName} {v.CtrlName} {v.VersionRaw}";
        if (AppMessageBox.Show($"Вывести из модерации и сделать релизной?\n\n{title}",
                "Модерация прошивок", MessageBoxButton.YesNo, MessageBoxImage.Question,
                MessageBoxResult.Yes) != MessageBoxResult.Yes)
            return;

        Release(v, title);
        LoadData();
    }

    private void RecentGrid_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (DataGridClickGuard.IsOverDataRow(e) && RecentGrid.SelectedItem is RecentRow row) EditTags(row);
    }

    private void EditTags(RecentRow row)
    {
        var v = row.Record;
        var title = $"{v.GroupName} {v.SubtypeName} {v.CtrlName} {v.VersionRaw}";
        var dlg = new EditFirmwareDialog(_services, v, title, _host) { Owner = Window.GetWindow(this) };
        if (dlg.ShowDialog() != true) return;

        EditFirmwareDialog.ApplyResult(dlg, _services, _host, v.Id!.Value);

        var release = AppMessageBox.Show(
            "Вывести версию из модерации и сделать релизной?",
            "Модерация прошивок", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.Yes) == MessageBoxResult.Yes;
        // Вместе со всеми записями-копиями этой же прошивки под другими подтипами — иначе подтип,
        // отмеченный только что в этом же диалоге, вернул бы версию в модерацию.
        var delivered = false;
        if (release) delivered = Release(v, title);

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
    private bool Release(FwVersionRecord v, string title)
    {
        _services.Db.MarkFwVersionReleasedWithLinked(v.Id!.Value);
        var delivered = ConfigSyncService.RecordAndPushModeration(_services,
            _services.Db.GetFwVersionIdsSharingFiles(v.Id!.Value), _services.CurrentUserName);

        _host.ShowStatus($"Версия выведена из модерации: {title}"
            + (delivered ? " (отправлено коллегам)" : ""),
            category: NotificationCategory.FirmwareAndParams);
        return delivered;
    }
}
