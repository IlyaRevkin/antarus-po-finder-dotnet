using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace AntarusPoFinder.Core.Services;

/// <summary>Уборка локальных копий прошивок — тех самых, что лежат в
/// <see cref="ConfigService.LocalFw"/> и позволяют работать, когда сетевого диска нет.
///
/// <b>Почему вообще понадобилось.</b> Задача «отказаться от локального хранения» ОТМЕНЕНА: «была
/// жалоба о том что диск отвалился и в фаиндере не найти прошивки, так что функционал что у нас
/// создаются локальные копии всех прошивок оставь, просто добавим настройку частоту очистки».
/// То есть копии остаются, регулируется только частота и глубина уборки.
///
/// <b>Что было до этого.</b> Уборка шла ЖЁСТКО и сразу: каждое копирование версии в кэш сносило все
/// остальные версии этой прошивки (<c>FirmwareSync.CleanupOldLocalVersions</c>). Настроить это было
/// нельзя, а «подержать под рукой ещё и предыдущую версию» приходилось через метку <c>.keep</c>
/// вручную, по одной. Отсюда и жалоба: уехал на объект — а в кэше ровно одна версия на шкаф.
///
/// <b>Главное свойство — не унести то, без чего не поработать без диска.</b> Поэтому защищено
/// ТРЁХСЛОЙНО, и каждый слой важнее полноты уборки:
/// <list type="number">
/// <item><description>версия с меткой <c>.keep</c> (наладчик закрепил её руками) — не трогается
/// никогда, ни по возрасту, ни по объёму;</description></item>
/// <item><description>САМАЯ СВЕЖАЯ версия каждой прошивки — не трогается никогда. Это и есть
/// «работа без диска»: унеси её, и шкаф, который человек вёз чинить, окажется без прошивки
/// вообще;</description></item>
/// <item><description>версия моложе <see cref="Policy.KeepDays"/> — не трогается по возрасту (но
/// может уйти под потолок объёма, если тот задан и превышен).</description></item>
/// </list>
///
/// <b>Уборка молчит.</b> Ни окон, ни подтверждений: она фоновая, и человек, который её не включал
/// и не видит, не должен получать от неё сообщений. План и применение разделены не ради диалога, а
/// ради теста: «что бы удалилось» проверяется без единого удаления.</summary>
public static class LocalCacheCleanup
{
    /// <summary>Как часто убираться. Значения те же, что лежат в настройке
    /// <see cref="ConfigService.LocalCleanupMode"/>.</summary>
    public static class Mode
    {
        public const string Never = "never";
        public const string Weekly = "weekly";
        public const string Monthly = "monthly";
    }

    /// <summary>Что и насколько агрессивно убирать.</summary>
    /// <param name="Frequency">Один из <see cref="Mode"/>.</param>
    /// <param name="KeepDays">Сколько дней держать невостребованную копию. 0 — по возрасту не
    /// убирать вовсе.</param>
    /// <param name="MaxGb">Потолок объёма кэша. 0 — по объёму не убирать.</param>
    public sealed record Policy(string Frequency, int KeepDays, int MaxGb)
    {
        public bool Enabled => !string.Equals(Frequency, Mode.Never, StringComparison.OrdinalIgnoreCase);

        /// <summary>Сколько ждать между уборками. Неизвестное значение читается как «ежемесячно» —
        /// настройка могла приехать с машины, где список режимов длиннее, и падать из-за этого
        /// фоновой уборке незачем.</summary>
        public TimeSpan Interval => string.Equals(Frequency, Mode.Weekly, StringComparison.OrdinalIgnoreCase)
            ? TimeSpan.FromDays(7)
            : TimeSpan.FromDays(30);
    }

    /// <summary>Одна локальная копия версии.</summary>
    public sealed record Copy(string Path, string FirmwareDir, string VersionRaw, long Bytes, DateTime LastUsed)
    {
        public bool Kept => LocalFirmwareCache.IsKeptDir(Path);
    }

    /// <summary>Что уборка собирается сделать. <paramref name="Freed"/> — сколько байт освободится.</summary>
    public sealed record Plan(IReadOnlyList<Copy> Delete, long Freed, long TotalBytes, int TotalCopies)
    {
        public bool Empty => Delete.Count == 0;
        public static readonly Plan Nothing = new(Array.Empty<Copy>(), 0, 0, 0);
    }

    /// <summary>Пора ли убираться. <paramref name="lastRun"/> — отметка из настроек; пусто или
    /// нечитаемо — пора (первый запуск или испорченное значение; лишняя уборка безвреднее
    /// бесконечного её откладывания).</summary>
    public static bool IsDue(Policy policy, string? lastRun, DateTime now)
    {
        if (!policy.Enabled) return false;
        if (!DateTime.TryParse(lastRun, out var at)) return true;
        return now - at >= policy.Interval;
    }

    /// <summary>Что лежит в кэше прямо сейчас. Отдельно от <see cref="Build"/>, чтобы страница
    /// настроек могла показать «сейчас занято столько-то», ничего не планируя и не удаляя.</summary>
    public static List<Copy> Scan(string cacheRoot)
    {
        var result = new List<Copy>();
        if (string.IsNullOrWhiteSpace(cacheRoot) || !Directory.Exists(cacheRoot)) return result;

        foreach (var firmwareDir in SafeDirs(cacheRoot))
            foreach (var versionDir in SafeDirs(firmwareDir))
                result.Add(new Copy(versionDir, firmwareDir, Path.GetFileName(versionDir),
                    SizeOf(versionDir), LastUsedOf(versionDir)));

        return result;
    }

    /// <summary>План уборки. Ничего не трогает — только решает.</summary>
    public static Plan Build(string cacheRoot, Policy policy, DateTime now)
    {
        if (!policy.Enabled) return Plan.Nothing;

        var copies = Scan(cacheRoot);
        if (copies.Count == 0) return Plan.Nothing;

        var total = copies.Sum(c => c.Bytes);

        // Самая свежая копия КАЖДОЙ прошивки неприкосновенна — слой 2 из доки класса. «Самая
        // свежая» берётся по времени использования папки, а не по номеру версии: номера у разных
        // прошивок устроены по-разному, а «чем недавно пользовались, то и нужно» верно всегда.
        var newestPerFirmware = copies
            .GroupBy(c => c.FirmwareDir, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.OrderByDescending(c => c.LastUsed).First().Path)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        bool Protected(Copy c) => c.Kept || newestPerFirmware.Contains(c.Path);

        var delete = new List<Copy>();

        // По возрасту.
        if (policy.KeepDays > 0)
        {
            var cutoff = now - TimeSpan.FromDays(policy.KeepDays);
            delete.AddRange(copies.Where(c => !Protected(c) && c.LastUsed < cutoff));
        }

        // По объёму — добираем самые давние из оставшихся, пока не уложимся в потолок. Считается
        // ПОСЛЕ возрастной уборки и с учётом уже намеченного: иначе потолок срабатывал бы на
        // объёме, которого после уборки уже не будет, и уносил бы лишнее.
        if (policy.MaxGb > 0)
        {
            var cap = (long)policy.MaxGb * 1024L * 1024L * 1024L;
            var planned = delete.Select(c => c.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var left = total - delete.Sum(c => c.Bytes);

            foreach (var candidate in copies
                         .Where(c => !Protected(c) && !planned.Contains(c.Path))
                         .OrderBy(c => c.LastUsed))
            {
                if (left <= cap) break;
                delete.Add(candidate);
                left -= candidate.Bytes;
            }
        }

        return new Plan(delete, delete.Sum(c => c.Bytes), total, copies.Count);
    }

    /// <summary>Выполнить план. Возврат — сколько папок реально унесено. Каждое удаление отдельно
    /// переживает ошибку: папка может быть открыта в проводнике или занята антивирусом, и срывать
    /// из-за одной такой всю уборку незачем.</summary>
    public static int Apply(Plan plan)
    {
        var done = 0;
        foreach (var copy in plan.Delete)
        {
            try
            {
                Directory.Delete(copy.Path, recursive: true);
                done++;
            }
            catch (Exception)
            {
                // best effort — следующая уборка попробует снова
            }
        }
        return done;
    }

    private static IEnumerable<string> SafeDirs(string path)
    {
        try { return Directory.EnumerateDirectories(path); }
        catch (Exception) { return Array.Empty<string>(); }
    }

    private static long SizeOf(string dir)
    {
        try
        {
            return Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories)
                .Sum(f => { try { return new FileInfo(f).Length; } catch (Exception) { return 0L; } });
        }
        catch (Exception) { return 0; }
    }

    /// <summary>Когда копией пользовались в последний раз. Берётся максимум из времени ОБРАЩЕНИЯ и
    /// времени ЗАПИСИ по всем файлам папки, а не время самой папки: папка версии создаётся один раз
    /// при скачивании и больше не меняется, поэтому по её собственной дате любая копия выглядит
    /// «давно не нужной» уже через месяц — даже та, которую вчера шили в шкаф.</summary>
    private static DateTime LastUsedOf(string dir)
    {
        var newest = DateTime.MinValue;
        try
        {
            foreach (var file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
            {
                try
                {
                    var info = new FileInfo(file);
                    if (info.LastAccessTime > newest) newest = info.LastAccessTime;
                    if (info.LastWriteTime > newest) newest = info.LastWriteTime;
                }
                catch (Exception) { /* файл исчез между перечислением и опросом */ }
            }

            if (newest == DateTime.MinValue) newest = Directory.GetLastWriteTime(dir);
        }
        catch (Exception) { newest = DateTime.Now; }

        return newest;
    }
}
