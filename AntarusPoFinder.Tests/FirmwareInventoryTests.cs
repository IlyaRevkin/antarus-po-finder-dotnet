using System.Collections.Generic;
using AntarusPoFinder.Core.Services;
using Xunit;

namespace AntarusPoFinder.Tests;

/// <summary>Сводка по базе прошивок: цифры, по которым можно принимать решения.
///
/// Тикет говорит «много откатанных и не все рабочие», и спорить об этом бессмысленно, пока никто не
/// видит чисел. Здесь проверяется, что числа честные — в первую очередь что откат и выключение
/// считаются порознь (это разные вещи: откат освобождает номер ПО, выключение — нет) и что
/// недоступный диск не превращается в отчёт «пропало всё».</summary>
public class FirmwareInventoryTests
{
    [Fact]
    public void Откат_и_выключение_считаются_порознь()
    {
        var statuses = new List<string?> { null, "active", FwStatus.RolledBack, FwStatus.Disabled, FwStatus.Disabled };
        var released = new List<bool> { true, true, true, true, true };

        var inventory = FirmwareInventory.Count(statuses, released, _ => null);

        Assert.Equal(5, inventory.Total);
        Assert.Equal(2, inventory.Active);
        Assert.Equal(1, inventory.RolledBack);
        Assert.Equal(2, inventory.Disabled);
    }

    [Fact]
    public void Недоступный_диск_не_считается_пропажей()
    {
        var statuses = new List<string?> { null, null, null };
        var released = new List<bool> { true, true, true };

        // null — «проверить было нечем»: путь пуст или шара отвалилась.
        var inventory = FirmwareInventory.Count(statuses, released, _ => null);

        Assert.Equal(0, inventory.FilesMissing);
        Assert.Equal(0, inventory.FilesChecked);
        Assert.Equal(3, inventory.FilesUnchecked);
        Assert.DoesNotContain("файл не найден", inventory.Summary());
    }

    [Fact]
    public void Пропавший_файл_попадает_в_сводку()
    {
        var statuses = new List<string?> { null, null };
        var released = new List<bool> { true, true };

        var inventory = FirmwareInventory.Count(statuses, released, i => i == 0);

        Assert.Equal(2, inventory.FilesChecked);
        Assert.Equal(1, inventory.FilesMissing);
        Assert.Contains("файл не найден у 1", inventory.Summary());
    }

    [Fact]
    public void Невыпущенные_считаются_отдельно_от_статуса()
    {
        // Модерация — это НЕ статус версии: прошивка может ждать модерации и быть в работе.
        var statuses = new List<string?> { null, null };
        var released = new List<bool> { true, false };

        var inventory = FirmwareInventory.Count(statuses, released, _ => null);

        Assert.Equal(2, inventory.Active);
        Assert.Equal(1, inventory.Unreleased);
        Assert.Contains("ждут модерации 1", inventory.Summary());
    }

    [Fact]
    public void В_чистой_базе_сводка_не_пугает()
    {
        var inventory = FirmwareInventory.Count(new List<string?>(), new List<bool>(), _ => null);
        Assert.Equal("всего версий 0, в работе 0", inventory.Summary());
    }
}
