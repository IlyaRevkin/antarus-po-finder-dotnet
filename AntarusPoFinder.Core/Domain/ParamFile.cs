namespace AntarusPoFinder.Core.Domain;

/// <summary>An uploaded parameter file for ПЧ/КПЧ/УПП, linked to a subtype + manufacturer.</summary>
public class ParamFile
{
    public int? Id { get; set; }
    public int? SubtypeId { get; set; }
    public string Manufacturer { get; set; } = "";
    public string Filename { get; set; } = "";
    public string DiskPath { get; set; } = "";
    public string Description { get; set; } = "";
    public string UploadDate { get; set; } = "";
    public bool Archived { get; set; }
    public string Tags { get; set; } = "";

    /// <summary>Стабильный межмашинный идентификатор строки (GUID). Проставляется при вставке
    /// (Database.AddParamFile) и бэкфиллится на старых базах; по нему синхронизация узнаёт «ту же
    /// самую» запись у соседа независимо от имени файла и производителя. Пусто только у экспорта со
    /// старой версии приложения — тогда импорт откатывается на матч по натуральному ключу.</summary>
    public string SyncId { get; set; } = "";

    // Populated by joins for display purposes.
    public string SubtypeName { get; set; } = "";
    public string FolderName { get; set; } = "";
    public string GroupName { get; set; } = "";
}
