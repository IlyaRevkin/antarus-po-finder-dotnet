using System;
using System.Linq;
using AntarusPoFinder.Core.Data;
using AntarusPoFinder.Core.Domain;
using AntarusPoFinder.Tests.TestHelpers;
using Xunit;

namespace AntarusPoFinder.Tests;

/// <summary>Исправленная подсказка «какой файл открывать» обязана доехать до коллег.
///
/// Жалоба Ильи 30.09.2026: «опять с кинко какая-то херня, у меня нормально работает, а у некоторых
/// коллег то DPJ теряется то ещё что-то».
///
/// «У меня работает, а у коллег нет» — признак того, что исправление не уезжает с машины. Подсказки
/// об исполняемом файле (executable_hint для ПЛК и hmi_executable_hint для панели) в общий конфиг
/// ВЫГРУЖАЮТСЯ и на приёме применяются, но по правилу «заполняем только пустое»: если у коллеги в
/// поле уже что-то лежит — пусть неверное, подобранное автоопределением, — приехавшее исправление
/// его не перепишет никогда.
///
/// Для путей к документам это правило верное: там каждый сам знает, где у него лежат файлы. А здесь
/// значение ровно одно на всех — это ИМЯ ФАЙЛА ВНУТРИ папки версии, одинаковой у всех машин, и
/// правит его человек именно затем, чтобы починить всем. У KINCO это и проявляется: в папке лежит
/// несколько .dpj, автоопределение выбирает не тот, Илья указывает нужный руками — и исправление
/// остаётся только у него.</summary>
public class OpenFileHintReachesColleaguesTests : IDisposable
{
    private readonly TempDb _fileA = new();
    private readonly TempDb _fileB = new();
    private readonly Database _a;
    private readonly Database _b;

    public OpenFileHintReachesColleaguesTests()
    {
        _a = new Database(_fileA.Path);
        _b = new Database(_fileB.Path);
    }

    public void Dispose()
    {
        _a.Dispose(); _b.Dispose();
        _fileA.Dispose(); _fileB.Dispose();
    }

    /// <summary>Заводит одну и ту же прошивку на обеих машинах — через обмен, чтобы у строк совпал
    /// sync_id и приёмник опознал её как ту же самую, а не завёл вторую.</summary>
    private (int idA, int idB) SeedSameFirmwareOnBoth()
    {
        var g = _a.GetAllEquipmentGroups().First(x => x.Name == "ПЖ");
        var s = _a.GetSubtypesForGroup(g.Id!.Value).First();
        var m = _a.GetAllModifications().First(x => x.ControllerName == "SMH4");

        var idA = _a.AddFwVersion(new FwVersionRecord
        {
            SubtypeId = s.Id!.Value, ControllerId = m.ControllerId,
            EqPrefix = g.Prefix, SubPrefix = s.Prefix, HwVersion = m.HwVersion,
            SwVersion = 1, DtStr = "20260101_0000", VersionRaw = "1.1.0001.0001.20260101_0000",
            Filename = "kinco.dpj", Status = "active",
        });

        _b.ImportHierarchyData(_a.ExportHierarchyData());
        var idB = _b.GetAllFwVersionsWithNames(includeArchived: true)
            .Single(v => v.VersionRaw == "1.1.0001.0001.20260101_0000").Id!.Value;
        return (idA, idB);
    }

    /// <summary>Главное. У коллеги в поле уже лежит НЕ ТОТ файл — именно так и бывает после
    /// автоопределения, когда в папке версии несколько .dpj. Исправление с машины Ильи обязано его
    /// перебить, иначе кнопка «Открыть HMI» у коллеги навсегда останется сломанной.</summary>
    [Fact]
    public void Исправленный_файл_hmi_перебивает_неверный_у_коллеги()
    {
        var (idA, idB) = SeedSameFirmwareOnBoth();

        // У коллеги автоопределение выбрало не тот файл.
        _b.UpdateFwVersion(idB, hmiExecutableHint: "temp/УПД_mk070_старый.dpj");
        // Илья указал правильный руками.
        _a.UpdateFwVersion(idA, hmiExecutableHint: "УПД_mk070_v8-26.dpj");

        _b.ImportHierarchyData(_a.ExportHierarchyData());

        var atColleague = _b.GetAllFwVersionsWithNames(includeArchived: true).Single(v => v.Id == idB);
        Assert.Equal("УПД_mk070_v8-26.dpj", atColleague.HmiExecutableHint);
    }

    /// <summary>То же самое для файла прошивки ПЛК: у KINCO и там выбор неоднозначен.</summary>
    [Fact]
    public void Исправленный_файл_плк_перебивает_неверный_у_коллеги()
    {
        var (idA, idB) = SeedSameFirmwareOnBoth();

        _b.UpdateFwVersion(idB, executableHint: "не_тот.dpj");
        _a.UpdateFwVersion(idA, executableHint: "правильный.dpj");

        _b.ImportHierarchyData(_a.ExportHierarchyData());

        var atColleague = _b.GetAllFwVersionsWithNames(includeArchived: true).Single(v => v.Id == idB);
        Assert.Equal("правильный.dpj", atColleague.ExecutableHint);
    }

    /// <summary>Обратная сторона: ПУСТОЕ входящее значение ничего не стирает. Машина, где подсказку
    /// ещё не трогали, не должна сносить исправление, сделанное на другой, — иначе починка жила бы
    /// до первого обмена с отстающим коллегой.</summary>
    [Fact]
    public void Пустое_входящее_значение_не_стирает_исправление()
    {
        var (idA, idB) = SeedSameFirmwareOnBoth();

        // У Ильи подсказка есть, у коллеги её нет вовсе — и коллега отправляет СВОЙ снимок.
        _a.UpdateFwVersion(idA, hmiExecutableHint: "УПД_mk070_v8-26.dpj");

        _a.ImportHierarchyData(_b.ExportHierarchyData());

        var atIlia = _a.GetAllFwVersionsWithNames(includeArchived: true).Single(v => v.Id == idA);
        Assert.Equal("УПД_mk070_v8-26.dpj", atIlia.HmiExecutableHint);
    }
}
