using System;
using System.IO;
using System.Linq;
using AntarusPoFinder.Core.Data;
using AntarusPoFinder.Core.Services;
using AntarusPoFinder.Tests.TestHelpers;
using Xunit;

namespace AntarusPoFinder.Tests;

/// <summary>«Инструкции на этот шкаф не будет» — признак рациональных шкафов.
///
/// Он живёт у ПОДТИПА, а не у версии: «инструкция не пишется» — свойство изделия, а не конкретной
/// прошивки. Спрашивай мы это при каждой загрузке — рано или поздно галочку забыли бы, и шкаф получил
/// бы обещание «инструкция в разработке», которое не сбудется никогда.</summary>
public class SubtypeNoInstructionTests
{
    private static string NewTempDb() => Path.Combine(Path.GetTempPath(), $"antarus_noinstr_{Guid.NewGuid():N}.db");

    private static void Cleanup(params string[] paths)
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (var db in paths)
            foreach (var f in new[] { db, db + "-wal", db + "-shm" })
                if (File.Exists(f)) File.Delete(f);
    }

    private static int SubtypeId(Database db, string group, string subtype)
    {
        var g = db.GetAllEquipmentGroups().First(x => x.Name == group);
        return db.GetSubtypesForGroup(g.Id!.Value).First(s => s.Name == subtype).Id!.Value;
    }

    /// <summary>Умолчание — «инструкция будет»: миграция на уже установленной копии не должна ничего
    /// поменять до тех пор, пока человек сам не отметит подтип.</summary>
    [Fact]
    public void TheFlag_IsOffByDefault_AndSurvivesOtherEdits()
    {
        var path = NewTempDb();
        try
        {
            using var db = new Database(path);
            var id = SubtypeId(db, "НГР", "КНС");

            Assert.False(db.SubtypeHasNoInstruction(id));

            db.SetSubtypeNoInstruction(id, true);
            Assert.True(db.SubtypeHasNoInstruction(id));
            Assert.True(db.GetAllEquipmentSubtypes().First(s => s.Id == id).NoInstruction);

            // Правка соседнего поля идёт обычным upsert-ом и галочку сбивать не должна.
            var subtype = db.GetAllEquipmentSubtypes().First(s => s.Id == id);
            subtype.Prefix = 7;
            db.UpsertEquipmentSubtype(subtype);
            Assert.True(db.SubtypeHasNoInstruction(id));

            db.SetSubtypeNoInstruction(id, false);
            Assert.False(db.SubtypeHasNoInstruction(id));
        }
        finally { Cleanup(path); }
    }

    /// <summary>Отметка едет между машинами: это свойство изделия, а не машины. Обратно (снятие) —
    /// НЕ едет, ровно как и удаление записей справочника: молчание машины со старой программой не
    /// должно снимать галочку, поставленную на новой.</summary>
    [Fact]
    public void TheFlag_TravelsBetweenMachines_ButUncheckingDoesNot()
    {
        var pathA = NewTempDb();
        var pathB = NewTempDb();
        try
        {
            using var dbA = new Database(pathA);
            using var dbB = new Database(pathB);
            dbB.ImportHierarchyData(dbA.ExportHierarchyData()); // рукопожатие: sync_id сошлись

            var idA = SubtypeId(dbA, "НГР", "КНС");
            dbA.SetSubtypeNoInstruction(idA, true);

            dbB.ImportHierarchyData(dbA.ExportHierarchyData());
            Assert.True(dbB.SubtypeHasNoInstruction(SubtypeId(dbB, "НГР", "КНС")));

            // Повторный обмен ничего не меняет — иначе счётчик «обновлено» врал бы на каждой синхронизации.
            var second = dbB.ImportHierarchyData(dbA.ExportHierarchyData());
            Assert.True(dbB.SubtypeHasNoInstruction(SubtypeId(dbB, "НГР", "КНС")));

            // Сняли на A — на B остаётся: снятие переносится руками, как и удаления.
            dbA.SetSubtypeNoInstruction(idA, false);
            dbB.ImportHierarchyData(dbA.ExportHierarchyData());
            Assert.True(dbB.SubtypeHasNoInstruction(SubtypeId(dbB, "НГР", "КНС")));
        }
        finally { Cleanup(pathA, pathB); }
    }

    /// <summary>Заглушку него не кладут: у отмеченного шкафа папка «Инструкция» остаётся пустой, а
    /// ссылку он получает на одну общую страницу в корне диска.</summary>
    [Fact]
    public void MarkedSubtypes_GetNoInDevelopmentStub_ButTheSharedPageAppears()
    {
        var path = NewTempDb();
        using var root = new TempRoot();
        try
        {
            using var db = new Database(path);
            db.SetSubtypeNoInstruction(SubtypeId(db, "НГР", "КНС"), true);

            var writer = new CountingWriter();
            var result = new HierarchyService(db).EnsureStructure(root.Path, writer);
            Assert.True(result.Ok, string.Join("; ", result.Errors));

            var marked = Path.Combine(root.Path, "ПО", "НГР", "КНС", "SMH5", "Инструкция");
            var ordinary = Path.Combine(root.Path, "ПО", "НГР", "УПД", "SMH5", "Инструкция");

            Assert.True(Directory.Exists(marked));
            Assert.Empty(Directory.GetFiles(marked));
            Assert.NotNull(InstructionStub.ExistingIn(ordinary));

            // Одна общая страница — в корне, без типа и подтипа в пути.
            Assert.True(File.Exists(InstructionStub.SharedNotPlannedPath(root.Path)));
        }
        finally { Cleanup(path); }
    }

    private sealed class CountingWriter : IInstructionStubWriter
    {
        public void Write(string path, string text) => Write(path, StubKind.InDevelopment, null);

        public void Write(string path, StubKind kind, string? versionRaw)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, StubLayoutSet.Default.For(kind).Title);
        }
    }
}
