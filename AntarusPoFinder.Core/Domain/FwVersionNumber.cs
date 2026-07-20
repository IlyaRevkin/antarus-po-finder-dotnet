using System;
using System.Globalization;
using System.Text.RegularExpressions;

namespace AntarusPoFinder.Core.Domain;

/// <summary>
/// Firmware version string: eq_prefix.sub_prefix.hw_version.sw_version[.YYYYMMDD_HHMM]
/// Example: 2.1.042.0001.20260422_1348 (НГР, КПЧ, hw42, sw1). hw_version is zero-padded to 3
/// digits and sw_version to 4 digits in the raw string (numbers wider than that are shown in
/// full, not truncated) — HwVersion/SwVersion properties stay plain ints. The trailing date/time is
/// optional — the manager decided it isn't actually needed, so upload/reservation now expose a
/// checkbox ("Добавлять дату/время") instead of always stamping DateTime.Now.
/// </summary>
public sealed class FwVersionNumber : IComparable<FwVersionNumber>
{
    private static readonly Regex ParsePattern =
        new(@"^(\d+)\.(\d+)\.(\d+)\.(\d+)(?:\.(\d{8}_\d{4}))?$", RegexOptions.Compiled);

    public string Raw { get; }
    public int EqPrefix { get; }
    public int SubPrefix { get; }
    public int HwVersion { get; }
    public int SwVersion { get; }
    public string DtStr { get; }

    private FwVersionNumber(string raw, int eqPrefix, int subPrefix, int hwVersion, int swVersion, string dtStr)
    {
        Raw = raw;
        EqPrefix = eqPrefix;
        SubPrefix = subPrefix;
        HwVersion = hwVersion;
        SwVersion = swVersion;
        DtStr = dtStr;
    }

    public static FwVersionNumber Build(int eqPrefix, int subPrefix, int hwVersion, int swVersion, DateTime? dt = null, bool includeDate = true)
    {
        var hwStr = hwVersion.ToString("D3", CultureInfo.InvariantCulture);
        var swStr = swVersion.ToString("D4", CultureInfo.InvariantCulture);

        if (!includeDate)
        {
            var rawNoDate = $"{eqPrefix}.{subPrefix}.{hwStr}.{swStr}";
            return new FwVersionNumber(rawNoDate, eqPrefix, subPrefix, hwVersion, swVersion, "");
        }

        var d = dt ?? DateTime.Now;
        var dtStr = d.ToString("yyyyMMdd_HHmm", CultureInfo.InvariantCulture);
        var raw = $"{eqPrefix}.{subPrefix}.{hwStr}.{swStr}.{dtStr}";
        return new FwVersionNumber(raw, eqPrefix, subPrefix, hwVersion, swVersion, dtStr);
    }

    public static FwVersionNumber? Parse(string s)
    {
        var m = ParsePattern.Match(s.Trim());
        if (!m.Success) return null;
        return new FwVersionNumber(
            s.Trim(),
            int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture),
            int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture),
            int.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture),
            int.Parse(m.Groups[4].Value, CultureInfo.InvariantCulture),
            m.Groups[5].Success ? m.Groups[5].Value : "");
    }

    public override string ToString() => Raw;

    public string FolderName => Raw;

    /// <summary>Raw version without the trailing date/time suffix — "eq.sub.hw.sw" only.</summary>
    public string NumberPart =>
        $"{EqPrefix}.{SubPrefix}.{HwVersion.ToString("D3", CultureInfo.InvariantCulture)}.{SwVersion.ToString("D4", CultureInfo.InvariantCulture)}";

    /// <summary>Human-readable: 'hw42.sw1  (22.04.2026 13:48)', or just 'hw42.sw1' when no date/time was stamped.</summary>
    public string Display
    {
        get
        {
            if (string.IsNullOrEmpty(DtStr)) return $"hw{HwVersion}.sw{SwVersion}";

            string dateStr = DtStr;
            if (DateTime.TryParseExact(DtStr, "yyyyMMdd_HHmm", CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out var d))
            {
                dateStr = d.ToString("dd.MM.yyyy HH:mm", CultureInfo.InvariantCulture);
            }
            return $"hw{HwVersion}.sw{SwVersion}  ({dateStr})";
        }
    }

    public int CompareTo(FwVersionNumber? other)
    {
        if (other is null) return 1;
        int c = EqPrefix.CompareTo(other.EqPrefix);
        if (c != 0) return c;
        c = SubPrefix.CompareTo(other.SubPrefix);
        if (c != 0) return c;
        c = HwVersion.CompareTo(other.HwVersion);
        if (c != 0) return c;
        c = SwVersion.CompareTo(other.SwVersion);
        if (c != 0) return c;
        return string.CompareOrdinal(DtStr, other.DtStr);
    }
}
