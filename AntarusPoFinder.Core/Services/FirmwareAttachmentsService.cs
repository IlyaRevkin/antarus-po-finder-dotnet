using System.Collections.Generic;
using System.IO;
using AntarusPoFinder.Core.Data;
using AntarusPoFinder.Core.Domain;
using AntarusPoFinder.Core.Infrastructure;

namespace AntarusPoFinder.Core.Services;

/// <summary>Что оператор хочет доложить/заменить у УЖЕ загруженной версии. null в поле — «не трогать
/// это вложение», пустая строка — «убрать ссылку» (файлы на диске при этом не удаляются: их могут
/// использовать другие версии того же контроллера — все три «карты»/инструкция лежат в общих папках
/// контроллера, а не внутри папки версии).</summary>
public class FirmwareAttachmentsRequest
{
    public string RootPath { get; set; } = "";

    /// <summary>Кто выкладывает инструкцию на хостинг — та же настройка, что и при загрузке новой
    /// версии, чтобы доложенная инструкция ложилась туда же, куда легла бы приложенная сразу. См.
    /// InstructionStorage.</summary>
    public IInstructionPublisher? InstructionPublisher { get; set; }

    public string GroupName { get; set; } = "";
    public string SubtypeName { get; set; } = "";
    public string ControllerName { get; set; } = "";

    public string? IoMapSourcePath { get; set; }
    public string? InstructionsSourcePath { get; set; }
    public string? ModbusMapSourcePath { get; set; }
    public string? HmiSourcePath { get; set; }

    /// <summary>Загрузочная прошивка ПЛК (.lfs, а для не-Segnetics — сам файл/проект прошивки),
    /// которую надо ДОЛОЖИТЬ в саму папку версии (disk_path) — не в общую папку контроллера, как
    /// «карты»/инструкция, а именно рядом с самой прошивкой, потому что по файлам этой папки карточка
    /// и считает флаги LFS/PSL. Тикет коллеги: «к уже загруженной прошивке доложить .lfs, если его
    /// нет». null — не трогать; файлы не заменяются массово, кладётся ровно выбранный файл
    /// (перезаписью одноимённого).</summary>
    public string? PlcFileSourcePath { get; set; }

    /// <summary>Исходный проект Segnetics (.psl) — НЕ загрузочный, отдельно от .lfs выше: у Segnetics
    /// это два разных файла (исходник SMLogix и собранный файл заливки), и оператор в модерации может
    /// доложить любой из них. Кладётся в ту же папку версии тем же способом, что и PlcFileSourcePath;
    /// разведены только ради ясности в UI (см. EditFirmwareDialog). null — не трогать.</summary>
    public string? PslFileSourcePath { get; set; }
}

/// <summary>Applied — человекочитаемые названия того, что реально изменилось (для статуса/тоста),
/// Warnings — не фатальные проблемы отдельных вложений: остальные всё равно применяются.</summary>
public record FirmwareAttachmentsResult(List<string> Applied, List<string> Warnings)
{
    public bool AnythingChanged => Applied.Count > 0;
}

/// <summary>Доп. файлы (Карта in/out, Карта modbus, Инструкция, HMI-проект) можно было приложить
/// ТОЛЬКО в момент загрузки новой версии прошивки: если карту прислали позже, единственным способом
/// было перезалить версию заново. Этот сервис делает ту же работу, что и соответствующая часть
/// FirmwareUploadService.Upload, но для существующей записи fw_versions — и используется из
/// «Настройки → Прошивки → Изменить» (EditFirmwareDialog).
///
/// Копирование намеренно то же самое, что при загрузке (FileSystemHelpers.CopyFileOrFolderShallow в
/// общие папки контроллера, HMI — в свою папку версии), чтобы «доложенный» файл лежал ровно там же,
/// где лежал бы, если бы его приложили сразу.</summary>
public static class FirmwareAttachmentsService
{
    /// <summary>Копирует HMI-проект в папку HMI контроллера под именем «{версия}_hmi» — общий код
    /// для загрузки новой версии (FirmwareUploadService.Upload) и для догрузки к существующей.
    /// Возвращает путь, который надо записать в fw_versions.hmi_path.
    ///
    /// <b>Выбор одного файла может означать выбор папки.</b> У FStudio (.fsprj) проект — это папка, а
    /// сам .fsprj лишь точка входа: модель панели и драйверы лежат рядом с ним. Раньше такой выбор
    /// копировал ОДИН файл, да ещё и переименовывал его в «{версия}_hmi.fsprj» — и проект открывался
    /// пустым («модель HMI не соответствует текущему программному обеспечению»). Теперь для таких
    /// форматов копируется вся папка, а имена файлов внутри сохраняются: переименовывать нечего —
    /// уникальность обеспечивает имя самой папки «{версия}_hmi» (см. <see cref="HmiProjectFormat"/>).
    ///
    /// replaceExisting=false (загрузка) — файлы докладываются поверх, как было исторически;
    /// replaceExisting=true (замена HMI у существующей версии) — старая папка проекта сносится
    /// целиком, иначе от предыдущего проекта остались бы «висящие» файлы, которых в новом нет.</summary>
    public static string CopyHmiProject(string hmiRootFolder, string versionRaw, string sourcePath, bool replaceExisting = false)
    {
        Directory.CreateDirectory(hmiRootFolder);
        var sourceFolder = Directory.Exists(sourcePath) ? sourcePath : HmiProjectFormat.ProjectFolderOf(sourcePath);
        if (sourceFolder is not null)
        {
            var hmiDstFolder = Path.Combine(hmiRootFolder, $"{versionRaw}_hmi");
            // Проект УЖЕ лежит там, куда мы собрались его класть — оператор выбрал сохранённый проект
            // повторно (или через другую букву сетевого диска). Копировать нечего, а копирование
            // папки в саму себя раньше падало «файл занят другим процессом».
            if (SamePath(sourceFolder, hmiDstFolder)) return hmiDstFolder;
            // Вложенность в любую сторону: при replaceExisting снос папки назначения унёс бы источник.
            if (IsInside(sourceFolder, hmiDstFolder) || IsInside(hmiDstFolder, sourceFolder))
                throw new IOException("Папка проекта панели вложена в папку назначения — выберите проект из другого места.");
            if (replaceExisting && Directory.Exists(hmiDstFolder)) FileSystemHelpers.RmtreeSafe(hmiDstFolder);
            Directory.CreateDirectory(hmiDstFolder);
            FileSystemHelpers.CopyTree(sourceFolder, hmiDstFolder, overwrite: false);
            RemoveOurSingleFileCopy(hmiRootFolder, versionRaw);
            return hmiDstFolder;
        }
        var hmiDstName = $"{versionRaw}_hmi{Path.GetExtension(sourcePath)}";
        var dst = Path.Combine(hmiRootFolder, hmiDstName);
        if (!SamePath(sourcePath, dst)) File.Copy(sourcePath, dst, overwrite: true);
        return dst;
    }

    /// <summary>То же, но для прежнего пути из записи: он мог указывать на обрубок в ДРУГОЙ папке
    /// (общая «HMI» контроллера до перестройки диска). Удаляем только собственную копию под нашим же
    /// именем и только если она не внутри нового проекта.</summary>
    private static void RemoveSupersededSingleFileCopy(string? previousPath, string versionRaw, string newPath)
    {
        if (string.IsNullOrWhiteSpace(previousPath)) return;
        if (!HmiProjectFormat.IsOurSingleFileCopy(previousPath, versionRaw)) return;
        if (SamePath(previousPath!, newPath) || IsInside(previousPath!, newPath)) return;
        try { if (File.Exists(previousPath)) File.Delete(previousPath); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>Сносит обрубок «{версия}_hmi.fsprj» — то, что программа клала на место проекта-папки
    /// до исправления. Теперь на его место лёг нормальный проект папкой, и оставлять рядом файл нельзя:
    /// у половины версий контроллера в общей папке «HMI» так и лежат оба, и открывается (а потом
    /// синхронизируется коллегам) именно пустой файл. Имя целиком наше — пользовательский файл так
    /// называться не может, поэтому удаление безопасно. Не удалось удалить — не беда: рабочий проект
    /// уже на месте, а путь в БД теперь указывает на папку.</summary>
    private static void RemoveOurSingleFileCopy(string hmiRootFolder, string versionRaw)
    {
        foreach (var ext in HmiProjectFormat.FolderProjectExtensions)
        {
            var stray = Path.Combine(hmiRootFolder, $"{versionRaw}{HmiProjectFormat.StoredFolderSuffix}{ext}");
            try { if (File.Exists(stray)) File.Delete(stray); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    /// <summary>Один и тот же путь с точностью до формы записи (хвостовой слеш, регистр, «..»).
    /// Недоступный путь не нормализуется — тогда сравниваем как есть.</summary>
    private static bool SamePath(string a, string b)
    {
        try
        {
            return string.Equals(Path.GetFullPath(a).TrimEnd(Path.DirectorySeparatorChar),
                Path.GetFullPath(b).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException)
        {
            return PathsEqual(a, b);
        }
    }

    /// <summary><paramref name="inner"/> лежит внутри <paramref name="outer"/> (строго — не он сам).</summary>
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

    /// <param name="shortcuts">Нужен только инструкции, уехавшей на третий диск (см.
    /// InstructionStorage): на первом остаётся ярлык. null — ярлык не создаётся.</param>
    /// <param name="stubs">Чем рисовать заглушку «Инструкция в разработке», если ссылку на
    /// инструкцию убрали (см. InstructionStub). null — заглушка не кладётся.</param>
    public static FirmwareAttachmentsResult Apply(Database db, HierarchyService hierarchy,
        FwVersionRecord record, FirmwareAttachmentsRequest request, IShortcutCreator? shortcuts = null,
        IInstructionStubWriter? stubs = null)
    {
        var applied = new List<string>();
        var warnings = new List<string>();

        if (string.IsNullOrEmpty(request.RootPath) || !Directory.Exists(request.RootPath))
            return new FirmwareAttachmentsResult(applied, new List<string> { "Сетевой диск недоступен — доп. файлы не изменены." });

        string root = request.RootPath, g = request.GroupName, s = request.SubtypeName, c = request.ControllerName;

        // Куда класть документ, решает VersionLayout: своя папка внутри версии у той, что уже
        // перестроена (docs/hierarchy-rework-plan.md, этап 4), общая папка контроллера у прежней.
        // Спрашиваем по КАЖДОМУ документу отдельно и в момент копирования — раскладка версии может
        // измениться между двумя правками одной и той же карточки.
        var versionDir = FirmwarePathLocalizer.Localize(record.DiskPath, root);
        var ctrlFolder = Path.Combine(HierarchyService.GroupSubFolder(root, g, s), c);
        string SlotFolder(string slot) => VersionLayout.SlotWriteFolder(versionDir, ctrlFolder, slot);

        string? ioMap = Resolve("Карта in/out", request.IoMapSourcePath, record.IoMapPath,
            () => SlotFolder(HierarchyFolders.IoMap), applied, warnings);
        string? modbus = Resolve("Карта modbus", request.ModbusMapSourcePath, record.ModbusMapPath,
            () => SlotFolder(HierarchyFolders.Modbus), applied, warnings);
        // Инструкция копируется не тем же Resolve, что «карты»: у неё есть выкладка на хостинг — эта
        // развилка живёт в InstructionStorage, здесь только подставляется способ копирования.
        string? instr = Resolve("Инструкция", request.InstructionsSourcePath, record.InstructionsPath,
            () => SlotFolder(HierarchyFolders.Instructions), applied, warnings,
            copy: (src, folder) => InstructionStorage.Copy(src, folder, root, warnings, record.VersionRaw,
                request.InstructionPublisher).StoredPath);

        // Ссылку на инструкцию убрали — папка снова остаётся без документа, и вместо пустоты в ней
        // должна лежать заглушка (см. InstructionStub). Ровно тот же случай, что и загрузка версии
        // без инструкции, поэтому и обрабатывается одинаково.
        if (instr is not null && instr.Length == 0)
            InstructionStub.EnsureForVersion(SlotFolder(HierarchyFolders.Instructions), root,
                record.VersionRaw, stubs, warnings, request.InstructionPublisher);

        string? hmi = null;
        if (request.HmiSourcePath is not null && !PathsEqual(request.HmiSourcePath, record.HmiPath))
        {
            if (request.HmiSourcePath.Length == 0)
            {
                hmi = "";
                applied.Add("HMI-проект (ссылка убрана)");
            }
            else if (!File.Exists(request.HmiSourcePath) && !Directory.Exists(request.HmiSourcePath))
            {
                warnings.Add($"HMI-проект: путь не найден — {request.HmiSourcePath}");
            }
            else
            {
                try
                {
                    hmi = CopyHmiProject(SlotFolder(HierarchyFolders.Hmi), record.VersionRaw, request.HmiSourcePath, replaceExisting: true);
                    applied.Add("HMI-проект");
                    // Прежний обрубок мог лежать не там, куда пишем сейчас: до перестройки диска папка
                    // «HMI» была общей у контроллера, а теперь она внутри версии (VersionLayout). Путь
                    // из записи — единственное, что про то место известно, поэтому чистим по нему.
                    RemoveSupersededSingleFileCopy(FirmwarePathLocalizer.Localize(record.HmiPath, root), record.VersionRaw, hmi);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    warnings.Add($"HMI-проект: {ex.Message}");
                }
            }
        }

        // Файлы прошивки (.lfs загрузочный и .psl исходник) — в саму папку версии (disk_path), а не
        // в общие папки контроллера. Независимы от доп. файлов выше: ничего не пишут в БД (флаги
        // LFS/PSL карточка считает по факту файлов в папке при следующем поиске), поэтому идут до
        // раннего выхода про UpdateFwVersionAttachments. У Segnetics это два разных файла — оба
        // ложатся одинаково, разведены только в UI (см. EditFirmwareDialog); у остальных заполняется
        // одно поле PlcFileSourcePath.
        CopyFirmwareFileIntoVersionFolder(record, root, request.PlcFileSourcePath, applied, warnings);
        CopyFirmwareFileIntoVersionFolder(record, root, request.PslFileSourcePath, applied, warnings);

        if (ioMap is null && modbus is null && instr is null && hmi is null)
            return new FirmwareAttachmentsResult(applied, warnings);

        db.UpdateFwVersionAttachments(record.Id!.Value, ioMap, instr, modbus, hmi);
        if (ioMap is not null) record.IoMapPath = ioMap;
        if (instr is not null) record.InstructionsPath = instr;
        if (modbus is not null) record.ModbusMapPath = modbus;
        if (hmi is not null) record.HmiPath = hmi;

        return new FirmwareAttachmentsResult(applied, warnings);
    }

    /// <summary>Копирует один файл прошивки (.lfs/.psl или сам проект) в САМУ папку версии
    /// (disk_path), а не в общие папки контроллера. Путь версии мог быть записан коллегой в его форме
    /// диска — приводим к нашей (FirmwarePathLocalizer), тот же приём, что при правке hw/поиске.
    /// null/пусто — ничего не делает (поле «не трогать»).</summary>
    private static void CopyFirmwareFileIntoVersionFolder(FwVersionRecord record, string root,
        string? src, List<string> applied, List<string> warnings)
    {
        if (string.IsNullOrEmpty(src)) return;
        if (!File.Exists(src))
        {
            warnings.Add($"Файл прошивки: путь не найден — {src}");
            return;
        }
        var versionFolder = FirmwarePathLocalizer.Localize(record.DiskPath, root);
        // disk_path мог указывать на одиночный файл (не папку) — тогда «папка версии» это его родитель.
        if (!Directory.Exists(versionFolder)) versionFolder = Path.GetDirectoryName(versionFolder) ?? "";
        if (string.IsNullOrEmpty(versionFolder) || !Directory.Exists(versionFolder))
        {
            warnings.Add("Файл прошивки: папка версии на диске недоступна — файл не добавлен.");
            return;
        }
        try
        {
            var dst = Path.Combine(versionFolder, Path.GetFileName(src));
            // Оператор выбрал файл, который УЖЕ лежит в папке версии — а это ровно то, что предлагает
            // диалог по умолчанию (он открывается в папке версии на сервере). Копирование файла в
            // самого себя Windows отвергает как «файл занят другим процессом», и модерация падала с
            // этой ошибкой на попытке указать разом HMI, .lfs и .psl, взятые оттуда же.
            if (SamePath(src, dst))
            {
                applied.Add($"Файл прошивки ({Path.GetFileName(src)}) — уже на месте");
                return;
            }
            File.Copy(src, dst, overwrite: true);
            var ext = Path.GetExtension(src).ToLowerInvariant();
            applied.Add(string.IsNullOrEmpty(ext) ? "Файл прошивки" : $"Файл прошивки ({ext})");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            warnings.Add($"Файл прошивки: {ex.Message}");
        }
    }

    /// <summary>Возвращает новое значение поля для записи в БД, или null — если менять нечего.
    /// <paramref name="copy"/> — чем копировать (источник, папка назначения) → путь для БД; по
    /// умолчанию обычное копирование в общую папку контроллера, инструкция подставляет своё.</summary>
    private static string? Resolve(string label, string? requested, string current, Func<string> destFolder,
        List<string> applied, List<string> warnings, Func<string, string, string>? copy = null)
    {
        if (requested is null || PathsEqual(requested, current)) return null;

        if (requested.Length == 0)
        {
            applied.Add($"{label} (ссылка убрана)");
            return "";
        }
        if (!File.Exists(requested) && !Directory.Exists(requested))
        {
            warnings.Add($"{label}: путь не найден — {requested}");
            return null;
        }
        try
        {
            var stored = copy is null
                ? FileSystemHelpers.CopyFileOrFolderShallow(requested, destFolder())
                : copy(requested, destFolder());
            applied.Add(label);
            return stored;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            warnings.Add($"{label}: {ex.Message}");
            return null;
        }
    }

    private static bool PathsEqual(string a, string b) =>
        string.Equals(a.Trim().TrimEnd(Path.DirectorySeparatorChar), b.Trim().TrimEnd(Path.DirectorySeparatorChar),
            StringComparison.OrdinalIgnoreCase);
}
