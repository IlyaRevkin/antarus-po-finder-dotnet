using System;
using System.IO;
using System.Linq;

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

    /// <summary>Чем открывать выбранный файл. Разделение не косметическое: неподвижная картинка,
    /// анимированный GIF и видео показываются тремя разными способами (кадр, покадровая анимация,
    /// проигрыватель), и решать это по расширению нужно ОДНОМУ месту — иначе список форматов в
    /// диалоге выбора файла и список, который умеет показывать окно, разъезжаются.</summary>
    public enum MediaKind
    {
        /// <summary>Расширение незнакомое — показывать нечем.</summary>
        Unknown,

        /// <summary>Обычная неподвижная картинка.</summary>
        Image,

        /// <summary>GIF: тот же декодер картинок, но кадров много и их надо перелистывать самим —
        /// WPF сам GIF не анимирует, показывает только первый кадр.</summary>
        AnimatedImage,

        /// <summary>Видеофайл — проигрыватель Windows (нужны установленные в системе кодеки).</summary>
        Video,
    }

    private static readonly string[] ImageExtensions =
        { ".png", ".jpg", ".jpeg", ".bmp", ".webp", ".tif", ".tiff", ".ico" };

    private static readonly string[] AnimatedExtensions = { ".gif" };

    private static readonly string[] VideoExtensions =
        { ".mp4", ".m4v", ".mov", ".avi", ".wmv", ".mkv", ".webm", ".mpg", ".mpeg", ".m2ts", ".3gp" };

    public static MediaKind KindOf(string? path)
    {
        var ext = "";
        try { ext = Path.GetExtension(path ?? "").ToLowerInvariant(); }
        catch (Exception) { return MediaKind.Unknown; }

        if (ext.Length == 0) return MediaKind.Unknown;
        if (Array.IndexOf(AnimatedExtensions, ext) >= 0) return MediaKind.AnimatedImage;
        if (Array.IndexOf(ImageExtensions, ext) >= 0) return MediaKind.Image;
        if (Array.IndexOf(VideoExtensions, ext) >= 0) return MediaKind.Video;
        return MediaKind.Unknown;
    }

    /// <summary>Фильтр для диалога выбора файла — собирается из тех же списков, что и
    /// <see cref="KindOf"/>: выбрать через диалог что-то, чего окно не покажет, невозможно.</summary>
    public static string DialogFilter()
    {
        var pictures = string.Join(";", ImageExtensions.Concat(AnimatedExtensions).Select(e => "*" + e));
        var videos = string.Join(";", VideoExtensions.Select(e => "*" + e));
        return $"Картинки и видео ({pictures};{videos})|{pictures};{videos}" +
               $"|Картинки ({pictures})|{pictures}" +
               $"|Видео ({videos})|{videos}" +
               "|Все файлы (*.*)|*.*";
    }

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
