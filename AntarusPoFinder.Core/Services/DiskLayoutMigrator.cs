using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using AntarusPoFinder.Core.Domain;

namespace AntarusPoFinder.Core.Services;

/// <summary>Разовая операция «привести уже накопленный диск к текущим правилам раскладки» — то, чего
/// не хватало после смены правил: правила поменялись только для НОВЫХ загрузок, а всё, что лежало на
/// диске годами, осталось как было («всё так же раскидано, файлы не переименованы»).
///
/// Что умеет (каждый пункт включается отдельно — см. <see cref="MigrationOptions"/>):
/// <list type="number">
/// <item><description><b>Имя файла прошивки = имя папки версии</b> (см. FirmwareNaming.BuildFirmwareFilename).
/// Переименование делается ТОЛЬКО там, где в папке версии ровно один файл прошивки: имя файла хранится
/// в <c>fw_versions.executable_hint</c>, а он при импорте общего конфига у совпавших строк не
/// обновляется — переименовав файл в многофайловой папке, мы осиротили бы подсказку у коллег и
/// «Открыть прошивку ПЛК» стало бы открывать не то. Папка версии при этом НЕ трогается: её имя —
/// якорь <c>disk_path</c>, и пока оно не меняется, миграция ничего не ломает у других машин.</description></item>
/// <item><description><b>Инструкции — к правилам</b>: имя файла «инструкция_&lt;версия&gt;» и
/// заглушка «Инструкция в разработке» там, где документа нет (см. <see cref="InstructionNaming"/>,
/// <see cref="InstructionStub"/>). Легшее на диск уходит и на хостинг (см.
/// <see cref="InstructionPublisher"/>).</description></item>
/// <item><description><b>Файлы прошивки — в подпапку «Прошивка»</b> внутри папки версии
/// (docs/hierarchy-rework-plan.md, этап 4). Имя самой папки версии НЕ меняется, поэтому disk_path
/// остаётся валидным у всех коллег, включая тех, кто ещё не обновился.</description></item>
/// <item><description><b>ОПЦ — внутрь контроллера</b>, с переименованием папки в номер заявки/SN
/// (этап 5). Единственная операция всей перестройки, которая МЕНЯЕТ disk_path; у коллег он не
/// обновится импортом конфига никогда, и чинится локальным проходом
/// HierarchyService.RepairOpcDiskPaths на каждой машине.</description></item>
/// </list>
///
/// <b>Имя папки версии не переименовывается никогда</b> (кроме ОПЦ) — этот якорь и делает этапы 4
/// бесплатными для синхронизации. Порядок выкатки тоже задан планом и соблюдён: сначала релиз,
/// который УМЕЕТ ЧИТАТЬ обе раскладки и ничего не переносит (VersionLayout/OpcLayout — режим
/// совместимости), и только потом, отдельным решением человека, галочки переноса в этом окне.
///
/// Три свойства, без которых такую операцию нельзя выпускать, и они здесь есть:
/// • <b>сухой прогон</b> — <see cref="Plan"/> ничего не делает, только перечисляет операции;
/// • <b>журнал</b> — <see cref="Apply"/> отдаёт список выполненного с исходом каждой операции,
///   вызывающий сохраняет его файлом ДО того, как показать результат человеку;
/// • <b>идемпотентность</b> — повторный прогон видит уже переименованное/переехавшее и не делает
///   ничего; прерванный на середине прогон дочищается следующим.</summary>
public static class DiskLayoutMigrator
{
    /// <summary>Файлы, которые в папке версии не считаются файлом прошивки: журнал изменений (его
    /// пишет само приложение) и ярлыки (их кладут для коллег со старым клиентом).</summary>
    private static readonly string[] NonFirmwareNames = { "CHANGELOG.md" };

    public enum OpKind
    {
        /// <summary>Переименовать файл прошивки в каноническое имя.</summary>
        RenameFirmware,

        /// <summary>Этап 4: собрать файлы прошивки в подпапку «Прошивка» внутри папки версии. Имя
        /// самой папки версии не меняется — значит disk_path остаётся валидным у всех коллег.</summary>
        FoldIntoVersion,

        /// <summary>Этап 5: перенести ОПЦ-версию из общей «ОПЦ» подтипа в «ОПЦ» её контроллера,
        /// переименовав папку в номер заявки/SN. Единственная операция, меняющая disk_path.</summary>
        MoveOpc,

        /// <summary>Привести имена файлов в папке «Инструкция» версии к «инструкция_&lt;версия&gt;»
        /// (см. InstructionNaming). Операция ПАПОЧНАЯ, а не пофайловая: файлы перечисляются в момент
        /// выполнения, а не планирования.</summary>
        RenameInstruction,

        /// <summary>Положить в папку «Инструкция» заглушку «Инструкция в разработке», если
        /// настоящего документа там нет (см. InstructionStub).</summary>
        PlaceInstructionStub,
    }

    public sealed class Op
    {
        public OpKind Kind { get; init; }

        /// <summary>Что переименовываем/переносим. Изменяемое ради одного случая — многофайловой
        /// папки версии, где файл прошивки указывает человек уже после планирования
        /// (см. <see cref="NeedsChoice"/>); у всех остальных операций задаётся планом и не меняется.</summary>
        public string Source { get; set; } = "";
        public string Target { get; set; } = "";

        /// <summary>Человеческое пояснение — что это за файл и почему операция нужна. Изменяемое:
        /// папочные операции (переименование инструкций) узнают, что именно они сделали, только в
        /// момент выполнения, и дописывают это сюда — журнал должен отвечать «что поменяли».</summary>
        public string Note { get; set; } = "";

        /// <summary>Папка версии (для переименования) — та, что реально найдена на диске.</summary>
        public string VersionDir { get; init; } = "";

        /// <summary>Значения <c>disk_path</c>, которыми эта папка записана в базе — по ним вызывающий
        /// правит записи. Обычно совпадают с <see cref="VersionDir"/>, но не всегда: у части строк
        /// путь мог устареть (папку переименовали на диске, и найдена она соседом —
        /// FirmwareDiskPresence.ResolveVersionDir). Правку имени файла надо доставить КАЖДОЙ такой
        /// строке, иначе в базе останется имя файла, которого на диске уже нет.</summary>
        public IReadOnlyList<string> RecordPaths { get; init; } = Array.Empty<string>();

        /// <summary>Имя файла до и после — то, чем правятся filename/executable_hint.</summary>
        public string OldName => Path.GetFileName(Source);
        public string NewName => Path.GetFileName(Target);

        /// <summary>ok / skip / error / off — заполняется в Apply. «off» значит «оператор снял
        /// галочку», то есть операция не выполнялась вовсе: план собирается программой, но решает,
        /// что из него делать, человек.</summary>
        public string Status { get; set; } = "";
        public string Error { get; set; } = "";

        /// <summary>Делать ли эту операцию. План — предложение, а не приговор: до этого выбор был
        /// только категориями («переименовать всё» / «не переименовывать»), и одна спорная строка
        /// заставляла отключать весь пункт целиком.</summary>
        public bool Selected { get; set; } = true;

        /// <summary>Файлы папки версии, из которых оператор выбирает прошивку вручную (см.
        /// <see cref="NeedsChoice"/>). Относительными путями от <see cref="VersionDir"/>.</summary>
        public IReadOnlyList<string> Candidates { get; init; } = Array.Empty<string>();

        /// <summary>Операция ждёт ручного выбора: в папке версии несколько файлов, и какой из них
        /// прошивка — программа не знает. Раньше такая папка просто уходила в Skipped и переименовать
        /// её было нечем; теперь строка остаётся в плане, а файл указывает человек.</summary>
        public bool NeedsChoice => Kind == OpKind.RenameFirmware && Source.Length == 0;

        /// <summary>Номер заявки и заводской SN — нужны, чтобы построить каноническое имя после
        /// ручного выбора файла (у ОПЦ они входят в имя, см. FirmwareNaming.BuildFirmwareFilename).</summary>
        public string RequestNum { get; init; } = "";
        public string CabinetSn { get; init; } = "";

        /// <summary>Id записи fw_versions, чей disk_path надо переписать после удачного переноса
        /// (только <see cref="OpKind.MoveOpc"/>). 0 — правка БД этой операции не нужна.</summary>
        public int FwVersionId { get; init; }

        /// <summary>Строка версии, к которой относится папка (нужна операциям по инструкции: имя
        /// файла строится от неё). У ОПЦ имя папки версией не является, поэтому берётся из записи.</summary>
        public string VersionRaw { get; init; } = "";

        public string KindLabel => Kind switch
        {
            OpKind.RenameFirmware => NeedsChoice ? "Указать файл прошивки" : "Переименовать прошивку",
            OpKind.FoldIntoVersion => "Файлы прошивки → «Прошивка»",
            OpKind.MoveOpc => "ОПЦ → внутрь контроллера",
            OpKind.RenameInstruction => "Переименовать инструкцию",
            OpKind.PlaceInstructionStub => "Заглушка «в разработке»",
            _ => "",
        };
    }

    /// <param name="FoldFilesIntoVersion">Этап 4: собрать файлы прошивки в подпапку «Прошивка».</param>
    /// <param name="MoveOpcIntoController">Этап 5: перенести ОПЦ внутрь контроллера.</param>
    /// <param name="FixInstructions">Привести инструкции версий к правилам: имя файла
    /// «инструкция_&lt;версия&gt;» и заглушка «Инструкция в разработке» там, где документа нет.</param>
    public sealed record MigrationOptions(bool RenameFirmwareFiles,
        bool FoldFilesIntoVersion = false, bool MoveOpcIntoController = false, bool FixInstructions = false);

    /// <summary>Вход планировщика. Версии берутся из БД (та же выборка, что и вкладка «Прошивки»):
    /// именно они задают, где папка версии и какое у файла должно быть каноническое имя.</summary>
    public sealed record MigrationInput(
        string Root,
        IReadOnlyList<FwVersionRecord> Versions,
        MigrationOptions Options);

    public sealed record MigrationPlan(IReadOnlyList<Op> Ops, IReadOnlyList<string> Skipped)
    {
        public int Count => Ops.Count;
    }

    // ── Планирование (сухой прогон) ──────────────────────────────────────────

    public static MigrationPlan Plan(MigrationInput input)
    {
        var ops = new List<Op>();
        var skipped = new List<string>();

        if (input.Options.RenameFirmwareFiles)
            PlanRenames(input, ops, skipped);
        // Порядок важен: сначала «Прошивка\» внутри версии, потом перенос ОПЦ. Наоборот было бы
        // неверно — переехавшая ОПЦ-папка перестала бы совпадать с disk_path, по которому этап 4
        // ищет свои папки версий в этом же прогоне.
        if (input.Options.FoldFilesIntoVersion)
            PlanFoldIntoVersion(input, ops, skipped);
        if (input.Options.MoveOpcIntoController)
            PlanOpcMoves(input, ops, skipped);
        // Имена инструкций и заглушки — ПОСЛЕДНИМИ и намеренно: сборка файлов внутрь версии выше
        // могла завести саму папку «Инструкция», и обе операции по инструкции обязаны увидеть уже
        // конечную картину. Обе перечисляют файлы в момент выполнения, поэтому порядок здесь и решает.
        if (input.Options.FixInstructions)
            PlanInstructionNaming(input, ops, skipped);

        return new MigrationPlan(ops, skipped);
    }

    // ── Инструкции: каноническое имя файла и заглушка ───────────────────────

    /// <summary>Две операции на каждую папку «Инструкция», ПРИНАДЛЕЖАЩУЮ версии: привести имена
    /// файлов к «инструкция_&lt;версия&gt;.&lt;расширение&gt;» и положить заглушку, если документа нет.
    ///
    /// <b>Почему только папки внутри версии.</b> Общая папка «Инструкция» контроллера (старая
    /// раскладка) принадлежит ВСЕМ его версиям сразу — какой из них приписать лежащий там файл,
    /// неизвестно, и переименование в «инструкция_2.1.0042.0001…» было бы выдумкой, а не приведением
    /// к правилам. Такие папки попадают в Skipped, а заглушки в них кладёт создание структуры
    /// (HierarchyService.ApplyStructurePlan) — там версия и не нужна.</summary>
    private static void PlanInstructionNaming(MigrationInput input, List<Op> ops, List<string> skipped)
    {
        var newLayoutSeen = false;

        foreach (var (dir, first, _) in VersionDirs(input))
        {
            // Своя папка «Инструкция» есть только у версии, которую уже перестроили (или перестроят
            // в этом же прогоне галочкой «Собрать файлы прошивок…»).
            if (!VersionLayout.IsNewLayout(dir) && !input.Options.FoldFilesIntoVersion) continue;
            newLayoutSeen = true;

            var folder = VersionLayout.SlotFolder(dir, HierarchyFolders.Instructions);

            ops.Add(new Op
            {
                Kind = OpKind.RenameInstruction,
                Source = folder,
                Target = folder,
                VersionDir = dir,
                VersionRaw = first.VersionRaw,
                Note = $"{first.VersionRaw}: имя файла → «{InstructionNaming.Prefix}{first.VersionRaw}»",
            });
            ops.Add(new Op
            {
                Kind = OpKind.PlaceInstructionStub,
                Source = folder,
                Target = InstructionStub.PathFor(folder, first.VersionRaw),
                VersionDir = dir,
                VersionRaw = first.VersionRaw,
                Note = $"{first.VersionRaw}: {InstructionStub.Text}, если документа нет",
            });
        }

        if (!newLayoutSeen)
            skipped.Add("Своих папок «Инструкция» у версий пока нет — имена файлов правятся только там, " +
                        "где инструкция принадлежит конкретной версии. Общие папки контроллера получат " +
                        "заглушки при создании недостающих папок структуры.");
    }

    // ── Этап 4: файлы прошивки внутрь «Прошивка\» ───────────────────────────

    /// <summary>Одна операция на папку версии: создать «Прошивка\» и перенести туда файлы верхнего
    /// уровня, кроме служебных (CHANGELOG.md остаётся в корне — его читает досмотр диска по
    /// фиксированному пути; ярлыки тоже остаются, они для людей в проводнике).
    ///
    /// Вместе с файлами заводятся все ПЯТЬ папок версии — «Прошивка» и четыре папки документов
    /// (VersionLayout.EnsureFolders). Именно этого и ждут от перестройки: человек открывает папку
    /// версии в проводнике и видит, куда что класть. Пустая папка документа ничего не прячет — пока в
    /// ней нет файлов, документ читается из общей папки контроллера (VersionLayout.SlotBestReadFolder).
    ///
    /// Чего операция СОЗНАТЕЛЬНО не делает — не копирует в них содержимое общих папок контроллера.
    /// Копирование удвоило бы диск и убило бы смысл «карту обновляют в одном месте, и она обновилась
    /// у всех версий». Внутрь версии попадают ровно те документы, которые приложат ИМЕННО К НЕЙ после
    /// переезда (VersionLayout.SlotWriteFolder).</summary>
    private static void PlanFoldIntoVersion(MigrationInput input, List<Op> ops, List<string> skipped)
    {
        foreach (var (dir, first, dbPaths) in VersionDirs(input))
        {
            // Планируем по тому, что осталось НАВЕРХУ, а не по наличию «Прошивка\». Разница видна
            // ровно в том случае, ради которого этот этап и делался продолжаемым: прогон, прерванный
            // обрывом шары, оставляет папку созданной, а часть файлов — наверху. Проверяй мы
            // «Прошивка\ уже есть», такая версия молча пропускалась бы всеми последующими прогонами
            // и осталась бы недоперестроенной навсегда.
            // Записи, чьей папки на диске нет, сюда не доходят вовсе — их отсеивает VersionDirs, так
            // же как и у переименования: создавать дерево там, где прошивки нет, эта операция не
            // должна. Кроме файлов, поводом для операции служат недостающие папки версии: их пять, и
            // завести их надо даже там, где файлы уже внизу (прерванный прогон) или где их не
            // осталось вовсе.
            var files = TopLevelFiles(dir).Where(f => !VersionLayout.IsServiceFile(f)).ToList();
            // Подпапки самого проекта (KINCO разворачивается в «plc» и «hmi») переезжают вместе с
            // файлами: без этого перестройка уносила вниз только то, что лежало отдельными файлами, а
            // программа ПЛК так и оставалась в «plc\» в корне версии.
            var strays = VersionLayout.StrayProjectFolders(dir);
            if (files.Count == 0 && strays.Count == 0 && VersionLayout.HasAllFolders(dir)) continue;

            var moving = new List<string>();
            if (files.Count > 0) moving.Add($"файлов {files.Count}");
            if (strays.Count > 0) moving.Add($"папок проекта {strays.Count}");

            ops.Add(new Op
            {
                Kind = OpKind.FoldIntoVersion,
                Source = dir,
                Target = VersionLayout.FirmwareFolder(dir),
                VersionDir = dir,
                RecordPaths = dbPaths,
                Note = moving.Count > 0
                    ? $"{first.VersionRaw}: {string.Join(", ", moving)} → «{VersionLayout.FirmwareFolderName}», папки версии"
                    : $"{first.VersionRaw}: завести недостающие папки версии",
            });
        }
    }

    // ── Этап 5: ОПЦ внутрь контроллера ──────────────────────────────────────

    /// <summary>Перенос ОПЦ-версии из общей «ОПЦ» подтипа в «ОПЦ» её контроллера с переименованием
    /// папки в номер заявки/SN. Три обязательных предосторожности, без которых это выпускать нельзя:
    /// <list type="number">
    /// <item><description><b>CHANGELOG.md дописывается ДО переноса</b> (см. Apply): после
    /// переименования имя папки номером версии уже не является, и восстановить его будет неоткуда —
    /// такая папка станет для досмотра диска безымянной.</description></item>
    /// <item><description><b>disk_path правится только после удачного переноса</b> — через колбэк
    /// вызывающего (см. Apply), в той же связке «диск → БД», что и переименование файла.</description></item>
    /// <item><description><b>Занятая цель не перезаписывается</b>: две ОПЦ-версии одного шкафа (одна
    /// заявка, разные сборки) дали бы одно имя папки — вторая уходит в Skipped с внятной причиной, а
    /// не затирает первую.</description></item>
    /// </list></summary>
    private static void PlanOpcMoves(MigrationInput input, List<Op> ops, List<string> skipped)
    {
        var claimed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var v in input.Versions)
        {
            if (!v.IsOpc || string.IsNullOrWhiteSpace(v.DiskPath)) continue;

            var dir = FirmwareDiskPresence.ResolveVersionDir(v.DiskPath, v.VersionRaw);
            if (string.IsNullOrEmpty(dir) || !SafeDirExists(dir)) continue;

            var ctrlFolder = Path.Combine(
                HierarchyService.GroupSubFolder(input.Root, v.GroupName, v.SubtypeName), v.CtrlName);
            if (!SafeDirExists(ctrlFolder))
            {
                skipped.Add($"{v.VersionRaw}: папки контроллера {v.CtrlName} на диске нет — перенос ОПЦ пропущен");
                continue;
            }

            var target = Path.Combine(OpcLayout.ControllerOpcFolder(ctrlFolder),
                OpcLayout.FolderName(v.RequestNum, v.CabinetSn, v.VersionRaw));

            // Уже переехала (повторный прогон, либо перенёс коллега) — молча пропускаем.
            if (string.Equals(Path.GetFullPath(dir).TrimEnd(Path.DirectorySeparatorChar),
                    Path.GetFullPath(target).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
                continue;

            if (SafeDirExists(target) || !claimed.Add(target))
            {
                skipped.Add($"{v.VersionRaw}: «{Path.GetFileName(target)}» в ОПЦ контроллера уже занято " +
                            "(две сборки под один шкаф) — перенесите вручную");
                continue;
            }

            ops.Add(new Op
            {
                Kind = OpKind.MoveOpc,
                Source = dir,
                Target = target,
                VersionDir = dir,
                FwVersionId = v.Id ?? 0,
                RecordPaths = new List<string> { v.DiskPath },
                Note = $"{v.VersionRaw} → {v.CtrlName}\\ОПЦ\\{Path.GetFileName(target)}",
            });
        }
    }

    /// <summary>Папки версий, по которым идут пооперационные проходы: по ПАПКЕ, а не по записи (одну
    /// папку могут делить несколько строк — конфигурации шкафа), вместе со всеми disk_path, которыми
    /// она записана в базе.
    ///
    /// internal, а не private: ровно этим же набором папок обходит диск чистильщик мусора
    /// (<see cref="DiskCleanupScanner"/>), и второй реализации «где на диске папки версий» быть не
    /// должно — она разошлась бы с этой на первом же исключении (переименованная папка, найденная
    /// соседом, несколько записей на одну папку).</summary>
    internal static List<(string Dir, FwVersionRecord First, List<string> Paths)> VersionDirs(MigrationInput input)
    {
        var byDir = new Dictionary<string, (FwVersionRecord First, List<string> Paths)>(StringComparer.OrdinalIgnoreCase);
        var order = new List<string>();

        foreach (var v in input.Versions)
        {
            var dir = FirmwareDiskPresence.ResolveVersionDir(v.DiskPath, v.VersionRaw);
            if (string.IsNullOrEmpty(dir) || !SafeDirExists(dir)) continue;

            if (!byDir.TryGetValue(dir, out var entry))
            {
                entry = (v, new List<string>());
                byDir[dir] = entry;
                order.Add(dir);
            }
            if (!string.IsNullOrEmpty(v.DiskPath) && !entry.Paths.Contains(v.DiskPath, StringComparer.OrdinalIgnoreCase))
                entry.Paths.Add(v.DiskPath);
        }

        return order.Select(dir =>
        {
            var (first, paths) = byDir[dir];
            return (dir, first, paths.Count > 0 ? paths : new List<string> { dir });
        }).ToList();
    }

    private static void PlanRenames(MigrationInput input, List<Op> ops, List<string> skipped)
    {
        // Одна папка версии может быть записана у нескольких строк (конфигурации шкафа делят файлы) —
        // планируем по папке, а не по записи, иначе на один файл придётся несколько переименований
        // (см. VersionDirs; там же собираются все disk_path этой папки — Op.RecordPaths).
        foreach (var (dir, v, dbPaths) in VersionDirs(input))
        {
            var files = FirmwareFilesIn(dir);
            if (files.Count == 0) continue;

            var number0 = FwVersionNumber.Parse(v.VersionRaw);
            if (files.Count > 1)
            {
                // Многофайловая папка (проект с ресурсами, ПЛК + панель рядом): какое из имён
                // «главное», программа не знает — это знает executable_hint, а он у коллег не
                // обновится. Раньше такая папка молча уходила в Skipped и переименовать её было
                // нечем вовсе. Теперь строка остаётся в плане и ждёт, пока файл укажет человек
                // (Op.NeedsChoice); не указал — операция просто не выполняется.
                if (number0 is null)
                {
                    skipped.Add($"{v.VersionRaw}: номер версии не разбирается — каноническое имя не построить");
                    continue;
                }
                ops.Add(new Op
                {
                    Kind = OpKind.RenameFirmware,
                    Source = "",
                    Target = "",
                    VersionDir = dir,
                    VersionRaw = v.VersionRaw,
                    RequestNum = v.RequestNum,
                    CabinetSn = v.CabinetSn,
                    RecordPaths = dbPaths.Count > 0 ? dbPaths : new List<string> { dir },
                    Candidates = files.Select(f => Path.GetRelativePath(dir, f)).ToList(),
                    Selected = false,
                    Note = $"{v.VersionRaw}: в папке файлов {files.Count} — укажите, какой из них прошивка",
                });
                continue;
            }

            var number = number0;
            if (number is null)
            {
                skipped.Add($"{v.VersionRaw}: номер версии не разбирается — каноническое имя не построить");
                continue;
            }

            var current = files[0];
            var ext = Path.GetExtension(current);
            var canonical = FirmwareNaming.BuildFirmwareFilename(number, ext, v.RequestNum, v.CabinetSn);
            var currentName = Path.GetFileName(current);
            if (string.Equals(currentName, canonical, StringComparison.Ordinal)) continue;

            ops.Add(new Op
            {
                Kind = OpKind.RenameFirmware,
                Source = current,
                Target = Path.Combine(dir, canonical),
                VersionDir = dir,
                RecordPaths = dbPaths.Count > 0 ? dbPaths : new List<string> { dir },
                Note = $"{currentName} → {canonical}",
            });
        }
    }

    // ── Выполнение ──────────────────────────────────────────────────────────

    /// <param name="renamed">Зовётся после КАЖДОГО удавшегося переименования — вызывающий правит в БД
    /// filename/executable_hint у всех записей этой папки (Op.RecordPaths, Op.OldName, Op.NewName).
    /// Отдельным колбэком, потому что Core-слой миграции про базу ничего не знает и работать должен
    /// и в тестах без неё.</param>
    /// <param name="progress">Сколько операций выполнено — для индикатора; зовётся из рабочего потока.</param>
    /// <param name="repointed">Зовётся после КАЖДОГО удавшегося переноса ОПЦ — вызывающий переписывает
    /// disk_path записи (Op.FwVersionId, Op.Target). Отдельно от <paramref name="renamed"/>, потому
    /// что правится другой столбец и другим методом БД.</param>
    /// <param name="stubs">Чем рисовать заглушку «Инструкция в разработке». null — операции
    /// заглушек просто пропускаются.</param>
    /// <param name="publisher">Кто выкладывает инструкции на хостинг. Задан вместе с
    /// <paramref name="firstRoot"/> — после перестройки инструкции и заглушки, легшие на первый диск,
    /// уходят на хостинг под теми же ключами, что и при обычной загрузке (иначе после перестройки QR
    /// вёл бы на старые адреса, а заглушек на хостинге не было бы вовсе). null — хостинг не настроен,
    /// выкладка просто не делается.</param>
    /// <param name="firstRoot">Корень первого диска — от него считается ключ объекта на хостинге
    /// (см. <see cref="InstructionPublisher"/>). Нужен только вместе с <paramref name="publisher"/>.</param>
    /// <param name="cancellationToken">Остановка ПО ГРАНИЦЕ ОПЕРАЦИИ. Каждая строка плана —
    /// самостоятельное переименование или перенос: между ними диск в согласованном состоянии, и
    /// прекратить работу здесь безопасно. Внутрь операции отмена не заглядывает НАМЕРЕННО — оборвать
    /// перенос папки на середине значит потерять её половину. Недоделанные строки помечаются
    /// «cancel» и попадают в журнал: повторный прогон обязан предложить их снова.</param>
    public static MigrationPlan Apply(MigrationPlan plan, Action<Op>? renamed,
        Action<int, int>? progress = null, Action<Op>? repointed = null,
        IInstructionStubWriter? stubs = null, IInstructionPublisher? publisher = null,
        string? firstRoot = null, CancellationToken cancellationToken = default)
    {
        var total = plan.Ops.Count;
        var done = 0;
        var cancelled = false;

        foreach (var op in plan.Ops)
        {
            if (cancelled || cancellationToken.IsCancellationRequested)
            {
                cancelled = true;
                op.Status = op.Selected ? "cancel" : "off";
                progress?.Invoke(++done, total);
                continue;
            }

            // Снятая галочка — не «пропущено», а «не запускали»: в журнале это разные вещи, и
            // повторный прогон обязан показать такую строку снова, а не считать её сделанной.
            if (!op.Selected) { op.Status = "off"; progress?.Invoke(++done, total); continue; }

            try
            {
                switch (op.Kind)
                {
                    case OpKind.RenameFirmware:
                        op.Status = op.NeedsChoice ? "skip" : RenameFile(op.Source, op.Target) ? "ok" : "skip";
                        if (op.Status == "ok") renamed?.Invoke(op);
                        break;

                    case OpKind.FoldIntoVersion:
                        op.Status = FoldIntoVersion(op.Source) ? "ok" : "skip";
                        break;

                    case OpKind.MoveOpc:
                        op.Status = MoveOpcFolder(op.Source, op.Target) ? "ok" : "skip";
                        if (op.Status == "ok") repointed?.Invoke(op);
                        break;

                    case OpKind.RenameInstruction:
                        op.Status = RenameInstructionsIn(op) ? "ok" : "skip";
                        break;

                    case OpKind.PlaceInstructionStub:
                        op.Status = InstructionStub.EnsureIn(op.Source, op.VersionRaw, stubs,
                            warnings: null) ? "ok" : "skip";
                        break;
                }
            }
            catch (Exception ex)
            {
                op.Status = "error";
                op.Error = ex.Message;
            }
            progress?.Invoke(++done, total);
        }

        // Выкладка на хостинг — ПОСЛЕ всех операций на диске: к этому моменту инструкции уже
        // переехали/переименовались, и в папках лежит их конечное состояние. Best-effort, как и вся
        // выкладка (см. InstructionPublisher): неудача уходит в журнал операции, а не валит перестройку.
        // Остановленный прогон на хостинг ничего не отправляет: половина папок ещё в старом виде,
        // и выложить их под новыми ключами значит развести диск и хостинг окончательно.
        if (!cancelled && publisher is not null && !string.IsNullOrEmpty(firstRoot))
            PublishRebuiltInstructions(plan, publisher, firstRoot!);

        return plan;
    }

    /// <summary>Выкладывает на хостинг инструкции и заглушки, легшие при перестройке на ПЕРВЫЙ диск.
    /// Работает по ПАПКАМ, а не по отдельным файлам: так и ключ считается ровно от раскладки на диске
    /// (см. <see cref="S3Settings.KeyFor"/>), и переименованные в этом же прогоне файлы уходят под
    /// своими конечными именами, и каждая папка выкладывается один раз, чем бы её ни затронули
    /// (приведение имён или заглушка).</summary>
    private static void PublishRebuiltInstructions(MigrationPlan plan, IInstructionPublisher publisher, string firstRoot)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var op in plan.Ops)
        {
            if (op.Status != "ok") continue;

            var folder = op.Kind switch
            {
                OpKind.RenameInstruction => op.Source,
                OpKind.PlaceInstructionStub => op.Source,
                _ => null,
            };
            if (string.IsNullOrEmpty(folder) || !IsUnderRoot(folder!, firstRoot)) continue;

            var norm = SafeFullPath(folder!)?.TrimEnd(Path.DirectorySeparatorChar) ?? folder!;
            if (!seen.Add(norm)) continue;

            var warnings = new List<string>();
            var url = publisher.Publish(folder!, folder!, firstRoot, warnings);
            if (warnings.Count > 0) op.Note += " · хостинг: " + string.Join("; ", warnings);
            else if (url is not null) op.Note += " · выложено на хостинг";
        }
    }

    /// <summary>Путь лежит внутри корня первого диска. Сравнение по нормализованным полным путям —
    /// без него зеркало на третьем диске приняли бы за папку первого.</summary>
    private static bool IsUnderRoot(string path, string root)
    {
        var full = SafeFullPath(path);
        var rootFull = SafeFullPath(root)?.TrimEnd(Path.DirectorySeparatorChar);
        if (full is null || string.IsNullOrEmpty(rootFull)) return false;
        return full.StartsWith(rootFull + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
               || string.Equals(full.TrimEnd(Path.DirectorySeparatorChar), rootFull, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Переименование внутри одной папки. Отдельно разобран случай «различие только в
    /// регистре» (старые имена писались заглавными: «….PSL»): Windows считает такие имена одним и тем
    /// же файлом, обычный File.Move на них падает «файл уже существует» — переименовываем через
    /// временное имя. Цель уже существует ОТДЕЛЬНЫМ файлом — не трогаем ничего (skip), затирать чужой
    /// файл миграция не должна.</summary>
    private static bool RenameFile(string source, string target)
    {
        if (!File.Exists(source)) return false;
        if (string.Equals(source, target, StringComparison.Ordinal)) return false;

        if (string.Equals(source, target, StringComparison.OrdinalIgnoreCase))
        {
            var tmp = Path.Combine(Path.GetDirectoryName(target)!, Path.GetFileName(target) + ".antarus-rename");
            File.Move(source, tmp);
            try
            {
                File.Move(tmp, target);
            }
            catch (Exception)
            {
                // Второй шаг не удался (файл успели открыть) — возвращаем исходное имя и падаем
                // наружу: оставить прошивку лежать под «….antarus-rename» нельзя, её перестанут
                // находить и открывать.
                try { File.Move(tmp, source); } catch (Exception) { /* и это не вышло — путь в журнале */ }
                throw;
            }
            return true;
        }

        if (File.Exists(target)) return false;
        File.Move(source, target);
        return true;
    }

    /// <summary>Этап 4 на диске: создать «Прошивка\» и перенести туда файлы прошивки верхнего уровня.
    /// Идемпотентно и продолжаемо — прерванный обрывом шары прогон дочищается следующим: уже
    /// перенесённые файлы просто не найдутся наверху, а «Прошивка\» создаётся один раз.
    ///
    /// Порядок операций строго такой: сначала ФАЙЛЫ, и только потом папка считается созданной для
    /// внешнего мира. На практике это значит «переносим по одному»: файл, который не удалось
    /// перенести (открыт в SMLogix), остаётся наверху и находится по-прежнему — VersionLayout ищет
    /// в обеих папках именно ради этого случая.</summary>
    private static bool FoldIntoVersion(string versionDir)
    {
        var files = Directory.EnumerateFiles(versionDir, "*", SearchOption.TopDirectoryOnly)
            .Where(f => !VersionLayout.IsServiceFile(f))
            .ToList();

        // Подпапки проекта считаем ДО EnsureFolders: после него в корне появятся пять собственных
        // папок версии, но StrayProjectFolders их и так не берёт — порядок здесь про наглядность.
        var strays = VersionLayout.StrayProjectFolders(versionDir);

        // Пять папок версии — и когда есть что переносить, и когда файлы уже наверху не лежат:
        // операция могла быть запланирована ровно ради недостающих папок (см. PlanFoldIntoVersion).
        var createdFolders = VersionLayout.EnsureFolders(versionDir);
        if (files.Count == 0 && strays.Count == 0) return createdFolders > 0;

        var target = VersionLayout.FirmwareFolder(versionDir);

        var moved = 0;
        foreach (var file in files)
        {
            var dst = Path.Combine(target, Path.GetFileName(file));
            // Одноимённый файл уже в «Прошивка\» (повторный прогон после частичного переноса, либо
            // коллега положил туда свежую копию) — свой НЕ затираем: удалить чужой файл эта операция
            // не вправе, он останется наверху и попадёт в следующий прогон.
            if (File.Exists(dst)) continue;
            File.Move(file, dst);
            moved++;
        }

        // Папка проекта переезжает целиком, со всей вложенностью: «plc» проекта KINCO — это не
        // «какие-то файлы рядом», а часть прошивки, и разбирать её на файлы нельзя.
        foreach (var stray in strays)
        {
            var dst = Path.Combine(target, Path.GetFileName(stray));
            // Занятая цель — тот же принцип, что и у файлов: чужое не трогаем, папка остаётся
            // наверху и попадёт в следующий прогон (или в чистильщик).
            if (Directory.Exists(dst) || File.Exists(dst)) continue;
            Directory.Move(stray, dst);
            moved++;
        }
        return moved > 0 || createdFolders > 0;
    }

    /// <summary>Привести имена файлов в одной папке «Инструкция» к каноническим. Файлы перечисляются
    /// ЗДЕСЬ, а не при планировании: до этой операции в том же прогоне могли отработать перенос
    /// инструкций на третий диск и сборка файлов внутрь версии, и посчитанный заранее список путей
    /// оказался бы устаревшим. Идемпотентно — уже канонические имена не трогаются, повторный прогон
    /// не делает ничего. Op.Note дополняется тем, что реально переименовали: журнал операции должен
    /// отвечать «что именно поменяли», а не только «в какой папке».</summary>
    private static bool RenameInstructionsIn(Op op)
    {
        if (!SafeDirExists(op.Source)) return false;

        // Настоящий документ в папке появился — заглушка уходит первой: имя у неё то же самое
        // каноническое, и пока она лежит, документу под него не встать (см. InstructionStub).
        var removedStub = InstructionStub.HasRealInstruction(op.Source) && InstructionStub.RemoveFrom(op.Source) > 0;

        var renamed = new List<string>();
        foreach (var file in TopLevelFiles(op.Source))
        {
            if (InstructionNaming.CanonicalNameFor(file, op.VersionRaw) is null) continue;
            var after = InstructionNaming.EnsureCanonicalName(file, op.VersionRaw);
            if (string.Equals(after, file, StringComparison.Ordinal)) continue;

            renamed.Add($"{Path.GetFileName(file)} → {Path.GetFileName(after)}");
        }

        if (removedStub) renamed.Add("убрана заглушка «в разработке» — документ приложили");
        if (renamed.Count == 0) return false;
        op.Note = string.Join(", ", renamed);
        return true;
    }

    /// <summary>Этап 5 на диске: перенести папку ОПЦ-версии в «ОПЦ» её контроллера под именем
    /// заявки/SN. CHANGELOG.md с номером версии дописывается ДО переноса и только если его там нет:
    /// после переименования имя папки номером версии уже не является, и без журнала такая папка стала
    /// бы для досмотра диска безымянной (см. OpcLayout.ResolveVersion).</summary>
    private static bool MoveOpcFolder(string source, string target)
    {
        if (!Directory.Exists(source)) return false;
        if (Directory.Exists(target)) return false; // цель занята — план уже сказал об этом человеку

        EnsureChangelogVersionHeader(source);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        Directory.Move(source, target);
        return true;
    }

    /// <summary>Дописывает CHANGELOG.md с одним лишь заголовком «# {номер версии}», если файла нет.
    /// Существующий не трогает вовсе: там уже есть и номер, и описание, и типы пуска, и переписывать
    /// их этой операцией нельзя.</summary>
    private static void EnsureChangelogVersionHeader(string versionDir)
    {
        var path = Path.Combine(versionDir, ChangelogFile.FileName);
        if (File.Exists(path)) return;

        var raw = Path.GetFileName(versionDir.TrimEnd(Path.DirectorySeparatorChar));
        if (FwVersionNumber.Parse(raw) is null) return; // имя папки уже не номер — придумывать нечего
        File.WriteAllText(path, $"# {raw}\n", new System.Text.UTF8Encoding(false));
    }

    // ── Обход диска ─────────────────────────────────────────────────────────

    /// <summary>Файлы прошивки этой версии: верхний уровень «Прошивка\», если версия уже перестроена
    /// (этап 4), иначе верхний уровень самой папки версии. Именно первая непустая из двух, а не обе
    /// вместе: иначе после перестройки один и тот же файл считался бы дважды и «в папке 2 файла»
    /// отменяло бы переименование там, где файл на самом деле один.</summary>
    private static List<string> FirmwareFilesIn(string dir)
    {
        foreach (var folder in VersionLayout.FirmwareFolders(dir))
        {
            try
            {
                var files = Directory.EnumerateFiles(folder, "*", SearchOption.TopDirectoryOnly)
                    .Where(f => !NonFirmwareNames.Contains(Path.GetFileName(f), StringComparer.OrdinalIgnoreCase))
                    .Where(f => !DocFileResolver.IsShortcut(f))
                    // Служебный мусор проводника файлом прошивки не бывает, а раньше делал папку
                    // «многофайловой» — и переименование в ней пропускалось целиком (см. JunkFiles).
                    .Where(f => !JunkFiles.IsJunk(f))
                    .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                    .ToList();
                if (files.Count > 0) return files;
            }
            catch (Exception)
            {
                // Недоступная папка — «файлов не нашли», следующая пара глаз (повторный прогон) доделает.
            }
        }
        return new List<string>();
    }

    private static List<string> TopLevelFiles(string dir)
    {
        try
        {
            return Directory.EnumerateFiles(dir, "*", SearchOption.TopDirectoryOnly).ToList();
        }
        catch (Exception)
        {
            return new List<string>();
        }
    }

    private static bool SafeDirExists(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        try { return Directory.Exists(path); }
        catch (Exception) { return false; }
    }

    private static string? SafeFullPath(string path)
    {
        try { return Path.GetFullPath(path); }
        catch (Exception) { return null; }
    }
}
