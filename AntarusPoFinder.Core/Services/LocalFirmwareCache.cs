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

    /// <summary>Имя файла-метки «закреплено локально». Пустой файл рядом с прошивкой в её папке
    /// версии — переживает и уборку старых копий (CleanupOldLocalVersions), и любое пересоздание БД:
    /// метка живёт на диске вместе с самими файлами, а не в базе. Так наладчик держит нужную старую
    /// версию под рукой, даже когда на сетевом диске её уже сделали неактуальной или удалили.</summary>
    public const string KeepMarkerName = ".keep";

    public static string KeepMarkerPath(string name, string versionRaw) =>
        Path.Combine(DirFor(name), versionRaw, KeepMarkerName);

    /// <summary>Закреплена ли конкретная версия в кэше — по метке в её папке.</summary>
    public static bool IsKept(string name, string versionRaw) => File.Exists(KeepMarkerPath(name, versionRaw));

    /// <summary>Закреплена ли версия, папка которой известна напрямую (для уборки, где имя прошивки
    /// уже не восстановить из имени каталога).</summary>
    public static bool IsKeptDir(string versionDir) => File.Exists(Path.Combine(versionDir, KeepMarkerName));

    /// <summary>Поставить/снять метку закрепления. Ставится только если папка версии реально существует
    /// (закреплять нечего, пока прошивка не скачана локально); снятие удаляет метку, если она есть.</summary>
    public static void SetKept(string name, string versionRaw, bool kept)
    {
        var dir = Path.Combine(DirFor(name), versionRaw);
        var marker = Path.Combine(dir, KeepMarkerName);
        if (kept)
        {
            if (!Directory.Exists(dir)) return;
            try { File.WriteAllText(marker, ""); } catch { /* best effort */ }
        }
        else
        {
            try { if (File.Exists(marker)) File.Delete(marker); } catch { /* best effort */ }
        }
    }
}
