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

/// <summary>Seed data mirrored 1:1 from the Python app's hierarchy.py — same prefixes/hw values,
/// so an existing po_finder.db keeps working without a data migration.</summary>
public static class HierarchyDefaultsData
{
    public static readonly DefaultEquipmentGroup[] EquipmentGroups =
    [
        new("НГР", 1, 1),
        new("ПЖ", 2, 2),
        new("ТГР", 3, 3),
    ];

    public static readonly DefaultSubType[] SubTypes =
    [
        new("НГР", "КПЧ", 1, "НГР-КПЧ", 1),
        new("НГР", "ВЗУ", 2, "НГР-ВЗУ", 2),
        new("НГР", "КНС", 3, "НГР-КНС", 3),
        new("НГР", "ПП", 4, "НГР-ПП", 4),
        new("ПЖ", "КПЧ", 1, "ПЖ-КПЧ", 1),
        new("ПЖ", "ХП", 2, "ПЖ-ХП", 2),
        new("ПЖ", "FD", 3, "ПЖ-FD", 3),
        new("ТГР", "—", 0, "ТГР", 1),
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
    /// Normal:        НГР-КПЧ_SMH5_2.1.042.001.20260422_1348.psl
    /// ОПЦ заявка:    ПЖ_SMH4_1.1.036.001_(01312)_20260422_1455.psl
    /// ОПЦ SN:        ПЖ_SMH4_1.1.036.001_SN00042_20260422_1455.psl
    /// Оба независимы — шкаф может иметь и заявку, и SN одновременно.
    /// </summary>
    public static string BuildFirmwareFilename(string folderName, string controller, FwVersionNumber version,
        string ext, string requestNum = "", string cabinetSn = "")
    {
        var parts = new[] { folderName, controller, version.NumberPart };
        var name = string.Join("_", System.Linq.Enumerable.Where(parts, p => !string.IsNullOrEmpty(p)));
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
