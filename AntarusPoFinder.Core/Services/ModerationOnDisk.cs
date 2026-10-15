using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AntarusPoFinder.Core.Data;
using AntarusPoFinder.Core.Domain;

namespace AntarusPoFinder.Core.Services;

/// <summary>РЕШЕНИЕ МОДЕРАЦИИ, КОТОРОЕ ЕДЕТ ДИСКОМ.
///
/// Зачем это понадобилось. Состояние модерации (released/archived) ездило между машинами только
/// обменом конфига. Обмен между двумя конкретными машинами может не работать месяцами — и молча:
/// каталоги у них при этом выглядят одинаковыми, потому что обе насканировали их с общего диска
/// сами, каждая своими строками. Ровно это и обнаружилось 14.10.2026 по слепкам двух машин: 46 общих
/// прошивок, у 28 из них РАЗНЫЕ sync_id, и 16 версий, выпущенных на одной машине, на второй всё ещё
/// висели в очереди. Жалоба: «у коллеги 21 прошивок на модерации висит а у меня 4».
///
/// Общий диск между этими машинами работает заведомо: файлы прошивок они видят одни и те же (в тех
/// же слепках — 10 854 файла против 10 858, разница только в свежей версии). Значит решение надо
/// возить там же, где уже едут описание, тип пуска и теги, — в CHANGELOG.md папки версии.
///
/// Это НЕ замена обмену конфигом, а второй, независимый путь. Оба монотонны и в одну сторону:
/// «выпущена» и «в архиве» ставятся, обратно не снимаются. Поэтому порядок прихода не важен и
/// встретиться они могут в любом сочетании, не отменяя друг друга.</summary>
public static class ModerationOnDisk
{
    /// <summary>Записать решение в папку версии. Тихо ничего не делает, если папки нет или на шаре
    /// нет прав на запись: модерация — операция в базе, и ронять её из-за недоступного диска нельзя.
    /// Возвращает число версий, которым отметку реально проставили.</summary>
    public static int Write(Database db, IEnumerable<int> versionIds, string author, DateTime? now = null)
    {
        var stamp = (now ?? DateTime.Now).ToString("yyyy-MM-ddTHH:mm:ss");
        var written = 0;
        foreach (var id in versionIds.Distinct())
        {
            var row = db.GetFwVersionById(id);
            if (row is null || string.IsNullOrEmpty(row.DiskPath)) continue;
            var mark = new FwModerationMark(row.Released, row.Archived, stamp, author ?? "");
            if (ChangelogFile.TryMarkModeration(row.DiskPath, mark)) written++;
        }
        return written;
    }

    /// <summary>Сколько версий подняло бы по решениям с диска, и какие именно.</summary>
    public record AbsorbResult(int Released, int Archived, List<string> Items);

    /// <summary>Принять решения, записанные на диске ДРУГИМИ машинами.
    ///
    /// Монотонно: 0 → 1 применяем, обратно — никогда. Отметка «в очереди» на диске чужого решения не
    /// отменяет: снимок мог быть записан до выпуска, а снять чужое «выпущена» по устаревшему файлу
    /// хуже, чем не заметить откат (откат — отдельное решение, он ездит своим каналом со временем
    /// правки).
    ///
    /// Отсутствие строки «Модерация:» в файле — это НЕ «в очереди», а «машина, писавшая файл, о
    /// модерации не знала»: старые CHANGELOG.md такой строки не содержат вовсе.</summary>
    public static AbsorbResult Absorb(Database db, bool apply = true)
    {
        var items = new List<string>();
        var released = 0;
        var archived = 0;

        foreach (var row in db.GetAllFwVersionsWithNames(includeArchived: true))
        {
            if (row.Id is null || string.IsNullOrEmpty(row.DiskPath)) continue;
            if (row.Released && row.Archived) continue;            // выше уже некуда
            if (!Directory.Exists(row.DiskPath)) continue;

            var mark = ChangelogFile.TryRead(row.DiskPath)?.Moderation;
            if (mark is null) continue;

            var liftRelease = mark.Released && !row.Released;
            var liftArchive = mark.Archived && !row.Archived;
            if (!liftRelease && !liftArchive) continue;

            if (liftRelease) released++;
            if (liftArchive) archived++;
            items.Add($"{row.GroupName}/{row.SubtypeName}/{row.CtrlName} {row.VersionRaw}"
                      + (liftRelease ? " — выпущена" : "") + (liftArchive ? " — в архив" : "")
                      + (string.IsNullOrEmpty(mark.By) ? "" : $" ({mark.By})"));

            if (!apply) continue;
            if (liftRelease) db.MarkFwVersionReleased(row.Id.Value);
            if (liftArchive) db.ArchiveFwVersion(row.Id.Value);
        }

        return new AbsorbResult(released, archived, items);
    }
}
