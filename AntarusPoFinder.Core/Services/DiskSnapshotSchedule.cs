using System;

namespace AntarusPoFinder.Core.Services;

/// <summary>Что делать с автоматическим слепком прямо сейчас.</summary>
public enum DiskSnapshotDecision
{
    /// <summary>Снимать.</summary>
    Take,

    /// <summary>Автоматика выключена на этой машине.</summary>
    Disabled,

    /// <summary>Ещё рано.</summary>
    TooSoon,

    /// <summary>Рабочий диск недоступен — пропускаем, но говорим об этом в журнал.</summary>
    NoDisk,
}

/// <summary>КОГДА СНИМАТЬ СЛЕПОК САМ, БЕЗ КНОПКИ, И КОГДА ЕГО НЕ ОТПРАВЛЯТЬ.
///
/// <b>Чего здесь боялись.</b> Слепок намеренно не делали автоматическим: опись диска с машины
/// КАЖДОГО коллеги ежедневно — это мусорный поток, в котором полезное тонет. Опасение верное, и
/// снимает его не отказ от автоматики, а два ограничения, которые здесь и живут:
///
/// 1. <b>Не чаще раза в сутки на машину</b> — и сутки считаются от последней УДАЧИ, а не по часам.
///    «Каждый день в три ночи» на рабочем ПК означает «никогда»: его в это время выключают.
/// 2. <b>Не отправлять, если содержимое не изменилось</b> — сравнение по отпечатку СОДЕРЖИМОГО
///    (<see cref="DiskSnapshot.ContentFingerprint"/>), а не по времени и не по имени машины.
///    Именно это и спасает от шести одинаковых описей одного диска: слепки разных машин одного и
///    того же дерева совпадают, потому что имя машины, время, версия программы и буква диска в
///    отпечаток не входят.
///
/// <b>Почему два срока, а не один.</b> Отметка УДАЧИ двигается на сутки, отметка ПОПЫТКИ — на час.
/// Без второй первая же неудача (диск отвалился на минуту) отодвинула бы слепок на сутки; без
/// первой обход сетевого дерева на тысячи папок шёл бы каждым тиком синхронизации, то есть каждые
/// несколько минут. Ровно та же пара сроков, что у ежесуточной резервной копии, и по тем же
/// причинам.</summary>
public static class DiskSnapshotSchedule
{
    /// <summary>Как часто снимать. Сутки: слепок нужен свежим, но не поминутным — по нему
    /// разбирают «что лежало на диске, когда это сломалось».</summary>
    public static TimeSpan Interval { get; set; } = TimeSpan.FromHours(24);

    /// <summary>Через сколько пробовать снова после неудачи или недоступного диска.</summary>
    public static TimeSpan RetryInterval { get; set; } = TimeSpan.FromHours(1);

    /// <summary>Ключи отметок. МЕСТНЫЕ (см. ConfigSyncService.SkipSettingsKeys): приехав с чужой
    /// машины, свежая отметка отменила бы слепок там, где его не снимали ни разу, а чужой отпечаток
    /// заставил бы машину молчать про СВОЙ диск.</summary>
    public const string LastDoneKey = "disk_snapshot_auto_at";
    public const string LastTryKey = "disk_snapshot_auto_try_at";
    public const string LastHashKey = "disk_snapshot_auto_hash";

    /// <summary>Настройка-выключатель. Политика предприятия (синхронизируется, как и
    /// backup_enabled): администратор решает один раз. ВКЛЮЧЕНО по умолчанию — функция, которую
    /// надо сперва найти и включить, в день разбора оказывается выключенной.</summary>
    public const string EnabledKey = "disk_snapshot_auto";

    public static DiskSnapshotDecision Decide(
        bool enabled, bool diskAvailable, DateTime nowUtc, DateTime lastDoneUtc, DateTime lastTryUtc)
    {
        if (!enabled) return DiskSnapshotDecision.Disabled;
        if (nowUtc - lastDoneUtc < Interval) return DiskSnapshotDecision.TooSoon;
        if (nowUtc - lastTryUtc < RetryInterval) return DiskSnapshotDecision.TooSoon;
        if (!diskAvailable) return DiskSnapshotDecision.NoDisk;
        return DiskSnapshotDecision.Take;
    }

    /// <summary>Отправлять ли снятое. Пустой прошлый отпечаток — отправлять: это первый слепок с
    /// этой машины. Пустой НОВЫЙ отпечаток (слепок не разобрался) — тоже отправлять: лучше лишний
    /// раз, чем молча совпасть с прошлым и не уехать никогда.</summary>
    public static bool ShouldSend(string? lastHash, string? currentHash) =>
        string.IsNullOrEmpty(currentHash) ||
        !string.Equals(lastHash, currentHash, StringComparison.Ordinal);
}
