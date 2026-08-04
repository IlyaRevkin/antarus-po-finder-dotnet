using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace AntarusPoFinder.Core.Services;

/// <summary>Наклейки — папка с готовыми шаблонами на печать («Проверено ОТК», «Внимание! Проверьте
/// перед подключением» и т.п.). Сущность нарочно предельно простая: это НЕ ещё один справочник с
/// таблицей, синхронизацией и модерацией, а просто общая папка, в которую кладут файлы, плюс окно,
/// откуда их видно и можно напечатать не разыскивая по диску (просьба Ильи: «чтобы была кнопка,
/// чтобы не искать… и она ради такой мелочи весь функционал не занимала»).
///
/// По умолчанию папка живёт рядом с остальным общим — <c>&lt;диск&gt;\Конфиг\Наклейки</c>: там же, где
/// общий конфиг, значит она уже доступна всем машинам и её не надо отдельно раздавать. Администратор
/// может указать любую другую (в том числе относительную корню — тогда буква диска у каждой машины
/// своя, и путь всё равно сойдётся).</summary>
public static class StickerTemplates
{
    public const string DefaultSubfolder = @"Конфиг\Наклейки";

    /// <summary>Куда смотреть за наклейками. <paramref name="configured"/> — то, что задано в
    /// настройках: пусто = папка по умолчанию, относительный путь = от корня диска, абсолютный —
    /// как есть. null — корень диска не настроен и путь не абсолютный: показывать нечего.</summary>
    public static string? FolderFor(string? diskRoot, string? configured)
    {
        var value = (configured ?? "").Trim();
        if (value.Length > 0 && Path.IsPathRooted(value)) return value;
        if (string.IsNullOrWhiteSpace(diskRoot)) return null;
        return Path.Combine(diskRoot, value.Length > 0 ? value : DefaultSubfolder);
    }

    /// <summary>Файлы-шаблоны в папке: всё, что можно открыть и напечатать, включая вложенные папки
    /// (наклейки удобно раскладывать по темам). Недоступная папка — пустой список, а не исключение:
    /// шара отваливается регулярно, и окно наклеек из-за этого падать не должно.</summary>
    public static List<string> List(string? folder)
    {
        if (string.IsNullOrWhiteSpace(folder)) return new List<string>();
        try
        {
            return Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories)
                .Where(f => !DocFileResolver.IsShortcut(f))
                .OrderBy(f => f, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
        }
        catch (Exception)
        {
            return new List<string>();
        }
    }
}
