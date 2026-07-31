using System;
using System.Threading;
using System.Threading.Tasks;

namespace AntarusPoFinder.Core.Loader;

/// <summary>Создаёт Automation backend либо недоступное состояние с точной причиной. Имитация
/// успешной загрузки и запуск GUI в качестве запасного пути не используются.</summary>
public static class FirmwareLoaderFactory
{
    public static IFirmwareLoaderBackend Create(string? configuredPath = null)
    {
        var resolved = SegneticsLoaderResolver.Resolve(configuredPath);
        if (resolved is not null) return new SegneticsLoaderBackend(resolved);

        var candidate = SegneticsLoaderResolver.CandidatePath(configuredPath);
        var reason = candidate is null
            ? "В настройках указан неподдерживаемый путь к Segnetics Loader. Укажите папку Loader, " +
              "SegneticsLoader.exe или SegneticsLoader.Automation.exe."
            : $"Segnetics Loader Automation не найден: {candidate}";
        return new UnavailableFirmwareLoaderBackend(reason);
    }
}

internal sealed class UnavailableFirmwareLoaderBackend : IFirmwareLoaderBackend
{
    public UnavailableFirmwareLoaderBackend(string reason) => UnavailableReason = reason;

    public string Name => "Segnetics Loader Automation";

    public string? DisplayVersion => null;

    public bool IsAvailable => false;

    public string? UnavailableReason { get; }

    public Task<LoaderResult> RunAsync(
        LoaderRequest request,
        IProgress<LoaderProgress> progress,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(LoaderResult.Fail(
            UnavailableReason ?? "Segnetics Loader Automation недоступен."));
    }
}
