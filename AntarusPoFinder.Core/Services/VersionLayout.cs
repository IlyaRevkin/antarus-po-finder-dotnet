using System.Collections.Generic;
using System.IO;
using System.Linq;
using AntarusPoFinder.Core.Domain;

namespace AntarusPoFinder.Core.Services;

/// <summary>Единственное место, которое знает, ГДЕ у версии лежат её файлы (docs/hierarchy-rework-plan.md,
/// этап 4). До этого этапа раскладка была одна: файл прошивки — прямо в папке версии, а «Инструкция /
/// Карта ВВ / Карта Modbus / HMI» — общие папки КОНТРОЛЛЕРА, одни на все его версии. Теперь у версии
/// есть своя раскладка:
///
/// <code>
/// ПО\&lt;тип&gt;\&lt;подтип&gt;\&lt;контроллер&gt;\&lt;версия&gt;\
///     Прошивка\        ← файлы прошивки (.lfs/.psl/проект)
///     Инструкция\
///     Карта Modbus\
///     Карта ВВ\
///     HMI\
///     CHANGELOG.md     ← формат не трогаем, остаётся в корне папки версии
/// </code>
///
/// <b>Главное свойство — режим совместимости.</b> Ни один метод здесь не требует, чтобы версия уже
/// переехала: читающие методы возвращают СПИСОК кандидатов (новая папка внутри версии, потом старое
/// общее место), а пишущие выбирают новое место только у той версии, которая на диске уже
/// перестроена (<see cref="IsNewLayout"/>). Поэтому релиз с этим классом можно ставить всем ДО того,
/// как кто-либо запустит перестройку диска: старые версии продолжают работать ровно как раньше,
/// новые — по-новому, и обе раскладки живут рядом сколько угодно долго.
///
/// <b>Имя папки версии не меняется никогда</b> (кроме ОПЦ — см. <see cref="OpcLayout"/>), и это
/// делает этап 4 бесплатным для синхронизации: <c>fw_versions.disk_path</c> остаётся валидным на всех
/// машинах, включая те, что ещё не обновились.</summary>
public static class VersionLayout
{
    /// <summary>Подпапка с файлами самой прошивки. Раньше её не было вовсе — файл лежал в корне папки
    /// версии, вперемешку с CHANGELOG.md и (после этапа 4) с папками документов.</summary>
    public const string FirmwareFolderName = "Прошивка";

    /// <summary>Четыре папки документов, которые этап 4 переносит с уровня контроллера внутрь версии.
    /// Порядок = порядок создания, чтобы в проводнике они всегда шли одинаково.</summary>
    public static readonly string[] SlotFolderNames =
    {
        HierarchyFolders.Instructions,
        HierarchyFolders.Modbus,
        HierarchyFolders.IoMap,
        HierarchyFolders.Hmi,
    };

    /// <summary>Папка доп. материалов версии — краткое руководство наладчика, специфика работы,
    /// прошивка ПЛК от поставщика и прочее (см. FwAttachment). Живёт рядом с четырьмя папками
    /// документов и адресуется теми же SlotWriteFolder/SlotReadFolders: у перестроенной версии это её
    /// собственная папка, у прежней — общая папка контроллера, потому что положить файл внутрь ещё не
    /// переехавшей версии значит спрятать его от коллег со старым клиентом.
    ///
    /// В <see cref="SlotFolderNames"/> НЕ входит намеренно: те четыре папки заводятся у каждой версии
    /// всегда и пустыми (человек должен видеть, куда что класть), а доп. материалы — редкое
    /// дополнение, и пустая пятая папка у каждой версии на диске означала бы «сюда тоже что-то
    /// полагается». Папка создаётся в момент, когда в неё реально что-то кладут
    /// (FirmwareExtraFilesService).</summary>
    public const string ExtrasFolderName = "Доп. материалы";

    /// <summary>Куда класть доп. материал — та же развилка «перестроенная версия / прежняя», что у
    /// <see cref="SlotWriteFolder"/>. Отдельный метод, чтобы имя папки не приходилось повторять на
    /// вызывающей стороне: этот класс — единственное место, знающее раскладку.</summary>
    public static string ExtrasWriteFolder(string? versionDir, string controllerFolder) =>
        SlotWriteFolder(versionDir, controllerFolder, ExtrasFolderName);

    /// <summary>Файлы, которые в папке версии никогда не считаются файлом прошивки и потому НЕ
    /// переезжают в «Прошивка\»: журнал изменений (его читает досмотр диска по фиксированному пути в
    /// корне папки версии — см. ChangelogFile) и ярлыки Windows.</summary>
    public static bool IsServiceFile(string path) =>
        string.Equals(Path.GetFileName(path), ChangelogFile.FileName, StringComparison.OrdinalIgnoreCase)
        || DocFileResolver.IsShortcut(path);

    /// <summary>Папка внутри версии, которая принадлежит самой раскладке, — «Прошивка» и четыре папки
    /// документов. Всё остальное, что лежит в корне папки версии подпапкой, туда не относится и почти
    /// всегда является ЧАСТЬЮ ПРОЕКТА: проект KINCO разворачивается своими подпапками <c>plc</c> и
    /// <c>hmi</c>, и когда его контенты легли в корень версии (загрузка до перестройки диска),
    /// программа ПЛК оказывается в <c>plc\</c> вместо «Прошивка\» — ровно та жалоба, из-за которой эта
    /// проверка и появилась.
    ///
    /// «ОПЦ» и «Паспорт» тоже считаются своими: это соседние узлы иерархии, а не части проекта, и
    /// затаскивать их внутрь «Прошивка\» нельзя (см. HierarchyFolders).</summary>
    public static bool IsVersionOwnFolder(string folderName) =>
        string.Equals(folderName, FirmwareFolderName, StringComparison.OrdinalIgnoreCase)
        || SlotFolderNames.Any(slot => string.Equals(folderName, slot, StringComparison.OrdinalIgnoreCase))
        || string.Equals(folderName, HierarchyFolders.Opc, StringComparison.OrdinalIgnoreCase)
        || string.Equals(folderName, HierarchyFolders.Passports, StringComparison.OrdinalIgnoreCase);

    /// <summary>Подпапки в корне версии, которые на самом деле принадлежат проекту прошивки и должны
    /// жить в «Прошивка\» рядом с ним. Недоступная папка — пустой список: чинить недоступное нельзя.
    ///
    /// Имя папки — не единственный признак, и вот почему. Проекты, где программа ПЛК и панель лежат
    /// одной папкой (KINCO и подобные), разворачиваются подпапками <c>plc</c> и <c>hmi</c> — а «hmi»
    /// это ЕЩЁ И имя одной из пяти папок раскладки, и файловая система Windows разницы в регистре не
    /// видит. Пока признак был чисто именной, перестройка диска уносила в «Прошивка\» только
    /// <c>plc</c>, а <c>hmi</c> оставляла наверху: проект разрывался пополам («пол папок пропало,
    /// исполняемый потерялся»), а его половина заодно становилась для программы папкой панели этой
    /// версии — и карточка показывала кусок проекта ПЛК как HMI-проект. См.
    /// <see cref="IsProjectPartFolder"/>.</summary>
    public static IReadOnlyList<string> StrayProjectFolders(string versionDir)
    {
        try
        {
            var dirs = Directory.EnumerateDirectories(versionDir, "*", SearchOption.TopDirectoryOnly).ToList();
            // Корень версии ВИДИМО содержит распакованный проект — есть хотя бы одна подпапка, к
            // раскладке отношения не имеющая («plc»). Без такого соседа спорную «hmi» не трогаем.
            var unpackedProject = dirs.Any(d => !IsVersionOwnFolder(Path.GetFileName(d)));
            return dirs.Where(d => IsProjectPartFolder(d, unpackedProject)).ToList();
        }
        catch (Exception) { return Array.Empty<string>(); }
    }

    /// <summary>Эта подпапка корня версии — часть проекта прошивки (её место в «Прошивка\»), а не
    /// папка самой раскладки.
    ///
    /// Разбирается ровно один спорный случай — «HMI». Остальные три папки документов названы
    /// по-русски («Инструкция», «Карта Modbus», «Карта ВВ») и с подпапкой проекта совпасть не могут,
    /// а «Прошивка», «ОПЦ» и «Паспорт» — соседние узлы дерева, их не трогаем никогда.
    ///
    /// Два признака, по которым спорная папка признаётся ЧУЖОЙ:
    /// <list type="number">
    /// <item><description><b>Регистр имени.</b> Свою папку заводит программа и всегда под именем
    /// <c>HMI</c> (<see cref="HierarchyFolders.Hmi"/>). Проекты называют свою подпапку <c>hmi</c> — и
    /// Windows, у которой имена регистронезависимы, ВТОРУЮ папку рядом уже не создаст: пришедшая с
    /// проектом <c>hmi</c> так и останется на диске под своим именем и будет молча работать за папку
    /// панели версии. Отличающийся регистр — значит имя пришло не от нас.</description></item>
    /// <item><description><b>Вторая половина проекта рядом.</b> Даже <c>HMI</c> в «правильном»
    /// регистре — часть проекта, если в корне версии лежит ещё и явная папка проекта
    /// (<paramref name="unpackedProjectInRoot"/>, тот самый <c>plc</c>).</description></item>
    /// </list>
    ///
    /// И два стоп-условия поверх них: папка ПУСТА (ровно то, что заводит <see cref="EnsureFolders"/>)
    /// или внутри лежит НАША копия проекта панели «{версия}_hmi»
    /// (<see cref="HmiProjectFormat.StoredFolderSuffix"/>) — обе означают «папка наша», и трогать её
    /// нельзя. Любое сомнение решается так же: лишний раз не перенести дешевле, чем утащить в
    /// «Прошивка\» настоящую папку панели версии.</summary>
    public static bool IsProjectPartFolder(string folder, bool unpackedProjectInRoot)
    {
        var name = Path.GetFileName(folder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (!IsVersionOwnFolder(name)) return true;
        if (!string.Equals(name, HierarchyFolders.Hmi, StringComparison.OrdinalIgnoreCase)) return false;

        var ourName = string.Equals(name, HierarchyFolders.Hmi, StringComparison.Ordinal);
        if (ourName && !unpackedProjectInRoot) return false;
        try
        {
            var entries = Directory.EnumerateFileSystemEntries(folder, "*", SearchOption.TopDirectoryOnly).ToList();
            if (entries.Count == 0) return false;
            return !entries.Any(IsOurStoredPanelProject);
        }
        catch (Exception) { return false; }
    }

    /// <summary>Запись внутри папки HMI — это копия проекта панели, положенная самой программой
    /// («{версия}_hmi», папкой или файлом). Имя папки при этом состоит из точек, и
    /// <c>GetFileNameWithoutExtension</c> откусил бы у неё «расширение» <c>.0001_hmi</c> — поэтому у
    /// папки берётся имя целиком, а «без расширения» только у файла.</summary>
    private static bool IsOurStoredPanelProject(string entry)
    {
        var trimmed = entry.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var name = SafeDirExists(trimmed) ? Path.GetFileName(trimmed) : Path.GetFileNameWithoutExtension(trimmed);
        if (name.EndsWith(HmiProjectFormat.StoredFolderSuffix, StringComparison.OrdinalIgnoreCase)) return true;
        // Проект панели, положенный ПОД СВОИМ ИМЕНЕМ (см. ProjectTree): у такого проекта имя папки и
        // имя файла проекта — одно и то же, переименовывать его в «{версия}_hmi» нельзя. Значит и
        // узнавать «нашу» папку HMI по одному суффиксу больше недостаточно: без этой ветки настоящая
        // папка панели версии выглядела бы половиной чужого проекта и уезжала в «Прошивка\».
        return ProjectTree.IsProjectTree(trimmed);
    }

    public static string FirmwareFolder(string versionDir) => Path.Combine(versionDir, FirmwareFolderName);

    public static string SlotFolder(string versionDir, string slot) => Path.Combine(versionDir, slot);

    /// <summary>Все пять папок версии разом — «Прошивка» и четыре папки документов. Заводятся вместе и
    /// всегда, даже пустыми: человек, открывший папку версии в проводнике, должен видеть, куда что
    /// класть («в контроллере лежит папка с названием версии и в ней 5 папок» — так это и просили).
    /// Пустая папка документа при этом ничего не прячет: читающая сторона считает «своей» только ту, в
    /// которой ЕСТЬ файлы, иначе берёт общую папку контроллера (см. <see cref="SlotBestReadFolder"/>).
    ///
    /// Заводит их ровно две операции — перестройка диска и создание новой папки версии при загрузке;
    /// обход диска (PlanStructure) их не планирует НИКОГДА: это версии × 5 обращений к сетевой шаре
    /// каждым тиком (docs/hierarchy-rework-plan.md, этап 4).
    ///
    /// Возвращает число созданных папок — 0 значит «всё уже на месте», и на этом стоит идемпотентность
    /// мигратора. Ошибки не глотает: недоступная шара обязана дойти до вызывающего.</summary>
    public static int EnsureFolders(string versionDir)
    {
        var created = 0;
        foreach (var folder in new[] { FirmwareFolder(versionDir) }
                     .Concat(SlotFolderNames.Select(slot => SlotFolder(versionDir, slot))))
        {
            if (Directory.Exists(folder)) continue;
            Directory.CreateDirectory(folder);
            created++;
        }
        return created;
    }

    /// <summary>Каких из пяти папок у версии не хватает — вопрос мигратора, который по этому ответу и
    /// решает, есть ли что делать. Недоступная папка версии — «не хватает ничего»: чинить недоступное
    /// нельзя, а планировать операцию, которая заведомо упадёт, незачем.</summary>
    public static bool HasAllFolders(string versionDir)
    {
        if (!SafeDirExists(versionDir)) return true;
        return SafeDirExists(FirmwareFolder(versionDir))
               && SlotFolderNames.All(slot => SafeDirExists(SlotFolder(versionDir, slot)));
    }

    /// <summary>Версия уже перестроена под новую раскладку — на диске есть её «Прошивка\». Именно эта
    /// папка, а не папки документов: документы у версии могут отсутствовать законно (карты нет), а
    /// «Прошивка\» мигратор создаёт всегда, даже пустую. Недоступная шара — false: не подтвердили,
    /// значит работаем по-старому (это всегда безопасное направление).</summary>
    public static bool IsNewLayout(string? versionDir) =>
        !string.IsNullOrWhiteSpace(versionDir) && SafeDirExists(FirmwareFolder(versionDir));

    /// <summary>Где искать файлы прошивки этой версии, в порядке предпочтения: «Прошивка\», если она
    /// есть, и сама папка версии. Обе всегда, а не «или-или»: во время перестройки (или после
    /// частичной, прерванной обрывом шары) часть файлов уже переехала, часть ещё нет, и потерять
    /// вторую половину нельзя.</summary>
    public static IReadOnlyList<string> FirmwareFolders(string? versionDir)
    {
        if (string.IsNullOrWhiteSpace(versionDir)) return Array.Empty<string>();
        var inner = FirmwareFolder(versionDir);
        return SafeDirExists(inner)
            ? new[] { inner, versionDir }
            : new[] { versionDir };
    }

    /// <summary>То же самое для списка папок-кандидатов (локальный кэш + сетевая папка версии —
    /// см. SearchView.VersionFolders): каждая разворачивается в свою пару.</summary>
    public static IReadOnlyList<string> FirmwareFolders(IEnumerable<string?> versionDirs) =>
        versionDirs.Where(d => !string.IsNullOrWhiteSpace(d)).SelectMany(d => FirmwareFolders(d)).ToList();

    /// <summary>Откуда ЧИТАТЬ документ (инструкцию, карту, HMI): сначала папка внутри версии, потом
    /// общая папка контроллера. Возвращаются только реально существующие папки — вызывающий перебирает
    /// их по порядку и берёт первое, что нашлось (или самое свежее из всех, см. DocFileResolver).
    ///
    /// <paramref name="controllerFolder"/> = null (папку контроллера не определили — например, версия
    /// лежит в локальном кэше) — просто выпадает из списка.</summary>
    public static IReadOnlyList<string> SlotReadFolders(string? versionDir, string? controllerFolder, string slot)
    {
        var result = new List<string>(2);
        if (!string.IsNullOrWhiteSpace(versionDir))
        {
            var inner = SlotFolder(versionDir, slot);
            if (SafeDirExists(inner)) result.Add(inner);
        }
        if (!string.IsNullOrWhiteSpace(controllerFolder))
        {
            var shared = Path.Combine(controllerFolder, slot);
            if (SafeDirExists(shared)) result.Add(shared);
        }
        return result;
    }

    /// <summary>Одна папка, из которой надо читать документ — та из кандидатов, где ФАЙЛЫ реально
    /// есть. Порядок: своя папка версии (если в ней что-то лежит), иначе общая папка контроллера,
    /// иначе первая существующая. Проверка «есть файлы», а не «есть папка», принципиальна: перестройка
    /// диска не копирует документы контроллера в каждую версию (это удвоило бы диск и убило бы саму
    /// идею «документ обновляют в одном месте»), поэтому у переехавшей версии своя папка «Инструкция»
    /// обычно пустая — и без этой проверки инструкция контроллера у неё бы «пропала».</summary>
    public static string? SlotBestReadFolder(string? versionDir, string? controllerFolder, string slot)
    {
        var candidates = SlotReadFolders(versionDir, controllerFolder, slot);
        foreach (var folder in candidates)
            if (HasFiles(folder)) return folder;
        return candidates.Count > 0 ? candidates[0] : null;
    }

    /// <summary>В папке есть файлы, ради которых её стоит выбрать. Служебный мусор файловой системы
    /// не считается — это тот же <c>Thumbs.db</c> из жалобы «сделал ссылку на Thumbs.db вместо
    /// заглушки», только ломающий не выбор ФАЙЛА, а выбор ПАПКИ: проводник заводит его в любой папке,
    /// куда заглянули за эскизами, и своя пустая «Инструкция» версии от одного такого файла
    /// становилась «непустой». Дальше документ искали в ней, не находили и говорили «инструкции нет»
    /// — при том, что в общей папке контроллера она лежит (см. <see cref="JunkFiles"/>).
    ///
    /// Заглушка при этом файлом считается НАМЕРЕННО, в отличие от <see cref="DocFileResolver.IsNotADocument"/>:
    /// она лежит ровно там, куда эта версия пишет свои документы, и означает «здесь документа пока
    /// нет», а не «смотри в другом месте». Перестань её учитывать — и версия с заглушкой начала бы
    /// показывать инструкцию контроллера, то есть чужой документ вместо честного «в разработке».</summary>
    private static bool HasFiles(string folder)
    {
        try
        {
            // Метка хозяина папки «Инструкция» (InstructionOwnership) здесь — ровно тот же случай,
            // что Thumbs.db: служебный файл, от которого пустая папка версии становилась бы
            // «непустой», и версия переставала бы показывать инструкцию контроллера.
            return Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories)
                .Any(f => !DocFileResolver.IsShortcut(f) && !JunkFiles.IsJunk(f) && !InstructionOwnership.IsMarker(f));
        }
        catch (Exception) { return false; }
    }

    /// <summary>Куда ПИСАТЬ документ. Новое место — только у версии, которая на диске уже перестроена:
    /// положив инструкцию внутрь ещё не переехавшей версии, мы спрятали бы её от всех коллег со старым
    /// клиентом (они смотрят только в общую папку контроллера), и «инструкция пропала» было бы
    /// абсолютно честным описанием. Пока версия старая — пишем туда же, куда писали всегда.</summary>
    public static string SlotWriteFolder(string? versionDir, string controllerFolder, string slot) =>
        IsNewLayout(versionDir) ? SlotFolder(versionDir!, slot) : Path.Combine(controllerFolder, slot);

    /// <summary>Куда писать файл прошивки: «Прошивка\» у перестроенной версии, сама папка версии у
    /// прежней. Та же логика и по той же причине, что у <see cref="SlotWriteFolder"/>.</summary>
    public static string FirmwareWriteFolder(string versionDir) =>
        IsNewLayout(versionDir) ? FirmwareFolder(versionDir) : versionDir;

    /// <summary>Папка КОНТРОЛЛЕРА по папке версии — то, рядом с чем лежат общие папки документов.
    /// Обычная версия: родитель. ОПЦ по новой раскладке (<c>&lt;контроллер&gt;\ОПЦ\&lt;заявка&gt;</c>):
    /// дед, потому что между версией и контроллером стоит ещё «ОПЦ». ОПЦ по старой
    /// (<c>&lt;подтип&gt;\ОПЦ\&lt;версия&gt;</c>): папки контроллера над ней нет вовсе — null, документы у такой
    /// версии ищутся только внутри неё самой.</summary>
    public static string? ControllerFolderOf(string? versionDir)
    {
        if (string.IsNullOrWhiteSpace(versionDir)) return null;
        var parent = SafeParent(versionDir);
        if (parent is null) return null;
        if (!string.Equals(Path.GetFileName(parent), HierarchyFolders.Opc, StringComparison.OrdinalIgnoreCase))
            return parent;

        // Над «ОПЦ» стоит либо контроллер (новая раскладка), либо тип/подтип (старая). Отличаем по
        // тому, есть ли выше по дереву ещё один уровень: у старой раскладки родитель «ОПЦ» — это уже
        // папка подтипа, и общих папок документов в ней нет. Проверять по справочнику контроллеров
        // здесь нельзя (класс не ходит в БД), поэтому опираемся на факт: у контроллера рядом с «ОПЦ»
        // лежат его папки документов, у подтипа — папки контроллеров. Наличие хотя бы одной папки
        // документа — достаточный и дешёвый признак.
        var above = SafeParent(parent);
        if (above is null) return null;
        return SlotFolderNames.Any(slot => SafeDirExists(Path.Combine(above, slot))) ? above : null;
    }

    // ── Исполнения: папки ВНУТРИ «Прошивка\» ────────────────────────────────────────────────────

    /// <summary>ИСПОЛНЕНИЕ как папка внутри «Прошивка\» — заранее сконфигурированная сборка той же
    /// самой прошивки (docs/firmware-executions.md, решение Ильи от 04.11.2026):
    ///
    /// <code>
    /// ПО\&lt;тип&gt;\&lt;подтип&gt;\&lt;контроллер&gt;\&lt;версия&gt;\
    ///     Прошивка\                 ← ИСХОДНАЯ (базовая) сборка — есть всегда, как и раньше
    ///         3 насоса\             ← готовая конфигурация исполнения
    ///         ПЧ Danfoss\
    /// </code>
    ///
    /// <b>Почему внутрь «Прошивка\», а не рядом с ней.</b> Папки версии перечислены в
    /// <see cref="IsVersionOwnFolder"/>, и всё, что лежит в корне версии подпапкой, перестройка диска
    /// считает ЧАСТЬЮ ПРОЕКТА и утаскивает в «Прошивка\». Положи исполнение в корень — его туда же и
    /// унесёт, только вперемешку с базовой сборкой.
    ///
    /// <b>Почему базовая папка не меняется.</b> Программа, которая про исполнения не знает (старая
    /// версия у коллеги), продолжает видеть прошивку ровно там, где видела: в «Прошивка\» или, у
    /// неперестроенной версии, прямо в папке версии. Исполнения — ДОПОЛНЕНИЕ, а не замена.
    ///
    /// <b>Почему список исполнений сюда не передаётся «сам».</b> Узнать, какие исполнения бывают у
    /// шкафа, по содержимому папки НЕЛЬЗЯ: внутри «Прошивка\» законно лежат подпапки проекта (plc,
    /// hmi у KINCO — см. <see cref="IsProjectPartFolder"/>), и от исполнения они по имени не
    /// отличаются. Поэтому все методы ниже принимают СПРАВОЧНИК исполнений подтипа и считают своей
    /// только ту папку, чьё имя в нём есть.</summary>
    public static string ExecutionFolderName(string? execution) => SanitizeFolderName(execution);

    /// <summary>Папка конкретного исполнения внутри версии. Пустое исполнение — сама «Прошивка\»:
    /// «исходное исполнение» это и есть базовая папка, отдельной папки у него нет.</summary>
    public static string ExecutionFolder(string versionDir, string? execution)
    {
        var name = ExecutionFolderName(execution);
        var firmware = FirmwareFolder(versionDir);
        return name.Length == 0 ? firmware : Path.Combine(firmware, name);
    }

    /// <summary>Куда ПИСАТЬ сборку исполнения. В отличие от <see cref="FirmwareWriteFolder"/> развилки
    /// «перестроенная версия / прежняя» здесь нет: папка исполнения живёт только внутри «Прошивка\»,
    /// и у ещё не перестроенной версии она заводится сейчас — пустая «Прошивка\» рядом с файлами,
    /// лежащими в корне. Читающая сторона от этого не страдает (<see cref="FirmwareFolders(string?)"/>
    /// отдаёт обе папки), а класть исполнение в корень версии нельзя: перестройка диска считает
    /// подпапки корня частью проекта и унесла бы его в «Прошивка\» вперемешку с базовой сборкой.</summary>
    public static string ExecutionWriteFolder(string versionDir, string? execution) =>
        ExecutionFolder(versionDir, execution);

    /// <summary>Какие исполнения в ЭТОЙ версии реально собраны: папки внутри «Прошивка\», чьё имя
    /// есть в справочнике <paramref name="catalog"/> и в которых лежит хоть один файл.
    ///
    /// Справочник обязателен именно здесь, а не «для порядка»: без него подпапки проекта (plc, hmi)
    /// выглядели бы исполнениями. И наоборот — исполнение, которого в справочнике нет, не
    /// «обнаруживается» папкой: справочник ведёт человек, и убранное им исполнение не должно
    /// воскресать от того, что папка на диске осталась (docs/firmware-executions.md, § «Как
    /// исполнение исчезает»).
    ///
    /// Порядок — как в справочнике. Недоступная папка версии — пустой список.</summary>
    public static IReadOnlyList<string> BuiltExecutions(string? versionDir, IEnumerable<string> catalog)
    {
        var result = new List<string>();
        if (string.IsNullOrWhiteSpace(versionDir)) return result;
        foreach (var execution in catalog)
            if (HasExecutionBuild(versionDir, execution))
                result.Add(execution);
        return result;
    }

    /// <summary>В этой версии есть собранная конфигурация под это исполнение. Пустая папка не
    /// считается: «папку завели, файл не положили» — это ровно то состояние, при котором наладчик
    /// обязан получить базовую сборку с пометкой, а не пустоту.</summary>
    public static bool HasExecutionBuild(string? versionDir, string? execution)
    {
        if (string.IsNullOrWhiteSpace(versionDir)) return false;
        if (ExecutionFolderName(execution).Length == 0) return false;
        var folder = ExecutionFolder(versionDir!, execution);
        return SafeDirExists(folder) && HasFiles(folder);
    }

    /// <summary>Папки, в которых лежит сборка этого исполнения, по списку папок-кандидатов версии
    /// (локальная копия раньше сетевой — см. SearchView.VersionFolders). Только реально собранные:
    /// пустой список означает «в этой версии такой конфигурации нет», и решение, что с этим делать,
    /// принимает FirmwareExecutionPick, а не этот класс.</summary>
    public static IReadOnlyList<string> ExecutionFolders(IEnumerable<string?> versionDirs, string? execution)
    {
        var result = new List<string>();
        if (ExecutionFolderName(execution).Length == 0) return result;
        foreach (var dir in versionDirs)
            if (HasExecutionBuild(dir, execution))
                result.Add(ExecutionFolder(dir!, execution));
        return result;
    }

    /// <summary>Папки исполнений, которые надо ИСКЛЮЧИТЬ при поиске базовой сборки. Поиск файла
    /// прошивки спускается во вложенные папки (LoaderFiles.Find — ради проектов, разложенных
    /// подпапками), и без этого списка базовая сборка, которой в папке почему-то не оказалось, молча
    /// подменилась бы файлом чужого исполнения. Молчаливая подмена — единственное, чего в этой
    /// задаче нельзя (docs/firmware-executions.md, требование 3).</summary>
    public static IReadOnlyList<string> ExecutionFoldersToSkip(IEnumerable<string?> versionDirs, IEnumerable<string> catalog)
    {
        var catalogList = catalog as IReadOnlyList<string> ?? catalog.ToList();
        var result = new List<string>();
        foreach (var dir in versionDirs)
        {
            if (string.IsNullOrWhiteSpace(dir)) continue;
            foreach (var execution in catalogList)
            {
                if (ExecutionFolderName(execution).Length == 0) continue;
                var folder = ExecutionFolder(dir!, execution);
                if (SafeDirExists(folder)) result.Add(folder);
            }
        }
        return result;
    }

    /// <summary>Имя исполнения → имя папки. Недопустимые в имени файла символы заменяются пробелом,
    /// пробелы схлопываются, точки и пробелы по краям срезаются (Windows молча отбрасывает их в
    /// конце имени папки, и папка «3 насоса.» нашлась бы как «3 насоса» — или не нашлась бы вовсе).
    /// Само имя человеку принадлежит: регистр и слова не трогаем.</summary>
    private static string SanitizeFolderName(string? execution)
    {
        if (string.IsNullOrWhiteSpace(execution)) return "";
        var invalid = Path.GetInvalidFileNameChars();
        var sb = new System.Text.StringBuilder(execution!.Length);
        foreach (var ch in execution!) sb.Append(invalid.Contains(ch) ? ' ' : ch);
        var parts = sb.ToString().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        return string.Join(" ", parts).Trim('.', ' ');
    }

    private static string? SafeParent(string path)
    {
        try { return Path.GetDirectoryName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)); }
        catch (Exception) { return null; }
    }

    private static bool SafeDirExists(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        try { return Directory.Exists(path); }
        catch (Exception) { return false; }
    }
}
