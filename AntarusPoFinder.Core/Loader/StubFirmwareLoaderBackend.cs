using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace AntarusPoFinder.Core.Loader;

/// <summary>
/// Заготовка лоадера: проходит те же стадии, что и настоящий (подготовка → проверка файла →
/// [форматирование] → [обновление ядра] → передача → проверка), пишет тот же прогресс и лог — но
/// НИЧЕГО не собирает и никуда не заливает. Нужна, чтобы весь обвес (диалог, прогресс-бар, лог,
/// локальная рабочая область, публикация на диск) можно было проверить и отдать коллеге до того,
/// как появится реальная интеграция.
/// <para>Каждая строка лога помечена «[заглушка]», <see cref="IsAvailable"/> = false, а в
/// <see cref="OutputDir"/> кладётся текстовый файл-памятка вместо .lfs — чтобы результат заглушки
/// физически нельзя было принять за настоящую сборку и случайно опубликовать на диск.</para>
/// </summary>
public sealed class StubFirmwareLoaderBackend : IFirmwareLoaderBackend
{
    /// <summary>Пауза между стадиями — только чтобы прогресс-бар и лог вели себя как при реальной
    /// работе, а не мигали разом. В тестах задаётся нулевой.</summary>
    private readonly TimeSpan _stepDelay;

    public StubFirmwareLoaderBackend(string? unavailableReason = null, TimeSpan? stepDelay = null)
    {
        UnavailableReason = unavailableReason ?? "Реальный лоадер ещё не подключён — это заготовка.";
        _stepDelay = stepDelay ?? TimeSpan.FromMilliseconds(350);
    }

    public string Name => "Заготовка лоадера";
    public bool IsAvailable => false;
    public string? UnavailableReason { get; }

    public const string StubMarkerFileName = "ЗАГЛУШКА_лоадера.txt";
    public const string LogPrefix = "[заглушка] ";

    public async Task<LoaderResult> RunAsync(LoaderRequest request, IProgress<LoaderProgress> progress, CancellationToken ct)
    {
        var steps = BuildSteps(request);
        for (var i = 0; i < steps.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var (stage, message) = steps[i];
            var percent = (int)Math.Round((i + 1) * 100.0 / (steps.Count + 1));
            progress.Report(new LoaderProgress(percent, stage, LogPrefix + message));
            if (_stepDelay > TimeSpan.Zero) await Task.Delay(_stepDelay, ct).ConfigureAwait(false);
        }

        ct.ThrowIfCancellationRequested();
        WriteMarker(request);

        progress.Report(new LoaderProgress(100, "Готово",
            LogPrefix + "Шаги пройдены, но реальных действий не выполнялось.", LoaderLogLevel.Warning));

        return LoaderResult.Ok(
            "Заготовка отработала: реальная сборка/загрузка не выполнялась. " +
            "Подключение настоящего лоадера — см. docs/loader-integration.md.",
            Array.Empty<string>());
    }

    private static List<(string Stage, string Message)> BuildSteps(LoaderRequest request)
    {
        var opts = request.Options;
        var target = string.IsNullOrWhiteSpace(opts.Target) ? "не указан" : opts.Target;
        var steps = new List<(string, string)>
        {
            ("Подготовка", $"Рабочая область: {request.WorkspaceDir}"),
            ("Подготовка", $"Источник (локальная копия): {request.SourcePath}"),
            ("Связь", $"Порт/адрес контроллера: {target}"),
        };

        if (request.Operation == LoaderOperation.Build)
        {
            steps.Add(("Сборка", "Проверка проекта .psl"));
            steps.Add(("Сборка", "Компиляция проекта в .lfs"));
        }
        else
        {
            steps.Add(("Проверка", "Проверка файла .lfs"));
            steps.Add(("Связь", "Подключение к контроллеру"));
        }

        steps.Add(("Форматирование", opts.Format
            ? "Форматирование памяти контроллера"
            : "Форматирование отключено — память не трогаем"));
        steps.Add(("Ядро", opts.UpdateKernel
            ? "Обновление ядра контроллера"
            : "Обновление ядра отключено — оставляем текущее"));

        if (request.Operation == LoaderOperation.Flash)
        {
            steps.Add(("Передача", "Передача программы в контроллер"));
            steps.Add(("Проверка", "Сверка записанного с исходным файлом"));
            steps.Add(("Запуск", "Перезапуск контроллера"));
        }
        else
        {
            steps.Add(("Результат", "Сбор собранных файлов в out\\"));
        }

        return steps;
    }

    private static void WriteMarker(LoaderRequest request)
    {
        if (string.IsNullOrEmpty(request.WorkspaceDir)) return;
        try
        {
            var outDir = Path.Combine(request.WorkspaceDir, "out");
            Directory.CreateDirectory(outDir);
            File.WriteAllText(Path.Combine(outDir, StubMarkerFileName),
                "Это файл-памятка заготовки лоадера.\r\n\r\n" +
                "Реальная сборка/загрузка НЕ выполнялась: настоящий лоадер ещё не подключён.\r\n" +
                $"Операция: {request.Operation}\r\n" +
                $"Версия: {request.VersionName}\r\n" +
                $"Источник: {request.SourcePath}\r\n" +
                $"Форматировать: {(request.Options.Format ? "да" : "нет")}\r\n" +
                $"Обновить ядро: {(request.Options.UpdateKernel ? "да" : "нет")}\r\n" +
                $"Порт/адрес: {request.Options.Target}\r\n");
        }
        catch (Exception) { /* памятка — вспомогательная, из-за неё запуск ронять не за что */ }
    }
}

/// <summary>Единственное место, где приложение решает, каким лоадером грузить. Пока всегда
/// заглушка — коллеге, подключающему настоящий лоадер, менять нужно ровно этот метод (и добавить
/// свою реализацию <see cref="IFirmwareLoaderBackend"/>).</summary>
public static class FirmwareLoaderFactory
{
    public static IFirmwareLoaderBackend Create(string? loaderExePath = null, TimeSpan? stepDelay = null)
    {
        var reason = string.IsNullOrWhiteSpace(loaderExePath)
            ? "Путь к лоадеру не задан (Настройки → Общие → «Лоадер»), и сама интеграция ещё не реализована."
            : File.Exists(loaderExePath)
                ? $"Лоадер найден ({loaderExePath}), но интеграция с ним ещё не реализована — см. docs/loader-integration.md."
                : $"По указанному пути лоадер не найден: {loaderExePath}";
        return new StubFirmwareLoaderBackend(reason, stepDelay);
    }
}
