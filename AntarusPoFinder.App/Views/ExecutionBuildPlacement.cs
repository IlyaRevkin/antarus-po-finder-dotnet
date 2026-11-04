using System.Collections.Generic;
using System.Windows;
using AntarusPoFinder.Core.Services;

namespace AntarusPoFinder.App.Views;

/// <summary>Один порядок «положить конфигурацию исполнения» на всех, кто это делает: карточка в
/// выдаче («Ещё» → «Конфигурация исполнения…») и форма загрузки.
///
/// Здесь живёт ровно то, что нельзя написать дважды: вопрос про НЕПУСТУЮ папку исполнения. Молчаливая
/// перезапись — это подмена того, что уедет в шкаф, и если бы у формы загрузки был свой экземпляр
/// этого правила, он разошёлся бы с карточкиным при первой же правке. Само правило (когда спрашивать)
/// задаёт Core — <see cref="FirmwareExecutionBuild.Result.NeedsReplaceConfirmation"/>; здесь только
/// диалог.</summary>
internal static class ExecutionBuildPlacement
{
    public const string DialogTitle = "Конфигурация исполнения";

    /// <summary>Положить файлы, спросив про замену непустой папки. null — человек отказался от
    /// замены: это не ошибка, и говорить ему про неё ещё раз не надо.</summary>
    public static FirmwareExecutionBuild.Result? Place(
        string? versionDir, string? execution, IReadOnlyList<string> files)
    {
        var outcome = FirmwareExecutionBuild.Place(versionDir, execution, files);
        if (outcome is not { Ok: false, NeedsReplaceConfirmation: true }) return outcome;

        if (AppMessageBox.Show(
                outcome.Error + System.Environment.NewLine + System.Environment.NewLine
                + "Заменить их выбранными? Старые файлы этой конфигурации будут удалены.",
                DialogTitle, MessageBoxButton.YesNo, MessageBoxImage.Question,
                MessageBoxResult.No) != MessageBoxResult.Yes) return null;

        return FirmwareExecutionBuild.Place(versionDir, execution, files, replaceExisting: true);
    }
}
