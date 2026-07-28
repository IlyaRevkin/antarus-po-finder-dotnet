using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace AntarusPoFinder.Core.Loader;

/// <summary>
/// Находит исполняемый файл Segnetics Loader. Приоритет: явно указанный оператором путь
/// (Настройки → Общие → «Лоадер»), затем встроенная копия, которую кладёт рядом с приложением
/// инсталлятор (<c>&lt;папка приложения&gt;\Loader\SegneticsLoader.exe</c>). Благодаря встроенной
/// копии лоадер работает «из коробки» — путь в настройках можно не задавать.
/// </summary>
public static class SegneticsLoaderResolver
{
    public const string LoaderExeName = "SegneticsLoader.exe";
    public const string BundledSubfolder = "Loader";

    /// <summary>Куда инсталлятор кладёт встроенный лоадер — рядом с exe приложения.</summary>
    public static string DefaultBundledPath =>
        Path.Combine(AppContext.BaseDirectory, BundledSubfolder, LoaderExeName);

    /// <summary>Путь к рабочему exe лоадера или <c>null</c>, если не нашли ни по настройке, ни
    /// среди встроенных. Проверяет существование файла, а не только непустоту строки — оператор
    /// мог указать путь, а лоадер потом переехать/удалиться.</summary>
    public static string? Resolve(string? configuredPath)
    {
        var configured = configuredPath?.Trim();
        if (!string.IsNullOrEmpty(configured) && File.Exists(configured)) return configured;

        var bundled = DefaultBundledPath;
        return File.Exists(bundled) ? bundled : null;
    }
}

/// <summary>
/// Реальный backend: отдаёт загрузку встроенному <b>Segnetics Loader</b> (кастомная сборка v2.6.0,
/// поставляется вместе с приложением). Segnetics Loader — это полноценное GUI-приложение (сборка
/// PSL → LFS через SMLogix, заливка LFS/ELF/ZOP в контроллер по SSH/SFTP, обновление ядра,
/// форматирование), у которого нет headless-режима, поэтому интеграция строится не на разборе его
/// вывода, а на запуске его окна с уже подставленным файлом: Loader умеет открываться по .lfs/.psl/
/// .zop, переданному аргументом (та же логика, что у файловых ассоциаций). Оператор доводит загрузку
/// в его окне — там для этого есть всё, чего в этой программе быть не должно.
/// </summary>
public sealed class SegneticsLoaderBackend : IFirmwareLoaderBackend
{
    private readonly string _exePath;

    public SegneticsLoaderBackend(string exePath) => _exePath = exePath;

    public string Name => "Segnetics Loader";
    public bool IsAvailable => File.Exists(_exePath);
    public string? UnavailableReason => IsAvailable ? null : $"Segnetics Loader не найден: {_exePath}";

    /// <summary>Запускает Segnetics Loader, при непустом пути — сразу с открытым файлом. Не ждёт
    /// завершения: это GUI, оператор работает в его окне. Бросает <see cref="Win32Exception"/>,
    /// если exe не удалось стартовать (нет .NET 8 Desktop Runtime и т.п.) — вызывающий сам решает,
    /// как об этом сообщить.</summary>
    public static Process Launch(string exePath, string? filePath)
    {
        var psi = new ProcessStartInfo
        {
            FileName = exePath,
            UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(exePath) ?? Environment.CurrentDirectory,
        };
        if (!string.IsNullOrWhiteSpace(filePath)) psi.ArgumentList.Add(filePath!);
        return Process.Start(psi) ?? throw new InvalidOperationException("Не удалось запустить Segnetics Loader.");
    }

    public Task<LoaderResult> RunAsync(LoaderRequest request, IProgress<LoaderProgress> progress, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!IsAvailable)
            return Task.FromResult(LoaderResult.Fail(UnavailableReason ?? "Segnetics Loader недоступен."));

        var file = request.SourcePath;
        progress.Report(new LoaderProgress(30, "Запуск", $"Открываю Segnetics Loader: {_exePath}"));

        try
        {
            Launch(_exePath, file);
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            return Task.FromResult(LoaderResult.Fail(
                "Не удалось запустить Segnetics Loader. Похоже, не установлен .NET 8 Desktop Runtime (x64). " +
                "Скачать: https://dotnet.microsoft.com/download/dotnet/8.0/runtime\n" +
                $"Подробности: {ex.Message}"));
        }
        catch (Exception ex)
        {
            return Task.FromResult(LoaderResult.Fail($"Не удалось запустить Segnetics Loader: {ex.Message}"));
        }

        progress.Report(new LoaderProgress(100, "Загрузчик открыт",
            string.IsNullOrWhiteSpace(file)
                ? "Segnetics Loader открыт. Выберите файл и выполните загрузку/сборку в его окне."
                : $"Segnetics Loader открыт с файлом: {file}. Выполните загрузку в его окне.",
            LoaderLogLevel.Success));

        return Task.FromResult(LoaderResult.Ok(
            "Segnetics Loader запущен — загрузка/сборка выполняется в его окне."));
    }
}
