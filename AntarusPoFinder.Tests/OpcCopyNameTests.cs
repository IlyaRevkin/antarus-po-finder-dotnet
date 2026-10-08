using AntarusPoFinder.Core.Domain;
using Xunit;

namespace AntarusPoFinder.Tests;

/// <summary>Что кладётся в буфер по «Копировать» на карточке выдачи.
///
/// Жалоба Ильи дословно: «в копировании версии не отображается номер заявки и сн, а для опц при
/// загрузке мы специально сделали написание файла и копироваться должно с ним».
///
/// Суть в том, что у ОПЦ голый номер версии бесполезен: он одинаков у всей линейки, и найти по нему
/// нужный файл на диске нельзя — файл назван с метками заявки и заводского номера
/// (FirmwareNaming.BuildFirmwareFilename). Скопированный номер обязан совпадать с именем файла, иначе
/// его некуда вставить.</summary>
public class OpcCopyNameTests
{
    private static FwVersionNumber Version(string raw) => FwVersionNumber.Parse(raw)!;

    /// <summary>Главное: скопированное и имя файла на диске — одно и то же (без расширения).
    /// Проверяется сравнением с самим построителем имени, а не с записанной сюда строкой: именно
    /// РАСХОЖДЕНИЕ этих двух мест и было бы ошибкой, а совпасть с константой они могут и порознь.</summary>
    [Fact]
    public void Скопированное_совпадает_с_именем_файла_на_диске()
    {
        var v = Version("1.1.036.001.20260422_1455");

        var copied = FirmwareNaming.CopyableVersionName(v.Raw, isOpc: true, "01312", "00042");
        var filename = FirmwareNaming.BuildFirmwareFilename(v, ".psl", "01312", "00042");

        Assert.Equal(filename, copied + ".psl");
    }

    [Fact]
    public void У_опц_копируется_номер_с_заявкой()
    {
        var copied = FirmwareNaming.CopyableVersionName("1.1.036.001.20260422_1455", isOpc: true, "01312", "");

        Assert.Contains("(01312)", copied);
        Assert.StartsWith("1.1.036.001.20260422_1455", copied);
    }

    [Fact]
    public void У_опц_копируется_номер_с_заводским_номером_шкафа()
    {
        var copied = FirmwareNaming.CopyableVersionName("1.1.036.001.20260422_1455", isOpc: true, "", "00042");

        Assert.Contains("_SN00042", copied);
    }

    /// <summary>У обычной прошивки копируется ровно её номер — как было. Метки там не из чего
    /// строить, и дописывать к номеру нечего.</summary>
    [Fact]
    public void У_обычной_прошивки_копируется_только_номер()
    {
        Assert.Equal("2.1.042.001.20260422_1348",
            FirmwareNaming.CopyableVersionName("2.1.042.001.20260422_1348", isOpc: false, "", ""));
    }

    /// <summary>ОПЦ без единого номера (такого быть не должно — см. OpcFields, одно из двух полей
    /// обязательно, — но запись могла приехать с чужой машины) копируется как обычная, а не с
    /// огрызком вроде «…_()».</summary>
    [Fact]
    public void Опц_без_номеров_копируется_как_обычная()
    {
        Assert.Equal("1.1.036.001.20260422_1455",
            FirmwareNaming.CopyableVersionName("1.1.036.001.20260422_1455", isOpc: true, "", ""));
    }
}
