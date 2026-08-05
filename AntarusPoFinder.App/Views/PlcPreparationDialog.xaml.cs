using System.Windows;
using AntarusPoFinder.Core.Loader;

namespace AntarusPoFinder.App.Views;

/// <summary>«Форматировать или нет» — вопрос перед заливкой в ПЛК. Вся смысловая часть (кого
/// спрашивать, что считать выбором по умолчанию, что писать в журнал) живёт в
/// <see cref="PlcPreparation"/> и проверяется тестами; здесь только окно.
///
/// Закрытие крестиком и Esc = отмена: молча начать необратимое форматирование, потому что человек
/// закрыл окно, недопустимо.</summary>
public partial class PlcPreparationDialog : Window
{
    public PlcPreparationAnswer Answer { get; private set; } = PlcPreparationAnswer.Cancel;

    private PlcPreparationDialog(string versionName, bool rememberedFormat)
    {
        InitializeComponent();
        QuestionText.Text = PlcPreparation.QuestionFor(versionName);

        var wasFormat = PlcPreparation.DefaultAnswer(rememberedFormat) == PlcPreparationAnswer.Format;
        RememberedText.Text = wasFormat
            ? "В прошлый раз выбирали «Форматировать и обновить ядро»."
            : "В прошлый раз выбирали «Без форматирования».";

        // Прошлый выбор — только подсветка и Enter, а не готовый ответ: окно всё равно ждёт нажатия.
        if (wasFormat) FormatBtn.IsDefault = true; else KeepBtn.IsDefault = true;
        Loaded += (_, _) => (wasFormat ? FormatBtn : KeepBtn).Focus();
    }

    private void Format_Click(object sender, RoutedEventArgs e) => Finish(PlcPreparationAnswer.Format);

    private void Keep_Click(object sender, RoutedEventArgs e) => Finish(PlcPreparationAnswer.Keep);

    private void Cancel_Click(object sender, RoutedEventArgs e) => Finish(PlcPreparationAnswer.Cancel);

    private void Finish(PlcPreparationAnswer answer)
    {
        Answer = answer;
        DialogResult = answer != PlcPreparationAnswer.Cancel;
        Close();
    }

    /// <summary>Спросить. Возвращает ответ; отмена — <see cref="PlcPreparationAnswer.Cancel"/>.</summary>
    public static PlcPreparationAnswer Ask(Window? owner, string versionName, bool rememberedFormat)
    {
        var dialog = new PlcPreparationDialog(versionName, rememberedFormat) { Owner = owner };
        dialog.ShowDialog();
        return dialog.Answer;
    }
}
