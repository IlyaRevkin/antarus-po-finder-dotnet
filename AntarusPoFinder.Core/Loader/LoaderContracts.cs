using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace AntarusPoFinder.Core.Loader;

/// <summary>Операция, которую Searcher выполняет через Segnetics Loader.</summary>
public enum LoaderOperation
{
    /// <summary>Определить тип выбранного файла и загрузить проект в ПЛК.</summary>
    Deploy,

    /// <summary>Только собрать .lfs из .psl изолированным SMLogix, к ПЛК не подключаться
    /// (см. docs/loader/LOADER_AUTOMATION_API.md, действие <c>build</c>). Нужна, чтобы наладчик
    /// видел готовый .lfs в поиске, а не собирал его каждый раз заливкой в контроллер.</summary>
    Build,
}

public enum LoaderLogLevel { Info, Warning, Error, Success }

/// <summary>Параметры интерактивной загрузки с карточки версии.</summary>
public sealed record LoaderOptions
{
    /// <summary>Перед загрузкой отформатировать проект и обновить ядро ПЛК.</summary>
    public bool FormatAndUpdateFirmware { get; init; }
}

/// <summary>Одно задание Segnetics Loader. Путь к проекту всегда локальный: сетевой источник
/// предварительно копируется в <see cref="LoaderWorkspace"/>.</summary>
public sealed record LoaderRequest
{
    public LoaderOperation Operation { get; init; } = LoaderOperation.Deploy;

    public string SourcePath { get; init; } = "";

    public string WorkspaceDir { get; init; } = "";

    /// <summary>Опциональный путь, куда Loader должен сохранить LFS после успешного deploy PSL.</summary>
    public string? OutputPath { get; init; }

    public string VersionName { get; init; } = "";

    public LoaderOptions Options { get; init; } = new();
}

/// <summary>Что именно окно загрузки должно сделать: какой файл взять и куда положить собранный
/// .lfs. Собирается вызывающей стороной (карточка поиска, модерация, загрузка новой версии) —
/// само окно никуда не ходит за этими путями и ничего не «доищет» само.</summary>
public sealed record LoaderJob
{
    public LoaderOperation Operation { get; init; } = LoaderOperation.Deploy;

    /// <summary>Имя версии для заголовка и имени рабочей области.</summary>
    public string VersionName { get; init; } = "";

    /// <summary>Файл, с которым работаем: .lfs (сразу заливаем) или .psl (Loader соберёт).</summary>
    public string SourcePath { get; init; } = "";

    /// <summary>Папка версии на сетевом диске — главная цель публикации собранного .lfs: только
    /// оттуда его увидят остальные машины.</summary>
    public string NetworkFolder { get; init; } = "";

    /// <summary>Папка версии в локальном кэше — зеркало, чтобы своя же карточка показала LFS сразу,
    /// не дожидаясь следующей синхронизации.</summary>
    public string LocalFolder { get; init; } = "";
}

/// <summary>Одна строка прогресса. <paramref name="Percent"/> = -1 означает неопределённый
/// прогресс.</summary>
public sealed record LoaderProgress(
    int Percent,
    string Stage,
    string Message,
    LoaderLogLevel Level = LoaderLogLevel.Info,
    bool UpdatesStage = true);

/// <summary>Итог операции Automation. <paramref name="Artifacts"/> содержит локальные пути,
/// сообщённые Loader, если они присутствовали в терминальном событии.</summary>
public sealed record LoaderResult(bool Success, string Message, IReadOnlyList<string> Artifacts)
{
    public static LoaderResult Ok(string message, IReadOnlyList<string>? artifacts = null) =>
        new(true, message, artifacts ?? Array.Empty<string>());

    public static LoaderResult Fail(string message) => new(false, message, Array.Empty<string>());
}

/// <summary>Локальный process API Segnetics Loader. Реализация запускает Automation-процесс,
/// транслирует JSONL-события в прогресс диалога и отправляет команду отмены через stdin.</summary>
public interface IFirmwareLoaderBackend
{
    string Name { get; }

    string? DisplayVersion { get; }

    bool IsAvailable { get; }

    string? UnavailableReason { get; }

    Task<LoaderResult> RunAsync(
        LoaderRequest request,
        IProgress<LoaderProgress> progress,
        CancellationToken cancellationToken);
}
