using System;
using System.Linq;
using AntarusPoFinder.Core.Domain;
using AntarusPoFinder.Core.Services;
using Xunit;

namespace AntarusPoFinder.Tests;

/// <summary>ОСМОТР СТАРОЙ МОДЕЛИ ИСПОЛНЕНИЙ (временный инструмент, см. LegacyExecutionScan).
///
/// Главное, что здесь проверяется: осмотр ПРЕДПОЛАГАЕТ и ничего не делает. Группа-кандидат — это
/// «похоже на исполнения одной прошивки», а не «это одна прошивка»: у записей разные номера и
/// разные файлы, и свести их может только человек.</summary>
public class LegacyExecutionScanTests
{
    private static FwVersionRecord Rec(int id, int sw, string execution, string disk = @"\\srv\ПО\x",
        string status = "active", int hw = 4, int subtype = 1, int controller = 2) => new()
    {
        Id = id, SubtypeId = subtype, ControllerId = controller, HwVersion = hw, SwVersion = sw,
        VersionRaw = $"1.1.{hw:0000}.{sw:0000}", Execution = execution, DiskPath = disk, Status = status,
        GroupName = "НГР", SubtypeName = "ПЖ", CtrlName = "SMH4",
    };

    /// <summary>Записи одного шкафа с разными номерами, где исполнение заполнено, собираются в
    /// группу-кандидат. Обычная линейка версий без исполнений — не собирается: сводить там нечего.</summary>
    [Fact]
    public void Кандидатом_становится_только_группа_с_исполнением()
    {
        var withExecution = new[] { Rec(1, 5, "3 насоса"), Rec(2, 6, "2 насоса") };
        var plainLine = new[] { Rec(3, 1, "", subtype: 9), Rec(4, 2, "", subtype: 9) };

        var result = LegacyExecutionScan.Scan(withExecution.Concat(plainLine), _ => true);

        Assert.Single(result.Candidates);
        Assert.Equal(2, result.Candidates[0].Members.Count);
        // Подсказка — самый свежий номер, и это именно подсказка: решает человек.
        Assert.Equal(6, result.Candidates[0].Suggested!.SwVersion);
    }

    /// <summary>Откатанные и ОПЦ в кандидаты не идут: у первых «версии не должно было быть», вторые —
    /// разовые сборки под конкретный шкаф, исполнений у них не бывает.</summary>
    [Fact]
    public void Откатанные_и_ОПЦ_в_осмотр_не_попадают()
    {
        var rolled = Rec(1, 5, "3 насоса", status: FwStatus.RolledBack);
        var opc = Rec(2, 6, "2 насоса");
        opc.IsOpc = true;
        var live = Rec(3, 7, "3 насоса");

        var result = LegacyExecutionScan.Scan(new[] { rolled, opc, live }, _ => true);

        Assert.Empty(result.Candidates);          // живая запись осталась одна — группы нет
        Assert.Empty(result.LegacyLayout);
    }

    /// <summary>Версии в режиме совместимости перечисляются отдельно: завести в них исполнение
    /// негде, папка исполнения живёт внутри «Прошивка\».</summary>
    [Fact]
    public void Версии_в_режиме_совместимости_перечислены_отдельно()
    {
        var old = Rec(1, 5, "", disk: @"\\srv\ПО\старая");
        var rebuilt = Rec(2, 6, "", disk: @"\\srv\ПО\новая");

        var result = LegacyExecutionScan.Scan(new[] { old, rebuilt },
            dir => dir.EndsWith("новая", StringComparison.Ordinal));

        Assert.Single(result.LegacyLayout);
        Assert.Equal(1, result.LegacyLayout[0].FwVersionId);
    }

    /// <summary>Отчёт говорит прямым текстом, что осмотр ничего не менял, — это и есть граница между
    /// «посмотреть» и «сделать».</summary>
    [Fact]
    public void Отчёт_называет_себя_осмотром()
    {
        var result = LegacyExecutionScan.Scan(new[] { Rec(1, 5, "3 насоса"), Rec(2, 6, "2 насоса") }, _ => true);
        var report = LegacyExecutionScan.Report(result, new DateTime(2026, 11, 4, 10, 0, 0));

        Assert.Contains("НИ ОДИН ФАЙЛ", report);
        Assert.Contains("3 насоса", report);
        Assert.Contains("Предложение", report);
    }
}
