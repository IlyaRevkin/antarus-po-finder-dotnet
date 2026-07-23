using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using AntarusPoFinder.Core.Data;

namespace AntarusPoFinder.App.Views;

/// <summary>Задача 1 (эталонная синхронизация) — предпросмотр разницы ПЕРЕД «Сделать это состояние
/// эталонным для всех» (см. NetworkSyncView.PushAuthoritative_Click). Список — приближение, не точный
/// прогноз: сравнение идёт по именам между локальным справочником этой машины и тем, что СЕЙЧАС
/// лежит в общем конфиге на диске (см. Database.PreviewAuthoritativeDiff/ConfigSyncService.
/// ReadCurrentDiskHierarchyAsync за тем, почему — эта машина не видит чужие базы данных вообще).
/// То, что здесь показано как «удалится», на конкретной чужой машине может быть пропущено мягким
/// FK-предохранителем (см. ImportHierarchyDataCore) — запись, которую там ещё использует локальная
/// прошивка или файл параметров, не удаляется.</summary>
public partial class AuthoritativeDiffDialog : Window
{
    /// <summary>true — оператор нажал «Подтвердить и отправить», false — «Отмена» или закрытие окна.</summary>
    public bool Confirmed { get; private set; }

    public AuthoritativeDiffDialog(AuthoritativeSyncDiff diff)
    {
        InitializeComponent();
        Build(diff);
    }

    private void Build(AuthoritativeSyncDiff diff)
    {
        var categories = diff.Categories.Where(c => c.HasChanges).ToList();
        NoChangesText.Visibility = categories.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        var borderBrush = (Brush)FindResource("BorderBrush2");
        var cardBackground = (Brush)FindResource("BgSidebarBrush");
        var addedBrush = (Brush)FindResource("SuccessBrush");
        var removedBrush = (Brush)FindResource("ErrorBrush");

        foreach (var cat in categories)
        {
            var stack = new StackPanel();
            stack.Children.Add(new TextBlock
            {
                Text = $"{cat.Label} (добавится: {cat.Added.Count}, удалится: {cat.Removed.Count})",
                FontWeight = FontWeights.Bold,
                Margin = new Thickness(0, 0, 0, 6),
            });

            if (cat.Added.Count > 0)
                stack.Children.Add(MakeList("+ ", cat.Added, addedBrush));
            if (cat.Removed.Count > 0)
                stack.Children.Add(MakeList("− ", cat.Removed, removedBrush));

            CategoriesPanel.Children.Add(new Border
            {
                BorderBrush = borderBrush,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(12),
                Margin = new Thickness(0, 0, 0, 12),
                Background = cardBackground,
                Child = stack,
            });
        }
    }

    private static TextBlock MakeList(string prefix, IReadOnlyList<string> names, Brush color) => new()
    {
        Text = string.Join("\n", names.Select(n => prefix + n)),
        Foreground = color,
        TextWrapping = TextWrapping.Wrap,
        Margin = new Thickness(0, 0, 0, 4),
    };

    private void Confirm_Click(object sender, RoutedEventArgs e)
    {
        Confirmed = true;
        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        Confirmed = false;
        DialogResult = false;
        Close();
    }
}
