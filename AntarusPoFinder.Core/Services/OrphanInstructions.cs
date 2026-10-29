using System.Collections.Generic;
using System.IO;
using System.Linq;
using AntarusPoFinder.Core.Data;
using AntarusPoFinder.Core.Domain;

namespace AntarusPoFinder.Core.Services;

/// <summary>Версия, которой можно отдать осиротевшую инструкцию. <paramref name="SameNumber"/> —
/// у неё ТОТ ЖЕ номер (hw и sw), и отличается версия только датой в хвосте: это и есть случай
/// «перезалили заново», ради которого всё затевалось, поэтому такие стоят первыми.</summary>
public sealed record OrphanCandidate(FwVersionRecord Version, string Label, bool SameNumber);

/// <summary>Найденная осиротевшая инструкция: документ есть, а версии, которой он принадлежал, в базе
/// нет.</summary>
public sealed record OrphanInstruction(
    string Document,
    string Folder,
    string VersionDir,
    string VersionRaw,
    string CabinetLabel,
    IReadOnlyList<OrphanCandidate> Candidates)
{
    /// <summary>Привязывать не к чему — у шкафа нет ни одной живой версии. Строку всё равно
    /// показываем: «документ висит в воздухе» это ответ, и человеку полезнее знать о нём, чем не
    /// знать.</summary>
    public bool Hopeless => Candidates.Count == 0;
}

/// <summary>Итог привязки. <paramref name="StoredPath"/> — куда лёг документ у новой версии, по
/// этому же адресу его откроет QR с наклейки.</summary>
public sealed record OrphanLinkResult(string StoredPath, bool ReplacedExisting, List<string> Warnings);

/// <summary>ПОТЕРЯННЫЕ ИНСТРУКЦИИ: документ на диске есть, а версии прошивки, которой он
/// принадлежал, в базе больше нет.
///
/// <b>Откуда задача.</b> Дословно: «по инструкции можно сделать, если прошивка этой версии
/// потерялась или перезаписалась новой датой предыдущий ссылку можно было бы прилинковать к новой в
/// настройках, а файндер сам бы вычислял потерянные так инструкции».
///
/// Случай реальный и частый по одной причине: дата стоит В САМОМ НОМЕРЕ версии
/// (<c>2.1.0004.0002.20260422_1348</c>, см. <see cref="FwVersionNumber"/>), а номер — это имя папки.
/// Перезалили ту же сборку с галочкой «добавлять дату» — получилась ДРУГАЯ папка, новая запись в
/// базе и пустая папка «Инструкция» у неё; документ остался в прежней папке, на которую больше никто
/// не смотрит. По QR с наклейки при этом может открываться и старый адрес, и «инструкция в
/// разработке» — в зависимости от того, какую наклейку успели напечатать.
///
/// <b>Четыре вопроса, которых Илья не задал, и ответы на них.</b>
/// <list type="number">
/// <item><description><b>Привязка — перенос файла или запись в базе?</b> Ни то, ни другое целиком:
/// документ КОПИРУЕТСЯ новой версии (её папка, её каноническое имя, своя выкладка на хостинг), а в
/// исходной папке остаётся как лежал. Перенос был бы хуже: найдись потерянная версия позже
/// (восстановили из резервной копии), её инструкция оказалась бы неизвестно где. Одна запись в базе
/// тоже не годится — QR ведёт по адресу, построенному из ПУТИ на диске, и без файла по новому пути
/// наклейка новой версии открывала бы пустоту.</description></item>
/// <item><description><b>Что, если кандидатов несколько?</b> Не выбираем за человека никогда.
/// Кандидаты показываются списком, с тем же номером — первыми, выбор делает человек. Угадать тут
/// нельзя: «ту же сборку перезалили» и «выпустили следующую версию» на диске выглядят одинаково, а
/// цена ошибки — чужая инструкция на шкафу.</description></item>
/// <item><description><b>Что, если потерянная версия потом найдётся?</b> Ничего страшного не
/// произойдёт: её папка не тронута, документ на месте, ссылка в её записи (если запись вернулась)
/// по-прежнему верна. Именно ради этого привязка и сделана копированием.</description></item>
/// <item><description><b>Почему повторно не предлагается.</b> В папке-сироте ставится отметка
/// «отдано версии такой-то» (<see cref="InstructionOwnership.MarkLinked"/>) — НА ДИСКЕ, а не в базе:
/// диск общий, база у каждого своя, и иначе каждый коллега разбирал бы одну и ту же находку
/// заново.</description></item>
/// </list>
///
/// <b>Цена обхода.</b> Диск сетевой, поэтому поиск идёт не по всему дереву, а по папкам
/// КОНТРОЛЛЕРОВ, известным базе: один листинг на шкаф плюс один на каждую папку версии, которой база
/// не знает. Зовётся по кнопке, а не при открытии страницы — обход сетевой шары на каждом показе
/// настроек ощущался бы как «программа подвисла».</summary>
public static class OrphanInstructions
{
    /// <summary>Найти осиротевшие инструкции. Ничего не меняет и в базу не пишет.</summary>
    public static List<OrphanInstruction> Find(Database db, string root)
    {
        var result = new List<OrphanInstruction>();
        if (string.IsNullOrWhiteSpace(root)) return result;

        var versions = db.GetAllFwVersionsWithNames(includeArchived: true);

        // Папки версий, которые база ЗНАЕТ. Путь приводим к форме своего диска: disk_path попадает в
        // базу и синхронизацией с чужих машин, где у диска другая буква, и без приведения все чужие
        // записи выглядели бы «неизвестными папками» — то есть сиротой оказался бы весь диск.
        var known = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var version in versions)
        {
            var dir = FirmwarePathLocalizer.Localize(version.DiskPath, root);
            if (!string.IsNullOrWhiteSpace(dir)) known.Add(Normalize(dir));
        }

        foreach (var cabinet in versions.GroupBy(v => (v.SubtypeId, v.ControllerId)))
        {
            var sample = cabinet.First();
            var controllerFolder = VersionDocFolders.OwnControllerFolder(root, sample.GroupName,
                sample.SubtypeName, sample.CtrlName);
            if (string.IsNullOrWhiteSpace(controllerFolder)) continue;

            var label = $"{sample.GroupName} / {sample.SubtypeName} / {sample.CtrlName}";
            var live = cabinet.Where(v => v.Id is not null && !v.Archived).ToList();

            foreach (var dir in SubFolders(controllerFolder))
            {
                var name = Path.GetFileName(dir);
                // Папки самой раскладки («Инструкция», «ОПЦ», «Паспорт»…) версиями не являются —
                // спрашиваем VersionLayout, единственное место, знающее раскладку.
                if (VersionLayout.IsVersionOwnFolder(name)) continue;
                if (known.Contains(Normalize(dir))) continue;

                var folder = VersionLayout.SlotFolder(dir, HierarchyFolders.Instructions);
                // Отметка «уже отдано такой-то версии» — находка разобрана, и повторять её не нужно
                // ни нам, ни коллеге с другой машины.
                if (!string.IsNullOrWhiteSpace(InstructionOwnership.Read(folder)?.LinkedTo)) continue;

                var document = DocumentIn(folder);
                if (document is null) continue;

                result.Add(new OrphanInstruction(document, folder, dir, name, label,
                    Candidates(live, name)));
            }
        }

        return result;
    }

    /// <summary>Кому эту инструкцию можно отдать: живые версии ТОГО ЖЕ шкафа. Первыми — те, у кого
    /// совпадает номер (hw и sw) и отличается только дата в хвосте: это и есть «перезалили заново».
    /// Дальше — остальные версии шкафа, от свежих к старым: «потерялась» бывает и без перезаливки, и
    /// тогда документ обычно нужен самой свежей.</summary>
    private static List<OrphanCandidate> Candidates(List<FwVersionRecord> live, string orphanVersionRaw)
    {
        var orphan = FwVersionNumber.Parse(orphanVersionRaw);

        return live
            .Select(v =>
            {
                var same = orphan is not null
                           && FwVersionNumber.Parse(v.VersionRaw) is { } parsed
                           && parsed.HwVersion == orphan.HwVersion
                           && parsed.SwVersion == orphan.SwVersion
                           && parsed.EqPrefix == orphan.EqPrefix
                           && parsed.SubPrefix == orphan.SubPrefix;
                return new OrphanCandidate(v, CandidateLabel(v, same), same);
            })
            .OrderByDescending(c => c.SameNumber)
            .ThenByDescending(c => c.Version.HwVersion)
            .ThenByDescending(c => c.Version.SwVersion)
            .ToList();
    }

    private static string CandidateLabel(FwVersionRecord v, bool sameNumber)
    {
        var label = v.VersionRaw;
        if (!string.IsNullOrWhiteSpace(v.Execution)) label += $" ({v.Execution})";
        if (sameNumber) label += " — тот же номер, другая дата";
        return label;
    }

    /// <summary>Отдать найденный документ выбранной версии.
    ///
    /// Копирование идёт тем же <see cref="InstructionStorage.Copy"/>, что и обычная догрузка
    /// инструкции, и это принципиально: там и каноническое имя (адрес под QR не зависит от того, как
    /// файл назывался у прежней версии), и выкладка на хостинг, и — главное — переезд ПРЕЖНЕЙ
    /// редакции получателя в «Прежние редакции». Второй реализации этих трёх правил в проекте быть
    /// не должно.</summary>
    public static OrphanLinkResult Link(Database db, string root, OrphanInstruction orphan,
        FwVersionRecord target, IInstructionPublisher? publisher = null)
    {
        var warnings = new List<string>();
        if (target.Id is not int id)
        {
            warnings.Add("У выбранной версии нет записи в базе — привязывать не к чему.");
            return new OrphanLinkResult("", false, warnings);
        }

        var names = db.GetFwVersionNames(id);
        if (names is not { } n)
        {
            warnings.Add("Не нашли, какому шкафу принадлежит выбранная версия.");
            return new OrphanLinkResult("", false, warnings);
        }

        var versionDir = FirmwarePathLocalizer.Localize(target.DiskPath, root);
        var ownControllerFolder = VersionDocFolders.OwnControllerFolder(root, n.GroupName, n.SubtypeName,
            n.ControllerName);
        if (string.IsNullOrWhiteSpace(ownControllerFolder))
        {
            warnings.Add("Не нашли папку контроллера выбранной версии.");
            return new OrphanLinkResult("", false, warnings);
        }

        var folder = VersionDocFolders.WriteFolder(versionDir, ownControllerFolder!, HierarchyFolders.Instructions);
        var replaced = DocumentIn(folder) is not null;

        var placed = InstructionStorage.Copy(orphan.Document, folder, root, warnings, target.VersionRaw, publisher);
        if (string.IsNullOrEmpty(placed.StoredPath))
        {
            warnings.Add("Документ не удалось положить новой версии — привязка не состоялась.");
            return new OrphanLinkResult("", false, warnings);
        }

        // Ссылку получают и записи, делящие с версией файлы (копии по подтипам, конфигурации): файл у
        // них один и тот же, и разъехавшиеся ссылки означали бы «у конфигурации инструкции нет».
        var ids = new List<int> { id };
        foreach (var sharing in db.GetFwVersionsSharingFiles(target.DiskPath, target.VersionRaw))
            if (sharing.Id is int sid && !ids.Contains(sid)) ids.Add(sid);
        foreach (var each in ids) db.UpdateFwVersionAttachments(each, instructionsPath: placed.StoredPath);
        target.InstructionsPath = placed.StoredPath;

        // Находка разобрана — отмечаем это в папке-сироте, иначе она всплывала бы при каждой
        // проверке, и у коллег тоже.
        InstructionOwnership.MarkLinked(orphan.Folder, target.VersionRaw);

        return new OrphanLinkResult(placed.StoredPath, replaced, warnings);
    }

    /// <summary>Настоящий документ в папке — не заглушка, не ярлык, не метка, не мусор. Верхний
    /// уровень: «Прежние редакции» лежат подпапкой и документом этой версии не являются.</summary>
    private static string? DocumentIn(string? folder)
    {
        if (string.IsNullOrWhiteSpace(folder)) return null;
        try
        {
            if (!Directory.Exists(folder)) return null;
            return Directory.EnumerateFiles(folder, "*", SearchOption.TopDirectoryOnly)
                .FirstOrDefault(f => !DocFileResolver.IsNotADocument(f));
        }
        catch (Exception) { return null; }
    }

    /// <summary>Подпапки папки контроллера. Недоступная шара — пустой список: отвалившийся диск это
    /// «не знаем», а не «сирот нет», и ронять из-за него проверку незачем.</summary>
    private static IReadOnlyList<string> SubFolders(string folder)
    {
        try { return Directory.EnumerateDirectories(folder, "*", SearchOption.TopDirectoryOnly).ToList(); }
        catch (Exception) { return Array.Empty<string>(); }
    }

    private static string Normalize(string path)
    {
        try { return Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar); }
        catch (Exception) { return path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar); }
    }
}
