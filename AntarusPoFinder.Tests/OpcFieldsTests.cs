using AntarusPoFinder.Core.Domain;

namespace AntarusPoFinder.Tests;

/// <summary>Одна галочка «ОПЦ» вместо прежних двух: включили — открылись два поля (серийный номер
/// шкафа и номер заявки), заполнить надо хотя бы одно. Здесь проверяется само правило — форма
/// загрузки и FirmwareUploadService.Prepare спрашивают его одним и тем же методом.</summary>
public class OpcFieldsTests
{
    [Fact]
    public void OpcOff_AnyFields_Valid()
    {
        Assert.Null(OpcFields.Validate(opcEnabled: false, cabinetSn: "", requestNum: ""));
        Assert.Null(OpcFields.Validate(opcEnabled: false, cabinetSn: "42", requestNum: "1312"));
    }

    [Fact]
    public void OpcOn_BothEmpty_Rejected()
    {
        Assert.Equal(OpcFields.BothEmptyError, OpcFields.Validate(opcEnabled: true, cabinetSn: "", requestNum: ""));
        Assert.False(OpcFields.IsValid(opcEnabled: true, cabinetSn: null, requestNum: null));
    }

    /// <summary>Пробелы — это не заполненное поле: иначе версия уехала бы в папку «ОПЦ» без единого
    /// признака, к какому шкафу она относится, а имя файла осталось бы без суффикса.</summary>
    [Fact]
    public void OpcOn_OnlyWhitespace_Rejected()
    {
        Assert.False(OpcFields.IsValid(opcEnabled: true, cabinetSn: "   ", requestNum: "\t"));
    }

    [Theory]
    [InlineData("42", "")]
    [InlineData("", "1312")]
    [InlineData("42", "1312")]
    public void OpcOn_AtLeastOneFilled_Valid(string cabinetSn, string requestNum)
    {
        Assert.Null(OpcFields.Validate(opcEnabled: true, cabinetSn, requestNum));
    }

    /// <summary>«При включённой ОПЦ поле sw не указывается»: галочку «не увеличивать версию ПО (sw)»
    /// форма прячет, а служба загрузки её игнорирует — чтобы поведение не зависело от того, успел ли
    /// интерфейс сбросить флажок.</summary>
    [Fact]
    public void SwVersionChoice_OnlyOutsideOpc()
    {
        Assert.True(OpcFields.SwVersionChoiceApplies(opcEnabled: false));
        Assert.False(OpcFields.SwVersionChoiceApplies(opcEnabled: true));
    }
}
