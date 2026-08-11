using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using AntarusPoFinder.Core.Services;

namespace AntarusPoFinder.Core.Loader;

/// <summary>Надо ли вообще что-то собирать у этой версии.</summary>
public enum LfsConversionNeed
{
    /// <summary>Есть .psl без .lfs и есть куда положить результат — можно собирать.</summary>
    Build,

    /// <summary>Собранный .lfs у версии уже лежит — делать нечего.</summary>
    AlreadyPresent,

    /// <summary>Исходника .psl нет (не Segnetics, или выложили только .lfs).</summary>
    NoSource,

    /// <summary>Папка версии на сетевом диске недоступна: собрать-то можно, но положить результат
    /// туда, где его увидят коллеги, — нельзя, а ради одного своего кэша гонять сборку незачем.</summary>
    Unreachable,
}

/// <summary>Что и куда собирать.</summary>
public sealed record LfsConversionPlan(string PslPath, LfsPublishPlan Publish)
{
    /// <summary>Имя результата — по имени исходника, чтобы пара .psl/.lfs в папке версии читалась
    /// глазами как одна прошивка.</summary>
    public string OutputFileName => LoaderFiles.LfsNameFor(PslPath);
}

public sealed record LfsConversionDecision(LfsConversionNeed Need, string Message, LfsConversionPlan? Plan);

public enum LfsConversionStatus { Built, Skipped, Unavailable, Failed, Cancelled }

public sealed record LfsConversionResult(
    LfsConversionStatus Status,
    string Message,
    IReadOnlyList<string> Published,
    IReadOnlyList<string> Warnings)
{
    public bool Success => Status is LfsConversionStatus.Built or LfsConversionStatus.Skipped;
}

/// <summary>Сборка .psl → .lfs ОТДЕЛЬНО от заливки в контроллер (действие <c>build</c> Automation
/// API, см. docs/loader/LOADER_AUTOMATION_API.md — к ПЛК не подключается, нужен только изолированный
/// SMLogix на машине).
///
/// Зачем: программист выкладывает исходник .psl, а наладчику на другой машине нужен готовый .lfs
/// «прямо в поиске». Раньше .lfs собирался только как побочный эффект заливки в ПЛК и оседал в
/// локальной рабочей области заливавшего — на диск он не попадал, и следующий наладчик снова видел
/// один .psl. Здесь сборка запускается сама по себе, а результат уезжает В ПАПКУ ВЕРСИИ НА СЕТЕВОМ
/// ДИСКЕ (см. <see cref="LfsPublisher"/>), где его увидят все.</summary>
public static class LfsConversionService
{
    /// <summary>Решение по версии: смотрим ровно те же папки, по которым карточка поиска считает
    /// флаги LFS/PSL, чтобы «собрать нечего» и «в карточке LFS ✓» не расходились.
    /// networkFolder — папка версии на диске (FwVersionRecord.DiskPath / HierarchyResult.FirmwareDir),
    /// localFolder — её копия в кэше, executableHint — выбранный в модерации файл прошивки.</summary>
    public static LfsConversionDecision Decide(string? networkFolder, string? localFolder, string? executableHint)
    {
        var publish = LfsPublisher.Plan(networkFolder, localFolder);
        if (publish.NetworkFolder is null)
        {
            return new LfsConversionDecision(LfsConversionNeed.Unreachable,
                string.IsNullOrWhiteSpace(networkFolder)
                    ? "У этой версии не записана папка на сетевом диске — собранный LFS некуда положить."
                    : $"Папка версии на сетевом диске недоступна: {networkFolder}",
                null);
        }

        // Смотрим ОБА места сразу — «Прошивка\» перестроенной версии и корень папки версии. Класть
        // результат мы будем в первое (см. LfsPublisher.Plan), но искать уже лежащее нужно в обоих:
        // .lfs, собранный до перестройки диска или прежней версией программы, лежит в корне, и не
        // увидев его, кнопка предлагала бы собрать заново то, что уже есть.
        var lookIn = VersionLayout.FirmwareFolders(networkFolder);

        if (LoaderFiles.FindIn(lookIn, LoaderFiles.LfsExtension) is { } existing)
        {
            return new LfsConversionDecision(LfsConversionNeed.AlreadyPresent,
                $"У версии уже есть собранный LFS: {Path.GetFileName(existing)}", null);
        }

        // Исходник берём именно с сетевого диска, а не из локального кэша: собранный файл ляжет
        // рядом с ним, и собирать его из устаревшей локальной копии значило бы выложить коллегам
        // .lfs, не соответствующий лежащему рядом .psl.
        var psl = LoaderFiles.ResolvePreferHint(lookIn, executableHint, LoaderFiles.PslExtension);
        if (psl is null)
        {
            return new LfsConversionDecision(LfsConversionNeed.NoSource,
                "В папке версии на диске нет исходника .psl — собирать нечего.", null);
        }

        return new LfsConversionDecision(LfsConversionNeed.Build, "", new LfsConversionPlan(psl, publish));
    }

    /// <summary>Собирает .lfs локально и публикует результат. Ошибка публикации в одну из папок —
    /// предупреждение, а не провал: собранный файл уже есть, и терять всю сборку из-за отвалившейся
    /// шары незачем (повторная попытка — по той же кнопке).</summary>
    public static async Task<LfsConversionResult> BuildAndPublishAsync(
        IFirmwareLoaderBackend backend,
        LfsConversionPlan plan,
        string workspaceRoot,
        string versionName,
        IProgress<LoaderProgress> progress,
        CancellationToken cancellationToken,
        Action<LoaderWorkspace>? workspaceCreated = null)
    {
        ArgumentNullException.ThrowIfNull(backend);
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(progress);

        if (!backend.IsAvailable)
        {
            return new LfsConversionResult(LfsConversionStatus.Unavailable,
                backend.UnavailableReason ?? "Segnetics Loader Automation недоступен.",
                Array.Empty<string>(), Array.Empty<string>());
        }

        LoaderWorkspace workspace;
        string localSource;
        try
        {
            workspace = LoaderWorkspace.Create(workspaceRoot, versionName);
            workspaceCreated?.Invoke(workspace);
            localSource = await Task.Run(() => workspace.Import(plan.PslPath), cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return Cancelled();
        }
        catch (Exception ex)
        {
            return new LfsConversionResult(LfsConversionStatus.Failed,
                $"Не удалось подготовить локальную копию исходника: {ex.Message}",
                Array.Empty<string>(), Array.Empty<string>());
        }

        progress.Report(new LoaderProgress(-1, "Сборка", $"Локальная копия исходника: {localSource}"));

        var outputPath = Path.Combine(workspace.OutputDir, plan.OutputFileName);
        var request = new LoaderRequest
        {
            Operation = LoaderOperation.Build,
            SourcePath = localSource,
            WorkspaceDir = workspace.Dir,
            OutputPath = outputPath,
            VersionName = versionName,
        };

        LoaderResult result;
        try
        {
            result = await backend.RunAsync(request, progress, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return Cancelled();
        }

        if (!result.Success)
            return new LfsConversionResult(LfsConversionStatus.Failed, result.Message,
                Array.Empty<string>(), Array.Empty<string>());

        if (!File.Exists(outputPath))
            return new LfsConversionResult(LfsConversionStatus.Failed,
                "Segnetics Loader сообщил об успешной сборке, но файл LFS не появился.",
                Array.Empty<string>(), Array.Empty<string>());

        var publish = await Task.Run(() => LfsPublisher.PublishAll(outputPath, plan.Publish)).ConfigureAwait(false);
        if (!publish.AnyPublished)
        {
            return new LfsConversionResult(LfsConversionStatus.Failed,
                "LFS собран, но его не удалось сохранить ни в одну папку версии.",
                publish.Published, publish.Warnings);
        }

        return new LfsConversionResult(LfsConversionStatus.Built,
            $"LFS собран и сохранён: {string.Join(", ", publish.Published)}",
            publish.Published, publish.Warnings);
    }

    private static LfsConversionResult Cancelled() => new(
        LfsConversionStatus.Cancelled, "Сборка отменена.", Array.Empty<string>(), Array.Empty<string>());
}
