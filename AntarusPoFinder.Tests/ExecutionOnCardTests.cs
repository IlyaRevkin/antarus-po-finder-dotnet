using AntarusPoFinder.Core.Domain;
using AntarusPoFinder.Core.Services;
using Xunit;

namespace AntarusPoFinder.Tests;

/// <summary>Исполнение доезжает до карточки поиска.
///
/// Жалоба Ильи 24.09.2026: «нужно, чтобы не номер конфигурации писал, а в чём суть исполнения. Если
/// конфигурация 1 — ничего не написано, если вторая — написано „2 конфигурация". У меня 1-я это
/// 2 насоса, 2-я это 3 и более насосов, и мне на карточке важен не номер, а количество насосов».
///
/// Причина была не в подписи, а в том, что исполнение до карточки вообще не доходило: результат
/// поиска его не нёс. Показывался только номер конфигурации — то есть как раз то, что наладчику
/// ничего не говорит.</summary>
public class ExecutionOnCardTests
{
    [Fact]
    public void TheExecution_ReachesTheSearchResult()
    {
        var row = new FwVersionRecord
        {
            VersionRaw = "3.1.0004.0002.20260101_0000",
            Execution = "3 и более насосов",
            ConfigName = "2 конфигурация",
        };

        var result = SearchService.ToHierarchyResult(row);

        Assert.Equal("3 и более насосов", result.Execution);
        // Конфигурацию не выбрасываем: она про другое — вариант одной и той же прошивки.
        Assert.Equal("2 конфигурация", result.ConfigName);
    }

    /// <summary>У обычной прошивки поле пустое, и карточка ведёт себя как раньше.</summary>
    [Fact]
    public void PlainFirmware_HasNoExecution()
    {
        var result = SearchService.ToHierarchyResult(new FwVersionRecord { VersionRaw = "3.1.0004.0001" });
        Assert.Equal("", result.Execution);
    }
}
