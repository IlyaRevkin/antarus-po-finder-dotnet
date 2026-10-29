using System.Globalization;
using System.Text.RegularExpressions;

namespace AntarusPoFinder.Core.Domain;

/// <summary>
/// Firmware version string: eq_prefix.sub_prefix.hw_version.sw_version[.YYYYMMDD_HHMM]
/// Example: 2.1.0042.0001.20260422_1348 (НГР, КПЧ, hw42, sw1). hw_version is zero-padded to 4 digits
/// (wider numbers are shown in full, not truncated); sw_version is printed by <see cref="SwNumberCode"/>
/// — счётчиком, который после 99999 продолжается буквой старшего разряда: A0000, A0001, …
/// HwVersion/SwVersion properties stay plain ints. The trailing date/time is
/// optional — the manager decided it isn't actually needed, so upload/reservation now expose a
/// checkbox ("Добавлять дату/время") instead of always stamping DateTime.Now.
/// </summary>
public sealed class FwVersionNumber : IComparable<FwVersionNumber>
{
    /// <summary>Номер ПО — это СЧЁТЧИК (см. <see cref="SwNumberCode"/>): 0001, 0002, … 99999, затем
    /// A0000, A0001 и дальше. Буква — старший разряд счёта, а не пометка варианта, поэтому в разборе
    /// она стоит на первом месте кода и за ней идут ровно четыре цифры. Кириллические двойники
    /// латинских букв принимаются тоже — наладчик набирает код в той раскладке, какая стоит.</summary>
    private static readonly Regex ParsePattern =
        new(@"^(\d+)\.(\d+)\.(\d+)\.([0-9A-Za-zА-ЯЁа-яё]\d*)(?:\.(\d{8}_\d{4}))?$", RegexOptions.Compiled);

    public string Raw { get; }
    public int EqPrefix { get; }
    public int SubPrefix { get; }
    public int HwVersion { get; }
    public int SwVersion { get; }

    /// <summary>Код номера ПО так, как он стоит в строке версии и в имени папки: «0001» или «A0000».
    /// Считается из <see cref="SwVersion"/> кодеком <see cref="SwNumberCode"/> — в базе и в сравнении
    /// номер остаётся целым.</summary>
    public string SwCode => SwNumberCode.Format(SwVersion);

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

    public static FwVersionNumber Build(int eqPrefix, int subPrefix, int hwVersion, int swVersion, DateTime? dt = null,
        bool includeDate = true)
    {
        var hwStr = hwVersion.ToString("D4", CultureInfo.InvariantCulture);
        var swStr = SwNumberCode.Format(swVersion);

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
        // Номер ПО разбирается кодеком: «0001» → 1, «A0000» → 100000. Не разобрался (буква не из
        // алфавита разрядов, цифр не четыре) — это не строка версии, а что-то постороннее.
        if (!SwNumberCode.TryParse(m.Groups[4].Value, out int sw)) return null;
        return new FwVersionNumber(
            s.Trim(),
            int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture),
            int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture),
            int.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture),
            sw,
            m.Groups[5].Success ? m.Groups[5].Value : "");
    }

    public override string ToString() => Raw;

    /// <summary>Human-readable: 'hw42.sw1  (22.04.2026 13:48)', or just 'hw42.sw1' when no date/time was stamped.</summary>
    public string Display
    {
        get
        {
            if (string.IsNullOrEmpty(DtStr)) return $"hw{HwVersion}.sw{SwNumberCode.FormatShort(SwVersion)}";

            string dateStr = DtStr;
            if (DateTime.TryParseExact(DtStr, "yyyyMMdd_HHmm", CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out var d))
            {
                dateStr = d.ToString("dd.MM.yyyy HH:mm", CultureInfo.InvariantCulture);
            }
            return $"hw{HwVersion}.sw{SwNumberCode.FormatShort(SwVersion)}  ({dateStr})";
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
        // Номер ПО сравнивается ЧИСЛОМ, а не кодом: «A0000» (100000) обязан идти после «99999»,
        // а лексически он встал бы перед «0002».
        c = SwVersion.CompareTo(other.SwVersion);
        if (c != 0) return c;
        return string.CompareOrdinal(DtStr, other.DtStr);
    }
}
