using System;
using System.Linq;
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

        // Не нашлось НИГДЕ — только тогда это ошибка. Перечисляем, где искали: «не найден» без
        // единого пути ничего наладчику не объясняет, а «укажите путь в настройках» вводит в
        // заблуждение, когда путь как раз указан, просто по нему уже ничего нет.
        var searched = SegneticsLoaderResolver.Candidates(configuredPath);
        var reason = "Segnetics Loader Automation не найден. Искали:\n" +
                     string.Join("\n", searched.Select(p => "• " + p)) +
                     "\n\nПереустановите программу (встроенный Loader ставится вместе с ней) или укажите " +
                     "путь к Loader в «Настройки → Лоадер».";
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
