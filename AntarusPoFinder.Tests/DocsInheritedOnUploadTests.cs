using System.Collections.Generic;
using System.Linq;
using AntarusPoFinder.Core.Data;
using AntarusPoFinder.Core.Domain;
using AntarusPoFinder.Tests.TestHelpers;
using Xunit;

namespace AntarusPoFinder.Tests;

/// <summary>Инструкция и карты переходят к новой версии шкафа.
///
/// Срочная жалоба Ильи 23.09.2026: «при загрузке новой версии инструкция не переносится от
/// предыдущей, и карта входов-выходов, и карта Modbus».
///
/// Документы описывают ШКАФ, а не сборку программы: обновили прошивку — шкаф тот же, схема та же,
/// инструкция та же. Без переноса каждая новая версия выходила пустой: на карточке ничего, QR вести
/// некуда, а документы всё это время лежали на диске в папке того же контроллера.
///
/// Берётся каждый документ у самой свежей версии, где он ЕСТЬ, а не «всё у предыдущей»: карту ВВ
/// могли приложить три версии назад, инструкцию — в прошлой, и «взять всё у предыдущей» потеряло бы
/// карту.</summary>
public class DocsInheritedOnUploadTests
{
    private static (int SubtypeId, int ControllerId) Cabinet(Database db)
    {
        var group = db.GetAllEquipmentGroups().First(g => g.Name == "ПЖ");
        var subtype = db.GetSubtypesForGroup(group.Id!.Value).First(s => s.Name == "ХП");
        var mod = db.GetAllModifications().First(m => m.ControllerName == "SMH4");
        return (subtype.Id!.Value, mod.ControllerId);
    }

    private static int Add(Database db, int sw, string instructions = "", string ioMap = "",
        string modbus = "", string status = "active")
    {
        var (subtypeId, controllerId) = Cabinet(db);
        var group = db.GetAllEquipmentGroups().First(g => g.Name == "ПЖ");
        return db.AddFwVersion(new FwVersionRecord
        {
            SubtypeId = subtypeId,
            ControllerId = controllerId,
            EqPrefix = group.Prefix,
            SubPrefix = 1,
            HwVersion = 4,
            SwVersion = sw,
            DtStr = $"2026010{sw}_0000",
            VersionRaw = $"3.1.0004.000{sw}.2026010{sw}_0000",
            Filename = "fw.psl",
            LaunchTypes = new List<string> { "ПЧ" },
            Status = status,
            InstructionsPath = instructions,
            IoMapPath = ioMap,
            ModbusMapPath = modbus,
        });
    }

    [Fact]
    public void EachDocument_ComesFromTheNewestVersionThatHasIt()
    {
        using var file = new TempDb();
        using var db = new Database(file.Path);
        var (subtypeId, controllerId) = Cabinet(db);

        // Карту ВВ приложили давно, инструкцию — недавно, карту Modbus не прикладывали вовсе.
        Add(db, sw: 1, ioMap: @"X:\ПО\ПЖ\ХП\SMH4\Карта ВВ\vv.pdf");
        Add(db, sw: 2, instructions: @"X:\ПО\ПЖ\ХП\SMH4\Инструкция\instr.pdf");

        var docs = db.GetLatestDocsForFirmware(subtypeId, controllerId);

        Assert.Equal(@"X:\ПО\ПЖ\ХП\SMH4\Инструкция\instr.pdf", docs.Instructions);
        Assert.Equal(@"X:\ПО\ПЖ\ХП\SMH4\Карта ВВ\vv.pdf", docs.IoMap);
        Assert.Equal("", docs.ModbusMap);
    }

    /// <summary>Откатанная версия не в счёт: её документы — это ровно то, от чего отказались.</summary>
    [Fact]
    public void RolledBackVersion_IsNotASource()
    {
        using var file = new TempDb();
        using var db = new Database(file.Path);
        var (subtypeId, controllerId) = Cabinet(db);

        Add(db, sw: 1, instructions: @"X:\старая\instr.pdf");
        Add(db, sw: 2, instructions: @"X:\откатанная\instr.pdf", status: "rolled_back");

        Assert.Equal(@"X:\старая\instr.pdf", db.GetLatestDocsForFirmware(subtypeId, controllerId).Instructions);
    }

    /// <summary>Свою же строку источником не считаем — иначе новая версия наследовала бы у себя.</summary>
    [Fact]
    public void TheVersionItself_IsExcluded()
    {
        using var file = new TempDb();
        using var db = new Database(file.Path);
        var (subtypeId, controllerId) = Cabinet(db);

        var only = Add(db, sw: 1, instructions: @"X:\своя\instr.pdf");
        Assert.Equal("", db.GetLatestDocsForFirmware(subtypeId, controllerId, exceptId: only).Instructions);
    }

    /// <summary>Чужой шкаф не источник: документы принадлежат своему подтипу и контроллеру.</summary>
    [Fact]
    public void AnotherCabinet_IsNotASource()
    {
        using var file = new TempDb();
        using var db = new Database(file.Path);

        Add(db, sw: 1, instructions: @"X:\ПЖ\instr.pdf");

        var group = db.GetAllEquipmentGroups().First(g => g.Name == "НГР");
        var other = db.GetSubtypesForGroup(group.Id!.Value).First();
        var mod = db.GetAllModifications().First(m => m.ControllerName == "SMH4");

        Assert.Equal("", db.GetLatestDocsForFirmware(other.Id!.Value, mod.ControllerId).Instructions);
    }
}
