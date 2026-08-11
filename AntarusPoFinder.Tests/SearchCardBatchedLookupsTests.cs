using System;
using System.Linq;
using AntarusPoFinder.Core.Data;
using AntarusPoFinder.Core.Domain;
using AntarusPoFinder.Tests.TestHelpers;
using Xunit;

namespace AntarusPoFinder.Tests;

/// <summary>Два запроса, которыми выдача поиска отвечает на «показывать ли кнопку „Параметры“» и
/// «сколько у версии доп. материалов», — по одному на всю выдачу вместо запроса на каждую карточку
/// (см. SearchView.BuildCard). Проверяется, что ответ РОВНО тот же, каким его давали прежние
/// поштучные вызовы GetParamFiles(subtypeId).Count > 0 и CountFwAttachments: иначе экономия
/// оборачивается пропавшей кнопкой или лишним значком на карточке.</summary>
public class SearchCardBatchedLookupsTests : IDisposable
{
    private readonly TempDb _file = new();
    private readonly Database _db;

    public SearchCardBatchedLookupsTests() => _db = new Database(_file.Path);

    public void Dispose()
    {
        _db.Dispose();
        _file.Dispose();
    }

    private int AddFirmware(int sw)
    {
        var group = _db.GetAllEquipmentGroups().First(g => g.Name == "ТГР");
        var subtype = _db.GetSubtypesForGroup(group.Id!.Value).First();
        var mod = _db.GetAllModifications().First(m => m.ControllerName == "SMH5");
        return _db.AddFwVersion(new FwVersionRecord
        {
            SubtypeId = subtype.Id!.Value,
            ControllerId = mod.ControllerId,
            EqPrefix = group.Prefix,
            SubPrefix = subtype.Prefix,
            HwVersion = mod.HwVersion,
            SwVersion = sw,
            DtStr = "20260101_0000",
            VersionRaw = $"3.0.0005.{sw:D4}.20260101_0000",
            Filename = "fw.psl",
            LaunchTypes = new() { "УПП" },
        });
    }

    private void AddParams(int subtypeId, bool archived = false) =>
        _db.AddParamFile(new ParamFile
        {
            SubtypeId = subtypeId,
            Manufacturer = "Delta",
            Filename = "p.par",
            DiskPath = "",
            Description = "",
            UploadDate = "2026-01-01",
            Tags = "",
            Archived = archived,
        });

    [Fact]
    public void SubtypeIdsWithParamFiles_MatchesThePerCardQuery_ForEverySubtype()
    {
        var subtypes = _db.GetAllEquipmentSubtypes().Where(s => s.Id is not null).ToList();
        Assert.True(subtypes.Count >= 3, "в справочнике по умолчанию должно быть несколько подтипов");

        AddParams(subtypes[0].Id!.Value);
        AddParams(subtypes[1].Id!.Value);
        AddParams(subtypes[1].Id!.Value); // второй файл того же подтипа не должен ничего менять

        var batched = _db.GetSubtypeIdsWithParamFiles();

        foreach (var subtype in subtypes)
        {
            var perCard = _db.GetParamFiles(subtypeId: subtype.Id!.Value).Count > 0;
            Assert.Equal(perCard, batched.Contains(subtype.Id!.Value));
        }
        Assert.Equal(2, batched.Count);
    }

    /// <summary>Архивный файл параметров кнопку не включает — прежний запрос его тоже отсекал
    /// (WHERE archived = 0), и разойдись здесь эти два условия, кнопка «Параметры» появилась бы у
    /// подтипа, у которого открывать нечего.</summary>
    [Fact]
    public void ArchivedParamFile_DoesNotCountAsHavingParams()
    {
        var subtype = _db.GetAllEquipmentSubtypes().First(s => s.Id is not null);
        AddParams(subtype.Id!.Value, archived: true);

        Assert.False(_db.GetParamFiles(subtypeId: subtype.Id!.Value).Count > 0);
        Assert.DoesNotContain(subtype.Id!.Value, _db.GetSubtypeIdsWithParamFiles());
    }

    [Fact]
    public void AttachmentCounts_MatchThePerVersionCount_AndSkipTombstoned()
    {
        var withTwo = AddFirmware(1);
        var withOne = AddFirmware(2);
        var withNone = AddFirmware(3);

        _db.AddFwAttachment(new FwAttachment { FwVersionId = withTwo, Filename = "a.pdf", DiskPath = "", Kind = FwAttachmentKinds.Other });
        _db.AddFwAttachment(new FwAttachment { FwVersionId = withTwo, Filename = "b.pdf", DiskPath = "", Kind = FwAttachmentKinds.Other });
        _db.AddFwAttachment(new FwAttachment { FwVersionId = withOne, Filename = "c.pdf", DiskPath = "", Kind = FwAttachmentKinds.Other });
        var removed = _db.AddFwAttachment(new FwAttachment { FwVersionId = withOne, Filename = "d.pdf", DiskPath = "", Kind = FwAttachmentKinds.Other });
        _db.TombstoneFwAttachment(removed);

        var counts = _db.GetFwAttachmentCounts();

        foreach (var id in new[] { withTwo, withOne, withNone })
            Assert.Equal(_db.CountFwAttachments(id), counts.GetValueOrDefault(id));

        Assert.Equal(2, counts.GetValueOrDefault(withTwo));
        Assert.Equal(1, counts.GetValueOrDefault(withOne));
        // Версии без вложений в словарь не попадают вовсе — карточка читает его через
        // GetValueOrDefault и получает тот же ноль, что раньше давал COUNT(*).
        Assert.DoesNotContain(withNone, counts.Keys);
    }
}
