using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using Microsoft.CSharp.RuntimeBinder;

namespace AntarusPoFinder.App.Services;

/// <summary>Scans directly through the WIA Automation Layer COM API (ProgID "WIA.CommonDialog")
/// instead of launching the separate "Сканер Windows" app — ShowAcquireImage() drives the
/// scanner's own acquire/device-selection UI in-process and hands back the scanned image, so no
/// external process window ever opens. Uses late-bound `dynamic` COM calls so the app doesn't need
/// a WIA type-library reference (wiaaut.dll is a Windows component, not a NuGet package).</summary>
public static class WiaScanner
{
    /// <summary>WIA_IPS_XRES / WIA_IPS_YRES — the scan item's horizontal/vertical resolution
    /// properties, in DPI.</summary>
    private const string PropHorizontalRes = "6147";
    private const string PropVerticalRes = "6148";

    /// <summary>Scans, then wraps the result into a single-page PDF at <paramref name="destPath"/>
    /// (should end in .pdf) sized to the document's real physical size at <paramref name="dpi"/>.
    /// Returns false with <paramref name="error"/> null if the user cancelled the scan dialog, or
    /// non-null if WIA itself is unavailable/failed.</summary>
    public static bool TryScan(string destPath, int dpi, out string? error)
    {
        error = null;
        var dialogType = Type.GetTypeFromProgID("WIA.CommonDialog");
        if (dialogType is null)
        {
            error = "WIA (служба сканирования Windows) недоступна на этом компьютере.";
            return false;
        }

        TryApplyResolution(dpi);

        object? dialog = null;
        var tempBmp = Path.Combine(Path.GetTempPath(), $"scan_{Guid.NewGuid():N}.bmp");
        try
        {
            dialog = Activator.CreateInstance(dialogType);
            dynamic dyn = dialog!;
            // UseCommonUI:false skips WIA's own property/preview screen (the "another window
            // where you click Scan again" the user was seeing) and scans straight from the
            // default device; AlwaysSelectDevice:false skips the device-picker too when there's
            // just one scanner. Falls back to the full dialog if this overload isn't supported.
            dynamic? imageFile;
            try
            {
                imageFile = dyn.ShowAcquireImage(AlwaysSelectDevice: false, UseCommonUI: false);
            }
            catch (RuntimeBinderException)
            {
                // This WIA version doesn't support the named-argument overload — fall back to
                // the full acquire dialog rather than failing the scan outright.
                imageFile = dyn.ShowAcquireImage();
            }
            if (imageFile is null) return false; // user cancelled the scan

            imageFile.SaveFile(tempBmp);
            Marshal.ReleaseComObject(imageFile);

            ConvertToPdf(tempBmp, destPath, dpi);
            return true;
        }
        catch (COMException ex)
        {
            error = $"Не удалось выполнить сканирование:\n{ex.Message}";
            return false;
        }
        finally
        {
            if (dialog is not null) Marshal.ReleaseComObject(dialog);
            try { if (File.Exists(tempBmp)) File.Delete(tempBmp); } catch { /* best effort */ }
        }
    }

    /// <summary>Best-effort: asks the default scanner to acquire at <paramref name="dpi"/>. Not
    /// every driver exposes or allows changing this property, so any failure here just means the
    /// scan proceeds at the device's own default resolution instead.</summary>
    private static void TryApplyResolution(int dpi)
    {
        var managerType = Type.GetTypeFromProgID("WIA.DeviceManager");
        if (managerType is null) return;

        object? manager = null;
        object? device = null;
        try
        {
            manager = Activator.CreateInstance(managerType);
            dynamic dyn = manager!;
            if (dyn.DeviceInfos.Count < 1) return;

            dynamic deviceInfo = dyn.DeviceInfos[1];
            device = deviceInfo.Connect();
            dynamic dev = device!;
            dynamic item = dev.Items[1];
            TrySetProperty(item.Properties, PropHorizontalRes, dpi);
            TrySetProperty(item.Properties, PropVerticalRes, dpi);
        }
        catch { /* scanner may be busy/unavailable — the acquire dialog below will report that */ }
        finally
        {
            if (device is not null) Marshal.ReleaseComObject(device);
            if (manager is not null) Marshal.ReleaseComObject(manager);
        }
    }

    private static void TrySetProperty(dynamic properties, string propId, int value)
    {
        try
        {
            dynamic prop = properties.Item(propId);
            prop.Value = value;
        }
        catch { /* read-only or unsupported on this device */ }
    }

    /// <summary>Re-encodes the scanned bitmap as JPEG and wraps it in a minimal PDF via
    /// <see cref="SimplePdfWriter"/> — no third-party PDF library needed.</summary>
    private static void ConvertToPdf(string bmpPath, string destPdfPath, int dpi)
    {
        using var scanned = new Bitmap(bmpPath);
        using var rgb = new Bitmap(scanned.Width, scanned.Height, PixelFormat.Format24bppRgb);
        using (var g = Graphics.FromImage(rgb))
            g.DrawImageUnscaled(scanned, 0, 0);

        var jpegCodec = ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == ImageFormat.Jpeg.Guid);
        using var encoderParams = new EncoderParameters(1);
        encoderParams.Param[0] = new EncoderParameter(Encoder.Quality, 85L);

        using var jpegStream = new MemoryStream();
        rgb.Save(jpegStream, jpegCodec, encoderParams);

        SimplePdfWriter.WriteJpegAsPdf(jpegStream.ToArray(), rgb.Width, rgb.Height, dpi, destPdfPath);
    }
}
