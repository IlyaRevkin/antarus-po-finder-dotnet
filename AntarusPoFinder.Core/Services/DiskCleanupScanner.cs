using System.Collections.Generic;
using System.IO;
using System.Linq;
using AntarusPoFinder.Core.Domain;

namespace AntarusPoFinder.Core.Services;

/// <summary>Поиск мусора и неправильно лежащих файлов на диске прошивок. Дословная просьба Ильи:
/// «надо сделать чистильщика который будет искать мусорные файлы не подходящие под структуру и
/// предлагать переименовать как они должны быть или удалить»; его же пример — файл
/// <c>пж_smh5_4.36.psl</c> в папке <c>…\SMH5\1.0.0005.0001\Прошивка</c> должен предлагаться к
/// переименованию в <c>1.0.0005.0001.psl</c>, а лежащий рядом файл инструкции — убираться оттуда.
///
/// <b>Чем это отличается от <see cref="DiskLayoutMigrator"/>.</b> Мигратор — разовая операция «привести
/// весь диск к правилам», она работает ПАПКАМИ и включается галочками. Здесь наоборот: пофайловый
/// список находок, каждая со своим объяснением и своим действием, которое человек подтверждает или
/// меняет по строке. Общее у них — архитектура (<see cref="Plan"/> ничего не трогает,
/// <see cref="Apply"/> идемпотентен и продолжаем) и переименование файла прошивки: каноническое имя
/// строит <see cref="FirmwareNaming.BuildFirmwareFilename"/>, а само переименование делает
/// <see cref="DiskLayoutMigrator.Apply"/> — второй реализации ни того, ни другого в проекте быть не
/// должно (см. <see cref="PlanRename"/>).
///
/// <b>Главное свойство — не съесть нужное.</b> Илья прямо просил продумать, «чтобы svg или какие-то
/// файлы для работы плк которые шьются не бинарником как кинко или овен в мусор не улетели». Отсюда
/// четыре правила, и они важнее полноты находок:
/// <list type="number">
/// <item><description>расширение из любого белого списка БД (ПЛК, HMI, схемы) — НИКОГДА не мусор;</description></item>
/// <item><description>файл, на который ссылается запись в базе, — не мусор и не переименовывается
/// «вслепую»: переименование такого файла обязано идти вместе с правкой filename/executable_hint
/// (см. <see cref="Finding.RecordPaths"/> и колбэк <c>renamed</c> у <see cref="Apply"/>);</description></item>
/// <item><description>всё внутри папки проекта (подпапки в «Прошивка\») не разбирается по файлам
/// вовсе — проект это единое целое, и лежащий в нём .svg или ресурс KINCO нас не касается;</description></item>
/// <item><description>«удалить» предлагается ТОЛЬКО по закрытому списку служебного мусора
/// (<see cref="JunkNames"/>/<see cref="JunkExtensions"/>). Всё незнакомое уходит в
/// <see cref="Issue.NeedsDecision"/> без предложенного действия: ошибка в сторону «оставить» здесь
/// всегда дешевле.</description></item>
/// </list>
///
/// По умолчанию отмечены галочкой только безвредные операции — переименование и перенос; «удалить»
/// человек отмечает сам (<see cref="Finding.Selected"/>).</summary>
public static class DiskCleanupScanner
{
    /// <summary>Что не так с файлом.</summary>
    public enum Issue
    {
        /// <summary>Файл прошивки назван не по правилу «имя файла = имя папки версии».</summary>
        FirmwareName,

        /// <summary>Файл (или папка проекта) лежит не там, где положено раскладке версии.</summary>
        WrongFolder,

        /// <summary>Служебный мусор по закрытому списку — к структуре диска отношения не имеет.</summary>
        Junk,

        /// <summary>Непонятный файл: ни в белых списках, ни в базе. Действие не предлагается —
        /// решает человек.</summary>
        NeedsDecision,
    }

    public enum Act { None, Rename, Move, Delete }

    /// <summary>Служебные файлы Windows и офиса — закрытый список, только точные имена.</summary>
    private static readonly string[] JunkNames =
    {
        "Thumbs.db", "ehthumbs.db", "desktop.ini", ".DS_Store",
    };

    /// <summary>Расширения незавершённых/временных файлов. Тоже закрытый список: «.old», «.copy» и
    /// прочее сюда сознательно НЕ попало — под таким именем на этом диске вполне может лежать
    /// нужная предыдущая сборка.</summary>
    private static readonly string[] JunkExtensions =
    {
        ".tmp", ".temp", ".part", ".partial", ".crdownload", ".bak",
    };

    public sealed class Finding
    {
        public Issue Issue { get; init; }

        /// <summary>Полный путь находки. Для <see cref="IsFolder"/> — папка проекта целиком.</summary>
        public string Path { get; init; } = "";

        /// <summary>Куда переименовать/перенести. Пусто у удаления и у «нужно решить».</summary>
        public string Target { get; init; } = "";

        public bool IsFolder { get; init; }

        /// <summary>Папка версии, к которой относится находка — нужна интерфейсу для группировки и
        /// журналу, чтобы по строке было понятно, о какой версии речь.</summary>
        public string VersionDir { get; init; } = "";
        public string VersionRaw { get; init; } = "";

        /// <summary>По-русски: ПОЧЕМУ предложено именно это. Показывается в колонке «что не так» —
        /// человек подтверждает операцию, а не гадает, что программа задумала.</summary>
        public string Reason { get; init; } = "";

        /// <summary>Что предлагается сделать. Изменяемое: человек вправе поменять действие по
        /// строке (например, решить, что незнакомый файл всё-таки мусор).</summary>
        public Act Action { get; set; }

        /// <summary>Отмечена ли строка к выполнению. По умолчанию true только у переименования и
        /// переноса — удаление отмечает человек.</summary>
        public bool Selected { get; set; }

        /// <summary>Какие действия вообще допустимы для этой находки — из чего интерфейс собирает
        /// выпадающий список. Перенос «куда-нибудь ещё» здесь не предлагается: цель вычислена
        /// правилами раскладки, и выбирать её вручную было бы уже не чисткой, а файловым менеджером.</summary>
        public IReadOnlyList<Act> AllowedActions => Issue switch
        {
            Issue.FirmwareName => new[] { Act.Rename, Act.None },
            Issue.WrongFolder => new[] { Act.Move, Act.None },
            Issue.Junk => new[] { Act.Delete, Act.None },
            _ => new[] { Act.None, Act.Delete },
        };

        /// <summary>ok / skip / error — заполняется в <see cref="Apply"/>.</summary>
        public string Status { get; set; } = "";
        public string Error { get; set; } = "";

        /// <summary>Операция мигратора, стоящая за переименованием прошивки. Само переименование
        /// выполняет он же — здесь мы только показываем и подтверждаем (см. док класса).</summary>
        internal DiskLayoutMigrator.Op? Rename { get; init; }

        /// <summary>Значения <c>disk_path</c>, которыми папка версии записана в базе: по ним
        /// вызывающий правит filename/executable_hint после удавшегося переименования. Пусто —
        /// правка базы этой находке не нужна.</summary>
        public IReadOnlyList<string> RecordPaths => Rename?.RecordPaths ?? Array.Empty<string>();

        public string OldName => System.IO.Path.GetFileName(Path);
        public string NewName => Target.Length > 0 ? System.IO.Path.GetFileName(Target) : "";

        public string IssueLabel => Issue switch
        {
            Issue.FirmwareName => "Имя не по правилу",
            Issue.WrongFolder => "Не в своей папке",
            Issue.Junk => "Служебный мусор",
            _ => "Нужно решить",
        };

        public static string ActionLabel(Act action) => action switch
        {
            Act.Rename => "Переименовать",
            Act.Move => "Перенести",
            Act.Delete => "Удалить",
            _ => "Ничего не делать",
        };
    }

    /// <param name="Versions">Те же записи, что показывает вкладка «Прошивки» (вместе с архивными) —
    /// именно они задают, где папки версий и какое имя у файла должно быть каноническим. Обход идёт
    /// по ним, а не по всему дереву диска: папка, о которой база не знает, — это находка для досмотра
    /// диска (HierarchyService), а не для чистильщика.</param>
    /// <param name="PlcExtensions">allowed_extensions — расширения проектов/прошивок ПЛК.</param>
    /// <param name="HmiExtensions">allowed_extensions_hmi.</param>
    /// <param name="SchematicExtensions">allowed_extensions_schematic.</param>
    /// <param name="ReferencedPaths">Пути, на которые ссылается база помимо самих версий (файлы
    /// параметров ПЧ/УПП и т.п.). Папка в этом списке защищает всё своё содержимое.</param>
    public sealed record CleanupInput(
        string Root,
        IReadOnlyList<FwVersionRecord> Versions,
        IReadOnlyCollection<string> PlcExtensions,
        IReadOnlyCollection<string> HmiExtensions,
        IReadOnlyCollection<string> SchematicExtensions,
        IReadOnlyCollection<string> ReferencedPaths);

    /// <param name="Skipped">Почему часть диска осталась неразобранной — то же назначение, что и у
    /// <see cref="DiskLayoutMigrator.MigrationPlan.Skipped"/>: человек должен видеть не только
    /// находки, но и места, куда чистильщик сознательно не полез.</param>
    public sealed record CleanupPlan(IReadOnlyList<Finding> Findings, IReadOnlyList<string> Skipped)
    {
        public int Count => Findings.Count;
    }

    // ── Планирование (сухой прогон) ──────────────────────────────────────────

    /// <summary>Обход диска без единой правки. Ходит на сетевую шару — звать только из фонового
    /// потока.</summary>
    public static CleanupPlan Plan(CleanupInput input)
    {
        var findings = new List<Finding>();
        var skipped = new List<string>();
        var ctx = new Scope(input);

        // Папки версий берём тем же обходом, что и перестройка диска (DiskLayoutMigrator.VersionDirs):
        // одна папка на несколько записей, переименованная папка находится соседом.
        foreach (var (dir, record, dbPaths) in DiskLayoutMigrator.VersionDirs(
                     new DiskLayoutMigrator.MigrationInput(input.Root, input.Versions,
                         new DiskLayoutMigrator.MigrationOptions(false))))
        {
            ScanVersion(dir, record, dbPaths, ctx, findings, skipped);
        }

        return new CleanupPlan(findings, skipped);
    }

    /// <summary>Переименование файла прошивки в каноническое имя. Само правило не переписывается —
    /// имя строит <see cref="FirmwareNaming.BuildFirmwareFilename"/>, а выполняет операцию
    /// <see cref="DiskLayoutMigrator.Apply"/> (там разобран случай «различие только в регистре»,
    /// который на Windows обычным File.Move не делается), поэтому здесь и рождается его
    /// <see cref="DiskLayoutMigrator.Op"/>.
    ///
    /// <b>Чем предохранитель отличается от мигратора.</b> Мигратор отказывается переименовывать в
    /// любой папке, где больше одного файла: он в базу не ходит и отличить прошивку от лежащего
    /// рядом документа не может. Чистильщик белые списки расширений знает, поэтому «прошивка +
    /// забытая рядом инструкция» для него не многофайловая папка, а один файл прошивки и одна
    /// находка «не в своей папке» — ровно тот случай, с которого просьба и началась. Смысл самого
    /// предохранителя сохранён: два РАЗНЫХ файла с расширениями из белых списков (ПЛК рядом с
    /// панелью) — по-прежнему отказ, потому что какой из них «главный», знает только
    /// executable_hint, а он у коллег импортом не обновляется.</summary>
    private static DiskLayoutMigrator.Op? PlanRename(string dir, FwVersionRecord record,
        IReadOnlyList<string> dbPaths, VersionScope ctx, List<string> skipped)
    {
        var folder = VersionLayout.IsNewLayout(dir) ? VersionLayout.FirmwareFolder(dir) : dir;
        var candidates = TopLevelFiles(folder)
            .Where(f => !ctx.Untouchable(f) && !LooksLikeInstruction(f) && ctx.Whitelisted(f))
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (candidates.Count == 0) return null;
        if (candidates.Count > 1)
        {
            skipped.Add($"{record.VersionRaw}: файлов прошивки {candidates.Count} — имя не трогаем " +
                        "(в многофайловой папке оно привязано к подсказке «чем открывать»)");
            return null;
        }

        var number = FwVersionNumber.Parse(record.VersionRaw);
        if (number is null)
        {
            skipped.Add($"{record.VersionRaw}: номер версии не разбирается — каноническое имя не построить");
            return null;
        }

        var current = candidates[0];
        var canonical = FirmwareNaming.BuildFirmwareFilename(
            number, Path.GetExtension(current), record.RequestNum, record.CabinetSn);
        if (string.Equals(Path.GetFileName(current), canonical, StringComparison.Ordinal)) return null;

        return new DiskLayoutMigrator.Op
        {
            Kind = DiskLayoutMigrator.OpKind.RenameFirmware,
            Source = current,
            Target = Path.Combine(folder, canonical),
            VersionDir = dir,
            RecordPaths = dbPaths.Count > 0 ? dbPaths : new List<string> { dir },
            Note = $"{Path.GetFileName(current)} → {canonical}",
        };
    }

    private static void ScanVersion(string dir, FwVersionRecord record, IReadOnlyList<string> dbPaths,
        Scope scope, List<Finding> findings, List<string> skipped)
    {
        var ctx = scope.For(dbPaths);
        var rename = PlanRename(dir, record, dbPaths, ctx, skipped);
        var renames = new Dictionary<string, DiskLayoutMigrator.Op>(StringComparer.OrdinalIgnoreCase);
        if (rename is not null) renames[rename.Source] = rename;

        var newLayout = VersionLayout.IsNewLayout(dir);
        var firmwareFolder = VersionLayout.FirmwareFolder(dir);

        // Корень папки версии. У неперестроенной версии файлы прошивки лежат именно здесь и это
        // нормально (режим совместимости VersionLayout) — переносить их некуда, пока «Прошивка\» нет.
        foreach (var file in TopLevelFiles(dir))
        {
            if (ctx.Untouchable(file)) continue;
            var finding = ClassifyRoot(file, dir, record, ctx, renames, newLayout, firmwareFolder);
            if (finding is not null) findings.Add(finding);
        }

        // Папки в корне версии: «Прошивка» и четыре папки документов — свои, всё остальное похоже на
        // проект ПЛК, оставленный в корне (тот самый «plc» рядом с файлом прошивки).
        if (newLayout)
            foreach (var sub in TopLevelDirs(dir))
            {
                var name = Path.GetFileName(sub);
                if (IsVersionOwnFolder(name)) continue;
                if (ctx.Referenced(sub)) continue;
                findings.Add(new Finding
                {
                    Issue = Issue.WrongFolder,
                    Path = sub,
                    Target = Path.Combine(firmwareFolder, name),
                    IsFolder = true,
                    VersionDir = dir,
                    VersionRaw = record.VersionRaw,
                    Action = Act.Move,
                    Selected = true,
                    Reason = $"Папка «{name}» в корне версии похожа на проект ПЛК. Все файлы прошивки " +
                             $"версии живут в «{VersionLayout.FirmwareFolderName}» — там их ищет программа.",
                });
            }

        // «Прошивка\»: файлы верхнего уровня. Внутрь подпапок не заходим вовсе — проект это единое
        // целое, и разбирать его по файлам чистильщик не вправе.
        if (newLayout)
            foreach (var file in TopLevelFiles(firmwareFolder))
            {
                if (ctx.Untouchable(file)) continue;
                var finding = ClassifyFirmwareFolder(file, dir, record, ctx, renames);
                if (finding is not null) findings.Add(finding);
            }

        // Папки документов: только явный служебный мусор. Что за документ лежит в «Карта ВВ» —
        // не наше дело: там законно оказывается что угодно, от .xlsx до скана, и объявлять всё это
        // «нужно решить» значило бы завалить список строками, на которые нечего ответить.
        foreach (var slot in VersionLayout.SlotFolderNames)
            foreach (var file in TopLevelFiles(VersionLayout.SlotFolder(dir, slot)))
            {
                if (ctx.Untouchable(file) || ctx.Referenced(file) || ctx.Whitelisted(file)) continue;
                if (JunkReason(file) is not { } reason) continue;
                findings.Add(Junk(file, dir, record, reason));
            }
    }

    private static Finding? ClassifyRoot(string file, string dir, FwVersionRecord record, VersionScope ctx,
        Dictionary<string, DiskLayoutMigrator.Op> renames, bool newLayout, string firmwareFolder)
    {
        if (renames.TryGetValue(file, out var op)) return Rename(op, dir, record);

        // Файл прошивки остался в корне уже перестроенной версии: программа его найдёт (VersionLayout
        // смотрит в обе папки), но лежать ему полагается в «Прошивка».
        if (newLayout && (ctx.Whitelisted(file) || ctx.Referenced(file)))
            return new Finding
            {
                Issue = Issue.WrongFolder,
                Path = file,
                Target = Path.Combine(firmwareFolder, Path.GetFileName(file)),
                VersionDir = dir,
                VersionRaw = record.VersionRaw,
                Action = Act.Move,
                Selected = true,
                Reason = $"Файл прошивки лежит в корне папки версии. После перестройки диска все они " +
                         $"собираются в «{VersionLayout.FirmwareFolderName}».",
            };

        if (ctx.Whitelisted(file) || ctx.Referenced(file)) return null;
        if (JunkReason(file) is { } reason) return Junk(file, dir, record, reason);
        return NeedsDecision(file, dir, record);
    }

    private static Finding? ClassifyFirmwareFolder(string file, string dir, FwVersionRecord record,
        VersionScope ctx, Dictionary<string, DiskLayoutMigrator.Op> renames)
    {
        if (renames.TryGetValue(file, out var op)) return Rename(op, dir, record);

        // Инструкция среди файлов прошивки — ровно второй случай из жалобы. Не удаляем: документ
        // нужный, он просто не на своём месте, и место это в раскладке версии уже есть.
        if (LooksLikeInstruction(file))
        {
            var folder = VersionLayout.SlotFolder(dir, HierarchyFolders.Instructions);
            var canonical = InstructionNaming.BuildFileName(record.VersionRaw, Path.GetExtension(file));
            return new Finding
            {
                Issue = Issue.WrongFolder,
                Path = file,
                Target = Path.Combine(folder, canonical.Length > 0 ? canonical : Path.GetFileName(file)),
                VersionDir = dir,
                VersionRaw = record.VersionRaw,
                Action = Act.Move,
                Selected = true,
                Reason = $"Документ инструкции лежит среди файлов прошивки. Его место — папка " +
                         $"«{HierarchyFolders.Instructions}» версии, откуда его берут карточка выдачи, печать и QR.",
            };
        }

        if (ctx.Whitelisted(file) || ctx.Referenced(file)) return null;
        if (JunkReason(file) is { } reason) return Junk(file, dir, record, reason);
        return NeedsDecision(file, dir, record);
    }

    private static Finding Rename(DiskLayoutMigrator.Op op, string dir, FwVersionRecord record) => new()
    {
        Issue = Issue.FirmwareName,
        Path = op.Source,
        Target = op.Target,
        VersionDir = dir,
        VersionRaw = record.VersionRaw,
        Action = Act.Rename,
        Selected = true,
        Rename = op,
        Reason = $"Имя файла прошивки должно совпадать с именем папки версии: «{op.OldName}» → «{op.NewName}». " +
                 "Тогда по файлу, вырванному из контекста (переслали почтой, скинули на флешку), видно, " +
                 "какой версии он принадлежит.",
    };

    private static Finding Junk(string file, string dir, FwVersionRecord record, string reason) => new()
    {
        Issue = Issue.Junk,
        Path = file,
        VersionDir = dir,
        VersionRaw = record.VersionRaw,
        Action = Act.Delete,
        // Удаление НЕ отмечается по умолчанию — даже опознанный мусор человек подтверждает сам.
        Selected = false,
        Reason = reason,
    };

    private static Finding NeedsDecision(string file, string dir, FwVersionRecord record) => new()
    {
        Issue = Issue.NeedsDecision,
        Path = file,
        VersionDir = dir,
        VersionRaw = record.VersionRaw,
        Action = Act.None,
        Selected = false,
        Reason = $"Расширение «{Ext(file)}» не входит ни в один белый список (ПЛК, HMI, схемы), и в базе " +
                 "на этот файл никто не ссылается. Мусором он от этого не становится: так выглядит и " +
                 "файл ПЛК, который шьётся не бинарником. Решите сами — оставить или удалить.",
    };

    /// <summary>Файл опознан как служебный мусор — возвращается объяснение, иначе null. Проверка
    /// идёт ПОСЛЕ белых списков и ссылок из базы (см. вызовы), поэтому расширение, добавленное
    /// оператором в настройках, всегда перевешивает этот список.</summary>
    private static string? JunkReason(string file)
    {
        var name = Path.GetFileName(file);

        if (JunkNames.Contains(name, StringComparer.OrdinalIgnoreCase))
            return $"«{name}» — служебный файл Windows, к структуре диска отношения не имеет.";

        if (name.StartsWith("~$", StringComparison.Ordinal))
            return $"«{name}» — временный файл Word/Excel, остаётся от незакрытого документа.";

        var ext = Path.GetExtension(name);
        if (JunkExtensions.Contains(ext, StringComparer.OrdinalIgnoreCase))
            return $"«{ext}» — недокачанный или временный файл, рабочей копией не является.";

        try
        {
            if (new FileInfo(file).Length == 0)
                return $"«{name}» — пустой файл (0 байт): при копировании на шару оборвалась запись.";
        }
        catch (Exception)
        {
            // Файл не прочитался (шара отвалилась, файл занят) — мусором его не объявляем.
        }
        return null;
    }

    /// <summary>Файл, чьё имя само говорит, что это инструкция. Список признаков закрытый и узкий:
    /// «какой-то .pdf в папке прошивки» инструкцией не считается и уезжает в «нужно решить» — вдруг
    /// это схема или паспорт, приложенный к сборке намеренно.</summary>
    private static bool LooksLikeInstruction(string file)
    {
        var name = Path.GetFileNameWithoutExtension(file);
        return name.Contains("инструкц", StringComparison.OrdinalIgnoreCase)
               || name.Contains("руководств", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Папка, которая у версии своя по раскладке, — её содержимое не «мусор в корне».</summary>
    private static bool IsVersionOwnFolder(string name) =>
        string.Equals(name, VersionLayout.FirmwareFolderName, StringComparison.OrdinalIgnoreCase)
        || VersionLayout.SlotFolderNames.Contains(name, StringComparer.OrdinalIgnoreCase)
        || string.Equals(name, HierarchyFolders.Opc, StringComparison.OrdinalIgnoreCase);

    private static string Ext(string file)
    {
        var ext = Path.GetExtension(file);
        return ext.Length > 0 ? ext : "без расширения";
    }

    // ── Что трогать нельзя ───────────────────────────────────────────────────

    /// <summary>Белые списки расширений и пути, занятые базой, — посчитанные один раз на весь прогон.
    ///
    /// <b>Ни одного обращения к диску.</b> Соблазн есть: проверить <c>Directory.Exists</c> у каждого
    /// пути из базы и защищать папки целиком по префиксу. Но записей о прошивках тысячи, диск сетевой,
    /// и это была бы тысяча лишних round-trip'ов ЕЩЁ ДО того, как чистильщик начнёт работать —
    /// проверка диска и так идёт минуты. Поэтому «файл под защищённой папкой» узнаётся подъёмом по
    /// родителям (папка-вложение сама лежит в этом же множестве), а имена файлов из
    /// <c>filename/executable_hint</c> сверяются в пределах своей папки версии — см.
    /// <see cref="For"/>.</summary>
    private sealed class Scope
    {
        private readonly HashSet<string> _extensions = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Абсолютные пути, занятые базой: вложения версий (HMI-проект, карты, инструкция)
        /// и файлы параметров ПЧ/УПП. Путь может оказаться и файлом, и папкой — разбирать это по
        /// диску не нужно, см. <see cref="ReferencedPath"/>.</summary>
        private readonly HashSet<string> _paths = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Имена файлов, на которые ссылаются записи, — по значению их <c>disk_path</c>.
        /// В базе имя хранится без пути, а лежать файл может и в «Прошивка\», и в корне версии
        /// (режим совместимости), поэтому сверяем именно имя, а не собранный путь.</summary>
        private readonly Dictionary<string, HashSet<string>> _namesByDiskPath = new(StringComparer.OrdinalIgnoreCase);

        private static readonly HashSet<string> NoNames = new(StringComparer.OrdinalIgnoreCase);

        public Scope(CleanupInput input)
        {
            foreach (var ext in input.PlcExtensions.Concat(input.HmiExtensions).Concat(input.SchematicExtensions))
            {
                var e = (ext ?? "").Trim().TrimStart('.');
                if (e.Length > 0) _extensions.Add("." + e);
            }

            foreach (var v in input.Versions)
            {
                foreach (var name in new[] { v.Filename, v.ExecutableHint, v.HmiExecutableHint })
                    AddName(v.DiskPath, name);
                foreach (var path in new[] { v.HmiPath, v.InstructionsPath, v.IoMapPath, v.ModbusMapPath })
                    AddPath(path);
            }

            foreach (var path in input.ReferencedPaths) AddPath(path);
        }

        private void AddName(string? diskPath, string? name)
        {
            if (string.IsNullOrWhiteSpace(diskPath) || string.IsNullOrWhiteSpace(name)) return;
            var key = Full(diskPath);
            if (key is null) return;
            if (!_namesByDiskPath.TryGetValue(key, out var names))
                _namesByDiskPath[key] = names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            names.Add(Path.GetFileName(name!));
        }

        private void AddPath(string? path)
        {
            if (string.IsNullOrWhiteSpace(path)) return;
            if (Full(path) is { } full) _paths.Add(full);
        }

        /// <summary>Взгляд на защиту с точки зрения ОДНОЙ папки версии: к общим спискам добавляются
        /// имена файлов, записанные у всех строк базы, которые указывают на эту папку. Путей у одной
        /// папки бывает несколько — конфигурации шкафа делят файлы, а у части строк disk_path успел
        /// устареть (см. DiskLayoutMigrator.VersionDirs).</summary>
        public VersionScope For(IReadOnlyList<string> diskPaths)
        {
            HashSet<string>? names = null;
            foreach (var path in diskPaths)
            {
                if (Full(path) is not { } key) continue;
                if (!_namesByDiskPath.TryGetValue(key, out var found)) continue;
                if (names is null) names = new HashSet<string>(found, StringComparer.OrdinalIgnoreCase);
                else names.UnionWith(found);
            }
            return new VersionScope(this, names ?? NoNames);
        }

        /// <summary>Расширение файла есть в одном из белых списков БД.</summary>
        public bool Whitelisted(string file) => _extensions.Contains(Path.GetExtension(file));

        /// <summary>Путь занят базой — сам или любым из своих родителей. Подъём по родителям и
        /// заменяет проверку «а это папка?»: вложение папкой (HMI-проект, сканы инструкции) лежит в
        /// том же множестве, и файл внутри него находит его как своего предка.</summary>
        public bool ReferencedPath(string path)
        {
            var full = Full(path);
            while (!string.IsNullOrEmpty(full))
            {
                if (_paths.Contains(full!)) return true;
                full = Path.GetDirectoryName(full);
            }
            return false;
        }

        /// <summary>Файл, который чистильщик не рассматривает вообще: CHANGELOG.md (его читает досмотр
        /// диска по фиксированному пути), ярлыки (их кладут для коллег со старым клиентом) и заглушка
        /// «Инструкция в разработке» — у неё своё имя и свой смысл, убирает её появление настоящего
        /// документа, а не чистка.</summary>
        public bool Untouchable(string file) =>
            VersionLayout.IsServiceFile(file) || InstructionStub.IsStub(file);

        private static string? Full(string path)
        {
            try { return Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar); }
            catch (Exception) { return null; }
        }
    }

    /// <summary>Защита в пределах одной папки версии — то, чем пользуется разбор файлов.</summary>
    private readonly struct VersionScope
    {
        private readonly Scope _scope;
        private readonly HashSet<string> _names;

        public VersionScope(Scope scope, HashSet<string> names)
        {
            _scope = scope;
            _names = names;
        }

        public bool Whitelisted(string file) => _scope.Whitelisted(file);
        public bool Untouchable(string file) => _scope.Untouchable(file);

        /// <summary>На путь ссылается запись в базе: по имени файла внутри этой папки версии либо по
        /// абсолютному пути вложения.</summary>
        public bool Referenced(string path) =>
            _names.Contains(Path.GetFileName(path)) || _scope.ReferencedPath(path);
    }

    // ── Выполнение ──────────────────────────────────────────────────────────

    /// <summary>Выполняет отмеченные находки. Свойства те же, что у <see cref="DiskLayoutMigrator.Apply"/>,
    /// и по тем же причинам: занятая цель не перезаписывается, ошибка на одном файле не роняет
    /// остальные, повторный прогон <see cref="Plan"/> после этого не находит ничего.</summary>
    /// <param name="renamed">Зовётся после КАЖДОГО удавшегося переименования файла прошивки —
    /// вызывающий правит filename/executable_hint у всех записей этой папки (Finding.RecordPaths,
    /// OldName, NewName). Без этого база осталась бы с именем файла, которого на диске уже нет.</param>
    /// <param name="progress">Сколько операций выполнено — для индикатора; зовётся из рабочего потока.</param>
    public static CleanupPlan Apply(CleanupPlan plan, Action<Finding>? renamed, Action<int, int>? progress = null)
    {
        var todo = plan.Findings.Where(f => f.Selected && f.Action != Act.None).ToList();
        var total = todo.Count;
        var done = 0;

        // Переименования отдаём мигратору целиком, одним подпланом: там уже разобран случай
        // «различие только в регистре» (старые имена писались заглавными), который на Windows
        // обычным File.Move не делается.
        var renames = todo.Where(f => f.Action == Act.Rename && f.Rename is not null).ToList();
        if (renames.Count > 0)
        {
            var byOp = renames.ToDictionary(f => f.Rename!, f => f);
            DiskLayoutMigrator.Apply(
                new DiskLayoutMigrator.MigrationPlan(renames.Select(f => f.Rename!).ToList(), Array.Empty<string>()),
                op => renamed?.Invoke(byOp[op]));
            foreach (var f in renames)
            {
                f.Status = f.Rename!.Status;
                f.Error = f.Rename!.Error;
                progress?.Invoke(++done, total);
            }
        }

        foreach (var f in todo.Where(f => f.Action != Act.Rename))
        {
            try
            {
                f.Status = f.Action switch
                {
                    Act.Move => Move(f) ? "ok" : "skip",
                    Act.Delete => Delete(f.Path) ? "ok" : "skip",
                    _ => "skip",
                };
            }
            catch (Exception ex)
            {
                f.Status = "error";
                f.Error = ex.Message;
            }
            progress?.Invoke(++done, total);
        }

        return plan;
    }

    private static bool Move(Finding f)
    {
        var targetDir = Path.GetDirectoryName(f.Target);
        if (string.IsNullOrEmpty(targetDir)) return false;

        if (f.IsFolder)
        {
            if (!Directory.Exists(f.Path)) return false;
            if (Directory.Exists(f.Target)) return false; // цель занята — чужое не трогаем
            Directory.CreateDirectory(targetDir);
            Directory.Move(f.Path, f.Target);
            return true;
        }

        if (!File.Exists(f.Path)) return false;
        Directory.CreateDirectory(targetDir);

        // Настоящая инструкция едет под каноническим именем, а его в папке уже занимает заглушка
        // «Инструкция в разработке» — она уходит первой, иначе документу под это имя не встать
        // (тот же порядок, что и в DiskLayoutMigrator.RenameInstructionsIn).
        if (LooksLikeInstruction(f.Path)) InstructionStub.RemoveFrom(targetDir);

        if (File.Exists(f.Target)) return false;
        File.Move(f.Path, f.Target);
        return true;
    }

    private static bool Delete(string path)
    {
        if (!File.Exists(path)) return false;
        File.Delete(path);
        return true;
    }

    // ── Обход диска ─────────────────────────────────────────────────────────

    private static List<string> TopLevelFiles(string dir)
    {
        try { return Directory.EnumerateFiles(dir, "*", SearchOption.TopDirectoryOnly).ToList(); }
        catch (Exception) { return new List<string>(); }
    }

    private static List<string> TopLevelDirs(string dir)
    {
        try { return Directory.EnumerateDirectories(dir).ToList(); }
        catch (Exception) { return new List<string>(); }
    }
}
