using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using AntarusPoFinder.Core.Domain;

namespace AntarusPoFinder.App.Views;

/// <summary>РАССЫЛКА НА ОДИН АДРЕС: кому и что именно.
///
/// Жалобы Ильи 15.10.2026: «надо сделать чтобы выбор что получать на почту не выпадающим списком
/// было где можно что-то одно выбрать, а что-то конкретное» и «нужно добавить для почт что заведены
/// редакт рассылки что получать и почты в целом».
///
/// До этого рассылка настраивалась построчно: выпадающий список с ОДНОЙ категорией плюс адрес, и
/// чтобы прислать человеку три вида событий, заводились три строки. Поправить заведённое было
/// нельзя вовсе — только удалить и набрать заново, вместе с адресом.
///
/// Формат хранения при этом не изменился: правила так и ездят в общем конфиге по одной категории на
/// строку (см. EmailRouting.ToSubscriptions/ToRules) — старые машины читают их как раньше.</summary>
public partial class EmailSubscriptionDialog : Window
{
    private readonly List<(string Id, CheckBox Box)> _boxes = new();

    /// <summary>Что получилось. null — отменили.</summary>
    public EmailRouting.EmailSubscription? Result { get; private set; }

    private EmailSubscriptionDialog(EmailRouting.EmailSubscription? existing)
    {
        InitializeComponent();

        foreach (var (category, label) in NotificationCategoryInfo.All)
        {
            var box = new CheckBox { Content = label, Margin = new Thickness(0, 0, 0, 6) };
            _boxes.Add((category.ToString(), box));
            CategoriesPanel.Children.Add(box);
        }

        RecipientInput.Text = existing?.Recipient ?? "";
        EverythingCheck.IsChecked = existing is null || existing.Everything;
        if (existing is not null && !existing.Everything)
            foreach (var (id, box) in _boxes)
                box.IsChecked = existing.Categories.Contains(id, System.StringComparer.OrdinalIgnoreCase);

        ApplyEverything();
        Loaded += (_, _) => RecipientInput.Focus();
    }

    /// <summary>«Всё подряд» и перечисление — взаимоисключающие: галочка «всё» и так перебивает
    /// остальные (см. EmailRouting.ToSubscriptions), и оставлять их доступными значило бы показывать
    /// выбор, который ни на что не влияет.</summary>
    private void Everything_Changed(object sender, RoutedEventArgs e) => ApplyEverything();

    private void ApplyEverything()
    {
        var all = EverythingCheck.IsChecked == true;
        CategoriesPanel.IsEnabled = !all;
        CategoriesPanel.Opacity = all ? 0.5 : 1.0;
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var to = RecipientInput.Text.Trim();
        if (to.Length == 0)
        {
            Fail("Впишите адрес или название группы AD.");
            return;
        }

        var everything = EverythingCheck.IsChecked == true;
        var categories = everything
            ? new List<string>()
            : _boxes.Where(b => b.Box.IsChecked == true).Select(b => b.Id).ToList();

        // Адрес без единой галочки писем не получит никогда — это не настройка, а молчаливая
        // пустышка в списке. Лучше сказать сразу, чем человек узнает об этом через неделю без писем.
        if (!everything && categories.Count == 0)
        {
            Fail("Отметьте, что присылать на этот адрес, — иначе писем не будет вовсе.");
            return;
        }

        Result = new EmailRouting.EmailSubscription(to, categories, Enabled: true);
        DialogResult = true;
        Close();
    }

    private void Fail(string message)
    {
        ErrorText.Text = message;
        ErrorText.Visibility = Visibility.Visible;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    /// <summary>Завести новую рассылку или поправить существующую. null на входе — новая.</summary>
    public static EmailRouting.EmailSubscription? Edit(Window? owner, EmailRouting.EmailSubscription? existing = null)
    {
        var dialog = new EmailSubscriptionDialog(existing) { Owner = owner };
        return dialog.ShowDialog() == true ? dialog.Result : null;
    }
}
