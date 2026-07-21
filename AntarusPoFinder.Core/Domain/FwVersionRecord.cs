using System;
using System.Collections.Generic;

namespace AntarusPoFinder.Core.Domain;

/// <summary>A row of the fw_versions table — one uploaded firmware version.</summary>
public class FwVersionRecord
{
    public int? Id { get; set; }
    public int SubtypeId { get; set; }
    public int ControllerId { get; set; }
    public int EqPrefix { get; set; }
    public int SubPrefix { get; set; }
    public int HwVersion { get; set; }
    public int SwVersion { get; set; }
    public string DtStr { get; set; } = "";
    public string VersionRaw { get; set; } = "";
    public string Filename { get; set; } = "";
    public string DiskPath { get; set; } = "";
    public string LocalPath { get; set; } = "";
    public string Description { get; set; } = "";
    public string Changelog { get; set; } = "";
    public List<string> LaunchTypes { get; set; } = new();
    public string IoMapPath { get; set; } = "";
    public string InstructionsPath { get; set; } = "";
    /// <summary>Optional second project — some controllers (e.g. Segnetics) ship a PLC file
    /// (.psl/.lfs, the main Filename/DiskPath above) plus a separate HMI project (.fsprj) with its
    /// own resources; kept as an independent optional slot rather than folded into DiskPath so the
    /// two can be uploaded, browsed and cleared separately (mirrors IoMapPath/InstructionsPath).</summary>
    public string HmiPath { get; set; } = "";
    /// <summary>When the main upload is a folder without a recognizable firmware extension inside
    /// (drivers/support files alongside the real executable), the operator picks which file in the
    /// folder is the one to actually run — recorded here purely for display, never used for copying
    /// (the whole folder is always copied, see UploadView.Upload_Click).</summary>
    public string ExecutableHint { get; set; } = "";
    /// <summary>Same as ExecutableHint but for HmiPath when it's a folder.</summary>
    public string HmiExecutableHint { get; set; } = "";
    public bool IsOpc { get; set; }
    public string RequestNum { get; set; } = "";
    /// <summary>Заводской SN шкафа — отдельно от RequestNum, т.к. для нестандартных (ОПЦ) прошивок
    /// одни наладчики идентифицируют экземпляр по SN шкафа, другие по номеру заявки; оба поля
    /// необязательны и независимы (см. переписку "Предложение по актуализации наименования прошивок").</summary>
    public string CabinetSn { get; set; } = "";
    public bool Archived { get; set; }
    public string UploadDate { get; set; } = "";
    public string Tags { get; set; } = "";
    public int? AuthorId { get; set; }
    public string Status { get; set; } = "active";
    public bool Released { get; set; }

    // Populated by joins for display purposes (not stored on this table).
    public string GroupName { get; set; } = "";
    public string SubtypeName { get; set; } = "";
    public string SubtypeFolder { get; set; } = "";
    public string CtrlName { get; set; } = "";
}
