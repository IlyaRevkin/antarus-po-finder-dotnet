using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace AntarusPoFinder.Core.Services;

/// <summary>Что именно делает долгая операция. Перечисление маленькое намеренно: сюда попадает
/// только то, что человек запускает и потом ЖДЁТ минутами, держа при этом внешний ресурс —
/// контроллер, Segnetics Loader, папку версии на диске. Быстрые вещи (открыть файл, сохранить тег)
/// учитывать здесь не надо: у них нет ни окна хода, ни конфликтов.</summary>
public enum LongOperationKind
{
    /// <summary>Заливка проекта в контроллер, вместе с форматированием проекта и обновлением ядра,
    /// если наладчик его попросил.</summary>
    PlcDeploy,

    /// <summary>Сборка .lfs из .psl изолированным SMLogix, без подключения к ПЛК.</summary>
    LfsBuild,

    /// <summary>Разовая перестройка раскладки уже накопленного диска.</summary>
    DiskRebuild,

    /// <summary>Чистка диска: поиск и удаление мусора по всему дереву.</summary>
    DiskCleanup,
}

/// <summary>Ключ «над чем работаем». Строкой, а не типом, потому что сравнивают его разные страницы,
/// у которых на руках разное: у карточки поиска — папка версии на диске, у модерации — та же папка,
/// но приведённая к своей форме диска. Нормализация здесь одна на всех.</summary>
public static class LongOperationSubject
{
    /// <summary>Пусто — операция не привязана к конкретной версии (перестройка диска, чистка).</summary>
    public const string None = "";

    /// <summary>Папка версии на диске. Регистр сворачивается: пути в Windows регистронезависимы, а
    /// в базе один и тот же каталог лежит записанным по-разному у разных коллег.</summary>
    public static string Folder(string? path)
    {
        var raw = path?.Trim() ?? "";
        if (raw.Length == 0) return None;
        string full;
        try { full = Path.GetFullPath(raw); }
        catch (Exception) { full = raw; }
        full = full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        // ToUpperInvariant, а не ToLowerInvariant: для ключей словаря рекомендован именно он —
        // повышение регистра обратимо для всех алфавитов, включая кириллицу в путях вида
        // «ПО\ПЖ\...», из которых эти ключи и состоят.
        return "folder:" + full.ToUpperInvariant();
    }
}

/// <summary>Одна идущая прямо сейчас долгая операция.</summary>
/// <param name="Kind">Что делаем.</param>
/// <param name="SubjectKey">Над чем (см. <see cref="LongOperationSubject"/>); пусто — над диском целиком.</param>
/// <param name="Title">Как это назвать человеку: «Загрузка в ПЛК: ТГР 2.0».</param>
/// <param name="StartedAt">Когда началось — чтобы отказ мог сказать, сколько уже идёт.</param>
public sealed record LongOperation(
    LongOperationKind Kind,
    string SubjectKey,
    string Title,
    DateTime StartedAt)
{
    public string Caption => LongOperationRules.Caption(Kind);
}

/// <summary>Право на выполнение операции: пока объект не освобождён, регистрация в
/// <see cref="LongOperationRegistry"/> держится и вторую такую же не пустят.</summary>
public interface ILongOperationLease : IDisposable
{
    LongOperation Operation { get; }
}

/// <summary>Правила «что с чем не уживается». Вынесены отдельной статикой без состояния, чтобы их
/// можно было прочитать и проверить, не заводя реестра и не запуская ничего.
///
/// ⚠️ Смысл ограничений — не «нельзя нажимать две кнопки», а физика:
/// Segnetics Loader Automation на машине один процесс и один контроллер на проводе; перестройка
/// раскладки диска перекладывает файлы под ногами у всего остального; сборка LFS пишет .lfs прямо в
/// папку версии на сетевом диске.</summary>
public static class LongOperationRules
{
    public static string Caption(LongOperationKind kind) => kind switch
    {
        LongOperationKind.PlcDeploy => "Загрузка в ПЛК",
        LongOperationKind.LfsBuild => "Сборка LFS",
        LongOperationKind.DiskRebuild => "Перестройка структуры диска",
        LongOperationKind.DiskCleanup => "Очистка диска",
        _ => "Операция",
    };

    /// <summary>Операция идёт через Segnetics Loader Automation. Он один на машину: второй запуск
    /// либо не стартует вовсе, либо перехватывает тот же COM/USB и роняет обе загрузки.</summary>
    public static bool UsesLoader(LongOperationKind kind) =>
        kind is LongOperationKind.PlcDeploy or LongOperationKind.LfsBuild;

    /// <summary>Операция ходит по всему дереву диска и двигает файлы — пока она идёт, любая работа
    /// с отдельной версией опирается на пути, которых через секунду может не быть.</summary>
    public static bool TouchesWholeDisk(LongOperationKind kind) =>
        kind is LongOperationKind.DiskRebuild or LongOperationKind.DiskCleanup;

    /// <summary>Можно ли безопасно оборвать операцию на полпути. Форматирование проекта и обновление
    /// ядра ПЛК — нельзя: контроллер остаётся без рабочей прошивки, и это надо СКАЗАТЬ, а не тихо
    /// погасить кнопку (см. <see cref="CancelWarning"/>).</summary>
    public static bool SafeToCancel(LongOperationKind kind, bool formatsController) =>
        !(kind == LongOperationKind.PlcDeploy && formatsController);

    /// <summary>Строка в окне хода: чем грозит остановка. null — грозить нечем, останавливать
    /// безопасно.</summary>
    public static string? CancelWarning(LongOperationKind kind, bool formatsController) =>
        SafeToCancel(kind, formatsController)
            ? null
            : "Остановить безопасно нельзя: идёт форматирование проекта и обновление ядра ПЛК. " +
              "Если оборвать сейчас, контроллер останется без рабочей прошивки, и заливать придётся заново.";

    /// <summary>Вопрос перед остановкой небезопасной операции. Кнопку «Остановить» не прячем: бывает,
    /// что оборвать всё равно надо (не тот файл, не тот контроллер), — но человек должен нажать её
    /// осознанно.</summary>
    public static string CancelConfirmation(string title) =>
        $"{title}\n\n" +
        "Сейчас идёт форматирование проекта и обновление ядра ПЛК. Если оборвать операцию, " +
        "контроллер может остаться без рабочей прошивки — заливать придётся с начала.\n\n" +
        "Всё равно остановить?";

    /// <summary>Почему <paramref name="candidate"/> сейчас запускать нельзя, при уже идущих
    /// <paramref name="active"/>. null — можно.
    ///
    /// Порядок проверок — от самого физического к самому частному: сперва занятый Loader и
    /// перекладывающая диск операция (они запрещают вообще всё), потом «эта же работа над этой же
    /// версией», потом «версия занята другой работой».</summary>
    public static string? Refusal(LongOperation candidate, IEnumerable<LongOperation> active)
    {
        var running = active as IReadOnlyList<LongOperation> ?? active.ToList();

        if (running.FirstOrDefault(o => TouchesWholeDisk(o.Kind)) is { } diskWide)
            return $"Сейчас идёт «{diskWide.Caption}» — она переносит файлы по всему диску. " +
                   $"«{Caption(candidate.Kind)}» запустится, когда она закончится.";

        if (TouchesWholeDisk(candidate.Kind) && running.Count > 0)
            return $"Сначала дождитесь окончания: {running[^1].Title}. " +
                   $"«{Caption(candidate.Kind)}» переносит файлы по всему диску, и запускать её " +
                   "поверх работающей операции нельзя.";

        // Ровно та же работа над той же версией — самый частый случай (двойной клик), и говорить
        // про «занятый Loader» здесь было бы враньём наоборот: занят он этой же самой операцией.
        // Поэтому этот случай разбирается ДО общего правила про Loader ниже.
        if (candidate.SubjectKey.Length > 0 &&
            running.Any(o => SameSubject(o, candidate) && o.Kind == candidate.Kind))
            return $"«{candidate.Caption}» для этой версии уже идёт.";

        if (UsesLoader(candidate.Kind) &&
            running.FirstOrDefault(o => UsesLoader(o.Kind)) is { } loaderBusy)
            return $"Segnetics Loader уже занят: {loaderBusy.Title}. " +
                   "Он умеет только одну операцию за раз — дождитесь окончания или остановите её.";

        if (candidate.SubjectKey.Length > 0 &&
            running.FirstOrDefault(o => SameSubject(o, candidate)) is { } subjectBusy)
            return $"Эта версия сейчас занята: {subjectBusy.Title}.";

        return null;
    }

    /// <summary>Почему с версией сейчас ничего делать нельзя (откатить, удалить, перезалить,
    /// открыть модерацию). null — свободна.</summary>
    public static string? SubjectBusyReason(string subjectKey, IEnumerable<LongOperation> active)
    {
        if (subjectKey.Length == 0) return null;
        var holder = active.FirstOrDefault(o => string.Equals(o.SubjectKey, subjectKey, StringComparison.Ordinal));
        return holder is null
            ? null
            : $"Сейчас идёт: {holder.Title}. Пока операция не закончится, эту версию менять нельзя — " +
              "она пишет файлы в её папку на диске.";
    }

    private static bool SameSubject(LongOperation left, LongOperation right) =>
        left.SubjectKey.Length > 0 &&
        string.Equals(left.SubjectKey, right.SubjectKey, StringComparison.Ordinal);
}

/// <summary>Список идущих прямо сейчас долгих операций — один на приложение (живёт в AppServices).
///
/// Появился, когда окна долгих операций перестали быть модальными. Раньше «нельзя запустить вторую
/// заливку» обеспечивалось само собой: пока висит модальное окно, нажать вторую кнопку физически
/// невозможно. Как только окно отпустило программу, эта защита исчезла — и вернуть её надо было
/// явно, иначе две заливки поделили бы один контроллер, а удаление версии случилось бы посреди
/// записи .lfs в её папку.</summary>
public sealed class LongOperationRegistry
{
    private readonly object _gate = new();
    private readonly List<LongOperation> _active = new();

    /// <summary>Список изменился: что-то началось или закончилось. Нужен страницам, чтобы
    /// перерисовать доступность кнопок над занятой версией.</summary>
    public event Action? Changed;

    public IReadOnlyList<LongOperation> Active
    {
        get { lock (_gate) return _active.ToArray(); }
    }

    public bool AnyRunning
    {
        get { lock (_gate) return _active.Count > 0; }
    }

    /// <summary>Занять ресурс. false — запускать нельзя, <paramref name="refusal"/> объясняет
    /// человеку почему (текст готов к показу, дописывать к нему ничего не надо).</summary>
    public bool TryBegin(
        LongOperationKind kind,
        string subjectKey,
        string title,
        out ILongOperationLease? lease,
        out string refusal)
    {
        lease = null;
        refusal = "";
        var candidate = new LongOperation(kind, subjectKey ?? LongOperationSubject.None, title, DateTime.Now);

        lock (_gate)
        {
            if (LongOperationRules.Refusal(candidate, _active) is { } reason)
            {
                refusal = reason;
                return false;
            }
            _active.Add(candidate);
        }

        lease = new Lease(this, candidate);
        Changed?.Invoke();
        return true;
    }

    /// <summary>Почему версию сейчас нельзя трогать. null — можно.</summary>
    public string? SubjectBusyReason(string subjectKey)
    {
        lock (_gate) return LongOperationRules.SubjectBusyReason(subjectKey ?? "", _active);
    }

    /// <summary>Идёт ли прямо сейчас операция такого рода — для подписей и тестов.</summary>
    public bool IsRunning(LongOperationKind kind)
    {
        lock (_gate) return _active.Any(o => o.Kind == kind);
    }

    private void End(LongOperation operation)
    {
        // ReferenceEquals, а не Remove по равенству: record сравнивается по значениям, и две
        // операции одного вида над одной версией с совпавшей до миллисекунды отметкой времени
        // сняли бы друг друга.
        int removed;
        lock (_gate) removed = _active.RemoveAll(o => ReferenceEquals(o, operation));
        if (removed > 0) Changed?.Invoke();
    }

    private sealed class Lease : ILongOperationLease
    {
        private readonly LongOperationRegistry _owner;
        private bool _disposed;

        public Lease(LongOperationRegistry owner, LongOperation operation)
        {
            _owner = owner;
            Operation = operation;
        }

        public LongOperation Operation { get; }

        /// <summary>Двойной Dispose приходит сам собой (using внутри блока, который закрывается по
        /// исключению) — второй раз снимать чужую операцию с тем же содержимым нельзя.</summary>
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _owner.End(Operation);
        }
    }
}
