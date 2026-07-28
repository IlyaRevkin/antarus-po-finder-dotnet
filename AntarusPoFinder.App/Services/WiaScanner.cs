using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using Microsoft.CSharp.RuntimeBinder;

namespace AntarusPoFinder.App.Services;

/// <summary>Scans directly through the WIA Automation Layer COM API. The primary path connects to
/// the default scanner via WIA.DeviceManager and calls Items[1].Transfer() — a fully silent, in-
/// process acquire with NO window at all (this is what the operator asked for: press "Сканировать"
/// and the scanner just runs). Only if no scanner can be enumerated do we fall back to
/// WIA.CommonDialog's acquire UI, so setups that expose a scanner only through the common dialog
/// still work. Uses late-bound `dynamic` COM calls so the app doesn't need a WIA type-library
/// reference (wiaaut.dll is a Windows component, not a NuGet package).</summary>
public static class WiaScanner
{
    /// <summary>WIA_IPS_XRES / WIA_IPS_YRES — the scan item's horizontal/vertical resolution
    /// properties, in DPI.</summary>
    private const string PropHorizontalRes = "6147";
    private const string PropVerticalRes = "6148";

    /// <summary>WiaDeviceType.ScannerDeviceType — filters the device list to scanners (skips
    /// cameras/video, which WIA also enumerates).</summary>
    private const int ScannerDeviceType = 1;

    /// <summary>wiaFormatBMP — the image format Transfer() hands back (a plain BMP we re-encode to
    /// PDF below).</summary>
    private const string FormatBmp = "{B96B3CAB-0728-11D3-9D7B-0000F81EF32E}";

    /// <summary>Scans, then wraps the result into a single-page PDF at <paramref name="destPath"/>
    /// (should end in .pdf) sized to the document's real physical size at <paramref name="dpi"/>.
    /// Returns false with <paramref name="error"/> null if the user cancelled a fallback scan
    /// dialog, or non-null if WIA itself is unavailable/failed.</summary>
    public static bool TryScan(string destPath, int dpi, out string? error)
    {
        error = null;
        var tempBmp = Path.Combine(Path.GetTempPath(), $"scan_{Guid.NewGuid():N}.bmp");
        try
        {
            // Primary: silent direct transfer from the default scanner — no window opens.
            if (TryDirectTransfer(dpi, tempBmp, out error))
            {
                ConvertToPdf(tempBmp, destPath, dpi);
                return true;
            }
            // error != null → a real scanner error (busy/no paper/driver) already surfaced, stop.
            if (error is not null) return false;

            // error == null → no scanner was enumerable through DeviceManager at all. Fall back to
            // the common acquire dialog so device-selection-only setups still function.
            if (!TryDialogAcquire(dpi, tempBmp, out error)) return false;

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
            try { if (File.Exists(tempBmp)) File.Delete(tempBmp); } catch { /* best effort */ }
        }
    }

    /// <summary>Connects to the first available scanner and transfers a page straight to
    /// <paramref name="tempBmp"/> with no UI. Returns true on success; false with
    /// <paramref name="error"/> null when there simply is no scanner to drive (caller then falls
    /// back to the dialog), or non-null when a scanner was found but the acquire itself failed.</summary>
    private static bool TryDirectTransfer(int dpi, string tempBmp, out string? error)
    {
        error = null;
        var managerType = Type.GetTypeFromProgID("WIA.DeviceManager");
        if (managerType is null) return false; // WIA not present → try the CommonDialog path

        object? manager = null;
        object? device = null;
        object? imageFile = null;
        try
        {
            manager = Activator.CreateInstance(managerType);
            dynamic dyn = manager!;
            dynamic infos = dyn.DeviceInfos;

            dynamic? scannerInfo = null;
            for (int i = 1; i <= infos.Count; i++) // WIA collections are 1-based
            {
                dynamic info = infos[i];
                if ((int)info.Type == ScannerDeviceType) { scannerInfo = info; break; }
            }
            if (scannerInfo is null) return false; // no scanner → let caller fall back to dialog

            device = scannerInfo.Connect();
            dynamic dev = device!;
            dynamic item = dev.Items[1];
            TrySetProperty(item.Properties, PropHorizontalRes, dpi);
            TrySetProperty(item.Properties, PropVerticalRes, dpi);

            imageFile = item.Transfer(FormatBmp);
            dynamic img = imageFile!;
            img.SaveFile(tempBmp);
            return true;
        }
        catch (COMException ex)
        {
            // A scanner exists but the scan failed (busy, no paper, cover open, driver error).
            // Report it rather than silently opening a dialog — the operator asked for a direct scan.
            error = $"Не удалось выполнить сканирование:\n{ex.Message}";
            return false;
        }
        finally
        {
            if (imageFile is not null) Marshal.ReleaseComObject(imageFile);
            if (device is not null) Marshal.ReleaseComObject(device);
            if (manager is not null) Marshal.ReleaseComObject(manager);
        }
    }

    /// <summary>Fallback acquire through WIA.CommonDialog when no scanner could be enumerated
    /// directly. Returns false with <paramref name="error"/> null if the user cancelled.</summary>
    private static bool TryDialogAcquire(int dpi, string tempBmp, out string? error)
    {
        error = null;
        var dialogType = Type.GetTypeFromProgID("WIA.CommonDialog");
        if (dialogType is null)
        {
            error = "WIA (служба сканирования Windows) недоступна на этом компьютере.";
            return false;
        }

        object? dialog = null;
        object? imageFile = null;
        try
        {
            dialog = Activator.CreateInstance(dialogType);
            dynamic dyn = dialog!;
            dynamic? acquired;
            try
            {
                acquired = dyn.ShowAcquireImage(AlwaysSelectDevice: false, UseCommonUI: false);
            }
            catch (RuntimeBinderException)
            {
                acquired = dyn.ShowAcquireImage();
            }
            if (acquired is null) return false; // user cancelled

            imageFile = acquired;
            dynamic img = imageFile!;
            img.SaveFile(tempBmp);
            return true;
        }
        finally
        {
            if (imageFile is not null) Marshal.ReleaseComObject(imageFile);
            if (dialog is not null) Marshal.ReleaseComObject(dialog);
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
