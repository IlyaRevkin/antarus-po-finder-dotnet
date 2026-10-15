using System.Linq;

namespace AntarusPoFinder.Core.Loader;

/// <summary>Ответ наладчика на вопрос «форматировать ли контроллер перед загрузкой».</summary>
public enum PlcPreparationAnswer
{
    /// <summary>Отформатировать проект и обновить ядро, потом залить.</summary>
    Format,

    /// <summary>Залить как есть, ничего в контроллере не стирая.</summary>
    Keep,

    /// <summary>Не грузить вовсе.</summary>
    Cancel,
}

/// <summary>Вопрос про подготовку ПЛК, задаваемый ПЕРЕД загрузкой.
///
/// <b>Что было не так.</b> Форматирование управлялось галочкой внутри окна загрузки, а само окно
/// стартовало операцию само, сразу после открытия, с ЗАПОМНЕННЫМ значением этой галочки. То есть
/// решение «стереть проект в контроллере и перешить ядро» принималось за наладчика — тем, что он
/// выбрал в прошлый раз на другом объекте. Увидеть это он мог только по строке в журнале, когда
/// форматирование уже шло. Операция необратимая, спрашивать про неё постфактум нельзя.
///
/// Теперь вопрос задаётся отдельным окном ДО запуска и только для заливки: сборка .lfs к контроллеру
/// не подключается вообще, и спрашивать там не о чем. Прошлый выбор остаётся ЗНАЧЕНИЕМ ПО УМОЛЧАНИЮ
/// (какая кнопка подсвечена), а не молчаливым решением.
///
/// Логика вынесена сюда из окна, чтобы её можно было проверить тестами без WPF.</summary>
public static class PlcPreparation
{
    /// <summary>Спрашиваем только там, где ответ вообще что-то меняет.</summary>
    public static bool ShouldAsk(LoaderOperation operation) => operation == LoaderOperation.Deploy;

    /// <summary>Форматировать ли по этому ответу. Отмена — это «не грузить», а не «не форматировать»:
    /// вызывающий обязан проверить <see cref="IsCancelled"/> раньше, чем спрашивать про формат.</summary>
    public static bool FormatFor(PlcPreparationAnswer answer) => answer == PlcPreparationAnswer.Format;

    public static bool IsCancelled(PlcPreparationAnswer answer) => answer == PlcPreparationAnswer.Cancel;

    /// <summary>Контроллеры, которым заливка БЕЗ подготовки известна как опасная.
    ///
    /// SMH5 — новое поколение (Rockchip): его проект в контроллере состоит не только из LFS, но и из
    /// ELF/ZOP-части, и Loader чистит её ИМЕННО в сценарии formatAndUpdateFirmware (см.
    /// docs/loader/LOADER_AUTOMATION_API.md, раздел про подготовку). Если залить один свежий LFS
    /// поверх чужой ELF/ZOP-части, контроллер получает несогласованную пару и после выключения
    /// питания уходит в бесконечную перезагрузку.
    ///
    /// Это ровно то, о чём сообщил Илья 15.10.2026: «при прошивке через фаиндер SMH5 они
    /// окирпичиваются… уходят в цикл бесконечной перезагрузки, потому приходится форматировать и
    /// заливать через SMLogix». Форматирование при ручной заливке и есть недостающий шаг.
    ///
    /// Поэтому для таких контроллеров «не форматировать» перестаёт быть выбором ПО УМОЛЧАНИЮ:
    /// прошлый ответ наладчика его больше не определяет. Запрета нет — решение остаётся за
    /// человеком, но принимается осознанно и с предупреждением перед глазами.</summary>
    private static readonly string[] FormatByDefaultFamilies = ["SMH5"];

    public static bool NeedsFormatByDefault(string? controllerName)
    {
        if (string.IsNullOrWhiteSpace(controllerName)) return false;
        var name = controllerName.Trim().ToUpperInvariant();
        return FormatByDefaultFamilies.Any(f => name.StartsWith(f, System.StringComparison.Ordinal));
    }

    /// <summary>Ответ, на котором стоит фокус при открытии окна, — прошлый выбор наладчика. Но для
    /// контроллеров из <see cref="NeedsFormatByDefault"/> прошлый выбор не наследуется: у них
    /// «залить как есть» кирпичит ПЛК, и предлагать это по привычке нельзя.</summary>
    public static PlcPreparationAnswer DefaultAnswer(bool rememberedFormat, string? controllerName = null) =>
        NeedsFormatByDefault(controllerName) || rememberedFormat
            ? PlcPreparationAnswer.Format
            : PlcPreparationAnswer.Keep;

    /// <summary>Предупреждение в окне подготовки — пусто, если предупреждать не о чем. Текст живёт
    /// здесь, рядом с правилом, а не в разметке окна: это часть решения, и проверяется тестом.</summary>
    public static string WarningFor(string? controllerName) =>
        NeedsFormatByDefault(controllerName)
            ? $"У {controllerName?.Trim()} загрузка без форматирования и обновления ядра приводила к "
              + "бесконечной перезагрузке контроллера после выключения питания: в нём остаётся "
              + "ELF/ZOP-часть от прежнего проекта, не совпадающая с новой прошивкой. "
              + "Её очищает только подготовка. Выбирайте «залить как есть», только если точно "
              + "понимаете, что делаете."
            : "";

    /// <summary>Строка в журнале операции: что именно наладчик выбрал. Пишется всегда, включая
    /// «без форматирования», — по журналу должно быть видно, что вопрос задавали и что ответили.</summary>
    public static string LogLine(PlcPreparationAnswer answer) => answer switch
    {
        PlcPreparationAnswer.Format => "Выбрано: отформатировать проект и обновить ядро ПЛК перед загрузкой.",
        PlcPreparationAnswer.Keep => "Выбрано: загрузить без форматирования и обновления ядра.",
        _ => "Загрузка отменена до запуска.",
    };

    /// <summary>Текст вопроса. Здесь, а не в разметке окна: формулировка про необратимость — часть
    /// решения задачи, а не оформление, и она должна проверяться тестом вместе с остальным.</summary>
    public static string QuestionFor(string versionName)
    {
        var what = string.IsNullOrWhiteSpace(versionName) ? "проект" : versionName;
        return $"Перед загрузкой «{what}» в контроллер:";
    }
}
