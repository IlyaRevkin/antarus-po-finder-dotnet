using System;
using System.IO;

namespace AntarusPoFinder.Core.Services;

/// <summary>Copies a file into the Осмотр folder and resets its «arrival age» — the counterpart to
/// <see cref="InspectionCleanupService"/>, which deletes files older than a configured age.
///
/// Why this exists: a parameter/document file can sit on the software share for years. A plain
/// File.Copy carries the source's old LastWriteTime over to the copy, so the auto-cleanup (which
/// measures age by LastWriteTime) treated a just-dropped file as ancient and deleted it seconds
/// after the operator moved it to Осмотр. Stamping LastWriteTime = now makes the file's age in the
/// folder count from the moment it was dropped, which is what "старше N минут" is supposed to mean.
/// Files that arrive fresh anyway (scan output, phone photos) already have LastWriteTime ≈ now, so
/// this only changes the copied-from-share case.</summary>
public static class InspectionDrop
{
    /// <summary>Copies <paramref name="sourceFile"/> into <paramref name="folder"/> (created if
    /// missing), overwriting a same-named file, then sets the copy's last-write time to
    /// <paramref name="now"/> so cleanup measures dwell time from the drop, not from the source's
    /// original date. Returns the destination path. Best-effort on the timestamp stamp — if the file
    /// system rejects SetLastWriteTime the copy still succeeds (age just falls back to whatever the
    /// copy inherited).</summary>
    public static string CopyInto(string folder, string sourceFile, DateTime now)
    {
        Directory.CreateDirectory(folder);
        var dest = Path.Combine(folder, Path.GetFileName(sourceFile));
        File.Copy(sourceFile, dest, overwrite: true);
        try { File.SetLastWriteTime(dest, now); } catch { /* best effort — copy still landed */ }
        return dest;
    }
}
