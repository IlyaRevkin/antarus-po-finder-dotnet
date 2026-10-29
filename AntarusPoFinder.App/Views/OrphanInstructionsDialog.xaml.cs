using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using AntarusPoFinder.Core.Domain;
using AntarusPoFinder.Core.Services;

namespace AntarusPoFinder.App.Views;

/// <summary>Окно «потерянные инструкции»: документ на диске есть, а версии прошивки, которой он
/// принадлежал, в базе нет. Разбор и правила — в <see cref="OrphanInstructions"/>, здесь только
/// показ и один щелчок «Привязать».
///
/// <b>Почему по кнопке «Проверить», а не при открытии.</b> Обход идёт по сетевой шаре: один листинг
/// на каждый шкаф плюс по одному на каждую незнакомую папку версии. На рабочем диске это ощутимые
/// секунды, и делать их при каждом открытии окна значит получить «настройки подвисают» — жалобу, уже
/// однажды полученную за синхронную выкладку на хостинг.
///
/// <b>Почему привязка сразу, а не по «Сохранить».</b> Здесь нет формы, которую можно отменить:
/// каждая строка — отдельное самостоятельное решение по отдельному документу, и копить их до общей
/// кнопки значит заставлять человека помнить, что он уже выбрал. Строка после привязки исчезает —
/// это и есть подтверждение.</summary>
public partial class OrphanInstructionsDialog : Window
{
    private readonly AppServices _services;
    private readonly IAppHost? _host;

    public OrphanInstructionsDialog(AppServices services, IAppHost? host = null)
    {
        InitializeComponent();
        _services = services;
        _host = host;
        StatusLabel.Text = "Проверка не запускалась.";
    }

    private async void Scan_Click(object sender, RoutedEventArgs e)
    {
        var root = _services.Cfg.RootPath();
        if (string.IsNullOrWhiteSpace(root))
        {
            StatusLabel.Text = "Диск прошивок не задан — проверять нечего (Настройки → Сетевые диски).";
            return;
        }

        ScanBtn.IsEnabled = false;
        StatusLabel.Text = "Смотрим диск…";
        try
        {
            // Обход уводится с потока интерфейса целиком: это сетевая работа, а неподвижное окно
            // человек читает как зависание независимо от того, сколько оно на самом деле длится.
            var found = await Task.Run(() => OrphanInstructions.Find(_services.Db, root));
            Show(found);
        }
        finally
        {
            ScanBtn.IsEnabled = true;
        }
    }

    private void Show(IReadOnlyList<OrphanInstruction> found)
    {
        FindingsPanel.Children.Clear();
        StatusLabel.Text = found.Count == 0
            ? "Потерянных инструкций не нашлось."
            : $"Найдено документов без своей версии: {found.Count}.";

        foreach (var orphan in found) FindingsPanel.Children.Add(BuildRow(orphan));
    }

    private UIElement BuildRow(OrphanInstruction orphan)
    {
        var box = new StackPanel { Margin = new Thickness(0, 4, 0, 10) };

        box.Children.Add(new TextBlock
        {
            Text = $"{orphan.CabinetLabel} — версия {orphan.VersionRaw}",
            FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
        });
        box.Children.Add(new TextBlock
        {
            Text = Path.GetFileName(orphan.Document),
            Style = (Style)FindResource("MutedText"),
            TextWrapping = TextWrapping.Wrap,
            ToolTip = orphan.Document,
        });

        if (orphan.Hopeless)
        {
            // Привязывать не к чему: у шкафа нет ни одной живой версии. Строку всё равно показываем —
            // «документ висит в воздухе» это ответ, и знать о нём полезнее, чем не знать.
            box.Children.Add(new TextBlock
            {
                Text = "У этого шкафа нет ни одной живой версии — привязать не к чему.",
                Style = (Style)FindResource("MutedText"),
                TextWrapping = TextWrapping.Wrap,
            });
            return box;
        }

        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
        var combo = new ComboBox { Width = 380, Height = 30, SelectedIndex = 0 };
        foreach (var candidate in orphan.Candidates) combo.Items.Add(candidate.Label);

        var apply = new Button { Content = "Привязать", Width = 120, Height = 30, Margin = new Thickness(8, 0, 0, 0) };
        apply.Click += (_, _) => Link(orphan, orphan.Candidates[combo.SelectedIndex < 0 ? 0 : combo.SelectedIndex], box);

        row.Children.Add(combo);
        row.Children.Add(apply);
        box.Children.Add(row);
        return box;
    }

    private void Link(OrphanInstruction orphan, OrphanCandidate candidate, UIElement row)
    {
        var result = OrphanInstructions.Link(_services.Db, _services.Cfg.RootPath(), orphan, candidate.Version,
            _services.Publisher());

        if (result.Warnings.Count > 0)
            AppMessageBox.Show(string.Join("\n", result.Warnings), "Потерянные инструкции",
                MessageBoxButton.OK, MessageBoxImage.Warning);

        if (string.IsNullOrEmpty(result.StoredPath)) return;

        FindingsPanel.Children.Remove(row);
        var replaced = result.ReplacedExisting
            ? " Прежняя инструкция этой версии убрана в «Прежние редакции»."
            : "";
        _host?.ShowStatus($"Инструкция привязана к версии {candidate.Version.VersionRaw}.{replaced}",
            category: NotificationCategory.FirmwareAndParams);
        StatusLabel.Text = $"Привязано: {candidate.Version.VersionRaw}.{replaced}";
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
