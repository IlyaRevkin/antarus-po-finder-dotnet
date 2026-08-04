using System;
using System.IO;

namespace AntarusPoFinder.Core.Services;

/// <summary>Тихая пасхалка на номере версии: одна фотография на всех, лежащая рядом с остальным общим
/// (наклейки, типовые паспорта) в <c>&lt;диск&gt;\Конфиг\Служебное</c>. Файл живёт на общем диске,
/// поэтому виден всем машинам без отдельной раздачи, а путь к нему хранится хвостом от корня диска
/// (см. <see cref="SharedFolderPath.ToPortable"/>) — буква диска у каждой машины своя, а хвост
/// сойдётся. Никакого UI и уведомлений: задал — лёг файл на диск — у всех через обычную
/// синхронизацию открывается.</summary>
public static class EasterEggPhoto
{
    public const string Subfolder = @"Конфиг\Служебное";

    /// <summary>Абсолютный путь к фотографии на этой машине из хранимого (машинно-независимого)
    /// значения настройки. Пусто — фотографии нет, null. Относительное значение разворачивается от
    /// корня диска; уже абсолютное (на всякий случай) отдаём как есть.</summary>
    public static string? Resolve(string? diskRoot, string? configured)
    {
        var value = (configured ?? "").Trim();
        if (value.Length == 0) return null;
        if (Path.IsPathRooted(value)) return value;
        if (string.IsNullOrWhiteSpace(diskRoot)) return null;
        return Path.Combine(diskRoot, value);
    }

    /// <summary>Копирует выбранный файл в общую папку на диске и возвращает машинно-независимое
    /// значение для хранения в настройке (хвост от корня диска). Диск не настроен, файла нет или
    /// копирование не удалось (шара отвалилась) — тихо возвращаем null, вызывающий ничего не
    /// сохраняет и не падает.</summary>
    public static string? Import(string? diskRoot, string? sourceFile)
    {
        if (string.IsNullOrWhiteSpace(diskRoot) || string.IsNullOrWhiteSpace(sourceFile)) return null;
        try
        {
            var folder = Path.Combine(diskRoot, Subfolder);
            Directory.CreateDirectory(folder);
            var dest = Path.Combine(folder, Path.GetFileName(sourceFile));
            File.Copy(sourceFile, dest, overwrite: true);
            return SharedFolderPath.ToPortable(diskRoot, dest, Subfolder);
        }
        catch (Exception)
        {
            return null;
        }
    }
}
