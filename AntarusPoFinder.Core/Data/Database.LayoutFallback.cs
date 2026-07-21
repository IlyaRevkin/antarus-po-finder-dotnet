using System.Collections.Generic;

namespace AntarusPoFinder.Core.Data;

public enum LayoutFallbackDecision { Ask, Always, Never }

public record LayoutFallbackLearningRow(string QueryKey, int YesCount, int NoCount, LayoutFallbackDecision Decision);

public partial class Database
{
    /// <summary>Once the net margin between confirmations and rejections for the exact same typed
    /// query reaches this, SearchService stops asking every time — it either applies the layout
    /// conversion silently (learned "yes") or stops trying it altogether (learned "no").</summary>
    public const int LayoutFallbackDecisionThreshold = 3;

    public LayoutFallbackDecision GetLayoutFallbackDecision(string queryKey)
    {
        var value = ExecuteScalar(
            "SELECT decision FROM layout_fallback_feedback WHERE query_key=@k",
            cmd => cmd.Parameters.AddWithValue("@k", queryKey)) as string;
        return value switch
        {
            "always" => LayoutFallbackDecision.Always,
            "never" => LayoutFallbackDecision.Never,
            _ => LayoutFallbackDecision.Ask,
        };
    }

    /// <summary>Records whether an operator confirmed ("да, ошибся раскладкой") or rejected ("нет,
    /// ввёл верно") the layout-converted query, then re-derives the decision from the accumulated
    /// counts — so answering consistently eventually stops the prompt for that exact input.</summary>
    public void RecordLayoutFallbackFeedback(string queryKey, bool wasCorrectGuess)
    {
        ExecuteNonQuery(
            """
            INSERT INTO layout_fallback_feedback(query_key, yes_count, no_count) VALUES(@k, @yes, @no)
            ON CONFLICT(query_key) DO UPDATE SET
                yes_count = yes_count + excluded.yes_count,
                no_count  = no_count + excluded.no_count
            """,
            cmd =>
            {
                cmd.Parameters.AddWithValue("@k", queryKey);
                cmd.Parameters.AddWithValue("@yes", wasCorrectGuess ? 1 : 0);
                cmd.Parameters.AddWithValue("@no", wasCorrectGuess ? 0 : 1);
            });

        using var reader = ExecuteReader(
            "SELECT yes_count, no_count FROM layout_fallback_feedback WHERE query_key=@k",
            cmd => cmd.Parameters.AddWithValue("@k", queryKey));
        if (!reader.Read()) return;
        var yes = GetInt(reader, "yes_count");
        var no = GetInt(reader, "no_count");
        reader.Close();

        if (yes - no >= LayoutFallbackDecisionThreshold)
            SetLayoutFallbackDecision(queryKey, "always");
        else if (no - yes >= LayoutFallbackDecisionThreshold)
            SetLayoutFallbackDecision(queryKey, "never");
    }

    private void SetLayoutFallbackDecision(string queryKey, string decision) =>
        ExecuteNonQuery(
            "UPDATE layout_fallback_feedback SET decision=@d WHERE query_key=@k",
            cmd =>
            {
                cmd.Parameters.AddWithValue("@d", decision);
                cmd.Parameters.AddWithValue("@k", queryKey);
            });

    /// <summary>Full dump for Настройки → Общие's learning-management grid — lets an administrator
    /// see exactly what's been learned and reset individual entries instead of only "reset everything".</summary>
    public List<LayoutFallbackLearningRow> GetAllLayoutFallbackLearning()
    {
        var result = new List<LayoutFallbackLearningRow>();
        using var reader = ExecuteReader(
            "SELECT query_key, yes_count, no_count, decision FROM layout_fallback_feedback ORDER BY query_key");
        while (reader.Read())
        {
            var decision = GetString(reader, "decision") switch
            {
                "always" => LayoutFallbackDecision.Always,
                "never" => LayoutFallbackDecision.Never,
                _ => LayoutFallbackDecision.Ask,
            };
            result.Add(new LayoutFallbackLearningRow(
                GetString(reader, "query_key"), GetInt(reader, "yes_count"), GetInt(reader, "no_count"), decision));
        }
        return result;
    }

    public void ResetLayoutFallbackLearning(string queryKey) =>
        ExecuteNonQuery("DELETE FROM layout_fallback_feedback WHERE query_key=@k", cmd => cmd.Parameters.AddWithValue("@k", queryKey));

    public void ResetAllLayoutFallbackLearning() => ExecuteNonQuery("DELETE FROM layout_fallback_feedback");
}
