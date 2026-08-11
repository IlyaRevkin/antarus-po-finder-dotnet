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

    /// <summary>Ответ, на котором стоит фокус при открытии окна, — прошлый выбор наладчика.</summary>
    public static PlcPreparationAnswer DefaultAnswer(bool rememberedFormat) =>
        rememberedFormat ? PlcPreparationAnswer.Format : PlcPreparationAnswer.Keep;

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
