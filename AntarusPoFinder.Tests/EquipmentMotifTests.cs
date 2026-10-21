using AntarusPoFinder.Core.Domain;
using Xunit;

namespace AntarusPoFinder.Tests;

/// <summary>ЧЕМ РИСОВАТЬ ХОД ЗАГРУЗКИ (бета 2.0).
///
/// Просьба Ильи: «так как у нас компания делает насосные станции блочные тепловые пункты КНС и тп,
/// можно как-то добавить стилизацию, к примеру загрузку прошивки полосу у нгр кпч отображать как
/// насосную станцию… КНС как будто качает из канализации».
///
/// Это не украшение. Заливка идёт минуты, и всё это время на экране полоса, по которой не видно
/// ничего, кроме процента, — в какой шкаф льём, не перепутали ли. Поэтому проверяется главное: что
/// мотив определяется по живым названиям из справочника и что КНС не превращается в обычную
/// насосную (качать стоки и качать воду — разные вещи, и спутать их на экране нельзя).</summary>
public class EquipmentMotifTests
{
    [Theory]
    [InlineData("НГР", "КНС")]
    [InlineData("НГР", "КНС-2")]
    [InlineData("", "кнс")]
    public void КНС_это_канализационная_а_не_обычная_насосная(string group, string subtype)
    {
        Assert.Equal(EquipmentMotif.Sewage, EquipmentMotifResolver.For(group, subtype));
    }

    [Theory]
    [InlineData("НГР", "КПЧ")]
    [InlineData("НГР", "2.0")]
    public void Насосные_группы_это_насосная_станция(string group, string subtype)
    {
        Assert.Equal(EquipmentMotif.PumpStation, EquipmentMotifResolver.For(group, subtype));
    }

    /// <summary>ПОЖАРНЫЕ ШКАФЫ — свой мотив, а не обычная насосная.
    ///
    /// Раньше «ПЖ» попадало в PumpStation: воду качают и те и другие. Просьба Ильи 21.10.2026 —
    /// «для пожарных можно чтобы в оранжево-красный открывалось и заливалось и типа тушило пожар».
    /// Различие нужно именно боковым зрением: перепутать пожарный шкаф с обычным дороже всего.</summary>
    [Theory]
    [InlineData("ПЖ", "ПИ")]
    [InlineData("ПЖ", "")]
    [InlineData("", "пожарный")]
    public void Пожарные_шкафы_узнаются_отдельно(string group, string subtype)
    {
        Assert.Equal(EquipmentMotif.Fire, EquipmentMotifResolver.For(group, subtype));
    }

    [Theory]
    [InlineData("ТГР", "")]
    [InlineData("", "БТП")]
    [InlineData("", "Тепловой пункт")]
    public void Тепловые_пункты_узнаются(string group, string subtype)
    {
        Assert.Equal(EquipmentMotif.HeatPoint, EquipmentMotifResolver.For(group, subtype));
    }

    /// <summary>Незнакомое название — обычная полоса, как было. Справочник пополняет администратор, и
    /// новый подтип не должен ни ломать экран, ни требовать похода в настройки за картинкой.</summary>
    [Theory]
    [InlineData("ВЗУ", "непонятно что")]
    [InlineData("", "")]
    [InlineData(null, null)]
    public void Незнакомое_оборудование_рисуется_обычной_полосой(string? group, string? subtype)
    {
        Assert.Equal(EquipmentMotif.Neutral, EquipmentMotifResolver.For(group, subtype));
    }

    /// <summary>Подпись словами обязательна там, где есть картинка: мотив ловится боковым зрением, а
    /// ошибиться шкафом дорого — значит должно быть и то, что читается прямо.</summary>
    [Theory]
    [InlineData(EquipmentMotif.PumpStation)]
    [InlineData(EquipmentMotif.Sewage)]
    [InlineData(EquipmentMotif.HeatPoint)]
    [InlineData(EquipmentMotif.Valve)]
    [InlineData(EquipmentMotif.Fire)]
    public void У_каждого_мотива_есть_подпись(EquipmentMotif motif)
    {
        Assert.NotEqual("", EquipmentMotifResolver.Caption(motif));
    }

    [Fact]
    public void У_обычной_полосы_подписи_нет()
    {
        Assert.Equal("", EquipmentMotifResolver.Caption(EquipmentMotif.Neutral));
    }
}
