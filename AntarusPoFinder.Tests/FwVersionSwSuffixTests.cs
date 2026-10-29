using AntarusPoFinder.Core.Domain;

namespace AntarusPoFinder.Tests;

/// <summary>Буква при номере ПО («2.1.0042.0001А») — тикет «буквы в номере SW».
///
/// Главное, что проверяется здесь: номер остался ЧИСЛОМ. На sw_version завязаны выдача следующего
/// номера (Database.GetNextSwVersion — MAX+1), сортировка истории и освобождение номера при откате;
/// превращение номера в строку сломало бы всё это разом, поэтому буква живёт рядом с числом, а не
/// внутри него.</summary>
public class FwVersionSwSuffixTests
{
    [Fact]
    public void Build_AppendsLetterToNumber_KeepingSwVersionNumeric()
    {
        var v = FwVersionNumber.Build(2, 1, 42, 1, includeDate: false, swSuffix: "а");

        Assert.Equal("2.1.0042.0001А", v.Raw);
        Assert.Equal(1, v.SwVersion);
        Assert.Equal("А", v.SwSuffix);
    }

    [Fact]
    public void Build_WithDate_PutsLetterBeforeTheDate()
    {
        var v = FwVersionNumber.Build(2, 1, 42, 7, new DateTime(2026, 4, 22, 13, 48, 0), swSuffix: "B");

        Assert.Equal("2.1.0042.0007B.20260422_1348", v.Raw);
        Assert.Equal("20260422_1348", v.DtStr);
    }

    /// <summary>Номер версии становится именем папки и файла на сетевом диске, поэтому в букву
    /// пускается ровно одна буква — не цифра, не знак, не слово.</summary>
    [Theory]
    [InlineData("", "")]
    [InlineData("  ", "")]
    [InlineData("1", "")]
    [InlineData("/", "")]
    [InlineData("ab", "A")]
    [InlineData("я", "Я")]
    public void NormalizeSwSuffix_AllowsOnlyASingleLetter(string input, string expected) =>
        Assert.Equal(expected, FwVersionNumber.NormalizeSwSuffix(input));

    /// <summary>Разбирать номер с буквой обязаны ОБЕ сборки: заведённая в бете прошивка уезжает в
    /// общий конфиг, и у коллеги с рабочей версией её номер должен остаться читаемым, иначе вместо
    /// версии он получит «не распознано».</summary>
    [Fact]
    public void Parse_ReadsLetterBack()
    {
        var v = FwVersionNumber.Parse("2.1.0042.0001А.20260422_1348");

        Assert.NotNull(v);
        Assert.Equal(1, v!.SwVersion);
        Assert.Equal("А", v.SwSuffix);
        Assert.Equal("20260422_1348", v.DtStr);
    }

    [Fact]
    public void Parse_PlainNumberHasNoSuffix()
    {
        var v = FwVersionNumber.Parse("2.1.0042.0001");

        Assert.NotNull(v);
        Assert.Equal("", v!.SwSuffix);
    }

    /// <summary>Вариант с буквой встаёт СЛЕДОМ за своим номером, а не вперемешку с ним.</summary>
    [Fact]
    public void Compare_PlainNumberComesBeforeTheLetteredOne()
    {
        var plain = FwVersionNumber.Build(2, 1, 42, 1, includeDate: false);
        var lettered = FwVersionNumber.Build(2, 1, 42, 1, includeDate: false, swSuffix: "А");
        var next = FwVersionNumber.Build(2, 1, 42, 2, includeDate: false);

        Assert.True(plain.CompareTo(lettered) < 0);
        Assert.True(lettered.CompareTo(next) < 0);
    }
}
