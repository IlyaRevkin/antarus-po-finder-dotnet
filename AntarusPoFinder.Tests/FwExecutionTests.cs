using System.Collections.Generic;
using System.Linq;
using AntarusPoFinder.Core.Data;
using AntarusPoFinder.Core.Domain;
using AntarusPoFinder.Core.Services;
using AntarusPoFinder.Tests.TestHelpers;
using Xunit;

namespace AntarusPoFinder.Tests;

/// <summary>ИСПОЛНЕНИЕ прошивки: несколько ОДНОВРЕМЕННО актуальных прошивок одного шкафа.
///
/// Просьба Ильи 16.09.2026: «Всё ещё нет возможности сделать несколько текущих версий, просто
/// разных. Допустим, в одной по умолчанию будет выбран один тип частотников, в другой количество
/// насосов другое и так далее. Поэтому должна быть возможность сделать несколько прошивок актуальных
/// для одного типа и подтипа».
///
/// Мешали этому три места сразу, и каждое проверяется здесь отдельно: схлопывание выдачи по ключу
/// «подтип + контроллер» (Database.Deduplicate), признак «эту версию уже сменила более свежая»
/// (Database.NotSuperseded, от него зависит очередь модерации) и метка «Текущая»
/// (FwHistoryStatus.Labels).
///
/// Вторая половина каждого сценария не менее важна первой: версии ОДНОГО исполнения обязаны
/// по-прежнему схлопываться к последней — иначе выдача превратилась бы в перечисление всей истории.</summary>
public class FwExecutionTests
{
    private static (EquipmentGroup, EquipmentSubType, ControllerModification) Seed(Database db)
    {
        var group = db.GetAllEquipmentGroups().First(g => g.Name == "НГР");
        var subtype = db.GetSubtypesForGroup(group.Id!.Value).First(s => s.Name == "КНС");
        var mod = db.GetAllModifications().First(m => m.ControllerName == "SMH4");
        return (group, subtype, mod);
    }

    private static int AddVersion(Database db, int sw, string execution, string tags = "КНС", bool opc = false)
    {
        var (group, subtype, mod) = Seed(db);
        return db.AddFwVersion(new FwVersionRecord
        {
            SubtypeId = subtype.Id!.Value,
            ControllerId = mod.ControllerId,
            EqPrefix = group.Prefix,
            SubPrefix = subtype.Prefix,
            HwVersion = mod.HwVersion,
            SwVersion = sw,
            DtStr = $"2026010{sw}_0000",
            VersionRaw = $"2.1.001.000{sw}.2026010{sw}_0000",
            Filename = "fw.psl",
            DiskPath = $@"X:\ПО\НГР\КНС\SMH4\{sw}",
            LaunchTypes = new List<string> { "ПЧ" },
            Tags = tags,
            Status = "active",
            Execution = execution,
            IsOpc = opc,
        });
    }

    // ── Выдача поиска ────────────────────────────────────────────────────────

    /// <summary>Две прошивки разных исполнений живут в выдаче рядом. До правки выживала одна — та,
    /// что набрала больше очков, — и вторая для наладчика просто не существовала.</summary>
    [Fact]
    public void TwoExecutions_OfTheSameCabinet_AreBothFound()
    {
        using var dbFile = new TempDb();
        using var db = new Database(dbFile.Path);

        var pumps = AddVersion(db, sw: 1, execution: "3 насоса");
        var drives = AddVersion(db, sw: 2, execution: "ПЧ Danfoss");

        var ids = db.SearchFwVersions(new[] { "КНС" }).Select(f => f.Row.Id).ToList();

        Assert.Contains(pumps, ids);
        Assert.Contains(drives, ids);
    }

    /// <summary>Обратная половина: внутри ОДНОГО исполнения выдача по-прежнему схлопывается к одной
    /// строке. Без этой проверки «починка» свелась бы к отключению схлопывания вовсе, и поиск начал
    /// бы вываливать всю историю версий.</summary>
    [Fact]
    public void VersionsOfTheSameExecution_StillCollapseToOne()
    {
        using var dbFile = new TempDb();
        using var db = new Database(dbFile.Path);

        AddVersion(db, sw: 1, execution: "3 насоса");
        AddVersion(db, sw: 2, execution: "3 насоса");

        var rows = db.SearchFwVersions(new[] { "КНС" }).Where(f => !f.Row.IsOpc).ToList();

        Assert.Single(rows);
        Assert.Equal(2, rows[0].Row.SwVersion);
    }

    /// <summary>Прошивки без исполнения ведут себя ровно как до его появления — схлопываются в одну.
    /// Это и есть обещание «пустое значение = прежнее поведение», на котором держится совместимость
    /// со всем накопленным.</summary>
    [Fact]
    public void VersionsWithoutExecution_BehaveExactlyAsBefore()
    {
        using var dbFile = new TempDb();
        using var db = new Database(dbFile.Path);

        AddVersion(db, sw: 1, execution: "");
        AddVersion(db, sw: 2, execution: "");

        var rows = db.SearchFwVersions(new[] { "КНС" }).ToList();

        Assert.Single(rows);
    }

    /// <summary>Обычная прошивка и прошивка с исполнением — тоже две разные линейки: пустое
    /// исполнение это полноправное значение, а не «любое».</summary>
    [Fact]
    public void PlainVersion_AndAnExecutionOne_DoNotCollapseIntoEachOther()
    {
        using var dbFile = new TempDb();
        using var db = new Database(dbFile.Path);

        var plain = AddVersion(db, sw: 1, execution: "");
        var special = AddVersion(db, sw: 2, execution: "3 насоса");

        var ids = db.SearchFwVersions(new[] { "КНС" }).Select(f => f.Row.Id).ToList();

        Assert.Contains(plain, ids);
        Assert.Contains(special, ids);
    }

    // ── «Уже сменила более свежая» и очередь модерации ───────────────────────

    /// <summary>Свежая прошивка ДРУГОГО исполнения не выкидывает соседнюю линейку из модерации.
    /// Пока сравнения исполнений в NotSuperseded не было, размеченная под «3 насоса» прошивка
    /// пропадала из очереди, стоило загрузить «ПЧ Danfoss» с бо́льшим номером.</summary>
    [Fact]
    public void ANewerVersionOfAnotherExecution_DoesNotSupersedeThisOne()
    {
        using var dbFile = new TempDb();
        using var db = new Database(dbFile.Path);

        var pumps = AddVersion(db, sw: 1, execution: "3 насоса");
        AddVersion(db, sw: 2, execution: "ПЧ Danfoss");

        var awaiting = db.GetUnreleasedFwVersionsWithNames().Select(v => v.Id).ToList();

        Assert.Contains(pumps, awaiting);
    }

    /// <summary>Обратная половина: внутри своего исполнения замена работает как работала — старая
    /// версия из очереди модерации уходит, размечать её незачем.</summary>
    [Fact]
    public void ANewerVersionOfTheSameExecution_StillSupersedesThisOne()
    {
        using var dbFile = new TempDb();
        using var db = new Database(dbFile.Path);

        var older = AddVersion(db, sw: 1, execution: "3 насоса");
        AddVersion(db, sw: 2, execution: "3 насоса");

        var awaiting = db.GetUnreleasedFwVersionsWithNames().Select(v => v.Id).ToList();

        Assert.DoesNotContain(older, awaiting);
    }

    // ── Метка «Текущая» ──────────────────────────────────────────────────────

    /// <summary>В истории шкафа обе линейки помечены как актуальные, и по подписи видно, какая чья.
    /// «Заменена» у прошивки, которую просто загрузили раньше соседней, — это ложь, и именно она
    /// заставляла считать, что несколько актуальных прошивок сделать нельзя.</summary>
    [Fact]
    public void BothExecutions_AreLabelledCurrent()
    {
        using var dbFile = new TempDb();
        using var db = new Database(dbFile.Path);

        AddVersion(db, sw: 1, execution: "3 насоса");
        AddVersion(db, sw: 2, execution: "ПЧ Danfoss");

        var (_, subtype, mod) = Seed(db);
        var history = db.GetFwVersionsHistory(subtype.Id!.Value, mod.ControllerId);
        var labels = FwHistoryStatus.Labels(history);

        Assert.DoesNotContain(FwHistoryStatus.Superseded, labels);
        Assert.Contains(FwHistoryStatus.Current, labels);
        Assert.Contains(FwHistoryStatus.CurrentForExecution("3 насоса"), labels);
    }

    /// <summary>Обратная половина: старая версия ТОГО ЖЕ исполнения так и остаётся «Заменена» —
    /// одновременно актуальных версий внутри одной линейки не бывает.</summary>
    [Fact]
    public void TheOlderVersionOfTheSameExecution_IsStillSuperseded()
    {
        using var dbFile = new TempDb();
        using var db = new Database(dbFile.Path);

        AddVersion(db, sw: 1, execution: "3 насоса");
        AddVersion(db, sw: 2, execution: "3 насоса");

        var (_, subtype, mod) = Seed(db);
        var labels = FwHistoryStatus.Labels(db.GetFwVersionsHistory(subtype.Id!.Value, mod.ControllerId));

        Assert.Contains(FwHistoryStatus.Superseded, labels);
    }

    // ── Мелочи, на которых держится остальное ────────────────────────────────

    /// <summary>«Последняя версия» считается внутри своего исполнения. От неё отсчитываются и «не
    /// увеличивать sw», и базовая версия ОПЦ, и перенос тегов: возьмись она из соседней линейки —
    /// новая прошивка «3 насоса» получила бы номер и теги двухнасосной.</summary>
    [Fact]
    public void LastActiveVersion_IsCountedWithinItsOwnExecution()
    {
        using var dbFile = new TempDb();
        using var db = new Database(dbFile.Path);

        AddVersion(db, sw: 1, execution: "3 насоса");
        AddVersion(db, sw: 5, execution: "ПЧ Danfoss");

        var (_, subtype, mod) = Seed(db);
        var last = db.GetLastActiveFwVersion(subtype.Id!.Value, mod.ControllerId, mod.HwVersion, "3 насоса");

        Assert.NotNull(last);
        Assert.Equal(1, last!.SwVersion);
    }

    /// <summary>Исполнение нормализуется на входе в таблицу: пробелы по краям и удвоенные внутри не
    /// должны заводить вторую линейку с тем же именем. Сравнение исполнений точное — см. FwExecution,
    /// — и без нормализации «3  насоса» и «3 насоса » оказались бы тремя разными исполнениями.</summary>
    [Fact]
    public void Execution_IsNormalizedOnWrite()
    {
        using var dbFile = new TempDb();
        using var db = new Database(dbFile.Path);

        var id = AddVersion(db, sw: 1, execution: "  3   насоса ");
        var row = db.GetFwVersionById(id);

        Assert.NotNull(row);
        Assert.Equal("3 насоса", row!.Execution);
    }

    /// <summary>Список исполнений шкафа — то, из чего выбирает форма загрузки. Пустое в него не
    /// попадает (оно и так всегда предложено первым), повторы схлопываются.</summary>
    [Fact]
    public void KnownExecutions_AreListedOncePerName_WithoutTheEmptyOne()
    {
        using var dbFile = new TempDb();
        using var db = new Database(dbFile.Path);

        AddVersion(db, sw: 1, execution: "");
        AddVersion(db, sw: 2, execution: "3 насоса");
        AddVersion(db, sw: 3, execution: "3 насоса");
        AddVersion(db, sw: 4, execution: "ПЧ Danfoss");

        var (_, subtype, mod) = Seed(db);
        var executions = db.GetFwExecutions(subtype.Id!.Value, mod.ControllerId);

        Assert.Equal(new[] { "3 насоса", "ПЧ Danfoss" }, executions);
    }

    /// <summary>ОПЦ-версии не участвуют в подсчёте следующего номера обычной линейки. Прямая
    /// проверка самого счётчика, отдельно от загрузки: правило живёт в SQL-запросе, и сломать его
    /// можно, не тронув FirmwareUploadService вовсе.</summary>
    [Fact]
    public void NextSwVersion_IgnoresOpcVersions()
    {
        using var dbFile = new TempDb();
        using var db = new Database(dbFile.Path);

        AddVersion(db, sw: 1, execution: "");
        AddVersion(db, sw: 9, execution: "", opc: true);

        var (_, subtype, mod) = Seed(db);

        Assert.Equal(2, db.GetNextSwVersion(subtype.Id!.Value, mod.ControllerId, mod.HwVersion));
    }

    /// <summary>Основы для ОПЦ — только живые прошивки обычной линейки, от свежих к старым: первой в
    /// списке формы стоит текущая, она же выбрана по умолчанию. Сама ОПЦ основой быть не может.</summary>
    [Fact]
    public void OpcBaseCandidates_AreTheNormalVersions_NewestFirst()
    {
        using var dbFile = new TempDb();
        using var db = new Database(dbFile.Path);

        AddVersion(db, sw: 1, execution: "");
        AddVersion(db, sw: 2, execution: "");
        AddVersion(db, sw: 7, execution: "", opc: true);

        var (_, subtype, mod) = Seed(db);
        var candidates = db.GetOpcBaseCandidates(subtype.Id!.Value, mod.ControllerId, mod.HwVersion);

        Assert.Equal(new[] { 2, 1 }, candidates.Select(c => c.SwVersion));
    }

    /// <summary>Ручная отметка «текущая» живёт внутри своего исполнения. Отметили версию «3 насоса» —
    /// отметка соседней линейки «ПЧ Danfoss» обязана уцелеть: иначе там снова стала бы текущей самая
    /// свежая версия, то есть выбор оператора молча отменялся бы чужим действием.</summary>
    [Fact]
    public void ManualCurrent_OfOneExecution_DoesNotClearTheOther()
    {
        using var dbFile = new TempDb();
        using var db = new Database(dbFile.Path);

        var drives = AddVersion(db, sw: 1, execution: "ПЧ Danfoss");
        AddVersion(db, sw: 2, execution: "ПЧ Danfoss");
        var pumps = AddVersion(db, sw: 3, execution: "3 насоса");

        Assert.True(db.SetFwVersionManualCurrent(drives));
        Assert.True(db.SetFwVersionManualCurrent(pumps));

        Assert.True(db.GetFwVersionById(drives)!.ManualCurrent);
        Assert.True(db.GetFwVersionById(pumps)!.ManualCurrent);
    }

    /// <summary>Исполнение правится и у уже заведённой прошивки: признак появился позже накопленного,
    /// и разнести его по линейкам можно только руками (окно модерации, EditFirmwareDialog).</summary>
    [Fact]
    public void Execution_CanBeSetOnAnAlreadyUploadedVersion()
    {
        using var dbFile = new TempDb();
        using var db = new Database(dbFile.Path);

        var id = AddVersion(db, sw: 1, execution: "");
        db.UpdateFwVersion(id, execution: " ПЧ Danfoss ");

        Assert.Equal("ПЧ Danfoss", db.GetFwVersionById(id)!.Execution);
    }
}
