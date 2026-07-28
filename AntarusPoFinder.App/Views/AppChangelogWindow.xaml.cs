using System.Collections.Generic;
using System.Linq;
using System.Windows;
using AntarusPoFinder.Core.Domain;

namespace AntarusPoFinder.App.Views;

/// <summary>Постоянная история «что менялось по версиям приложения» — то же, что разовое окно «Что
/// нового» показывает при обновлении (MainWindowViewModel.CheckWhatsNewAsync), но сохранённое в
/// ConfigService.AppChangelogHistory, чтобы к нему можно было вернуться потом. Список версий слева
/// (новые сверху), тело release notes выбранной — справа.</summary>
public partial class AppChangelogWindow : Window
{
    private class Row
    {
        public AppChangelogEntry Entry { get; init; } = null!;
        public string VersionLabel => $"v{Entry.Version}";
        /// <summary>Когда обновление увидели на этой машине (не дата релиза как такового — журнал
        /// per-machine, см. AppChangelogEntry).</summary>
        public string DateLabel => Entry.SeenAt.ToString("dd.MM.yyyy");
        public string Body => Entry.Notes;
    }

    public AppChangelogWindow(List<AppChangelogEntry> history)
    {
        InitializeComponent();

        var rows = (history ?? new List<AppChangelogEntry>())
            .Select(e => new Row { Entry = e }).ToList();
        VersionsList.ItemsSource = rows;

        if (rows.Count == 0)
        {
            // Журнал ещё пуст — первая установка / ни одного зафиксированного обновления. Прячем
            // список и показываем понятное объяснение вместо пустого окна.
            Intro.Text = "Здесь появится список изменений по версиям после того, как программа " +
                         "обновится. Пока обновлений на этом компьютере не было.";
            BodyText.Text = "";
        }
        else
        {
            VersionsList.SelectedIndex = 0;
        }
    }

    private void VersionsList_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        BodyText.Text = VersionsList.SelectedItem is Row row ? row.Body : "";
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
