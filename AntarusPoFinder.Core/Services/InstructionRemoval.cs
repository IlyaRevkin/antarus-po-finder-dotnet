using System.Collections.Generic;
using System.IO;
using System.Linq;
using AntarusPoFinder.Core.Data;
using AntarusPoFinder.Core.Domain;

namespace AntarusPoFinder.Core.Services;

/// <summary>Что именно исчезнет, если удалить инструкцию у этой версии. Считается ДО удаления и
/// целиком показывается человеку: удаление идёт по общему диску и по общему бакету, обратно ничего
/// не достанешь, и «нажал — а что удалилось, непонятно» здесь недопустимо.</summary>
public sealed record InstructionRemovalPlan
{
    /// <summary>Документ на диске, который будет удалён: файл или папка постраничных сканов. null —
    /// удалять нечего (документа нет, или в папке лежит одна заглушка).</summary>
    public string? DiskPath { get; init; }

    /// <summary>Документ — это папка сканов, а не один файл. От этого зависит и удаление с диска, и
    /// то, сколько объектов уйдёт из бакета (там «папка» — общий префикс ключей).</summary>
    public bool IsFolder { get; init; }

    /// <summary>В папке инструкции лежит только заглушка «в разработке». Удалять нечего: заглушка и
    /// должна там лежать, пока документа нет (см. <see cref="InstructionStub"/>).</summary>
    public bool OnlyStub { get; init; }

    /// <summary>Записи БД, у которых надо снять <c>instructions_path</c>, — эта версия и её копии по
    /// подтипам шкафа с конфигурациями: у них общий disk_path и общие файлы, документ у них один и
    /// тот же (см. <see cref="Database.GetFwVersionsSharingFiles"/>).</summary>
    public IReadOnlyList<int> UnlinkIds { get; init; } = Array.Empty<int>();

    /// <summary>Хоть у одной из этих записей ссылка на инструкцию непустая — значит есть что снимать
    /// даже тогда, когда на диске файла уже нет (запись осталась висеть).</summary>
    public bool HasLink { get; init; }

    /// <summary>Другие версии, которые читают ЭТОТ ЖЕ документ, — по именам, как их видит человек.
    /// Так бывает у версий, не переехавших на новую раскладку: папка «Инструкция» у них общая на весь
    /// контроллер (см. <see cref="VersionLayout"/>), и удаление файла унесло бы инструкцию у всех
    /// сразу. Список непустой — файл не удаляется, снимается только ссылка.</summary>
    public IReadOnlyList<string> UsedAlsoBy { get; init; } = Array.Empty<string>();

    /// <summary>Папка инструкции — та, куда после удаления ляжет заглушка.</summary>
    public string? Folder { get; init; }

    public bool Shared => UsedAlsoBy.Count > 0;

    /// <summary>Файл будет удалён с диска и с хостинга. Иначе всё, что произойдёт, — снятие ссылки.</summary>
    public bool DeletesFile => DiskPath is not null && !Shared;

    /// <summary>Делать нечего вовсе: ни файла, ни ссылки.</summary>
    public bool NothingToDo => DiskPath is null && !HasLink;
}

/// <summary>Итог удаления. <paramref name="Removed"/> — файл действительно ушёл с диска (а не только
/// снялась ссылка): по нему вызывающий решает, о чём отчитаться.</summary>
public sealed record InstructionRemovalResult(bool Removed, List<string> Applied, List<string> Warnings)
{
    public bool AnythingChanged => Applied.Count > 0;
}

/// <summary>Удаление инструкции у версии — зеркало <see cref="InstructionStorage"/>. Просьба владельца
/// дословно: «надо ещё наверное возможность удалить инструкцию добавить». До сих пор можно было только
/// снять ССЫЛКУ (пустое поле в модерации): документ оставался лежать на диске и на хостинге, карточка
/// продолжала показывать «инструкция ✓» (она смотрит на файлы в папке, а не в базу), а по QR со шкафа
/// открывался тот самый документ, который «удалили».
///
/// Поэтому удаление здесь — это все четыре действия сразу, в жёстком порядке:
/// <list type="number">
/// <item><description>файл (или папка сканов) убирается с диска;</description></item>
/// <item><description>копия убирается с хостинга — иначе наклейка на шкафу продолжала бы открывать
/// удалённый документ, и это худший из возможных исходов: программа говорит «нет», железо говорит
/// «есть»;</description></item>
/// <item><description>снимается <c>instructions_path</c> — у этой версии и у её копий по подтипам,
/// потому что файл у них общий;</description></item>
/// <item><description>на освободившееся место кладётся заглушка «Инструкция в разработке» и уезжает
/// на хостинг. Это не украшение: ссылка под QR обязана открываться всегда, наклейка уже на шкафу, и
/// вместо ошибки хостинга человек должен увидеть «документ в работе».</description></item>
/// </list>
///
/// <b>Чего этот код не делает и делать не должен.</b> Документ, который читают ДРУГИЕ версии, с диска
/// не удаляется (см. <see cref="InstructionRemovalPlan.UsedAlsoBy"/>): у версии, не переехавшей на
/// новую раскладку, папка «Инструкция» общая на весь контроллер, и «удалить у одной» там технически
/// означает «удалить у всех». В этом случае снимается только ссылка, а человеку прямо говорится, что
/// файл остался и почему.</summary>
public static class InstructionRemoval
{
    /// <summary>Что произойдёт при удалении. Ходит в базу и на диск, сеть не трогает: план нужен для
    /// вопроса «точно удаляем?», и висеть на сетевом запросе перед этим вопросом незачем.</summary>
    public static InstructionRemovalPlan Plan(Database db, FwVersionRecord record, string root)
    {
        if (record.Id is not int id) return new InstructionRemovalPlan();

        var siblings = db.GetFwVersionsSharingFiles(record.DiskPath, record.VersionRaw);
        var unlink = new List<int> { id };
        foreach (var s in siblings)
            if (s.Id is int sid && !unlink.Contains(sid)) unlink.Add(sid);

        var hasLink = !string.IsNullOrWhiteSpace(record.InstructionsPath)
                      || siblings.Any(s => !string.IsNullOrWhiteSpace(s.InstructionsPath));

        var versionDir = FirmwarePathLocalizer.Localize(record.DiskPath, root);
        var controllerFolder = VersionLayout.ControllerFolderOf(versionDir);
        var folder = VersionLayout.SlotBestReadFolder(versionDir, controllerFolder, HierarchyFolders.Instructions)
                     ?? (string.IsNullOrWhiteSpace(versionDir)
                         ? null
                         : VersionLayout.SlotFolder(versionDir, HierarchyFolders.Instructions));

        var document = ResolveDocument(record, root, folder);
        if (document is null)
            return new InstructionRemovalPlan
            {
                UnlinkIds = unlink,
                HasLink = hasLink,
                Folder = folder,
                OnlyStub = InstructionStub.ExistingIn(folder) is not null,
            };

        return new InstructionRemovalPlan
        {
            DiskPath = document,
            IsFolder = SafeIsFolder(document),
            UnlinkIds = unlink,
            HasLink = hasLink,
            Folder = folder,
            UsedAlsoBy = OtherReaders(db, unlink, folder, controllerFolder, document),
        };
    }

    /// <summary>Выполнить удаление по уже посчитанному плану.
    ///
    /// <paramref name="unpublisher"/> = null — хостинг не настроен, и тогда всё, что связано с
    /// бакетом, просто не происходит: это штатное состояние машины без ключей, а не отказ.
    /// <paramref name="stubs"/> = null — заглушку рисовать нечем (Core не умеет PDF сам), место
    /// остаётся пустым.</summary>
    public static InstructionRemovalResult Apply(Database db, FwVersionRecord record, string root,
        InstructionRemovalPlan plan, IInstructionStubWriter? stubs = null,
        IInstructionPublisher? publisher = null, IInstructionUnpublisher? unpublisher = null)
    {
        var applied = new List<string>();
        var warnings = new List<string>();

        if (plan.NothingToDo)
            return new InstructionRemovalResult(false, applied, new List<string> { "Инструкции у этой версии нет — удалять нечего." });

        var removed = false;
        if (plan.DeletesFile)
        {
            // Не удалилось — дальше не идём. Снятая ссылка при оставшемся на диске документе даёт
            // худшее из состояний: в базе «инструкции нет», в папке лежит документ, и карточка (она
            // смотрит на файлы, а не в базу) продолжает показывать «инструкция ✓».
            if (!DeleteFromDisk(plan.DiskPath!, plan.IsFolder, warnings))
                return new InstructionRemovalResult(false, applied, warnings);

            removed = true;
            applied.Add(plan.IsFolder ? "Инструкция удалена с диска (папка сканов)" : "Инструкция удалена с диска");

            if (unpublisher is not null)
            {
                var keys = unpublisher.Unpublish(plan.DiskPath!, root, plan.IsFolder, warnings);
                foreach (var key in keys) db.SaveHostingCheck(key, present: false, "");
                if (keys.Count > 0) applied.Add($"С хостинга убрано объектов: {keys.Count}");
            }
        }
        else if (plan.Shared)
        {
            warnings.Add($"Файл остался на диске: его читают и другие версии ({string.Join(", ", plan.UsedAlsoBy)}). " +
                         "У этой версии снята только ссылка.");
        }

        if (plan.HasLink)
        {
            foreach (var id in plan.UnlinkIds)
                db.UpdateFwVersionAttachments(id, instructionsPath: "");
            record.InstructionsPath = "";
            applied.Add(plan.UnlinkIds.Count > 1
                ? $"Ссылка на инструкцию снята ({plan.UnlinkIds.Count} записи: копии по подтипам и конфигурации)"
                : "Ссылка на инструкцию снята");
        }

        // Заглушка кладётся только там, где документа больше нет: у общего файла, оставшегося у
        // соседей, «Инструкция в разработке» рядом была бы прямой ложью.
        if (removed) PlaceStub(db, plan, root, record.VersionRaw, stubs, publisher, unpublisher, applied, warnings);

        return new InstructionRemovalResult(removed, applied, warnings);
    }

    private static void PlaceStub(Database db, InstructionRemovalPlan plan, string root, string versionRaw,
        IInstructionStubWriter? stubs, IInstructionPublisher? publisher, IInstructionUnpublisher? unpublisher,
        List<string> applied, List<string> warnings)
    {
        if (string.IsNullOrWhiteSpace(plan.Folder)) return;

        // Выкладываем заглушку сами, а не параметром EnsureForVersion: нужен АДРЕС выложенного, чтобы
        // поправить локальное наблюдение «лежит ли на хостинге». Без этого карточка после удаления
        // говорила бы «нет на хостинге» по ссылке, которая на самом деле открывается: ключ у заглушки
        // тот же, что был у документа (имя каноническое), и мы только что пометили его как удалённый.
        if (InstructionStub.EnsureForVersion(plan.Folder, root, versionRaw, stubs, warnings) > 0)
            applied.Add("На место инструкции положена заглушка «Инструкция в разработке»");

        var stub = InstructionStub.ExistingIn(plan.Folder);
        if (stub is null || publisher is null) return;

        var url = publisher.Publish(stub, stub, root, warnings);
        if (url is null) return;

        applied.Add("Заглушка выложена на хостинг — ссылка с наклейки продолжает открываться");
        if (unpublisher?.KeyOf(stub, root) is { } key) db.SaveHostingCheck(key, present: true, url);
    }

    /// <summary>Документ инструкции этой версии на диске — настоящий, не заглушка и не ярлык. Сначала
    /// путь из базы (он мог быть записан коллегой в форме его диска — приводим к нашей), потом обход
    /// папки: документ могли положить руками мимо программы, и «удалить инструкцию» обязано работать
    /// и с ним — именно его показывает карточка.</summary>
    private static string? ResolveDocument(FwVersionRecord record, string root, string? folder)
    {
        var stored = FirmwarePathLocalizer.Localize(record.InstructionsPath, root);
        if (!string.IsNullOrWhiteSpace(stored))
        {
            try
            {
                if (Directory.Exists(stored)) return stored;
                if (File.Exists(stored) && !DocFileResolver.IsNotADocument(stored)) return stored;
            }
            catch (Exception) { /* недоступный путь — идём смотреть папку */ }
        }

        if (string.IsNullOrWhiteSpace(folder)) return null;
        try
        {
            if (!Directory.Exists(folder)) return null;
            return Directory.EnumerateFiles(folder, "*", SearchOption.TopDirectoryOnly)
                .FirstOrDefault(f => !DocFileResolver.IsNotADocument(f));
        }
        catch (Exception) { return null; }
    }

    /// <summary>Кто ещё читает этот же документ. Две причины, и обе смотрятся строками, без обхода
    /// диска: у сотни версий это сотня обращений к сетевой шаре ради одного вопроса.
    /// <list type="bullet">
    /// <item><description>папка инструкции — ОБЩАЯ папка контроллера (версия ещё не переехала на новую
    /// раскладку): её читают все версии этого контроллера;</description></item>
    /// <item><description>у другой версии в базе записан ровно этот путь — так бывает после переездов
    /// и ручных правок, и такой файл тоже не наш, чтобы его удалять.</description></item>
    /// </list></summary>
    private static IReadOnlyList<string> OtherReaders(Database db, IReadOnlyList<int> unlink,
        string? folder, string? controllerFolder, string document)
    {
        var sharedFolder = !string.IsNullOrWhiteSpace(controllerFolder)
                           && !string.IsNullOrWhiteSpace(folder)
                           && PathsEqual(folder!, Path.Combine(controllerFolder!, HierarchyFolders.Instructions));

        var readers = new List<string>();
        foreach (var other in db.GetAllFwVersionsWithNames())
        {
            if (other.Id is not int id || unlink.Contains(id)) continue;

            var pointsHere = !string.IsNullOrWhiteSpace(other.InstructionsPath)
                             && PathsEqual(other.InstructionsPath, document);
            var underController = sharedFolder && !string.IsNullOrWhiteSpace(other.DiskPath)
                                  && IsInside(other.DiskPath, controllerFolder!);

            if (pointsHere || underController)
                readers.Add($"{other.CtrlName} {other.VersionRaw}".Trim());
        }

        return readers.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static bool DeleteFromDisk(string path, bool folder, List<string> warnings)
    {
        try
        {
            if (folder)
            {
                if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
            }
            else if (File.Exists(path))
            {
                File.Delete(path);
            }
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            warnings.Add($"Инструкция не удалена: {ex.Message}. " +
                         "Чаще всего документ открыт в просмотрщике — закройте его и повторите.");
            return false;
        }
    }

    private static bool SafeIsFolder(string path)
    {
        try { return Directory.Exists(path); }
        catch (Exception) { return false; }
    }

    /// <summary><paramref name="inner"/> лежит внутри <paramref name="outer"/> (или это он сам).</summary>
    private static bool IsInside(string inner, string outer)
    {
        try
        {
            var root = Path.GetFullPath(outer).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            return Path.GetFullPath(inner).StartsWith(root, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException)
        {
            return false;
        }
    }

    private static bool PathsEqual(string a, string b) =>
        string.Equals(a.Trim().TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            b.Trim().TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            StringComparison.OrdinalIgnoreCase);
}
