using System.Windows;
using AntarusPoFinder.Core.Services;

namespace AntarusPoFinder.App.Views;

public enum HmiRepairChoice { Cancel, OpenAnyway, Repair }

/// <summary>Что делать с проектом панели, который лежит на диске одним файлом (см.
/// HmiProjectFormat.LooksStrippedOfCompanions). Раньше на этом месте был Да/Нет-вопрос «всё равно
/// открыть?» с советом сходить в модерацию — совет не работал: у версии, где записан ОДИН файл, поле
/// «Открывать файл» указывает в папку прошивки, и что там ни выбери, открывался всё тот же пустой
/// проект. Поэтому чинить надо отсюда же, из того места, где человек на проблему наткнулся.
///
/// Путь и содержимое папки показываются намеренно: у оператора рядом с ОРИГИНАЛОМ всё на месте, и без
/// этих двух строк предупреждение выглядит ошибкой программы («я же проверил, файлы лежат») — оно
/// говорит про нашу копию на сетевом диске, а не про его папку.</summary>
public partial class HmiRepairDialog : Window
{
    public HmiRepairChoice Choice { get; private set; } = HmiRepairChoice.Cancel;

    public HmiRepairDialog(string strippedPath)
    {
        InitializeComponent();
        PathText.Text = strippedPath;
        NeighboursText.Text = "Рядом с ней: " + HmiProjectFormat.Neighbourhood(strippedPath);
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => Close();

    private void OpenAnyway_Click(object sender, RoutedEventArgs e)
    {
        Choice = HmiRepairChoice.OpenAnyway;
        Close();
    }

    private void Repair_Click(object sender, RoutedEventArgs e)
    {
        Choice = HmiRepairChoice.Repair;
        Close();
    }

    public static HmiRepairChoice Ask(Window? owner, string strippedPath)
    {
        var dlg = new HmiRepairDialog(strippedPath) { Owner = owner };
        dlg.ShowDialog();
        return dlg.Choice;
    }
}
