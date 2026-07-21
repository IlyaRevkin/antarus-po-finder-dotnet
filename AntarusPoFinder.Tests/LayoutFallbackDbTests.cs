using System.IO;
using AntarusPoFinder.Core.Data;
using Xunit;

namespace AntarusPoFinder.Tests;

/// <summary>Covers Database.LayoutFallback — the per-query learning behind the "забыл переключить
/// раскладку" search prompt: after enough consistent yes/no feedback for the exact same typed query,
/// the app should stop asking and either always apply the conversion or never try it again.</summary>
public class LayoutFallbackDbTests
{
    private static string NewTempDb() => Path.Combine(Path.GetTempPath(), $"antarus_layout_fallback_db_test_{System.Guid.NewGuid():N}.db");

    private static void Cleanup(params string[] dbPaths)
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (var db in dbPaths)
            foreach (var f in new[] { db, db + "-wal", db + "-shm" })
                if (File.Exists(f)) File.Delete(f);
    }

    [Fact]
    public void UnknownQuery_DefaultsToAsk()
    {
        var path = NewTempDb();
        try
        {
            using var db = new Database(path);
            Assert.Equal(LayoutFallbackDecision.Ask, db.GetLayoutFallbackDecision("GJ;FH"));
        }
        finally { Cleanup(path); }
    }

    [Fact]
    public void RepeatedConfirmations_EventuallyLearnsAlways()
    {
        var path = NewTempDb();
        try
        {
            using var db = new Database(path);
            for (var i = 0; i < Database.LayoutFallbackDecisionThreshold; i++)
            {
                Assert.Equal(LayoutFallbackDecision.Ask, db.GetLayoutFallbackDecision("GJ;FH"));
                db.RecordLayoutFallbackFeedback("GJ;FH", wasCorrectGuess: true);
            }
            Assert.Equal(LayoutFallbackDecision.Always, db.GetLayoutFallbackDecision("GJ;FH"));
        }
        finally { Cleanup(path); }
    }

    [Fact]
    public void RepeatedRejections_EventuallyLearnsNever()
    {
        var path = NewTempDb();
        try
        {
            using var db = new Database(path);
            for (var i = 0; i < Database.LayoutFallbackDecisionThreshold; i++)
                db.RecordLayoutFallbackFeedback("KINCO", wasCorrectGuess: false);

            Assert.Equal(LayoutFallbackDecision.Never, db.GetLayoutFallbackDecision("KINCO"));
        }
        finally { Cleanup(path); }
    }

    [Fact]
    public void MixedFeedback_BelowMargin_KeepsAsking()
    {
        var path = NewTempDb();
        try
        {
            using var db = new Database(path);
            db.RecordLayoutFallbackFeedback("QUERY", wasCorrectGuess: true);
            db.RecordLayoutFallbackFeedback("QUERY", wasCorrectGuess: false);
            db.RecordLayoutFallbackFeedback("QUERY", wasCorrectGuess: true);

            Assert.Equal(LayoutFallbackDecision.Ask, db.GetLayoutFallbackDecision("QUERY"));
        }
        finally { Cleanup(path); }
    }

    [Fact]
    public void CustomThreshold_LearnsAlways_AfterFewerConfirmationsThanDefault()
    {
        var path = NewTempDb();
        try
        {
            using var db = new Database(path);
            const int customThreshold = 1;
            Assert.True(customThreshold < Database.LayoutFallbackDecisionThreshold);

            db.RecordLayoutFallbackFeedback("GJ;FH", wasCorrectGuess: true, customThreshold);

            Assert.Equal(LayoutFallbackDecision.Always, db.GetLayoutFallbackDecision("GJ;FH"));
        }
        finally { Cleanup(path); }
    }

    [Fact]
    public void CustomThreshold_HigherThanDefault_KeepsAskingPastTheDefaultCount()
    {
        var path = NewTempDb();
        try
        {
            using var db = new Database(path);
            var customThreshold = Database.LayoutFallbackDecisionThreshold + 2;

            for (var i = 0; i < Database.LayoutFallbackDecisionThreshold; i++)
                db.RecordLayoutFallbackFeedback("GJ;FH", wasCorrectGuess: true, customThreshold);

            // Would have flipped to Always by now under the default threshold (see
            // RepeatedConfirmations_EventuallyLearnsAlways above) but must still be asking here.
            Assert.Equal(LayoutFallbackDecision.Ask, db.GetLayoutFallbackDecision("GJ;FH"));
        }
        finally { Cleanup(path); }
    }

    [Fact]
    public void ResetOne_ClearsOnlyThatQuery()
    {
        var path = NewTempDb();
        try
        {
            using var db = new Database(path);
            db.RecordLayoutFallbackFeedback("A", wasCorrectGuess: true);
            db.RecordLayoutFallbackFeedback("B", wasCorrectGuess: true);

            db.ResetLayoutFallbackLearning("A");

            var all = db.GetAllLayoutFallbackLearning();
            Assert.DoesNotContain(all, r => r.QueryKey == "A");
            Assert.Contains(all, r => r.QueryKey == "B");
        }
        finally { Cleanup(path); }
    }

    [Fact]
    public void ResetAll_ClearsEverything()
    {
        var path = NewTempDb();
        try
        {
            using var db = new Database(path);
            db.RecordLayoutFallbackFeedback("A", wasCorrectGuess: true);
            db.RecordLayoutFallbackFeedback("B", wasCorrectGuess: false);

            db.ResetAllLayoutFallbackLearning();

            Assert.Empty(db.GetAllLayoutFallbackLearning());
        }
        finally { Cleanup(path); }
    }
}
