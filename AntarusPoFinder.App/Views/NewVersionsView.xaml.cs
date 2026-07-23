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
        public bool IsRolledBack => Record.Status == "rolled_back";
        public bool IsSuperseded { get; init; }
        public string StatusLabel => IsRolledBack ? "Откатана" : IsSuperseded ? "Заменена" : "Текущая";
    }

    public NewVersionsView(AppServices services, IAppHost host)
    {
        InitializeComponent();
        _services = services;
        _host = host;
        Loaded += (_, _) => LoadData();
    }

    public void RefreshIfActive() => LoadData();

    private void LoadData()
    {
        var data = _services.Db.GetUnreleasedFwVersionsWithNames();
        RecentGrid.ItemsSource = data.Select(v => new RecentRow
        {
            Record = v,
            IsSuperseded = v.Status != "rolled_back" &&
                _services.Db.GetLastActiveFwVersion(v.SubtypeId, v.ControllerId, v.HwVersion)?.Id != v.Id,
        }).ToList();
    }

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
        var dlg = new EditFirmwareDialog(_services.Db, v, title) { Owner = Window.GetWindow(this) };
        if (dlg.ShowDialog() != true) return;

        _services.Db.UpdateFwVersion(v.Id!.Value, dlg.ResultDescription, dlg.ResultTags, dlg.ResultLaunchTypes,
            dlg.ResultHmiExecutableHint, dlg.ResultExecutableHint);

        var release = AppMessageBox.Show(
            "Вывести версию из модерации и сделать релизной?",
            "Модерация тегов", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.Yes) == MessageBoxResult.Yes;
        if (release) _services.Db.MarkFwVersionReleased(v.Id!.Value);

        _host.ShowStatus(release ? $"Версия выведена из модерации: {v.VersionRaw}" : $"Теги обновлены: {v.VersionRaw}", category: NotificationCategory.FirmwareAndParams);
        LoadData();
    }
}
