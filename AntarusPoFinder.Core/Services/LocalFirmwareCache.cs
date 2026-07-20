using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace AntarusPoFinder.Core.Services;

/// <summary>Path/name helpers for the naladchik's locally cached firmware copies under
/// <see cref="ConfigService.LocalFw"/> — single source of truth so Search's "Скачать"/"Обновить"
/// flow and the background firmware-update scan agree on where a given firmware lives on disk.</summary>
public static class LocalFirmwareCache
{
    public static string SanitizeName(string name) => Regex.Replace(name, @"[^\w\-]", "_");

    public static string DirFor(string name) => Path.Combine(ConfigService.LocalFw, SanitizeName(name));

    public static bool HasVersion(string name, string versionRaw)
    {
        var dir = Path.Combine(DirFor(name), versionRaw);
        return Directory.Exists(dir) && Directory.EnumerateFileSystemEntries(dir).Any();
    }

    public static bool HasAny(string name)
    {
        var dir = DirFor(name);
        return Directory.Exists(dir) && Directory.EnumerateFileSystemEntries(dir, "*", SearchOption.AllDirectories).Any();
    }
}
