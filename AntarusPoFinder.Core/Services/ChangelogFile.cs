using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using AntarusPoFinder.Core.Domain;

namespace AntarusPoFinder.Core.Services;

/// <summary>Что удалось вычитать из CHANGELOG.md рядом с прошивкой. Пустые поля означают «в файле
/// этого не было» — вызывающий сам решает, чем их заменить.</summary>
public record ChangelogContent(string Description, List<string> LaunchTypes);

/// <summary>CHANGELOG.md, который кладётся в папку каждой версии при загрузке — единственный носитель
/// описания и типов пуска, который живёт НА ДИСКЕ, а не только в локальной базе конкретной машины.
///
/// Почему это важно: fw_versions синхронизируются между машинами двумя независимыми путями —
/// config-обменом (полная запись со всеми полями) и сканированием диска (HierarchyService.
/// SyncFwFromDisk, которое видит только папки). Раньше второй путь вставлял строку с заглушкой
/// «(синхронизировано с диска)» вместо описания, и поскольку config-импорт additive-only и НЕ трогает
/// уже существующую локальную строку, эта заглушка оставалась навсегда — ровно то, на что жаловались:
/// «прошивки, что я добавил на другом компе, появляются с описанием "синхронизировано с диска"».
/// Теперь сканирование диска читает описание и типы пуска из CHANGELOG.md, т.е. из того же источника,
/// который записала загрузившая машина.</summary>
public static class ChangelogFile
{
    public const string FileName = "CHANGELOG.md";

    /// <summary>Заглушка, которой SyncFwFromDisk помечает строки, для которых на диске не нашлось
    /// CHANGELOG.md. Публичная, потому что config-импорт обязан считать такое описание «пустым» и
    /// разрешить входящему настоящему описанию его перезаписать (см. ImportHierarchyDataCore).</summary>
    public const string DiskSyncPlaceholder = "(синхронизировано с диска)";

    public static void Write(string versionFolder, FwVersionNumber fwv, IEnumerable<string> launchTypes, string description)
    {
        var lines = new List<string>
        {
            $"# {fwv.Raw}",
            $"Дата: {fwv.DtStr}",
            $"Тип пуска: {string.Join(", ", launchTypes)}",
        };
        if (!string.IsNullOrEmpty(description))
        {
            lines.Add("");
            lines.Add(description);
        }
        File.WriteAllText(Path.Combine(versionFolder, FileName), string.Join("\n", lines), new UTF8Encoding(false));
    }

    /// <summary>Возвращает null, если файла нет или он нечитаем — вызывающий откатывается на заглушку.
    /// Разбор намеренно снисходительный: CHANGELOG.md люди правят руками прямо на сетевом диске, и
    /// потерять описание из-за лишней пустой строки или переставленных местами шапочных полей было бы
    /// хуже, чем принять слегка неканоничный файл.</summary>
    public static ChangelogContent? TryRead(string versionFolder)
    {
        string[] lines;
        try { lines = File.ReadAllLines(Path.Combine(versionFolder, FileName)); }
        catch { return null; }

        var launchTypes = new List<string>();
        var descLines = new List<string>();
        bool inBody = false;

        foreach (var line in lines)
        {
            if (!inBody)
            {
                if (line.StartsWith("# ", StringComparison.Ordinal)) continue;
                if (line.StartsWith("Дата:", StringComparison.OrdinalIgnoreCase)) continue;
                if (line.StartsWith("Тип пуска:", StringComparison.OrdinalIgnoreCase))
                {
                    launchTypes.AddRange(line["Тип пуска:".Length..]
                        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
                    continue;
                }
                if (string.IsNullOrWhiteSpace(line)) continue;
                inBody = true;
            }
            descLines.Add(line);
        }

        // Хвостовые пустые строки в описание не тащим — иначе повторная запись файла давала бы
        // «другое» описание при каждом круге чтение→запись.
        while (descLines.Count > 0 && string.IsNullOrWhiteSpace(descLines[^1]))
            descLines.RemoveAt(descLines.Count - 1);

        return new ChangelogContent(string.Join("\n", descLines).Trim(), launchTypes);
    }
}
