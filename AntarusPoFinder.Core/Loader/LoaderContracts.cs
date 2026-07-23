using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace AntarusPoFinder.Core.Loader;

/// <summary>Что именно делает лоадер за один запуск. Разделено, потому что у сборки и заливки
/// разные источники и разный «конец пути»: сборка кладёт результат обратно на диск (см.
/// <see cref="LoaderRequest.PublishDir"/>), заливка ничего на диск не пишет.</summary>
public enum LoaderOperation
{
    /// <summary>Собрать проект (.psl) в загружаемый файл (.lfs) — ЛОКАЛЬНО, и только потом
    /// опубликовать результат в папку версии на сетевом диске.</summary>
    Build,

    /// <summary>Залить готовый .lfs в контроллер.</summary>
    Flash,
}

public enum LoaderLogLevel { Info, Warning, Error, Success }

/// <summary>Параметры запуска — ровно то, что оператор отмечает галочками в диалоге. Набор
/// намеренно маленький: это заготовка, реальный лоадер коллеги может потребовать больше, для этого
/// есть <see cref="Extra"/>, чтобы не ломать контракт при каждом новом флаге.</summary>
public sealed record LoaderOptions
{
    /// <summary>Форматировать память контроллера перед загрузкой.</summary>
    public bool Format { get; init; }

    /// <summary>Обновить ядро (системное ПО) контроллера, если версия не совпадает.</summary>
    public bool UpdateKernel { get; init; }

    /// <summary>Порт/адрес контроллера — «COM3», «192.168.1.10» и т.п. Формат не валидируется:
    /// его задаёт реальный лоадер, а не эта заготовка.</summary>
    public string Target { get; init; } = "";

    public IReadOnlyDictionary<string, string> Extra { get; init; } = new Dictionary<string, string>();
}

/// <summary>Одно задание лоадеру. Все пути — ЛОКАЛЬНЫЕ (см. <see cref="LoaderWorkspace"/>):
/// приложение не клиент-серверное, поэтому работа идёт в локальной рабочей области, а сетевого
/// диска касается только публикация результата.</summary>
public sealed record LoaderRequest
{
    public LoaderOperation Operation { get; init; } = LoaderOperation.Flash;

    /// <summary>Локальный файл (или папка) — источник: .psl для сборки, .lfs для заливки.</summary>
    public string SourcePath { get; init; } = "";

    /// <summary>Локальная рабочая область этого запуска — сюда лоадер пишет всё промежуточное
    /// и итоговое (подпапка <c>out</c>).</summary>
    public string WorkspaceDir { get; init; } = "";

    /// <summary>Куда положить результат ПОСЛЕ успешной сборки (папка версии на диске). Пусто —
    /// ничего не публиковать (обычный случай для <see cref="LoaderOperation.Flash"/>).</summary>
    public string PublishDir { get; init; } = "";

    /// <summary>Человекочитаемое имя версии — только для логов/заголовков.</summary>
    public string VersionName { get; init; } = "";

    public LoaderOptions Options { get; init; } = new();
}

/// <summary>Одна строка прогресса. <paramref name="Percent"/> = -1 — «неизвестно сколько ещё»
/// (диалог показывает неопределённый прогресс-бар).</summary>
public sealed record LoaderProgress(
    int Percent,
    string Stage,
    string Message,
    LoaderLogLevel Level = LoaderLogLevel.Info);

/// <summary>Итог запуска. <paramref name="Artifacts"/> — локальные пути к тому, что получилось
/// (для сборки — .lfs и всё, что лоадер положил рядом).</summary>
public sealed record LoaderResult(bool Success, string Message, IReadOnlyList<string> Artifacts)
{
    public static LoaderResult Ok(string message, IReadOnlyList<string>? artifacts = null) =>
        new(true, message, artifacts ?? Array.Empty<string>());

    public static LoaderResult Fail(string message) => new(false, message, Array.Empty<string>());
}

/// <summary>
/// Точка подключения реального лоадера. Сейчас в приложении есть только заглушка
/// (<see cref="StubFirmwareLoaderBackend"/>) — она проходит все те же стадии и пишет тот же лог, но
/// ничего не собирает и никуда не заливает.
/// <para>
/// Коллеге, который будет подключать настоящий лоадер: достаточно реализовать этот интерфейс
/// (например, запуском CLI лоадера с разбором его вывода) и вернуть свою реализацию из
/// <see cref="FirmwareLoaderFactory"/>. Всё остальное — диалог, прогресс-бар, лог, локальная
/// рабочая область и публикация результата на диск — уже написано и трогать не нужно. Подробный
/// контракт: <c>docs/loader-integration.md</c>.
/// </para>
/// </summary>
public interface IFirmwareLoaderBackend
{
    /// <summary>Отображается в заголовке диалога — оператор должен видеть, чем именно грузит.</summary>
    string Name { get; }

    /// <summary>false — подключён не настоящий лоадер, а заготовка. Диалог в этом случае показывает
    /// предупреждение и не даёт принять результат за реальную загрузку.</summary>
    bool IsAvailable { get; }

    /// <summary>Почему недоступен (путь к лоадеру не задан, файл не найден и т.п.) — показывается
    /// оператору как есть. null, если доступен.</summary>
    string? UnavailableReason { get; }

    Task<LoaderResult> RunAsync(LoaderRequest request, IProgress<LoaderProgress> progress, CancellationToken ct);
}
