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

    // Populated by joins for display purposes.
    public string SubtypeName { get; set; } = "";
    public string FolderName { get; set; } = "";
    public string GroupName { get; set; } = "";
}
