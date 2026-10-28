using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using AntarusPoFinder.Core.Domain;
using AntarusPoFinder.Core.Services;

namespace AntarusPoFinder.App.Views;

/// <summary>Кому положить выбранный документ-инструкцию: список версий шкафа с галочками и готовые
/// наборы — «только эта», «эта и более ранние», «все версии шкафа».
///
/// <b>Зачем окно вообще.</b> Инструкция описывает ШКАФ, а не конкретную сборку программы: нашли в
/// ней ошибку — исправленная нужна всем версиям сразу, а прикладывать её приходилось по одной,
/// открывая модерацию столько раз, сколько у шкафа версий. Слова Ильи: «вдруг ошибку нашли, а QR
/// ведёт на старую с ошибкой и нужно перезалить новую без ошибки… мб галочки применить инструкцию ко
/// всем прошивкам более ранней версии».
///
/// <b>Галочка «ко всем более ранним» по умолчанию СНЯТА.</b> Это решение, а не недосмотр: замена
/// идёт по общему диску и по общему бакету, отменить её нечем (прежняя редакция сохраняется, но
/// возвращать её придётся руками), и переписать десяток версий одним кликом должно быть осознанным
/// действием. Лишняя галочка стоит одного щелчка, тихая перезапись — вечера разбирательств.
///
/// <b>Сама версия отмечена и заблокирована.</b> Ей документ кладёт обычный путь сохранения
/// модерации (см. <see cref="EditFirmwareDialog"/>), окно только добавляет к ней соседей; показана
/// она ради честной картины «что в итоге получится», а снять её тут — значит обещать отмену,
/// которой не будет.</summary>
public partial class InstructionScopeDialog : Window
{
    private readonly List<(InstructionTarget Target, CheckBox Check)> _rows = new();

    /// <summary>Версии, которым раздать документ, — БЕЗ самой правимой версии: её обрабатывает
    /// обычное сохранение модерации, и класть документ дважды незачем.</summary>
    public List<FwVersionRecord> Selected { get; private set; } = new();

    public InstructionScopeDialog(IReadOnlyList<InstructionTarget> targets, string documentPath, string cabinetTitle)
    {
        InitializeComponent();
        TitleLabel.Text = $"{cabinetTitle}: {Path.GetFileName(documentPath)}";

        foreach (var target in targets)
        {
            var check = new CheckBox
            {
                Content = RowText(target),
                Margin = new Thickness(0, 2, 0, 2),
                IsChecked = target.IsSelf,
                // Правимая версия отмечена всегда: документ ей кладёт обычное сохранение.
                IsEnabled = !target.IsSelf,
                ToolTip = target.HasInstruction
                    ? "У версии уже есть инструкция — она уедет в «Прежние редакции», на её место ляжет выбранный документ."
                    : "У версии инструкции нет — ляжет выбранный документ.",
            };
            check.Checked += (_, _) => UpdateCount();
            check.Unchecked += (_, _) => UpdateCount();
            _rows.Add((target, check));
            ChecksPanel.Children.Add(check);
        }

        UpdateCount();
    }

    private static string RowText(InstructionTarget target)
    {
        var text = target.Label;
        if (target.IsSelf) text += " — эта версия";
        else if (target.IsEarlier) text += " — более ранняя";
        text += target.HasInstruction ? "; инструкция есть, будет заменена" : "; инструкции нет";
        return text;
    }

    /// <summary>Готовый набор «эта и более ранние». Снятая галочка возвращает набор к «только эта»:
    /// иначе она работала бы в одну сторону и снять её было бы нечем, кроме как щёлкать по версиям
    /// поштучно.</summary>
    private void Earlier_Changed(object sender, RoutedEventArgs e)
    {
        var on = EarlierCheck.IsChecked == true;
        foreach (var (target, check) in _rows)
            if (target.IsEarlier) check.IsChecked = on;
        UpdateCount();
    }

    private void OnlyThis_Click(object sender, RoutedEventArgs e)
    {
        foreach (var (target, check) in _rows) check.IsChecked = target.IsSelf;
        EarlierCheck.IsChecked = false;
        UpdateCount();
    }

    private void AllVersions_Click(object sender, RoutedEventArgs e)
    {
        foreach (var (_, check) in _rows) check.IsChecked = true;
        EarlierCheck.IsChecked = true;
        UpdateCount();
    }

    private void UpdateCount()
    {
        var others = _rows.Count(r => !r.Target.IsSelf && r.Check.IsChecked == true);
        var replaced = _rows.Count(r => !r.Target.IsSelf && r.Check.IsChecked == true && r.Target.HasInstruction);
        CountLabel.Text = others == 0
            ? "Только эта версия"
            : $"Ещё версий: {others}" + (replaced > 0 ? $", из них с заменой инструкции: {replaced}" : "");
        ApplyBtn.Content = others == 0 ? "Закрыть" : "Положить";
    }

    /// <summary>Собрать набор из отмеченных галочек. Отдельным методом от обработчика кнопки, чтобы
    /// его можно было проверить тестом: <c>DialogResult</c> задаётся только у окна, показанного как
    /// диалог, и тест, не открывая окна, на этой строке падал бы, ничего не проверив.</summary>
    public void CollectSelection() =>
        Selected = _rows.Where(r => !r.Target.IsSelf && r.Check.IsChecked == true)
            .Select(r => r.Target.Version).ToList();

    private void Apply_Click(object sender, RoutedEventArgs e)
    {
        CollectSelection();
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
