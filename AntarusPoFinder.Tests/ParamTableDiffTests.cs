using System.Collections.Generic;
using System.Linq;
using AntarusPoFinder.Core.Domain;
using AntarusPoFinder.Core.Services;
using Xunit;

namespace AntarusPoFinder.Tests;

/// <summary>Разбор изменений между двумя ревизиями таблицы параметров (ParamTableDiff).
///
/// Считает его программа, и это принципиально: на просьбу «перечисли, что поменял» человек пишет
/// «поправил параметры», а на объекте ищут глазами ровно одну строчку — «P0-10: 50 → 55».</summary>
public class ParamTableDiffTests
{
    private static ParamTableRow P(string code, string value, string? title = null,
        string applicability = "", string appliesWhen = "", string group = ParamGroupCatalog.Main) => new()
    {
        Kind = ParamRowKind.Param,
        Code = code,
        Title = title ?? ("Параметр " + code),
        Value = value,
        ValueState = ParamValueState.Set,
        Applicability = applicability,
        AppliesWhen = appliesWhen,
        GroupName = group,
    };

    private static ParamTableRow Note(string text, string appliesWhen = "") => new()
    {
        Kind = ParamRowKind.Note,
        Title = text,
        ValueState = ParamValueState.Set,
        AppliesWhen = appliesWhen,
        GroupName = ParamGroupCatalog.Main,
    };

    // ── Четыре вида изменений ────────────────────────────────────────────────────────────────

    [Fact]
    public void ValueChange_IsItsOwnKind_NotJustAnEdit()
    {
        var before = new List<ParamTableRow> { P("P0-10", "50") };
        var after = new List<ParamTableRow> { P("P0-10", "55") };

        var diff = ParamTableDiff.Compare(before, after);

        Assert.Equal(1, diff.ValueChanged);
        Assert.Equal(0, diff.Edited);
        Assert.Contains("P0-10: 50 → 55", ParamTableDiff.Describe(diff));
    }

    [Fact]
    public void AddedAndRemoved_AreCounted()
    {
        var before = new List<ParamTableRow> { P("P0-02", "2"), P("P0-03", "9") };
        var after = new List<ParamTableRow> { P("P0-02", "2"), P("P5-00", "1") };

        var diff = ParamTableDiff.Compare(before, after);

        Assert.Equal(1, diff.Added);
        Assert.Equal(1, diff.Removed);
        var text = ParamTableDiff.Describe(diff);
        Assert.Contains("Добавлено (1): P5-00", text);
        Assert.Contains("Убрано (1): P0-03", text);
    }

    [Fact]
    public void DescriptionChange_IsAnEdit_NotAValueChange()
    {
        var before = new List<ParamTableRow> { P("P0-02", "2", "Выбор канала") };
        var after = new List<ParamTableRow> { P("P0-02", "2", "Выбор канала команды запуска") };

        var diff = ParamTableDiff.Compare(before, after);

        Assert.Equal(0, diff.ValueChanged);
        Assert.Equal(1, diff.Edited);
        Assert.Contains("Поправлены описания: 1", ParamTableDiff.Describe(diff));
    }

    [Fact]
    public void NothingChanged_SaysSoPlainly()
    {
        var rows = new List<ParamTableRow> { P("P0-02", "2"), Note("В ПЛК выставить частоту 55Гц") };

        var diff = ParamTableDiff.Compare(rows, rows.Select(r => r.Clone()).ToList());

        Assert.False(diff.Any);
        Assert.Equal("Изменений нет.", ParamTableDiff.Describe(diff));
    }

    [Fact]
    public void MovedToAnotherGroup_IsAnEdit_NotDeleteAndAdd()
    {
        // Группа НЕ входит в ключ строки: перенос «P1-01» из «Прочего» в «Двигатель» — это правка
        // одной строки, а не пропажа одной и появление другой.
        var before = new List<ParamTableRow> { P("P1-01", "15", group: ParamGroupCatalog.Other) };
        var after = new List<ParamTableRow> { P("P1-01", "15", group: ParamGroupCatalog.Motor) };

        var diff = ParamTableDiff.Compare(before, after);

        Assert.Equal(0, diff.Added);
        Assert.Equal(0, diff.Removed);
        Assert.Equal(1, diff.Edited);
    }

    // ── Ключ строки ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public void SameCodeDifferentApplicability_AreTwoDifferentRows()
    {
        // «P4-00 только для ПЧ №1» и «P4-00 для ПЧ №2» — разные строки о разных аппаратах. Схлопни
        // их — и половина изменений исчезнет из разбора, а наладчик выставит параметр не туда.
        var before = new List<ParamTableRow>
        {
            P("P4-00", "0", applicability: "Только для ПЧ №1"),
            P("P4-00", "0", applicability: "Только для ПЧ №2"),
        };
        var after = new List<ParamTableRow>
        {
            P("P4-00", "0", applicability: "Только для ПЧ №1"),
            P("P4-00", "5", applicability: "Только для ПЧ №2"),
        };

        var diff = ParamTableDiff.Compare(before, after);

        Assert.Equal(1, diff.ValueChanged);
        Assert.Equal(0, diff.Added);
        Assert.Equal(0, diff.Removed);
    }

    [Fact]
    public void SameCodeDifferentCondition_AreTwoDifferentRows()
    {
        var before = new List<ParamTableRow> { P("P0-10", "50"), P("P0-10", "55", appliesWhen: "Для 55 ГЦ") };
        var after = new List<ParamTableRow> { P("P0-10", "50"), P("P0-10", "60", appliesWhen: "Для 55 ГЦ") };

        var diff = ParamTableDiff.Compare(before, after);

        Assert.Equal(1, diff.ValueChanged);
        Assert.Equal("Для 55 ГЦ", diff.Changes[0].After!.AppliesWhen);
    }

    [Fact]
    public void CyrillicCaseAndSpacing_DoNotInventChanges()
    {
        // ⚠️ Свёрнуто это В .NET, а не в СУБД: COLLATE NOCASE у SQLite кириллицу не сворачивает
        // вовсе, и «Только для ПЧ №1» с «ТОЛЬКО ДЛЯ ПЧ №1» уехали бы двумя разными строками.
        var before = new List<ParamTableRow> { P("P4-00", "0", applicability: "Только для ПЧ №1") };
        var after = new List<ParamTableRow> { P("P4-00", "0", applicability: "ТОЛЬКО  ДЛЯ   ПЧ №1") };

        Assert.False(ParamTableDiff.Compare(before, after).Any);
    }

    [Fact]
    public void ApplicabilityAndConditionCannotBleedIntoEachOther()
    {
        // Части ключа разделены служебным символом. С обычным пробелом пара («Для ПЧ №1», «от 18 кВт»)
        // дала бы тот же ключ, что и («Для ПЧ №1 от 18 кВт», «») — и одно изменение потерялось бы.
        var before = new List<ParamTableRow> { P("P5-22", "1", applicability: "Для ПЧ №1", appliesWhen: "от 18 кВт") };
        var after = new List<ParamTableRow> { P("P5-22", "1", applicability: "Для ПЧ №1 от 18 кВт") };

        var diff = ParamTableDiff.Compare(before, after);

        Assert.Equal(1, diff.Added);
        Assert.Equal(1, diff.Removed);
    }

    [Fact]
    public void TwoNotesInOneGroup_AreNotTheSameRow()
    {
        // У пояснения кода нет, и ключом ему служит собственный текст.
        var before = new List<ParamTableRow> { Note("В ПЛК выставить частоту 55Гц") };
        var after = new List<ParamTableRow> { Note("В ПЛК выставить частоту 55Гц"), Note("Проверить фазировку") };

        var diff = ParamTableDiff.Compare(before, after);

        Assert.Equal(1, diff.Added);
        Assert.Contains("«Проверить фазировку»", ParamTableDiff.Describe(diff));
    }

    [Fact]
    public void DuplicateRowInSource_DoesNotBreakTheComparison()
    {
        // Один и тот же параметр записан в файле дважды подряд — так бывает. Разбор от этого не
        // падает: побеждает первая строка, как её и увидит человек сверху вниз.
        var before = new List<ParamTableRow> { P("P0-02", "2"), P("P0-02", "3") };
        var after = new List<ParamTableRow> { P("P0-02", "2") };

        var diff = ParamTableDiff.Compare(before, after);

        Assert.False(diff.Any);
    }

    // ── Три состояния значения ───────────────────────────────────────────────────────────────

    [Fact]
    public void AskAndOnSite_AreDifferentFromEachOtherAndFromEmpty()
    {
        var ask = new ParamTableRow { Code = "P0-10", Title = "Максимальная частота", ValueState = ParamValueState.Ask };
        var onSite = new ParamTableRow { Code = "P0-10", Title = "Максимальная частота", ValueState = ParamValueState.OnSite };
        var empty = new ParamTableRow { Code = "P0-10", Title = "Максимальная частота", ValueState = ParamValueState.Set, Value = "" };

        // «Уточнить по ПЛК» и «снимается с шильдика» — разные вещи, и переход между ними обязан
        // попасть в разбор: пустая ячейка в обоих случаях выглядит одинаково.
        Assert.Equal(1, ParamTableDiff.Compare(new[] { ask }, new[] { onSite }).ValueChanged);
        Assert.Equal(1, ParamTableDiff.Compare(new[] { onSite }, new[] { empty }).ValueChanged);
        Assert.Equal(1, ParamTableDiff.Compare(new[] { ask }, new[] { empty }).ValueChanged);
    }

    [Fact]
    public void StatesAreShownByWordsNotByBlankSpace()
    {
        var before = new[] { new ParamTableRow { Code = "P0-10", ValueState = ParamValueState.Ask } };
        var after = new[] { P("P0-10", "55") };

        Assert.Contains("P0-10: ? → 55", ParamTableDiff.Describe(ParamTableDiff.Compare(before, after)));
        Assert.Contains("P0-10: 55 → по месту",
            ParamTableDiff.Describe(ParamTableDiff.Compare(after, new[] { new ParamTableRow { Code = "P0-10", ValueState = ParamValueState.OnSite } })));
    }

    // ── Подсветка строк и длинные списки ─────────────────────────────────────────────────────

    [Fact]
    public void ByKey_LetsTheTableHighlightRowsWithoutSearchingTheList()
    {
        var before = new List<ParamTableRow> { P("P0-02", "2"), P("P0-03", "9") };
        var after = new List<ParamTableRow> { P("P0-02", "7"), P("P5-00", "1") };

        var diff = ParamTableDiff.Compare(before, after);

        Assert.Equal(ParamTableDiff.ChangeKind.ValueChanged, diff.ByKey[ParamTableDiff.KeyOf(P("P0-02", "7"))]);
        Assert.Equal(ParamTableDiff.ChangeKind.Added, diff.ByKey[ParamTableDiff.KeyOf(P("P5-00", "1"))]);
        Assert.False(diff.ByKey.ContainsKey(ParamTableDiff.KeyOf(P("P9-99", "0"))));
    }

    [Fact]
    public void LongListIsCutShort_TheSummaryStaysOneLine()
    {
        var before = new List<ParamTableRow>();
        var after = Enumerable.Range(1, 30).Select(i => P($"P0-{i:00}", "1")).ToList();

        var text = ParamTableDiff.Describe(ParamTableDiff.Compare(before, after));

        Assert.Contains("Добавлено (30)", text);
        Assert.Contains("и ещё 22", text);
        Assert.DoesNotContain("P0-30", text);
    }

    [Fact]
    public void FirstRevision_IsEverythingAdded()
    {
        // У самой первой ревизии предыдущей нет вовсе — сравнивать её не с чем, и падать на этом
        // разбор не должен.
        var diff = ParamTableDiff.Compare(null, new List<ParamTableRow> { P("P0-02", "2") });

        Assert.Equal(1, diff.Added);
    }

    // ── Свои столбцы документа ───────────────────────────────────────────────────────────────

    private static ParamTableRow WithExtra(ParamTableRow row, string key, string value)
    {
        var copy = row.Clone();
        copy.Extra = ParamRowExtra.With(copy.Extra, key, value);
        return copy;
    }

    [Fact]
    public void AChangeInAnOwnColumn_IsItsOwnKind_NotBuriedUnderEdits()
    {
        var before = new List<ParamTableRow> { WithExtra(P("P0-10", "50"), "Диапазон", "0…50") };
        var after = new List<ParamTableRow> { WithExtra(P("P0-10", "50"), "Диапазон", "0…60") };

        var diff = ParamTableDiff.Compare(before, after);

        // Свои столбцы заводят как раз под то, что нужно выставлять и сверять. Потеряйся эта
        // правка в куче «поправлены описания» — столбец завели бы и не увидели, что в нём меняли.
        var change = Assert.Single(diff.Changes);
        Assert.Equal(ParamTableDiff.ChangeKind.ExtraChanged, change.Kind);
        Assert.Contains("Диапазон: 0…50 → 0…60", ParamTableDiff.Describe(diff));
    }

    [Fact]
    public void AChangedValueOutranksAChangedOwnColumn()
    {
        // У строки одна пометка, и значение — то, что человек реально вобьёт в частотник.
        var before = new List<ParamTableRow> { WithExtra(P("P0-10", "50"), "Диапазон", "0…50") };
        var after = new List<ParamTableRow> { WithExtra(P("P0-10", "55"), "Диапазон", "0…60") };

        Assert.Equal(ParamTableDiff.ChangeKind.ValueChanged, Assert.Single(ParamTableDiff.Compare(before, after).Changes).Kind);
    }

    [Fact]
    public void ReorderedKeysInsideACellAreNotAChange()
    {
        // Тот же набор, записанный иначе, — это разный вывод сериализатора, а не правка документа.
        var before = new List<ParamTableRow> { R("P0-10", "50", "{\"Б\":\"2\",\"А\":\"1\"}") };
        var after = new List<ParamTableRow> { R("P0-10", "50", "{\"А\":\"1\", \"Б\": \"2\"}") };

        Assert.False(ParamTableDiff.Compare(before, after).Any);
    }

    private static ParamTableRow R(string code, string value, string extra)
    {
        var row = P(code, value);
        row.Extra = extra;
        return row;
    }

    [Fact]
    public void AColumnFilledForTheFirstTime_IsReportedAsANewColumn()
    {
        var before = new List<ParamTableRow> { P("P0-10", "50") };
        var after = new List<ParamTableRow> { WithExtra(P("P0-10", "50"), "Диапазон", "0…60") };

        var diff = ParamTableDiff.Compare(before, after);

        var column = Assert.Single(diff.ColumnChanges);
        Assert.True(column.Added);
        Assert.Equal("Диапазон", column.Key);
        Assert.Equal(1, column.Filled);
        Assert.Contains("Заведён свой столбец «Диапазон» (заполнен в строках: 1)", ParamTableDiff.Describe(diff));
    }

    [Fact]
    public void AColumnEmptiedEverywhere_IsReportedAsRemoved()
    {
        var before = new List<ParamTableRow> { WithExtra(P("P0-10", "50"), "Диапазон", "0…60") };
        var after = new List<ParamTableRow> { P("P0-10", "50") };

        var diff = ParamTableDiff.Compare(before, after);

        Assert.False(Assert.Single(diff.ColumnChanges).Added);
        Assert.Contains("Убран свой столбец «Диапазон»", ParamTableDiff.Describe(diff));
    }

    [Fact]
    public void ANewColumnMakesTheRevisionCountAsChanged_EvenWithoutOtherEdits()
    {
        // «Добавление столбца — тоже изменение»: без этого редакция, в которой только и сделали,
        // что завели и заполнили столбец, отчиталась бы «изменений нет».
        var before = new List<ParamTableRow> { P("P0-10", "50") };
        var after = new List<ParamTableRow> { WithExtra(P("P0-10", "50"), "Диапазон", "0…60") };

        Assert.True(ParamTableDiff.Compare(before, after).Any);
        Assert.DoesNotContain("Изменений нет", ParamTableDiff.Describe(ParamTableDiff.Compare(before, after)));
    }
}
