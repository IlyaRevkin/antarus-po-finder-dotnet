using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
/// <item><description><b>Инструкции — на третий диск</b>, если он настроен: файлы переезжают в
/// зеркальную папку (см. <see cref="InstructionDiskResolver"/>), на первом по желанию остаётся .lnk.
/// В БД пути не правятся вовсе — там и так лежит путь на ПЕРВОМ диске, а читающая сторона сама
/// считает зеркало (см. <see cref="InstructionStorage"/>).</description></item>
/// </list>
///
/// Три свойства, без которых такую операцию нельзя выпускать, и они здесь есть:
/// • <b>сухой прогон</b> — <see cref="Plan"/> ничего не делает, только перечисляет операции;
/// • <b>журнал</b> — <see cref="Apply"/> отдаёт список выполненного с исходом каждой операции,
///   вызывающий сохраняет его файлом ДО того, как показать результат человеку;
/// • <b>идемпотентность</b> — повторный прогон видит уже переименованное/переехавшее и не делает
///   ничего; прерванный на середине прогон дочищается следующим.
///
/// Класс сознательно НЕ трогает: имена папок версий, ОПЦ-раскладку и перенос пяти папок внутрь
/// версии (docs/hierarchy-rework-plan.md, этапы 4–5) — это переезд данных с изменением disk_path,
/// его нельзя делать раньше, чем все машины научатся читать обе раскладки.</summary>
public static class DiskLayoutMigrator
{
    /// <summary>Файлы, которые в папке версии не считаются файлом прошивки: журнал изменений (его
    /// пишет само приложение) и ярлыки (их кладут для коллег со старым клиентом).</summary>
    private static readonly string[] NonFirmwareNames = { "CHANGELOG.md" };

    public enum OpKind
    {
        /// <summary>Переименовать файл прошивки в каноническое имя.</summary>
        RenameFirmware,

        /// <summary>Перенести файл инструкции на третий диск.</summary>
        MoveInstruction,

        /// <summary>Положить на первом диске ярлык на уехавшую инструкцию.</summary>
        InstructionShortcut,
    }

    public sealed class Op
    {
        public OpKind Kind { get; init; }
        public string Source { get; init; } = "";
        public string Target { get; init; } = "";

        /// <summary>Человеческое пояснение — что это за файл и почему операция нужна.</summary>
        public string Note { get; init; } = "";

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

        /// <summary>ok / skip / error — заполняется в Apply.</summary>
        public string Status { get; set; } = "";
        public string Error { get; set; } = "";

        public string KindLabel => Kind switch
        {
            OpKind.RenameFirmware => "Переименовать прошивку",
            OpKind.MoveInstruction => "Инструкция → третий диск",
            _ => "Ярлык на инструкцию",
        };
    }

    public sealed record MigrationOptions(bool RenameFirmwareFiles, bool MoveInstructionsToThirdDisk);

    /// <summary>Вход планировщика. Версии берутся из БД (та же выборка, что и вкладка «Прошивки»):
    /// именно они задают, где папка версии и какое у файла должно быть каноническое имя.</summary>
    public sealed record MigrationInput(
        string Root,
        string? ThirdRoot,
        bool CreateShortcuts,
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
        if (input.Options.MoveInstructionsToThirdDisk)
            PlanInstructionMoves(input, ops, skipped);

        return new MigrationPlan(ops, skipped);
    }

    private static void PlanRenames(MigrationInput input, List<Op> ops, List<string> skipped)
    {
        // Одна папка версии может быть записана у нескольких строк (конфигурации шкафа делят файлы) —
        // планируем по папке, а не по записи, иначе на один файл придётся несколько переименований.
        // Попутно собираем все disk_path, которыми эта папка записана в базе (см. Op.RecordPaths).
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

        foreach (var dir in order)
        {
            var (v, dbPaths) = byDir[dir];

            var files = FirmwareFilesIn(dir);
            if (files.Count == 0) continue;
            if (files.Count > 1)
            {
                // Многофайловая папка (проект с ресурсами, ПЛК + панель рядом): какое из имён
                // «главное», знает только executable_hint, а он у коллег не обновится — не трогаем.
                skipped.Add($"{v.VersionRaw}: в папке {files.Count} файла — переименование пропущено " +
                            "(в многофайловой папке имя файла привязано к подсказке «чем открывать»)");
                continue;
            }

            var number = FwVersionNumber.Parse(v.VersionRaw);
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

    private static void PlanInstructionMoves(MigrationInput input, List<Op> ops, List<string> skipped)
    {
        if (string.IsNullOrWhiteSpace(input.ThirdRoot))
        {
            skipped.Add("Третий диск не настроен — переносить инструкции некуда");
            return;
        }
        if (!SafeDirExists(input.ThirdRoot))
        {
            skipped.Add($"Третий диск недоступен ({input.ThirdRoot}) — перенос инструкций пропущен");
            return;
        }

        foreach (var folder in InstructionFolders(input.Root))
        {
            var mirror = InstructionDiskResolver.Mirror(input.Root, input.ThirdRoot, folder);
            if (mirror is null) continue;

            foreach (var file in TopLevelFiles(folder))
            {
                // Ярлык — не документ: он и остаётся на первом диске указателем на уехавший файл.
                if (DocFileResolver.IsShortcut(file)) continue;

                var target = Path.Combine(mirror, Path.GetFileName(file));
                ops.Add(new Op
                {
                    Kind = OpKind.MoveInstruction,
                    Source = file,
                    Target = target,
                    Note = Path.GetFileName(file),
                });
                if (input.CreateShortcuts)
                    ops.Add(new Op
                    {
                        Kind = OpKind.InstructionShortcut,
                        Source = target,
                        Target = Path.Combine(folder, Path.GetFileName(file) + ".lnk"),
                        Note = "чтобы у коллеги со старым клиентом папка не выглядела пустой",
                    });
            }
        }
    }

    // ── Выполнение ──────────────────────────────────────────────────────────

    /// <param name="renamed">Зовётся после КАЖДОГО удавшегося переименования — вызывающий правит в БД
    /// filename/executable_hint у всех записей этой папки (Op.RecordPaths, Op.OldName, Op.NewName).
    /// Отдельным колбэком, потому что Core-слой миграции про базу ничего не знает и работать должен
    /// и в тестах без неё.</param>
    /// <param name="shortcuts">Чем создавать .lnk. null — ярлыки просто не создаются.</param>
    /// <param name="progress">Сколько операций выполнено — для индикатора; зовётся из рабочего потока.</param>
    public static MigrationPlan Apply(MigrationPlan plan, Action<Op>? renamed,
        IShortcutCreator? shortcuts, Action<int, int>? progress = null)
    {
        var total = plan.Ops.Count;
        var done = 0;

        foreach (var op in plan.Ops)
        {
            try
            {
                switch (op.Kind)
                {
                    case OpKind.RenameFirmware:
                        op.Status = RenameFile(op.Source, op.Target) ? "ok" : "skip";
                        if (op.Status == "ok") renamed?.Invoke(op);
                        break;

                    case OpKind.MoveInstruction:
                        op.Status = MoveFile(op.Source, op.Target) ? "ok" : "skip";
                        break;

                    case OpKind.InstructionShortcut:
                        if (shortcuts is null || !File.Exists(op.Source)) { op.Status = "skip"; break; }
                        Directory.CreateDirectory(Path.GetDirectoryName(op.Target)!);
                        shortcuts.Create(op.Target, op.Source, "Инструкция лежит на диске инструкций");
                        op.Status = "ok";
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

        return plan;
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

    private static bool MoveFile(string source, string target)
    {
        if (!File.Exists(source)) return false;
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        if (File.Exists(target))
        {
            // Файл уже лежит на третьем диске (прогон повторный или коллега перенёс раньше). Свежий
            // на первом диске — переносим поверх, иначе просто убираем дубль с первого.
            if (File.GetLastWriteTimeUtc(source) > File.GetLastWriteTimeUtc(target))
                File.Copy(source, target, overwrite: true);
            File.Delete(source);
            return true;
        }
        File.Move(source, target);
        return true;
    }

    // ── Обход диска ─────────────────────────────────────────────────────────

    /// <summary>Файлы верхнего уровня папки версии, которые считаются файлом прошивки.</summary>
    private static List<string> FirmwareFilesIn(string dir)
    {
        try
        {
            return Directory.EnumerateFiles(dir, "*", SearchOption.TopDirectoryOnly)
                .Where(f => !NonFirmwareNames.Contains(Path.GetFileName(f), StringComparer.OrdinalIgnoreCase))
                .Where(f => !DocFileResolver.IsShortcut(f))
                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch (Exception)
        {
            return new List<string>();
        }
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

    /// <summary>Все папки «Инструкция» под ПО на первом диске. Обход по дереву, а не по записям БД:
    /// инструкции лежат в общих папках контроллера, у версии своей папки нет, и часть из них
    /// накопилась вообще без записи в базе.</summary>
    private static List<string> InstructionFolders(string root)
    {
        var po = Path.Combine(root, "ПО");
        if (!SafeDirExists(po)) return new List<string>();
        try
        {
            // Материализуем внутри try: обход сетевой шары падает не при создании перечислителя, а
            // посреди самого обхода — отвалившийся диск не должен ронять планирование целиком.
            return Directory.EnumerateDirectories(po, HierarchyFolders.Instructions, SearchOption.AllDirectories).ToList();
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
}
