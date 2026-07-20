using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace AntarusPoFinder.Core.Services;

public record PslComposition(string Mode, uint? Raw, bool HeaderValid);
public record PslPlc(string DeviceKey, string Model, string Modification);
public record PslInfo(PslPlc Plc, PslComposition Composition);

/// <summary>
/// Reads PLC model/composition metadata directly from Segnetics SMLogix project files (.psl)
/// so the upload form can suggest a controller. 1:1 port of app/infrastructure/psl_inspector.py.
/// </summary>
public static class PslInspector
{
    private const int HeaderLength = 56;
    private const int CompositionOffset = 52;
    private static readonly byte[] Magic = Encoding.ASCII.GetBytes("@S M   L o g i x   P r o j e c t");

    private static readonly Regex DeviceJsonRe =
        new(@"""(?:key|owner)""\s*:\s*""(?<key>[^""]+)""", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex DeviceXmlRe =
        new(@"<device>\s*(?<device>[^<]+)\s*</device>", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex DeviceKeyRe = new(
        @"^(?<model>SMH4|SMH5|PXL2|PIXEL\d+)(?:-(?<hardware_model>(?<modification>\d{4})-\d{2}-\d+|.+))?$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static PslInfo Inspect(string path)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException($"PSL file was not found: {path}");

        var data = File.ReadAllBytes(path);
        var deviceKey = ReadDeviceKey(data);
        return new PslInfo(
            new PslPlc(deviceKey, ExtractModel(deviceKey), ExtractModification(deviceKey)),
            ReadComposition(data));
    }

    private static PslComposition ReadComposition(byte[] data)
    {
        if (data.Length < HeaderLength)
            return new PslComposition("unknown", null, false);

        if (!data.AsSpan(0, Magic.Length).SequenceEqual(Magic))
            return new PslComposition("unknown", null, false);

        var rawMode = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(CompositionOffset, 4));
        var mode = rawMode switch { 0 => "debug", 1 => "working", _ => "unknown" };
        return new PslComposition(mode, rawMode, true);
    }

    private static string ReadDeviceKey(byte[] data)
    {
        var text = Encoding.UTF8.GetString(data);
        var references = new List<string>();

        foreach (Match m in DeviceJsonRe.Matches(text))
            references.Add(NormalizeDeviceKey(m.Groups["key"].Value));
        foreach (Match m in DeviceXmlRe.Matches(text))
            references.Add(NormalizeDeviceKey(m.Groups["device"].Value));

        return MergeDeviceKeys(references);
    }

    private static string MergeDeviceKeys(List<string> references)
    {
        var known = references.Where(r => ExtractModel(r).Length > 0).ToList();
        if (known.Count == 0) return "";

        var models = known.Select(ExtractModel).Distinct().ToList();
        if (models.Count != 1) return "";

        var hardwareModels = known.Select(ExtractHardwareModel).Where(h => h.Length > 0).Distinct().ToList();
        if (hardwareModels.Count > 1) return "";

        return known.OrderByDescending(r => ExtractHardwareModel(r).Length > 0 ? 1 : 0).First();
    }

    private static string ExtractHardwareModel(string deviceKey)
    {
        var m = DeviceKeyRe.Match(deviceKey);
        return m.Success && m.Groups["hardware_model"].Success ? Normalize(m.Groups["hardware_model"].Value) : "";
    }

    private static string ExtractModel(string deviceKey)
    {
        var m = DeviceKeyRe.Match(deviceKey);
        return m.Success ? Normalize(m.Groups["model"].Value) : "";
    }

    private static string ExtractModification(string deviceKey)
    {
        var m = DeviceKeyRe.Match(deviceKey);
        return m.Success && m.Groups["modification"].Success ? Normalize(m.Groups["modification"].Value) : "";
    }

    private static string Normalize(string value) => value.Replace("\0", "").Trim().ToUpperInvariant();

    private static string NormalizeDeviceKey(string value)
    {
        var normalized = Normalize(value);
        return DeviceKeyRe.IsMatch(normalized) ? normalized : "";
    }
}
