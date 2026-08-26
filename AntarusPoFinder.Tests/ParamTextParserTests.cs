using System.Linq;
using AntarusPoFinder.Core.Domain;
using AntarusPoFinder.Core.Services;
using Xunit;

namespace AntarusPoFinder.Tests;

/// <summary>Разбор накопленных текстовых файлов параметров ПЧ/УПП (ParamTextParser).
///
/// Проверяется ровно то, на чём разбор ломается в жизни: три вида скобок означают РАЗНОЕ, а формат
/// строки соблюдается «как получилось» — пробел перед скобкой то есть, то нет, тире между значением
/// и названием то есть, то нет, значения может не быть вовсе.
///
/// Эталон — живой файл с диска (ESQ-230 2025 - КПЧ(Задание Modbus).txt). Он вписан сюда текстом, а
/// не читается с D:\: диск есть только у Ильи, а тест обязан идти на любой машине и в CI.</summary>
public class ParamTextParserTests
{
    /// <summary>Тот самый файл, слово в слово (прочитан из cp1251). Правки в нём — только вместе с
    /// правкой на диске: он здесь как образец формата, а не как удобный набор строк.</summary>
    private const string LiveSample = """
        ESQ-230 - КПЧ(Задание Modbus) Новая серия ПЧ 2025г

        =================[Настройка ШУ]

        P0-02(2) - Выбор канала команды запуска -  Протокол связи
        P0-03(9) - Основной канал задания частоты - Протокол связи
        P0-10(?) - Максимальная частота (Гц), указать в соответствии с ПЛК

        <<<<<<<<<[Только для ПЧ №1]
        P4-00(0) - Функция DI1 - Нет функции (для сигнала протечки на ПЛК)
        >>>>>>>>>

        <<<<<<<<<[Для ПЧ №1 и ПЧ №2]
        P4-39(1) - Выбор типа AI1 - (4-20мА)
        >>>>>>>>>

        P5-00(1) - Режим работы выхода FM - Дискретный выход
        P5-01(1) - Функция выхода FM - работа
        P5-02(15) - Функция реле - Готовность
        PD-01 (3) - Формат данных - 8-N-1
        PD-02 (N) - Адрес ПЧ

        ----------------[Для ПЧ от 18,5 кВт]
        P5-22 (00001) Выбор логики дискретных выходов

        ----------------[Для 55 ГЦ]
        В ПЛК выставить частоту 55Гц
        P0-10 (55) Максимальная частота, Гц
        P0-12 (55) Верхний предел частоты, Гц

        ================[Двигатель]

        P0-17 - Время разгона 1
        P0-18 - Время замедления 1
        P0-19 - Единица времени разгона\замедления.
                   По умолчанию 1 (0=1с, 1=0.1с, 2=0.01с)

        P1-01 - Мощность
        P1-02 - Напряжение
        P1-03 - Ток
        P1-05 - Скорость
        P1-37 - Автонастройка: 1 - без вращения, 2 - с вращением
        """;

    private static ParamTableRow Row(ParamTextParser.ParseResult result, string code) =>
        result.Rows.First(r => r.Code == code);

    // ── Живой файл целиком ───────────────────────────────────────────────────────────────────

    [Fact]
    public void LiveFile_FirstLineBecomesTitle_NotAParameter()
    {
        var result = ParamTextParser.Parse(LiveSample);

        Assert.Equal("ESQ-230 - КПЧ(Задание Modbus) Новая серия ПЧ 2025г", result.Title);
        // «ESQ-230» разбирается регулярным выражением кода ровно как «P0-02» — заголовок отличает
        // только положение до первой секции. Проверяем, что параметром он всё-таки не стал.
        Assert.DoesNotContain(result.Rows, r => r.Code == "ESQ-230");
    }

    [Fact]
    public void LiveFile_EveryMeaningfulLineSurvives()
    {
        var result = ParamTextParser.Parse(LiveSample);

        // 21 параметр + одно пояснение «В ПЛК выставить частоту 55Гц». Строка с отступом
        // («По умолчанию 1 (0=1с…)») своей строкой НЕ становится — она приклеивается к P0-19.
        Assert.Equal(21, result.Rows.Count(r => r.Kind == ParamRowKind.Param));
        Assert.Equal(1, result.Rows.Count(r => r.Kind == ParamRowKind.Note));
    }

    [Fact]
    public void LiveFile_SectionsBecomeGroups()
    {
        var result = ParamTextParser.Parse(LiveSample);

        Assert.Equal(ParamGroupCatalog.Main, Row(result, "P0-02").GroupName);
        Assert.Equal(ParamGroupCatalog.Motor, Row(result, "P1-01").GroupName);
    }

    // ── Три вида скобок означают РАЗНОЕ ──────────────────────────────────────────────────────

    [Fact]
    public void AngleBrackets_MarkApplicabilityOfRowsInsideOnly()
    {
        var result = ParamTextParser.Parse(LiveSample);

        Assert.Equal("Только для ПЧ №1", Row(result, "P4-00").Applicability);
        Assert.Equal("Для ПЧ №1 и ПЧ №2", Row(result, "P4-39").Applicability);
        // Строка ПОСЛЕ «>>>» применимости уже не имеет: иначе наладчик выставил бы её не тому
        // частотнику — ровно то, ради чего применимость и заведена свойством строки.
        Assert.Equal("", Row(result, "P5-00").Applicability);
        Assert.Equal("", Row(result, "P0-02").Applicability);
    }

    [Fact]
    public void Dashes_MarkConditionNotGroup()
    {
        var result = ParamTextParser.Parse(LiveSample);

        var p522 = Row(result, "P5-22");
        Assert.Equal("Для ПЧ от 18,5 кВт", p522.AppliesWhen);
        // Подгруппа по условию НЕ создаёт своей группы: группа остаётся от секции.
        Assert.Equal(ParamGroupCatalog.Main, p522.GroupName);
        Assert.Equal("", p522.Applicability);
    }

    [Fact]
    public void Condition_ResetsAtNextSection()
    {
        var result = ParamTextParser.Parse(LiveSample);

        // «Для 55 ГЦ» кончается вместе с секцией «Настройка ШУ»: к «Двигателю» оно отношения не имеет.
        Assert.Equal("Для 55 ГЦ", Row(result, "P0-12").AppliesWhen);
        Assert.Equal("", Row(result, "P0-17").AppliesWhen);
    }

    [Fact]
    public void SameCodeTwiceUnderDifferentCondition_StaysTwoRows()
    {
        var result = ParamTextParser.Parse(LiveSample);

        // P0-10 в файле дважды: «(?)» для всех и «(55)» для 55 Гц. Схлопнуть их — потерять половину
        // смысла файла (см. ParamTableDiff.KeyOf, где применимость и условие входят в ключ).
        var both = result.Rows.Where(r => r.Code == "P0-10").ToList();
        Assert.Equal(2, both.Count);
        Assert.Equal(ParamValueState.Ask, both[0].ValueState);
        Assert.Equal("55", both[1].Value);
        Assert.Equal("Для 55 ГЦ", both[1].AppliesWhen);
    }

    [Fact]
    public void SectionWithoutBrackets_IsStillASection()
    {
        var result = ParamTextParser.Parse("====== Двигатель ======\nP1-01 - Мощность");

        Assert.Equal(ParamGroupCatalog.Motor, Row(result, "P1-01").GroupName);
    }

    [Fact]
    public void PlainSeparatorLine_IsNotARow()
    {
        // «=====» и «*****» без единой буквы — украшение. Осев пояснением, они замусорили бы таблицу.
        var result = ParamTextParser.Parse("=====\n*****\n-----\nP1-01 - Мощность");

        Assert.Single(result.Rows);
    }

    [Fact]
    public void UnclosedApplicabilityBlock_IsReportedNotHidden()
    {
        var result = ParamTextParser.Parse("==[Настройка]\n<<<[Только для ПЧ №1]\nP1-01 - Мощность");

        Assert.Equal("Только для ПЧ №1", Row(result, "P1-01").Applicability);
        Assert.Contains(result.Warnings, w => w.Contains("не закрыт"));
    }

    // ── Отклонения формата строки ────────────────────────────────────────────────────────────

    [Fact]
    public void ValueInBrackets_ReadsWithAndWithoutSpaceBeforeBracket()
    {
        var result = ParamTextParser.Parse(LiveSample);

        Assert.Equal("2", Row(result, "P0-02").Value);   // «P0-02(2)» — без пробела
        Assert.Equal("3", Row(result, "PD-01").Value);   // «PD-01 (3)» — с пробелом
        Assert.Equal(ParamValueState.Set, Row(result, "PD-01").ValueState);
    }

    [Fact]
    public void MissingDashBetweenValueAndName_DoesNotLoseTheRow()
    {
        var result = ParamTextParser.Parse(LiveSample);

        // «P5-22 (00001) Выбор логики дискретных выходов» — тире нет вовсе.
        Assert.Equal("Выбор логики дискретных выходов", Row(result, "P5-22").Title);
        Assert.Equal("00001", Row(result, "P5-22").Value);
    }

    [Fact]
    public void QuestionMarkValue_MeansAskThePlc_AndIsWarnedAbout()
    {
        var result = ParamTextParser.Parse(LiveSample);

        var p010 = result.Rows.First(r => r.Code == "P0-10");
        Assert.Equal(ParamValueState.Ask, p010.ValueState);
        Assert.Equal("", p010.Value);
        Assert.Equal("? — уточнить по ПЛК", p010.ValueDisplay);
        Assert.Contains(result.Warnings, w => w.Contains("P0-10"));
    }

    [Fact]
    public void NoValueAtAll_MeansTakenOnSite_NotZero()
    {
        var result = ParamTextParser.Parse(LiveSample);

        // «P1-01 - Мощность» — значение снимается с шильдика двигателя на объекте. Пустая ячейка без
        // пометки читалась бы как «ноль», и это была бы ложь в самой важной колонке.
        var p101 = Row(result, "P1-01");
        Assert.Equal(ParamValueState.OnSite, p101.ValueState);
        Assert.Equal("", p101.Value);
        Assert.Equal("— по месту", p101.ValueDisplay);
    }

    [Fact]
    public void NonNumericValue_IsKeptAsWritten()
    {
        // «PD-02 (N) - Адрес ПЧ»: значение вообще не число, и придумывать за человека нечего.
        var result = ParamTextParser.Parse(LiveSample);

        Assert.Equal("N", Row(result, "PD-02").Value);
        Assert.Equal(ParamValueState.Set, Row(result, "PD-02").ValueState);
    }

    [Fact]
    public void IndentedLine_ContinuesPreviousDescription()
    {
        var result = ParamTextParser.Parse(LiveSample);

        var p019 = Row(result, "P0-19");
        Assert.Contains("По умолчанию 1", p019.Description);
        Assert.DoesNotContain(result.Rows, r => r.Title.StartsWith("По умолчанию"));
    }

    [Fact]
    public void PlainTextLine_StaysAsNote()
    {
        var result = ParamTextParser.Parse(LiveSample);

        // «В ПЛК выставить частоту 55Гц» — указание наладчику. Выбрось его, и подгруппа «Для 55 ГЦ»
        // теряет смысл: непонятно, откуда взялись 55.
        var note = result.Rows.Single(r => r.Kind == ParamRowKind.Note);
        Assert.Equal("В ПЛК выставить частоту 55Гц", note.Title);
        Assert.Equal("Для 55 ГЦ", note.AppliesWhen);
        Assert.Equal("", note.Code);
    }

    [Fact]
    public void DataFormatInDescription_IsNotMistakenForACode()
    {
        var result = ParamTextParser.Parse(LiveSample);

        // «8-N-1» стоит хвостом описания PD-01 и на код похож ровно настолько, чтобы наивный разбор
        // завёл параметр «8-N».
        Assert.Equal("Формат данных", Row(result, "PD-01").Title);
        Assert.Equal("8-N-1", Row(result, "PD-01").Description);
        Assert.DoesNotContain(result.Rows, r => r.Code.StartsWith("8"));
    }

    [Fact]
    public void ManufacturerCodeStyles_AreAllRecognized()
    {
        // Девять производителей — девять написаний кода. Общее только строение.
        var result = ParamTextParser.Parse("""
            ==[Настройка]
            P0-02(2) - Инновэнс
            PD-01 (3) - Инновэнс, второй ряд
            F0.00 (1) - Точка вместо дефиса
            b1-01 (0) - Yaskawa
            1-20 (5) - Danfoss без букв
            """);

        Assert.Equal(new[] { "P0-02", "PD-01", "F0.00", "b1-01", "1-20" },
            result.Rows.Where(r => r.Kind == ParamRowKind.Param).Select(r => r.Code));
    }

    // ── Название против описания ─────────────────────────────────────────────────────────────

    [Fact]
    public void NameAndDescription_SplitOnDash()
    {
        var result = ParamTextParser.Parse(LiveSample);

        Assert.Equal("Выбор канала команды запуска", Row(result, "P0-02").Title);
        Assert.Equal("Протокол связи", Row(result, "P0-02").Description);
    }

    [Fact]
    public void EnumerationAfterColon_DoesNotEatTheName()
    {
        var result = ParamTextParser.Parse(LiveSample);

        // «Автонастройка: 1 - без вращения, 2 - с вращением». Деление по первому « - » дало бы
        // название «Автонастройка: 1» — бессмыслицу в самой заметной колонке.
        var p137 = Row(result, "P1-37");
        Assert.Equal("Автонастройка", p137.Title);
        Assert.Equal("1 - без вращения, 2 - с вращением", p137.Description);
    }

    [Theory]
    // Обычное деление тире.
    [InlineData("Мощность - в киловаттах", "Мощность", "в киловаттах")]
    // Тире нет — делит двоеточие.
    [InlineData("Частота: верхний предел", "Частота", "верхний предел")]
    // Делить нечем — всё уходит в название целиком, а не рубится наугад.
    [InlineData("Просто название", "Просто название", "")]
    public void SplitNameAndDescription_Cases(string input, string name, string description)
    {
        var (gotName, gotDescription) = ParamTextParser.SplitNameAndDescription(input);

        Assert.Equal(name, gotName);
        Assert.Equal(description, gotDescription);
    }

    // ── Файл без секций ──────────────────────────────────────────────────────────────────────

    [Fact]
    public void FileWithoutSections_KeepsFirstLineAsParameter()
    {
        // Кто-то вёл файл сплошным списком. Заголовка в нём нет — и съесть первый параметр под
        // видом названия документа нельзя.
        var result = ParamTextParser.Parse("P0-02(2) - Выбор канала\nP0-03(9) - Канал задания");

        Assert.Equal("", result.Title);
        Assert.Equal(2, result.Rows.Count);
        Assert.Equal("P0-02", result.Rows[0].Code);
        Assert.Equal(ParamGroupCatalog.Main, result.Rows[0].GroupName);
    }

    [Fact]
    public void UnknownSection_KeepsItsOwnName_AndSaysSo()
    {
        var result = ParamTextParser.Parse("=====[Настройка редуктора]\nP9-01 (1) - Что-то своё");

        // Раньше незнакомая секция уезжала в «Прочее». Жалоба владельца («визуально нет разделения
        // разделов, как в том же txt») этого и касалась: в накопленных файлах разделы названы
        // по-своему, и сваленные в одну группу они переставали отбивать таблицу вообще. Своё имя
        // раздела сохраняет ту же разбивку, что видна в исходнике.
        Assert.Equal("Настройка редуктора", Row(result, "P9-01").GroupName);
        Assert.Contains(result.Warnings, w => w.Contains("Настройка редуктора"));
    }

    [Fact]
    public void EmptyInput_IsNotAFailure()
    {
        var result = ParamTextParser.Parse("");

        Assert.Empty(result.Rows);
        Assert.Equal("", result.Title);
    }

    // ── Формы, найденные прогоном по всему накопленному на диске ─────────────────────────────
    //
    // Каждый случай ниже взят с живого файла из «Прочее\!Файлы параметров ПЧ». До этих правил
    // разбор не находил НИ ОДНОГО параметра в 16 файлах из 93 — целые семейства (INNOVERT, ABB,
    // Vacon) написаны в форме, которой он не знал.

    [Fact]
    public void CodeWithoutSeparator_IsRecognized_Innovert()
    {
        var result = ParamTextParser.Parse("Innovert ТГР\nPa00 (1) - Частота\nPd25 (3) - Выходное реле");

        Assert.Equal(new[] { "Pa00", "Pd25" }, result.Rows.Select(r => r.Code));
    }

    [Fact]
    public void FourDigitCode_IsRecognized_Abb()
    {
        var result = ParamTextParser.Parse("ABB ACS 310\n1611 (3) - Вид параметра\n9905 НОМ. НАПРЯЖЕНИЕ");

        Assert.Equal(new[] { "1611", "9905" }, result.Rows.Select(r => r.Code));
    }

    [Fact]
    public void CodeWithSpace_AndTabSeparatedValue_IsRecognized_Vacon()
    {
        // «P 17.2 - 0<таб>Скрыть часть параметров - выкл»: и код с пробелом, и значение, отбитое
        // от описания табуляцией. Формат объявлен в самом файле первой строкой.
        var result = ParamTextParser.Parse("Vacon 20 - КПЧ\nP 17.2 - 0\t\tСкрыть часть параметров - выкл");

        var row = result.Rows.Single();
        Assert.Equal("P 17.2", row.Code);
        Assert.Equal("0", row.Value);
        Assert.Equal("Скрыть часть параметров", row.Title);
        Assert.Equal("выкл", row.Description);
    }

    [Fact]
    public void DoubleSpaceInsideName_IsNotTakenForAValue()
    {
        // Ровно то, из-за чего значение ищется по ТАБУЛЯЦИИ, а не по «двум пробелам подряд»:
        // «Максимальное входное  напряжение» — обычная опечатка, и по ней половина названия
        // уехала бы в столбец «Значение».
        var result = ParamTextParser.Parse("Innovert\nPd01 - Максимальное входное  напряжение");

        var row = result.Rows.Single();
        Assert.Equal("", row.Value);
        Assert.StartsWith("Максимальное", row.Title);
    }

    [Fact]
    public void CyrillicLookalikesInCode_AreBroughtBackToLatin()
    {
        // «С00.16» у VEDA набрано русской «С», «Р2-28» русской «Р». В самом частотнике это
        // латинские C00.16 и P2-28 — код это то, что человек ВБИВАЕТ, и русская буква там значит,
        // что набрать его по таблице нельзя.
        var result = ParamTextParser.Parse("VEDA\nС00.16 (1) - Напряжение Ai\nР2-28 (1) - Реле F");

        Assert.Equal(new[] { "C00.16", "P2-28" }, result.Rows.Select(r => r.Code));
    }

    [Fact]
    public void FootnoteMarker_DoesNotHideTheParameter_AndBringsItsExplanation()
    {
        var result = ParamTextParser.Parse(string.Join("\n", new[]
        {
            "VEDA Дробилка",
            "*F06.00 (1) - Выходные сигналы",
            "F06.21 (1) - Цифровой выход",
            "",
            "* - при использовании AO вместо ModBus",
        }));

        // Помеченная звёздочкой строка — параметр, а не пояснение: раньше она уходила в текст
        // целиком, вместе с кодом.
        Assert.Equal("F06.00", result.Rows[0].Code);
        // Сама сноска отдельной строкой в таблицу не идёт: её текст стоит в «Когда нужно» у той
        // строки, к которой она относится.
        Assert.Equal("при использовании AO вместо ModBus", result.Rows[0].AppliesWhen);
        Assert.Equal("", result.Rows[1].AppliesWhen);
        Assert.Equal(2, result.Rows.Count);
    }

    [Fact]
    public void ConditionInBracketsBeforeCode_DoesNotLoseTheParameter()
    {
        var result = ParamTextParser.Parse("M740\nU0-04 (16) - Датчик\n(если требуется) \tU0-15 (55) - Максимальная частота");

        var row = result.Rows.Single(r => r.Code == "U0-15");
        Assert.Equal("55", row.Value);
        Assert.Equal("если требуется", row.AppliesWhen);
    }

    [Fact]
    public void SelfClosingApplicabilityBlock_DoesNotLeakToTheRestOfTheFile()
    {
        // «<<<<< Для схем без HL1 - (11) Сухой ход >>>>>» — блок открыт и закрыт ОДНОЙ строкой.
        // Прежний разбор принимал её за открытие и метил ею весь остаток файла, причём вместе с
        // прилипшими «>>>>>».
        var result = ParamTextParser.Parse(string.Join("\n", new[]
        {
            "M740",
            "U2-11 (0) - DI2",
            "<<<<< Для схем без HL1 - (11) Сухой ход >>>>>",
            "U2-12 (0) - DI3",
        }));

        Assert.All(result.Rows, r => Assert.Equal("", r.Applicability));
        var note = result.Rows.Single(r => r.Kind == ParamRowKind.Note);
        Assert.Equal("Для схем без HL1 - (11) Сухой ход", note.Title);
        Assert.DoesNotContain(result.Warnings, w => w.Contains("не закрыт"));
    }

    [Fact]
    public void BracketOnlySection_IsASection_Innovert()
    {
        var result = ParamTextParser.Parse("Innovert\nPa00 (1) - Частота\n\n[Мотор]\nPC09 - Напряжение двигателя");

        Assert.Equal(ParamGroupCatalog.Main, Row(result, "Pa00").GroupName);
        Assert.Equal(ParamGroupCatalog.Motor, Row(result, "PC09").GroupName);
    }

    [Fact]
    public void BareHeading_SplitsTheTableIntoSections()
    {
        // «Параметры электродвигателя» без единого «=» — так отбита большая часть накопленных
        // файлов. Без этого правила ВЕСЬ файл оказывался одной группой.
        var result = ParamTextParser.Parse(string.Join("\n", new[]
        {
            "M740 НГР X",
            "U0-04 (16) - Датчик",
            "",
            "Параметры электродвигателя",
            "U3.33 - Мощность",
        }));

        Assert.Equal(ParamGroupCatalog.Main, Row(result, "U0-04").GroupName);
        Assert.Equal(ParamGroupCatalog.Motor, Row(result, "U3.33").GroupName);
        Assert.Contains(result.Warnings, w => w.Contains("принята за раздел"));
    }

    [Fact]
    public void NoteRightUnderCondition_StaysANote_NotAHeading()
    {
        // Оговорка «перед заголовком пусто» держит именно это: «В ПЛК выставить частоту 55Гц»
        // стоит сразу под «-----[Для 55 ГЦ]» и заголовком раздела не является.
        var result = ParamTextParser.Parse(LiveSample);

        Assert.Equal(1, result.Rows.Count(r => r.Kind == ParamRowKind.Note));
    }

    [Fact]
    public void ModelNameOnTheFirstLine_IsTheTitle_NotAParameter()
    {
        // «M740 НГР X» разбирается как код ровно так же, как «Pa00»: буква и цифры. Отличает его
        // то, что значения у него нет, а приставка («M») не та, что у кодов всего остального файла
        // («U»).
        var result = ParamTextParser.Parse("M740 НГР X\nU0-04 (16) - Датчик\nU1-00 (4) - Вход\nU2-15 (8) - Реле");

        Assert.Equal("M740 НГР X", result.Title);
        Assert.Equal(3, result.Rows.Count);
    }

    [Fact]
    public void ValueWithUnit_FillsTheUnitColumn()
    {
        // Столбец «Ед.» до этого не заполнялся вообще ничем — ни разбором, ни импортом.
        var result = ParamTextParser.Parse("M740\nU1-03 (0.5 сек) - Время\nU1-05 (10 Bar) - Давление");

        Assert.Equal("0.5", Row(result, "U1-03").Value);
        Assert.Equal("сек", Row(result, "U1-03").Unit);
        Assert.Equal("10", Row(result, "U1-05").Value);
        Assert.Equal("Bar", Row(result, "U1-05").Unit);
    }

    [Fact]
    public void UnitAfterCommaInTheName_MovesToTheUnitColumn()
    {
        var result = ParamTextParser.Parse(LiveSample);

        var p012 = Row(result, "P0-12");
        Assert.Equal("Верхний предел частоты", p012.Title);
        Assert.Equal("Гц", p012.Unit);
    }

    [Fact]
    public void UnknownWordAfterComma_IsNotAUnit()
    {
        // Список единиц закрытый намеренно: иначе в столбец «Ед.» уезжал бы любой хвост после
        // последней запятой.
        var result = ParamTextParser.Parse("VEDA\nF01.01 (1) - Запуск, вход");

        var row = result.Rows.Single();
        Assert.Equal("", row.Unit);
        Assert.Equal("Запуск, вход", row.Title);
    }

    [Fact]
    public void PlaceholderValue_MeansTakenOnSite_NotALiteralValue()
    {
        // «(По шильду)», «(Настраивается по месту)» — так написана добрая половина параметров
        // двигателя. Прежде наладчик читал в таблице «выставить „По шильду“».
        var result = ParamTextParser.Parse("Vacon\nP 1.1 - (По шильду)\tНапряжение\nU0.18 (Настраивается по месту) Время разгона");

        Assert.All(result.Rows.Where(r => r.Kind == ParamRowKind.Param), r =>
        {
            Assert.Equal(ParamValueState.OnSite, r.ValueState);
            Assert.Equal("", r.Value);
        });
    }

    [Fact]
    public void WindowsLineEndings_ReadTheSameAsUnix()
    {
        var windows = ParamTextParser.Parse(LiveSample.Replace("\n", "\r\n"));
        var unix = ParamTextParser.Parse(LiveSample);

        Assert.Equal(unix.Rows.Count, windows.Rows.Count);
        Assert.Equal(unix.Title, windows.Title);
    }
}
