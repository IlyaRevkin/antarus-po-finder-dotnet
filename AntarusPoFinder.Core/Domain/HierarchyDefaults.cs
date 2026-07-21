namespace AntarusPoFinder.Core.Domain;

/// <summary>Special folder names, always created alongside controller folders.</summary>
public static class HierarchyFolders
{
    public const string Instructions = "Инструкция";
    public const string IoMap = "Карта ВВ";
    public const string Opc = "ОПЦ";
    public const string UnknownFw = "! Неизвестное";
    public const string UnknownParams = "! Неизвестные параметры";
}

public record DefaultEquipmentGroup(string Name, int Prefix, int SortOrder);
public record DefaultSubType(string GroupName, string Name, int Prefix, string FolderName, int SortOrder);
public record DefaultController(string Name, int SortOrder);
public record DefaultModification(string ControllerName, string DisplayName, int HwVersion, int SortOrder, string Description);

/// <summary>Seed data for a brand-new po_finder.db — full 5-group catalogue (ПЖ/НГР/ТГР/ВЗУ/ШУЗ),
/// mirroring the reference numbering table (see FwVersionTableExportService, which now builds that
/// table straight off the live DB rows these seed) — prefixes ПЖ=1, НГР=2, ТГР=3, ВЗУ=4, ШУЗ=5,
/// confirmed with colleagues. Existing installs are never rewritten wholesale from this array — see
/// Database.EnsureDefaultEquipmentGroups/EnsureDefaultEquipmentSubtypes, which only add a group/
/// subtype here if no row with that name exists yet, never touching/renaming/re-prefixing what's
/// already there (e.g. an install's existing НГР-ВЗУ subtype stays exactly as-is even though ВЗУ
/// is also its own top-level group below).</summary>
public static class HierarchyDefaultsData
{
    public static readonly DefaultEquipmentGroup[] EquipmentGroups =
    [
        new("ПЖ", 1, 1),
        new("НГР", 2, 2),
        new("ТГР", 3, 3),
        new("ВЗУ", 4, 4),
        new("ШУЗ", 5, 5),
    ];

    public static readonly DefaultSubType[] SubTypes =
    [
        new("ПЖ", "2.0", 0, "ПЖ-2.0", 1),
        new("ПЖ", "FD", 1, "ПЖ-FD", 2),
        new("ПЖ", "КПЧ", 2, "ПЖ-КПЧ", 3),
        new("ПЖ", "ХП", 3, "ПЖ-ХП", 4),
        new("ПЖ", "ПИ", 4, "ПЖ-ПИ", 5),
        new("ПЖ", "ПКР", 5, "ПЖ-ПКР", 6),
        new("ПЖ", "ПКР ПИ", 6, "ПЖ-ПКР ПИ", 7),
        new("НГР", "2.0", 0, "НГР-2.0", 1),
        new("НГР", "КНС", 1, "НГР-КНС", 2),
        new("НГР", "УПД", 2, "НГР-УПД", 3),
        new("НГР", "КР", 3, "НГР-КР", 4),
        new("НГР", "XL", 4, "НГР-XL", 5),
        new("НГР", "X2", 5, "НГР-X2", 6),
        new("ТГР", "—", 0, "ТГР", 1),
        new("ВЗУ", "—", 0, "ВЗУ", 1),
        new("ВЗУ", "ПИ", 1, "ВЗУ-ПИ", 2),
        new("ШУЗ", "—", 0, "ШУЗ", 1),
    ];

    public static readonly DefaultController[] Controllers =
    [
        new("SMH4", 1),
        new("SMH5", 2),
        new("KINCO", 3),
        new("PIXEL2", 4),
        new("PIXEL", 5),
    ];

    public static readonly DefaultModification[] Modifications =
    [
        new("SMH4", "SMH4", 4, 1, "HMI 4.3\", 5DI / 3DO / 2AI / 2AO, RS-485, Ethernet"),
        new("SMH5", "SMH5", 5, 1, "HMI 5\", 8DI / 8DO / 4AI / 2AO, RS-485, Ethernet"),
        new("KINCO", "KINCO", 20, 1, "ПЛК KINCO K-серия, модульный, высокоскоростной I/O"),
        new("PIXEL2", "PIXEL2", 40, 1, "универсальный"),
        new("PIXEL2", "PIXEL2-1020", 41, 2, "8DI / 6DO / 8AI / 2AO"),
        new("PIXEL2", "PIXEL2-1021", 42, 3, "8DI / 5DO / 8AI / 4AO"),
        new("PIXEL2", "PIXEL2-1320", 43, 4, "8DI / 6DO / 6AI / 2AO"),
        new("PIXEL2", "PIXEL2-1321", 44, 5, "8DI / 5DO / 6AI / 4AO"),
        new("PIXEL2", "PIXEL2-3022", 45, 6, "16DI / 12DO"),
        new("PIXEL2", "PIXEL2-3322", 46, 7, "16DI / 8DO / 4AI"),
        new("PIXEL2", "PIXEL2-3422", 47, 8, "16DI / 8DO / 4AO"),
        // Pixel — оригинальное (первое) поколение, отдельное от PIXEL2.
        // Ориентировочный список моделей по каталогу Segnetics, не проверен по реальным шкафам.
        new("PIXEL", "PIXEL-2511", 48, 1, "первое поколение Pixel, требует уточнения характеристик"),
        new("PIXEL", "PIXEL-2512", 49, 2, "первое поколение Pixel, требует уточнения характеристик"),
        new("PIXEL", "PIXEL-2514", 50, 3, "первое поколение Pixel, требует уточнения характеристик"),
        new("PIXEL", "PIXEL-2515", 51, 4, "первое поколение Pixel, требует уточнения характеристик"),
    ];
}

public static class FirmwareNaming
{
    /// <summary>
    /// Normal:        2.1.042.001.20260422_1348.psl
    /// ОПЦ заявка:    1.1.036.001_(01312)_20260422_1455.psl
    /// ОПЦ SN:        1.1.036.001_SN00042_20260422_1455.psl
    /// Оба независимы — шкаф может иметь и заявку, и SN одновременно.
    ///
    /// Round with the "убрать группу/подтип/контроллер из имени файла" request: the filename used to
    /// be prefixed with the group/subtype folder name and controller name (e.g.
    /// "НГР-КПЧ_SMH5_2.1.042...") and — for a lone .psl upload specifically — always carried a
    /// trailing "_0" regardless of the real sw_version (a Segnetics-toolchain-compatibility quirk
    /// per earlier round notes). Per the operator's explicit call: the folder path already encodes
    /// group/subtype/controller (nothing is lost — see HierarchyService.FwPath), and the numeric
    /// version string alone (`FwVersionNumber.Parse`) is enough for the app to re-derive them for
    /// search/display, so keeping them in the filename too was redundant and made names unwieldy.
    /// Group/subtype/controller now get added as ordinary Tags instead (see UploadView.Upload_Click)
    /// so tag-based search keeps finding the file by the same words. The "_0" suffix is dropped
    /// entirely (no replacement) — flagged in the release notes as worth re-confirming against the
    /// Segnetics toolchain in practice, since that's the one part of this specific rename that isn't
    /// purely cosmetic.
    /// </summary>
    public static string BuildFirmwareFilename(FwVersionNumber version, string ext, string requestNum = "", string cabinetSn = "")
    {
        var name = version.NumberPart;
        if (!string.IsNullOrEmpty(requestNum))
            name += $"_({requestNum})";
        if (!string.IsNullOrEmpty(cabinetSn))
            name += $"_SN{cabinetSn}";
        if (!string.IsNullOrEmpty(version.DtStr))
            name += $"_{version.DtStr}";
        if (!string.IsNullOrEmpty(ext) && !ext.StartsWith('.'))
            ext = "." + ext;
        return (name + ext).ToUpperInvariant();
    }
}
