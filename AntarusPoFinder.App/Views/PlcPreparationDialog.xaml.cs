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

    private PlcPreparationDialog(string versionName, bool rememberedFormat, string? controllerName)
    {
        InitializeComponent();
        QuestionText.Text = PlcPreparation.QuestionFor(versionName);

        // Для контроллеров, которым заливка без подготовки кирпичит ПЛК (SMH5), прошлый выбор
        // наладчика НЕ наследуется — см. PlcPreparation.NeedsFormatByDefault.
        // ДВА РАЗНЫХ ПРЕДУПРЕЖДЕНИЯ, и оба показываются ДО запуска:
        //   • про контроллер — «этому заливка без подготовки ломает ПЛК» (SMH5);
        //   • про подключение — «подготовка сейчас не сработает, Loader не на USB».
        // Второе появилось после того, как у коллеги заливка упала красной «Ошибкой» уже в процессе,
        // хотя сказать об этом можно было одной фразой заранее.
        var notes = new System.Collections.Generic.List<string>();
        var warning = PlcPreparation.WarningFor(controllerName);
        if (warning.Length > 0) notes.Add(warning);

        var obstacle = PlcPreparation.ConnectionObstacle(LoaderConnectionSettings.ReadMode());
        if (obstacle.Length > 0) notes.Add(obstacle);

        if (notes.Count > 0)
        {
            WarningText.Text = string.Join(System.Environment.NewLine + System.Environment.NewLine, notes);
            WarningBox.Visibility = Visibility.Visible;
        }

        // «Залить как есть» у SMH5 не просто не выбрано — его нет: этот путь подтверждённо кирпичит
        // заводской контроллер (см. PlcPreparation.AllowKeep). Кнопка убирается целиком, а не
        // гасится: выключенная кнопка читается как «что-то настрой, и станет можно».
        if (!PlcPreparation.AllowKeep(controllerName)) KeepBtn.Visibility = Visibility.Collapsed;

        // Loader точно не на USB — подготовка не сработает, поэтому по умолчанию предлагаем залить
        // как есть (а у SMH5, где «как есть» ломает ПЛК, выбора не остаётся вовсе: там сперва надо
        // починить подключение, и об этом прямо написано выше).
        var blocked = PlcPreparation.ConnectionBlocks(LoaderConnectionSettings.ReadMode());
        var wasFormat = !blocked
                        && PlcPreparation.DefaultAnswer(rememberedFormat, controllerName) == PlcPreparationAnswer.Format;
        RememberedText.Text = warning.Length > 0
            ? "Для этого контроллера по умолчанию предлагается подготовка, независимо от прошлого выбора."
            : wasFormat
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
    public static PlcPreparationAnswer Ask(Window? owner, string versionName, bool rememberedFormat,
        string? controllerName = null)
    {
        var dialog = new PlcPreparationDialog(versionName, rememberedFormat, controllerName) { Owner = owner };
        dialog.ShowDialog();
        return dialog.Answer;
    }
}
