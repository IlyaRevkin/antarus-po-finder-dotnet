using System;
using System.Linq;
using AntarusPoFinder.Core.Services;
using Xunit;

namespace AntarusPoFinder.Tests;

/// <summary>Защита от конфликтов долгих операций. Появилась не «на всякий случай»: пока окно
/// загрузки было модальным, вторую заливку нельзя было запустить физически — программа стояла. Как
/// только окно перестало запирать программу, эта защита пропала вместе с модальностью, и её нужно
/// было сделать заново, уже явно.
///
/// Проверяем ровно то, что раньше обеспечивалось запертым окном: один Segnetics Loader на машину,
/// перестройка диска не соседствует ни с чем, а версию, в папку которой прямо сейчас пишут .lfs,
/// нельзя откатить или удалить.</summary>
public class LongOperationsTests
{
    private static LongOperation Op(LongOperationKind kind, string subject = "", string title = "работа") =>
        new(kind, subject, title, DateTime.Now);

    // ── Правила без реестра ────────────────────────────────────────────────

    [Fact]
    public void FreeMachine_AllowsAnything()
    {
        Assert.Null(LongOperationRules.Refusal(Op(LongOperationKind.PlcDeploy), Array.Empty<LongOperation>()));
        Assert.Null(LongOperationRules.Refusal(Op(LongOperationKind.DiskRebuild), Array.Empty<LongOperation>()));
    }

    /// <summary>Loader один на машину: вторая заливка перехватила бы тот же USB/COM и уронила обе.</summary>
    [Fact]
    public void SecondLoaderOperation_Refused_EvenForAnotherVersion()
    {
        var running = new[] { Op(LongOperationKind.PlcDeploy, LongOperationSubject.Folder(@"C:\ПО\A"), "Загрузка в ПЛК: A") };

        var refusal = LongOperationRules.Refusal(
            Op(LongOperationKind.LfsBuild, LongOperationSubject.Folder(@"C:\ПО\B"), "Сборка LFS: B"), running);

        Assert.NotNull(refusal);
        Assert.Contains("Segnetics Loader уже занят", refusal);
        Assert.Contains("Загрузка в ПЛК: A", refusal);
    }

    /// <summary>Перестройка раскладки двигает файлы по всему дереву — под ней не должно идти ничего.</summary>
    [Fact]
    public void WhileDiskRebuildRuns_EverythingElseRefused()
    {
        var running = new[] { Op(LongOperationKind.DiskRebuild, title: "Перестройка структуры диска") };

        var refusal = LongOperationRules.Refusal(Op(LongOperationKind.PlcDeploy, "folder:X", "Загрузка в ПЛК: X"), running);

        Assert.NotNull(refusal);
        Assert.Contains("переносит файлы по всему диску", refusal);
    }

    [Fact]
    public void DiskRebuild_RefusedWhileAnythingElseRuns()
    {
        var running = new[] { Op(LongOperationKind.LfsBuild, "folder:X", "Сборка LFS: X") };

        var refusal = LongOperationRules.Refusal(Op(LongOperationKind.DiskRebuild, title: "Перестройка"), running);

        Assert.NotNull(refusal);
        Assert.Contains("Сборка LFS: X", refusal);
    }

    [Fact]
    public void TwoDiskWideOperations_DoNotOverlap()
    {
        var running = new[] { Op(LongOperationKind.DiskCleanup, title: "Очистка диска") };

        Assert.NotNull(LongOperationRules.Refusal(Op(LongOperationKind.DiskRebuild, title: "Перестройка"), running));
    }

    /// <summary>Одна и та же работа над той же версией — самый частый двойной клик.</summary>
    [Fact]
    public void SameKindSameSubject_SaysItIsAlreadyRunning()
    {
        var subject = LongOperationSubject.Folder(@"C:\ПО\ПЖ\2.0\1.5");
        var running = new[] { Op(LongOperationKind.LfsBuild, subject, "Сборка LFS: 1.5") };

        var refusal = LongOperationRules.Refusal(Op(LongOperationKind.LfsBuild, subject, "Сборка LFS: 1.5"), running);

        Assert.Equal("«Сборка LFS» для этой версии уже идёт.", refusal);
    }

    /// <summary>Операции без привязки к версии друг друга по «предмету» не блокируют — иначе две
    /// независимые чистки разных папок отказывали бы друг другу без причины.</summary>
    [Fact]
    public void EmptySubject_DoesNotCollideWithEmptySubject()
    {
        var running = new[] { Op(LongOperationKind.PlcDeploy, LongOperationSubject.None, "Загрузка в ПЛК") };

        // Отказ будет, но по причине занятого Loader, а не «та же версия».
        var refusal = LongOperationRules.Refusal(
            Op(LongOperationKind.LfsBuild, LongOperationSubject.None, "Сборка"), running);
        Assert.Contains("Segnetics Loader", refusal);
    }

    // ── Ключ версии ────────────────────────────────────────────────────────

    /// <summary>Один и тот же каталог, записанный по-разному (регистр, хвостовой слеш, точка),
    /// обязан давать один ключ: карточка поиска и модерация приходят с разными формами пути.</summary>
    [Fact]
    public void Folder_NormalisesCaseSlashAndDots()
    {
        var a = LongOperationSubject.Folder(@"C:\ПО\ПЖ\Контроллер\1.5");
        var b = LongOperationSubject.Folder(@"c:\по\пж\контроллер\1.5\");
        var c = LongOperationSubject.Folder(@"C:\ПО\ПЖ\Контроллер\.\1.5");

        Assert.Equal(a, b);
        Assert.Equal(a, c);
    }

    [Fact]
    public void Folder_EmptyStaysEmpty()
    {
        Assert.Equal(LongOperationSubject.None, LongOperationSubject.Folder(null));
        Assert.Equal(LongOperationSubject.None, LongOperationSubject.Folder("   "));
    }

    // ── Занятая версия ─────────────────────────────────────────────────────

    [Fact]
    public void SubjectBusyReason_NamesTheOperation()
    {
        var subject = LongOperationSubject.Folder(@"C:\ПО\A\1.5");
        var running = new[] { Op(LongOperationKind.LfsBuild, subject, "Сборка LFS: 1.5") };

        var reason = LongOperationRules.SubjectBusyReason(subject, running);

        Assert.NotNull(reason);
        Assert.Contains("Сборка LFS: 1.5", reason);
    }

    [Fact]
    public void SubjectBusyReason_OtherVersionIsFree()
    {
        var running = new[] { Op(LongOperationKind.LfsBuild, LongOperationSubject.Folder(@"C:\ПО\A\1.5"), "Сборка") };

        Assert.Null(LongOperationRules.SubjectBusyReason(LongOperationSubject.Folder(@"C:\ПО\A\1.6"), running));
    }

    // ── Отмена ─────────────────────────────────────────────────────────────

    [Fact]
    public void PlainDeploy_IsSafeToCancel()
    {
        Assert.True(LongOperationRules.SafeToCancel(LongOperationKind.PlcDeploy, formatsController: false));
        Assert.Null(LongOperationRules.CancelWarning(LongOperationKind.PlcDeploy, formatsController: false));
    }

    /// <summary>Форматирование прервать нельзя — и об этом надо СКАЗАТЬ, а не молча погасить кнопку.</summary>
    [Fact]
    public void FormattingDeploy_WarnsInsteadOfStayingSilent()
    {
        Assert.False(LongOperationRules.SafeToCancel(LongOperationKind.PlcDeploy, formatsController: true));

        var warning = LongOperationRules.CancelWarning(LongOperationKind.PlcDeploy, formatsController: true);
        Assert.NotNull(warning);
        Assert.Contains("форматирование", warning);
        Assert.Contains("без рабочей прошивки", warning);
    }

    /// <summary>Сборка LFS к контроллеру не подключается — галка форматирования на неё не влияет.</summary>
    [Fact]
    public void LfsBuild_IsAlwaysSafeToCancel()
    {
        Assert.True(LongOperationRules.SafeToCancel(LongOperationKind.LfsBuild, formatsController: true));
    }

    /// <summary>Подсказка про отмену есть ВСЕГДА — и когда обрывать безопасно тоже. Молчание в
    /// безопасном случае оператор читает как «а вдруг нельзя» и просто ждёт зря.</summary>
    [Fact]
    public void CancelHint_IsNeverEmpty()
    {
        foreach (var kind in Enum.GetValues<LongOperationKind>())
        foreach (var formats in new[] { false, true })
            Assert.False(string.IsNullOrWhiteSpace(LongOperationRules.CancelHint(kind, formats)));
    }

    [Fact]
    public void CancelHint_ForFormattingDeploy_IsTheWarning()
    {
        Assert.Equal(
            LongOperationRules.CancelWarning(LongOperationKind.PlcDeploy, formatsController: true),
            LongOperationRules.CancelHint(LongOperationKind.PlcDeploy, formatsController: true));
    }

    [Fact]
    public void CancelHint_ForPlainDeploy_PromisesTheOldFirmwareStays()
    {
        var hint = LongOperationRules.CancelHint(LongOperationKind.PlcDeploy, formatsController: false);

        Assert.Contains("Остановить можно", hint);
        Assert.Contains("прошивкой, что была", hint);
    }

    [Fact]
    public void CancelConfirmation_AsksAndExplains()
    {
        var text = LongOperationRules.CancelConfirmation("Загрузка в ПЛК: ТГР 2.0");

        Assert.Contains("Загрузка в ПЛК: ТГР 2.0", text);
        Assert.Contains("Всё равно остановить?", text);
    }

    // ── Реестр ─────────────────────────────────────────────────────────────

    [Fact]
    public void Registry_BeginThenReleaseFreesTheResource()
    {
        var registry = new LongOperationRegistry();
        var subject = LongOperationSubject.Folder(@"C:\ПО\A\1.5");

        Assert.True(registry.TryBegin(LongOperationKind.PlcDeploy, subject, "Загрузка в ПЛК: 1.5", out var lease, out _));
        Assert.True(registry.IsRunning(LongOperationKind.PlcDeploy));
        Assert.False(registry.TryBegin(LongOperationKind.PlcDeploy, subject, "Загрузка в ПЛК: 1.5", out _, out var refusal));
        Assert.NotEmpty(refusal);

        lease!.Dispose();

        Assert.False(registry.AnyRunning);
        Assert.True(registry.TryBegin(LongOperationKind.PlcDeploy, subject, "Загрузка в ПЛК: 1.5", out _, out _));
    }

    /// <summary>Отказ не должен ничего занимать: иначе неудачная попытка запустить вторую заливку
    /// навсегда «занимала» бы Loader и первая, закончившись, не освободила бы его.</summary>
    [Fact]
    public void Registry_RefusedAttemptDoesNotRegister()
    {
        var registry = new LongOperationRegistry();
        registry.TryBegin(LongOperationKind.PlcDeploy, "folder:A", "Первая", out var first, out _);

        registry.TryBegin(LongOperationKind.LfsBuild, "folder:B", "Вторая", out var second, out _);

        Assert.Null(second);
        Assert.Single(registry.Active);
        first!.Dispose();
        Assert.False(registry.AnyRunning);
    }

    /// <summary>Двойной Dispose приходит сам собой (using внутри блока, закрывшегося по исключению);
    /// снять чужую, ещё идущую операцию он не должен.</summary>
    [Fact]
    public void Registry_DoubleDispose_DoesNotFreeSomebodyElse()
    {
        var registry = new LongOperationRegistry();
        registry.TryBegin(LongOperationKind.DiskCleanup, "", "Очистка", out var cleanup, out _);
        cleanup!.Dispose();
        cleanup.Dispose();

        registry.TryBegin(LongOperationKind.DiskRebuild, "", "Перестройка", out var rebuild, out _);
        Assert.NotNull(rebuild);
        Assert.Single(registry.Active);
    }

    /// <summary>Две операции одного вида над одной версией с совпавшей отметкой времени — record
    /// сравнивается по значению, и снятие первой не должно уносить вторую. Реестр их одновременно
    /// не пустит, поэтому проверяем сам механизм снятия на паре, где предмет разный.</summary>
    [Fact]
    public void Registry_ReleasingOneKeepsTheOther()
    {
        var registry = new LongOperationRegistry();
        registry.TryBegin(LongOperationKind.PlcDeploy, "folder:A", "Одинаковое имя", out var first, out _);
        first!.Dispose();
        registry.TryBegin(LongOperationKind.PlcDeploy, "folder:A", "Одинаковое имя", out var second, out _);

        first.Dispose();

        Assert.True(registry.AnyRunning);
        second!.Dispose();
        Assert.False(registry.AnyRunning);
    }

    [Fact]
    public void Registry_ChangedFiresOnBeginAndEnd()
    {
        var registry = new LongOperationRegistry();
        var fired = 0;
        registry.Changed += () => fired++;

        registry.TryBegin(LongOperationKind.LfsBuild, "folder:A", "Сборка", out var lease, out _);
        lease!.Dispose();

        Assert.Equal(2, fired);
    }

    [Fact]
    public void Registry_SubjectBusyReason_TracksActive()
    {
        var registry = new LongOperationRegistry();
        var subject = LongOperationSubject.Folder(@"C:\ПО\A\1.5");
        registry.TryBegin(LongOperationKind.LfsBuild, subject, "Сборка LFS: 1.5", out var lease, out _);

        Assert.NotNull(registry.SubjectBusyReason(subject));
        lease!.Dispose();
        Assert.Null(registry.SubjectBusyReason(subject));
    }

    [Fact]
    public void Captions_AreHumanReadable()
    {
        Assert.Equal("Загрузка в ПЛК", LongOperationRules.Caption(LongOperationKind.PlcDeploy));
        Assert.Equal("Сборка LFS", LongOperationRules.Caption(LongOperationKind.LfsBuild));
        Assert.All(
            Enum.GetValues<LongOperationKind>().Select(LongOperationRules.Caption),
            caption => Assert.False(string.IsNullOrWhiteSpace(caption)));
    }
}
