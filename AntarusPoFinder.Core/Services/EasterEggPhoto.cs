using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace AntarusPoFinder.Core.Services;

/// <summary>Тихая пасхалка на номере версии: общая ПАПКА <c>&lt;диск&gt;\Конфиг\Служебное</c> рядом с
/// остальным общим (наклейки, типовые паспорта). Файлы живут на общем диске, поэтому видны всем
/// машинам без отдельной раздачи. Никакого UI и уведомлений: положил файл — у всех открывается.
///
/// Показывается ВСЯ папка, а не одна выбранная запись. Так было раньше — «какую именно показывать»
/// хранилось настройкой, — и это расходилось: каждый видел ту, которую задал сам, а не ту, которую
/// добавил коллега (настройка едет отдельно от файла и перетирается последним записавшим). У папки
/// расходиться нечему: содержимое общее по построению, порядок считается одинаково у всех
/// (см. <see cref="List"/>).</summary>
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

    /// <summary>Всё, что лежит в общей папке и умеет показываться, — новое первым. Чужие файлы
    /// (документы, архивы, случайно скопированные .ini) отсеиваются по расширению: список ровно тот,
    /// который окно просмотра осилит.
    ///
    /// Порядок считается ОДИНАКОВО на всех машинах: сначала время записи файла (новое сверху), при
    /// совпадении — имя. Папка у всех одна и та же, значит и лента у всех одна и та же — коллега
    /// добавил файл, и он первым же открывается у остальных. Время берётся файловое, а не «когда я
    /// это увидел», иначе порядок был бы личным у каждого.
    ///
    /// Папки нет, диск не настроен или шара отвалилась — пустой список, вызывающий просто ничего не
    /// показывает.</summary>
    public static IReadOnlyList<string> List(string? diskRoot)
    {
        if (string.IsNullOrWhiteSpace(diskRoot)) return Array.Empty<string>();
        try
        {
            var folder = Path.Combine(diskRoot, Subfolder);
            if (!Directory.Exists(folder)) return Array.Empty<string>();

            return Directory.EnumerateFiles(folder)
                .Where(f => KindOf(f) != MediaKind.Unknown)
                .Select(f => (Path: f, Written: SafeWriteTime(f)))
                .OrderByDescending(x => x.Written)
                .ThenBy(x => Path.GetFileName(x.Path), StringComparer.OrdinalIgnoreCase)
                .Select(x => x.Path)
                .ToList();
        }
        catch (Exception)
        {
            return Array.Empty<string>();
        }
    }

    /// <summary>Время записи файла; недоступно (файл исчез между перечислением и опросом, нет прав) —
    /// минимальное, такой файл уедет в конец ленты, но список из-за него не развалится.</summary>
    private static DateTime SafeWriteTime(string path)
    {
        try { return File.GetLastWriteTimeUtc(path); }
        catch (Exception) { return DateTime.MinValue; }
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
