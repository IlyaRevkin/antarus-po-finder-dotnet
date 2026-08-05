using AntarusPoFinder.Core.Loader;
using Xunit;

namespace AntarusPoFinder.Tests;

/// <summary>Вопрос «форматировать ли контроллер» перед загрузкой ПО.
///
/// <b>Что было не так.</b> Форматированием управляла галочка ВНУТРИ окна загрузки, а окно стартовало
/// операцию само, сразу после открытия, с запомненным значением этой галочки. То есть необратимое
/// «стереть проект в контроллере и перешить ядро» решалось за наладчика тем, что он выбрал в прошлый
/// раз на другом объекте, а увидеть это он мог только по строке в журнале — когда форматирование уже
/// шло.
///
/// Логика вынесена из окна в <see cref="PlcPreparation"/> именно затем, чтобы её можно было запереть
/// тестами без WPF: сам диалог (PlcPreparationDialog) только показывает эти три ответа.</summary>
public class PlcPreparationTests
{
    /// <summary>Спрашиваем только там, где ответ вообще что-то меняет: сборка .lfs к контроллеру не
    /// подключается, и вопрос про форматирование в ней бессмысленный (и пугающий).</summary>
    [Theory]
    [InlineData(LoaderOperation.Deploy, true)]
    [InlineData(LoaderOperation.Build, false)]
    public void TheQuestion_IsAskedOnlyBeforeWritingToTheController(LoaderOperation operation, bool asked) =>
        Assert.Equal(asked, PlcPreparation.ShouldAsk(operation));

    /// <summary>Отмена — это «не грузить вовсе», а не «залить без форматирования». Перепутать эти два
    /// исхода нельзя: во втором случае в контроллер всё-таки пишут.</summary>
    [Fact]
    public void Cancel_MeansDoNotLoadAtAll()
    {
        Assert.True(PlcPreparation.IsCancelled(PlcPreparationAnswer.Cancel));
        Assert.False(PlcPreparation.IsCancelled(PlcPreparationAnswer.Format));
        Assert.False(PlcPreparation.IsCancelled(PlcPreparationAnswer.Keep));

        Assert.True(PlcPreparation.FormatFor(PlcPreparationAnswer.Format));
        Assert.False(PlcPreparation.FormatFor(PlcPreparationAnswer.Keep));
        // На отмене форматировать нечего — вызывающий обязан проверить IsCancelled раньше.
        Assert.False(PlcPreparation.FormatFor(PlcPreparationAnswer.Cancel));
    }

    /// <summary>Прошлый выбор остаётся ЗНАЧЕНИЕМ ПО УМОЛЧАНИЮ (какая кнопка подсвечена), а не молчаливым
    /// решением: наладчик, который каждый раз форматирует, не должен каждый раз целиться мышью.</summary>
    [Theory]
    [InlineData(true, PlcPreparationAnswer.Format)]
    [InlineData(false, PlcPreparationAnswer.Keep)]
    public void ThePreviousChoice_IsOnlyTheDefault(bool remembered, PlcPreparationAnswer expected) =>
        Assert.Equal(expected, PlcPreparation.DefaultAnswer(remembered));

    /// <summary>В журнале операции видно, что вопрос задавали и что ответили — включая «без
    /// форматирования». Иначе спор «я такого не выбирал» нечем закрыть.</summary>
    [Fact]
    public void EveryAnswer_LeavesATraceInTheOperationLog()
    {
        Assert.Contains("отформатировать", PlcPreparation.LogLine(PlcPreparationAnswer.Format));
        Assert.Contains("без форматирования", PlcPreparation.LogLine(PlcPreparationAnswer.Keep));
        Assert.Contains("отменена", PlcPreparation.LogLine(PlcPreparationAnswer.Cancel));

        // Строки разные — по журналу должно быть однозначно понятно, что выбрали.
        Assert.NotEqual(PlcPreparation.LogLine(PlcPreparationAnswer.Format),
            PlcPreparation.LogLine(PlcPreparationAnswer.Keep));
    }

    /// <summary>В вопросе названо то, что грузят: наладчик у шкафа держит открытыми несколько окон, и
    /// «вы уверены?» без имени версии — это ровно то диалоговое окно, которое закрывают не глядя.</summary>
    [Fact]
    public void TheQuestion_NamesWhatIsAboutToBeLoaded()
    {
        Assert.Contains("2.1.0042.0001", PlcPreparation.QuestionFor("2.1.0042.0001"));
        // Имени нет (такое бывает у сборной операции) — вопрос всё равно осмысленный.
        Assert.Contains("проект", PlcPreparation.QuestionFor(""));
        Assert.Contains("проект", PlcPreparation.QuestionFor("   "));
    }
}
