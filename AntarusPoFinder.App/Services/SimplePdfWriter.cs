using System.Globalization;
using System.IO;
using System.Text;

namespace AntarusPoFinder.App.Services;

/// <summary>Hand-rolled single-page PDF around one JPEG image — no PDF library dependency needed
/// since PDF's DCTDecode filter accepts a JPEG byte stream directly as an XObject. Used to turn a
/// WIA scan into a real, appropriately-sized (based on scan DPI) PDF instead of a raw .bmp.</summary>
public static class SimplePdfWriter
{
    public static void WriteJpegAsPdf(byte[] jpeg, int pixelWidth, int pixelHeight, int dpi, string destPath)
    {
        var widthPt = pixelWidth * 72.0 / dpi;
        var heightPt = pixelHeight * 72.0 / dpi;

        using var ms = new MemoryStream();
        var offsets = new long[6]; // objects 1..5; index 0 unused

        void WriteAscii(string s) => ms.Write(Encoding.ASCII.GetBytes(s));
        string F(double d) => d.ToString("F2", CultureInfo.InvariantCulture);

        WriteAscii("%PDF-1.4\n");

        offsets[1] = ms.Position;
        WriteAscii("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");

        offsets[2] = ms.Position;
        WriteAscii("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");

        offsets[3] = ms.Position;
        WriteAscii($"3 0 obj\n<< /Type /Page /Parent 2 0 R /Resources << /XObject << /Im0 4 0 R >> " +
                    $"/ProcSet [/PDF /ImageC] >> /MediaBox [0 0 {F(widthPt)} {F(heightPt)}] /Contents 5 0 R >>\nendobj\n");

        offsets[4] = ms.Position;
        WriteAscii($"4 0 obj\n<< /Type /XObject /Subtype /Image /Width {pixelWidth} /Height {pixelHeight} " +
                    $"/ColorSpace /DeviceRGB /BitsPerComponent 8 /Filter /DCTDecode /Length {jpeg.Length} >>\nstream\n");
        ms.Write(jpeg, 0, jpeg.Length);
        WriteAscii("\nendstream\nendobj\n");

        offsets[5] = ms.Position;
        var content = $"q {F(widthPt)} 0 0 {F(heightPt)} 0 0 cm /Im0 Do Q";
        WriteAscii($"5 0 obj\n<< /Length {content.Length} >>\nstream\n{content}\nendstream\nendobj\n");

        var xrefOffset = ms.Position;
        WriteAscii("xref\n0 6\n");
        WriteAscii("0000000000 65535 f \n");
        for (var i = 1; i <= 5; i++)
            WriteAscii($"{offsets[i]:D10} 00000 n \n");
        WriteAscii($"trailer\n<< /Size 6 /Root 1 0 R >>\nstartxref\n{xrefOffset}\n%%EOF");

        File.WriteAllBytes(destPath, ms.ToArray());
    }
}
