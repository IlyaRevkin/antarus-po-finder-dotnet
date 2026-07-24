using System.Linq;
using System.Windows;
using System.Windows.Controls;
using AntarusPoFinder.Core.Domain;
using AntarusPoFinder.Core.Services;

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

    private void RecentGrid_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (DataGridClickGuard.IsOverDataRow(e) && RecentGrid.SelectedItem is RecentRow row) EditTags(row);
    }

    private void EditTags(RecentRow row)
    {
        var v = row.Record;
        var title = $"{v.GroupName} {v.SubtypeName} {v.CtrlName} {v.VersionRaw}";
        var dlg = new EditFirmwareDialog(_services, v, title) { Owner = Window.GetWindow(this) };
        if (dlg.ShowDialog() != true) return;

        _services.Db.UpdateFwVersion(v.Id!.Value, dlg.ResultDescription, dlg.ResultTags, dlg.ResultLaunchTypes,
            dlg.ResultHmiExecutableHint, dlg.ResultExecutableHint);
        EditFirmwareDialog.ReportChanges(dlg, _host);

        var release = AppMessageBox.Show(
            "Вывести версию из модерации и сделать релизной?",
            "Модерация прошивок", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.Yes) == MessageBoxResult.Yes;
        // Вместе со всеми записями-копиями этой же прошивки под другими подтипами — иначе подтип,
        // отмеченный только что в этом же диалоге, вернул бы версию в модерацию.
        if (release) _services.Db.MarkFwVersionReleasedWithLinked(v.Id!.Value);

        _host.ShowStatus(release ? $"Версия выведена из модерации: {v.VersionRaw}" : $"Теги обновлены: {v.VersionRaw}", category: NotificationCategory.FirmwareAndParams);
        LoadData();
    }
}
