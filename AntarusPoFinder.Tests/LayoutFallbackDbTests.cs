using AntarusPoFinder.Core.Data;
using AntarusPoFinder.Tests.TestHelpers;
using Xunit;

namespace AntarusPoFinder.Tests;

/// <summary>Covers Database.LayoutFallback — the per-query learning behind the "забыл переключить
/// раскладку" search prompt: after enough consistent yes/no feedback for the exact same typed query,
/// the app should stop asking and either always apply the conversion or never try it again.</summary>
public class LayoutFallbackDbTests
{
    [Fact]
    public void UnknownQuery_DefaultsToAsk()
    {
        using var dbFile = new TempDb();
        using var db = new Database(dbFile.Path);
        Assert.Equal(LayoutFallbackDecision.Ask, db.GetLayoutFallbackDecision("GJ;FH"));
    }

    [Fact]
    public void RepeatedConfirmations_EventuallyLearnsAlways()
    {
        using var dbFile = new TempDb();
        using var db = new Database(dbFile.Path);
        for (var i = 0; i < Database.LayoutFallbackDecisionThreshold; i++)
        {
            Assert.Equal(LayoutFallbackDecision.Ask, db.GetLayoutFallbackDecision("GJ;FH"));
            db.RecordLayoutFallbackFeedback("GJ;FH", wasCorrectGuess: true);
        }
        Assert.Equal(LayoutFallbackDecision.Always, db.GetLayoutFallbackDecision("GJ;FH"));
    }

    [Fact]
    public void RepeatedRejections_EventuallyLearnsNever()
    {
        using var dbFile = new TempDb();
        using var db = new Database(dbFile.Path);
        for (var i = 0; i < Database.LayoutFallbackDecisionThreshold; i++)
            db.RecordLayoutFallbackFeedback("KINCO", wasCorrectGuess: false);

        Assert.Equal(LayoutFallbackDecision.Never, db.GetLayoutFallbackDecision("KINCO"));
    }

    [Fact]
    public void MixedFeedback_BelowMargin_KeepsAsking()
    {
        using var dbFile = new TempDb();
        using var db = new Database(dbFile.Path);
        db.RecordLayoutFallbackFeedback("QUERY", wasCorrectGuess: true);
        db.RecordLayoutFallbackFeedback("QUERY", wasCorrectGuess: false);
        db.RecordLayoutFallbackFeedback("QUERY", wasCorrectGuess: true);

        Assert.Equal(LayoutFallbackDecision.Ask, db.GetLayoutFallbackDecision("QUERY"));
    }

    [Fact]
    public void CustomThreshold_LearnsAlways_AfterFewerConfirmationsThanDefault()
    {
        using var dbFile = new TempDb();
        using var db = new Database(dbFile.Path);
        const int customThreshold = 1;
        Assert.True(customThreshold < Database.LayoutFallbackDecisionThreshold);

        db.RecordLayoutFallbackFeedback("GJ;FH", wasCorrectGuess: true, customThreshold);

        Assert.Equal(LayoutFallbackDecision.Always, db.GetLayoutFallbackDecision("GJ;FH"));
    }

    [Fact]
    public void CustomThreshold_HigherThanDefault_KeepsAskingPastTheDefaultCount()
    {
        using var dbFile = new TempDb();
        using var db = new Database(dbFile.Path);
        var customThreshold = Database.LayoutFallbackDecisionThreshold + 2;

        for (var i = 0; i < Database.LayoutFallbackDecisionThreshold; i++)
            db.RecordLayoutFallbackFeedback("GJ;FH", wasCorrectGuess: true, customThreshold);

        // Would have flipped to Always by now under the default threshold (see
        // RepeatedConfirmations_EventuallyLearnsAlways above) but must still be asking here.
        Assert.Equal(LayoutFallbackDecision.Ask, db.GetLayoutFallbackDecision("GJ;FH"));
    }

    [Fact]
    public void ResetOne_ClearsOnlyThatQuery()
    {
        using var dbFile = new TempDb();
        using var db = new Database(dbFile.Path);
        db.RecordLayoutFallbackFeedback("A", wasCorrectGuess: true);
        db.RecordLayoutFallbackFeedback("B", wasCorrectGuess: true);

        db.ResetLayoutFallbackLearning("A");

        var all = db.GetAllLayoutFallbackLearning();
        Assert.DoesNotContain(all, r => r.QueryKey == "A");
        Assert.Contains(all, r => r.QueryKey == "B");
    }

    [Fact]
    public void ResetAll_ClearsEverything()
    {
        using var dbFile = new TempDb();
        using var db = new Database(dbFile.Path);
        db.RecordLayoutFallbackFeedback("A", wasCorrectGuess: true);
        db.RecordLayoutFallbackFeedback("B", wasCorrectGuess: false);

        db.ResetAllLayoutFallbackLearning();

        Assert.Empty(db.GetAllLayoutFallbackLearning());
    }
}
