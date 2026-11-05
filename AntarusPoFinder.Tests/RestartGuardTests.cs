using System;
using System.Collections.Generic;
using AntarusPoFinder.Core.Services;
using Xunit;

namespace AntarusPoFinder.Tests;

/// <summary>Программа не перезапускается посреди работы с контроллером.
///
/// Жалоба наладчика дословно: «автоматический перезапуск Finder для обновления происходит при
/// заливке прошивки в контроллер, что может привести к окирпичиванию контроллера». Установка
/// обновления закрывает процесс, а вместе с ним обрывается загрузчик, который в этот момент пишет
/// в ПЛК. Цена ошибки здесь — не неудобство, а мёртвый контроллер на объекте.</summary>
public class RestartGuardTests
{
    private static LongOperation Op(LongOperationKind kind, string title = "ТГР 2.0") =>
        new(kind, LongOperationSubject.None, title, DateTime.Now);

    [Theory]
    [InlineData(LongOperationKind.PlcDeploy)]
    [InlineData(LongOperationKind.LfsBuild)]
    [InlineData(LongOperationKind.DiskRebuild)]
    [InlineData(LongOperationKind.LegacyImport)]
    public void Операции_с_недописанным_результатом_запрещают_перезапуск(LongOperationKind kind)
    {
        Assert.True(LongOperationRules.BlocksAppRestart(kind));
        Assert.NotNull(LongOperationRules.RestartBlockReason(new[] { Op(kind) }));
    }

    [Fact]
    public void Очистка_диска_перезапуску_не_мешает()
    {
        // Она удаляет уже ненужное поштучно: обрыв оставляет просто меньше удалённого.
        Assert.False(LongOperationRules.BlocksAppRestart(LongOperationKind.DiskCleanup));
        Assert.Null(LongOperationRules.RestartBlockReason(new[] { Op(LongOperationKind.DiskCleanup) }));
    }

    [Fact]
    public void В_тишине_перезапуск_разрешён()
    {
        Assert.Null(LongOperationRules.RestartBlockReason(Array.Empty<LongOperation>()));
        Assert.Null(LongOperationRules.RestartBlockReason(new List<LongOperation>()));
    }

    [Fact]
    public void Причина_называет_операцию_а_не_отделывается_общими_словами()
    {
        // «Обновление отложено» без причины читается как зависшая программа.
        var reason = LongOperationRules.RestartBlockReason(new[] { Op(LongOperationKind.PlcDeploy, "НГР КПЧ 2.1") });

        Assert.Contains("загрузка в плк", reason);
        Assert.Contains("НГР КПЧ 2.1", reason);
    }

    [Fact]
    public void Реестр_отвечает_тем_же_правилом()
    {
        var registry = new LongOperationRegistry();
        Assert.Null(registry.RestartBlockReason());

        Assert.True(registry.TryBegin(LongOperationKind.PlcDeploy, LongOperationSubject.None,
            "Загрузка в ПЛК: ТГР 2.0", out var lease, out _));
        Assert.NotNull(registry.RestartBlockReason());

        lease!.Dispose();
        // Операция закончилась — отложенное обновление обязано поехать само.
        Assert.Null(registry.RestartBlockReason());
    }
}
