using System.IO;

namespace AntarusPoFinder.Core.Services;

/// <summary>Общая папка на диске предприятия, которую администратор может переназначить: наклейки,
/// типовые паспорта и всё, что появится дальше в этом же духе. Правило одно на всех и записано
/// здесь, а не копией в каждой сущности: пусто — подпапка по умолчанию от корня диска,
/// относительный путь — тоже от корня (буква диска у каждой машины своя, и путь всё равно
/// сойдётся), абсолютный — как есть. Корень не настроен и путь не абсолютный — показывать нечего,
/// null.</summary>
public static class SharedFolderPath
{
    public static string? Resolve(string? diskRoot, string? configured, string defaultSubfolder)
    {
        var value = (configured ?? "").Trim();
        if (value.Length > 0 && Path.IsPathRooted(value)) return value;
        if (string.IsNullOrWhiteSpace(diskRoot)) return null;
        return Path.Combine(diskRoot, value.Length > 0 ? value : defaultSubfolder);
    }
}
