using System.Collections.Generic;
using System.Linq;
using AntarusPoFinder.Core.Domain;
using AntarusPoFinder.Core.Services;
using Xunit;

namespace AntarusPoFinder.Tests;

/// <summary>ParamTableView — правила ПОКАЗА таблицы параметров: подзаголовки блоков вместо колонок
/// применимости и колонки, появляющиеся по факту содержимого.
///
/// Заведены по третьему подряд замечанию владельца о нечитаемости таблицы. Числа, из-за которых
/// правила именно такие, записаны в доке самого ParamTableView.</summary>
public class ParamTableViewTests
{
    private static ParamTableRow Row(string group, string code, string title = "",
        string applicability = "", string when = "", string factory = "", string unit = "",
        string value = "1", string state = ParamValueState.Set, string description = "") =>
        new()
        {
            GroupName = group,
            Code = code,
            Title = title.Length > 0 ? title : code,
            Value = value,
            ValueState = state,
            Factory = factory,
            Unit = unit,
            Description = description,
            Applicability = applicability,
            AppliesWhen = when,
        };

    // ── Подзаголовки блоков ──────────────────────────────────────────────────────────────────

    [Fact]
    public void Blocks_RowsWithoutMarks_AreOnePlainBlock()
    {
        var rows = new List<ParamTableRow>
        {
            Row("Основные настройки", "P0-02"),
            Row("Основные настройки", "P0-03"),
        };

        var blocks = ParamTableView.Blocks(rows);

        Assert.Equal(2, blocks.Count);
        Assert.Equal(blocks[0], blocks[1]);
        Assert.True(blocks[0].IsPlain);
        Assert.Equal("", blocks[0].Title);
    }

    [Fact]
    public void Blocks_MarkedRun_GetsItsOwnTitle()
    {
        var rows = new List<ParamTableRow>
        {
            Row("Основные настройки", "P0-02"),
            Row("Основные настройки", "P4-00", applicability: "Только для ПЧ №1"),
            Row("Основные настройки", "P4-01", applicability: "Только для ПЧ №1"),
        };

        var blocks = ParamTableView.Blocks(rows);

        Assert.True(blocks[0].IsPlain);
        Assert.Equal("Только для ПЧ №1", blocks[1].Title);
        Assert.Equal(blocks[1], blocks[2]);
        Assert.NotEqual(blocks[0], blocks[1]);
    }

    /// <summary>⚠️ Главный тест этого файла. Строки без пометки в живом файле идут ДВУМЯ кусками, а
    /// между ними блоки применимости. Ключ из одного заголовка склеил бы оба куска в одну группу и
    /// поднял второй кусок к первому — то есть переписал бы порядок документа.</summary>
    [Fact]
    public void Blocks_SecondPlainRun_IsADifferentBlockThanTheFirst()
    {
        var rows = new List<ParamTableRow>
        {
            Row("Основные настройки", "P0-02"),
            Row("Основные настройки", "P4-00", applicability: "Только для ПЧ №1"),
            Row("Основные настройки", "P5-00"),
        };

        var blocks = ParamTableView.Blocks(rows);

        Assert.True(blocks[0].IsPlain);
        Assert.True(blocks[2].IsPlain);
        Assert.NotEqual(blocks[0], blocks[2]);
        Assert.Equal(3, blocks.Select(b => b.Index).Distinct().Count());
    }

    /// <summary>Тот же заголовок в ДРУГОМ разделе — другой блок: полоса, перетёкшая через границу
    /// раздела, легла бы поверх заголовка раздела.</summary>
    [Fact]
    public void Blocks_SameTitleInAnotherSection_IsADifferentBlock()
    {
        var rows = new List<ParamTableRow>
        {
            Row("Основные настройки", "P0-10", when: "Для 55 ГЦ"),
            Row("Двигатель", "P1-01", when: "Для 55 ГЦ"),
        };

        var blocks = ParamTableView.Blocks(rows);

        Assert.Equal("Для 55 ГЦ", blocks[0].Title);
        Assert.Equal("Для 55 ГЦ", blocks[1].Title);
        Assert.NotEqual(blocks[0], blocks[1]);
    }

    [Fact]
    public void TitleOf_BothMarks_AreKeptTogether()
    {
        var row = Row("Основные настройки", "P4-00",
            applicability: "Только для ПЧ №1", when: "Для 55 Гц");

        Assert.Equal("Только для ПЧ №1 · Для 55 Гц", ParamTableView.TitleOf(row));
    }

    [Fact]
    public void Blocks_NoRows_IsEmpty() => Assert.Empty(ParamTableView.Blocks(null));

    /// <summary>Тот самый файл ESQ-230 целиком: два раздела, четыре подписанных блока и три куска
    /// без пометки. Проверка не на числа ради чисел — именно этот порядок владелец читает глазами
    /// в блокноте и требует получить в программе.</summary>
    [Fact]
    public void Blocks_RealDocument_KeepsSourceOrder()
    {
        var rows = new List<ParamTableRow>
        {
            Row("Основные настройки", "P0-02"),
            Row("Основные настройки", "P0-03"),
            Row("Основные настройки", "P0-10"),
            Row("Основные настройки", "P4-00", applicability: "Только для ПЧ №1"),
            Row("Основные настройки", "P4-39", applicability: "Для ПЧ №1 и ПЧ №2"),
            Row("Основные настройки", "P5-00"),
            Row("Основные настройки", "PD-02"),
            Row("Основные настройки", "P5-22", when: "Для ПЧ от 18,5 кВт"),
            Row("Основные настройки", "P0-12", when: "Для 55 ГЦ"),
            Row("Двигатель", "P1-01"),
        };

        var blocks = ParamTableView.Blocks(rows);

        Assert.Equal(new[] { "", "", "", "Только для ПЧ №1", "Для ПЧ №1 и ПЧ №2", "", "",
                             "Для ПЧ от 18,5 кВт", "Для 55 ГЦ", "" },
                     blocks.Select(b => b.Title).ToArray());
        // Кусков (то есть полос и промежутков между ними) ровно семь, а не пять: два «без пометки»
        // куска внутри раздела и ещё один в «Двигателе» — разные блоки.
        Assert.Equal(7, blocks.Select(b => b.Index).Distinct().Count());
    }

    // ── Колонки по факту содержимого ─────────────────────────────────────────────────────────

    [Fact]
    public void NeedsFactory_NothingFilled_IsFalse()
    {
        var rows = new List<ParamTableRow> { Row("Г", "P0-02"), Row("Г", "P0-03", factory: "  ") };

        Assert.False(ParamTableView.NeedsFactory(rows));
        Assert.False(ParamTableView.NeedsFactory(null));
    }

    [Fact]
    public void NeedsFactory_OneRowFilled_IsTrue()
    {
        var rows = new List<ParamTableRow> { Row("Г", "P0-02"), Row("Г", "P0-03", factory: "0") };

        Assert.True(ParamTableView.NeedsFactory(rows));
    }

    [Fact]
    public void NeedsChange_FirstRevision_IsFalse()
    {
        var rows = new List<ParamTableRow> { Row("Г", "P0-02") };

        Assert.False(ParamTableView.NeedsChange(null, rows));
    }

    [Fact]
    public void NeedsChange_NothingChanged_IsFalse()
    {
        var rows = new List<ParamTableRow> { Row("Г", "P0-02") };
        var diff = ParamTableDiff.Compare(rows, rows);

        Assert.False(ParamTableView.NeedsChange(diff, rows));
    }

    [Fact]
    public void NeedsChange_ValueChanged_IsTrue()
    {
        var before = new List<ParamTableRow> { Row("Г", "P0-10", value: "50") };
        var after = new List<ParamTableRow> { Row("Г", "P0-10", value: "55") };
        var diff = ParamTableDiff.Compare(before, after);

        Assert.True(ParamTableView.NeedsChange(diff, after));
    }

    // ── Значение и хвост названия ────────────────────────────────────────────────────────────

    [Fact]
    public void ValueDisplay_Unit_IsGluedToTheValue()
    {
        Assert.Equal("55 Гц", Row("Г", "P0-10", value: "55", unit: "Гц").ValueDisplay);
        Assert.Equal("55", Row("Г", "P0-10", value: "55").ValueDisplay);
    }

    /// <summary>Три состояния значения переживают приклеивание единицы: пустая ячейка у «уточнить»
    /// и «по месту» читалась бы как «ноль».</summary>
    [Fact]
    public void ValueDisplay_ThreeStates_AreKept()
    {
        Assert.Equal("? — уточнить по ПЛК",
            Row("Г", "P0-10", value: "", state: ParamValueState.Ask, unit: "Гц").ValueDisplay);
        Assert.Equal("— по месту",
            Row("Г", "P1-01", value: "", state: ParamValueState.OnSite, unit: "кВт").ValueDisplay);
    }

    /// <summary>Одинокая единица без значения — не ответ на вопрос «что выставить».</summary>
    [Fact]
    public void ValueDisplay_UnitWithoutValue_ShowsNothing() =>
        Assert.Equal("", Row("Г", "P0-10", value: "", unit: "Гц").ValueDisplay);

    [Fact]
    public void Tail_Description_GoesAfterADash()
    {
        Assert.Equal(" — Протокол связи",
            ParamTableView.Tail(Row("Г", "P0-02", description: "Протокол связи")));
        Assert.Equal("", ParamTableView.Tail(Row("Г", "P0-02")));
        Assert.Equal("", ParamTableView.Tail(null));
    }
}
