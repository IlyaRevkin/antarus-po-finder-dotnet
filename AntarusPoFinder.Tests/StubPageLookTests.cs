using System;
using System.Linq;
using System.Threading;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AntarusPoFinder.App.Services;
using AntarusPoFinder.Core.Services;
using Xunit;
using ZXing;

namespace AntarusPoFinder.Tests;

/// <summary>Вид страницы-заглушки. Она уходит ЗАКАЗЧИКУ, поэтому проверяется не «нарисовалось без
/// исключения», а то, что на листе действительно есть документ: фирменный знак, линии шапки и
/// подвала, выделенный блок контактов и работающий QR.
///
/// Первую версию владелец забраковал словами «сделай, чтобы это красиво адекватно выглядело»:
/// текст стоял столбиком по центру, вокруг пустая треть листа, ни шапки, ни подвала. Эти проверки
/// удерживают то, что вместо этого собрано.</summary>
public class StubPageLookTests
{
    /// <summary>Рисование идёт на STA-потоке: визуалы WPF на пуле xUnit не живут. Application тут не
    /// нужен — страница рисуется без единого {StaticResource}.</summary>
    private static RenderTargetBitmap Render(StubKind kind)
    {
        RenderTargetBitmap? bitmap = null;
        Exception? error = null;
        var set = StubLayoutSet.Default.Sane();
        var thread = new Thread(() =>
        {
            try
            {
                bitmap = InstructionStubWriter.Draw(set.For(kind), "2.1.0042.0001", set.Contacts);
                bitmap.Freeze();
            }
            catch (Exception ex) { error = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join(TimeSpan.FromMinutes(1));
        if (error is not null) throw new InvalidOperationException(error.ToString(), error);
        return bitmap!;
    }

    private static byte[] Pixels(RenderTargetBitmap bitmap)
    {
        var stride = bitmap.PixelWidth * 4;
        var buffer = new byte[stride * bitmap.PixelHeight];
        bitmap.CopyPixels(buffer, stride, 0);
        return buffer;
    }

    /// <summary>Есть ли на листе хоть один пиксель фирменного синего в этой полосе по высоте. Так
    /// проверяются знак, линия шапки и полоса блока контактов — по цвету, а не по координатам:
    /// координаты меняются при любой правке вёрстки, а фирменный цвет на месте обязан быть.</summary>
    private static bool HasBrandBlue(RenderTargetBitmap bitmap, byte[] px, double fromY, double toY)
    {
        var stride = bitmap.PixelWidth * 4;
        for (var y = (int)(bitmap.PixelHeight * fromY); y < (int)(bitmap.PixelHeight * toY); y++)
        for (var x = 0; x < bitmap.PixelWidth; x++)
        {
            var i = y * stride + x * 4;
            // Pbgra32: B, G, R, A
            if (Math.Abs(px[i] - AntarusMark.Blue.B) <= 6
                && Math.Abs(px[i + 1] - AntarusMark.Blue.G) <= 6
                && Math.Abs(px[i + 2] - AntarusMark.Blue.R) <= 6)
                return true;
        }
        return false;
    }

    /// <summary>ГЛАВНОЕ про QR: он должен читаться и приводить к ЗВОНКУ. Человек у шкафа с бумажной
    /// страницей в руках наводит телефон — и номер уже набирается, а не переписывается по одной цифре.
    /// Проверяется настоящим декодером по готовой картинке страницы, а не по содержимому,
    /// которое мы сами же в код и положили.</summary>
    [Theory]
    [InlineData(StubKind.InDevelopment)]
    [InlineData(StubKind.NotPlanned)]
    [InlineData(StubKind.ServiceNote)]
    public void TheServiceQr_OnEveryPage_DecodesIntoACallToService(StubKind kind)
    {
        var bitmap = Render(kind);
        var px = Pixels(bitmap);

        var reader = new BarcodeReaderGeneric { Options = { PossibleFormats = new[] { BarcodeFormat.QR_CODE } } };
        var result = reader.Decode(new RGBLuminanceSource(px, bitmap.PixelWidth, bitmap.PixelHeight,
            RGBLuminanceSource.BitmapFormat.BGRA32));

        Assert.NotNull(result);
        Assert.StartsWith("tel:", result!.Text);
        // Цифры номера — те же, что напечатаны рядом словами: расходиться им нельзя.
        Assert.Equal(new string(ServiceContacts.Phone.Where(char.IsDigit).ToArray()), result.Text["tel:".Length..]);
    }

    /// <summary>Знак и линия шапки нарисованы: страница должна выглядеть документом ANTARUS, а не
    /// запиской. Ищем фирменный синий в верхней пятой листа.</summary>
    [Theory]
    [InlineData(StubKind.InDevelopment)]
    [InlineData(StubKind.NotPlanned)]
    [InlineData(StubKind.ServiceNote)]
    public void EveryPage_HasTheBrandedHeader(StubKind kind)
    {
        var bitmap = Render(kind);
        Assert.True(HasBrandBlue(bitmap, Pixels(bitmap), 0.0, 0.20),
            "в шапке нет ни знака, ни линии — страница снова выглядит недовёрстанной");
    }

    /// <summary>Три вида — одна серия: у всех одинаковая рамка документа. Проверяется тем, что и знак
    /// в шапке, и блок контактов в нижней половине есть у каждого.</summary>
    [Fact]
    public void TheThreeKinds_ShareTheSameDocumentFrame()
    {
        foreach (var kind in StubKinds.All)
        {
            var bitmap = Render(kind);
            var px = Pixels(bitmap);
            Assert.True(HasBrandBlue(bitmap, px, 0.0, 0.20), $"{kind}: нет шапки");
            Assert.True(HasBrandBlue(bitmap, px, 0.50, 1.0), $"{kind}: нет блока контактов");
        }
    }

    /// <summary>Логотип разбирается из тех же контуров, что лежат в Assets/antarus-logo.svg, — и это
    /// именно кольцо с треугольником, а не «похожий кружок»: у знака два цвета, оба обязаны быть.</summary>
    [Fact]
    public void TheMark_DrawsBothOfItsColours()
    {
        var bitmap = Render(StubKind.InDevelopment);
        var px = Pixels(bitmap);
        var stride = bitmap.PixelWidth * 4;

        bool red = false, blue = false;
        for (var y = 0; y < bitmap.PixelHeight / 5; y++)
        for (var x = 0; x < bitmap.PixelWidth / 3; x++)
        {
            var i = y * stride + x * 4;
            if (Math.Abs(px[i] - AntarusMark.Red.B) <= 8 && Math.Abs(px[i + 1] - AntarusMark.Red.G) <= 8
                                                         && Math.Abs(px[i + 2] - AntarusMark.Red.R) <= 8) red = true;
            if (Math.Abs(px[i] - AntarusMark.Blue.B) <= 6 && Math.Abs(px[i + 1] - AntarusMark.Blue.G) <= 6
                                                          && Math.Abs(px[i + 2] - AntarusMark.Blue.R) <= 6) blue = true;
        }

        Assert.True(blue, "кольцо знака не нарисовано");
        Assert.True(red, "треугольник знака не нарисован");
    }
}
