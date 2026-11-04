using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using AntarusPoFinder.Core.Domain;

namespace AntarusPoFinder.Core.Services;

/// <summary>⚠️ ВРЕМЕННЫЙ ИНСТРУМЕНТ. Осмотр того, что осталось от ПРЕЖНЕЙ модели исполнений, —
/// чтобы дерево можно было перестроить руками и осознанно. Когда перестроено, этот класс вместе со
/// своим окном можно выкинуть целиком; ничего, кроме отчёта, от него не зависит.
///
/// <b>Зачем он нужен.</b> До 04.11.2026 исполнение было пометкой на ОТДЕЛЬНОЙ прошивке: «2 насоса» и
/// «3 насоса» — две записи, два номера ПО, две папки. Новая модель кладёт исполнения папками внутрь
/// ОДНОЙ версии. Свести старые записи автоматически нельзя: у них разные номера и разные файлы, и
/// машина не знает, какие из них на самом деле одна прошивка, а какие — честно разные. Поэтому
/// машина только ПРЕДЛАГАЕТ группы-кандидаты, а решает человек.
///
/// <b>Что этот класс делает и чего не делает.</b> Делает: смотрит записи и говорит, что нашёл.
/// НЕ делает: не трогает ни одного файла и ни одной записи. Действие — отдельный, подтверждаемый
/// шаг; порядок «сначала осмотр, потом действие» здесь не вежливость, а единственная защита от
/// перемещения чужих файлов на общей сетевой шаре.</summary>
public static class LegacyExecutionScan
{
    /// <summary>Версия, которая на диске лежит ПО-СТАРОМУ: файлы прямо в папке версии, своей
    /// «Прошивка\» нет. Исполнения такой версии завести негде — папка исполнения живёт внутри
    /// «Прошивка\». Перестраивает такие версии НЕ этот инструмент, а штатная перестройка диска
    /// (Настройки → «Перестроить диск», DiskLayoutMigrator): она это умеет, проверена и умеет
    /// откатываться. Здесь — только счёт и список, чтобы было видно, сколько осталось.</summary>
    public sealed record LegacyLayoutVersion(int FwVersionId, string Title, string DiskPath);

    /// <summary>Одна запись-кандидат внутри группы.</summary>
    public sealed record MergeMember(
        int FwVersionId, int SwVersion, string VersionRaw, string Execution, string DiskPath, bool Current);

    /// <summary>Группа записей, которые ПОХОЖИ на исполнения одной прошивки: один шкаф, один
    /// контроллер, одна модификация железа, а номера ПО разные и хотя бы у одной записи заполнено
    /// исполнение.
    ///
    /// «Похожи» — это максимум, что машина имеет право сказать. Решение «это одна прошивка, главным
    /// остаётся номер такой-то, у этого файла исполнение такое-то» принимает человек, и инструмент
    /// обязан спросить его прежде, чем что-либо двигать.</summary>
    public sealed record MergeCandidate(string Title, IReadOnlyList<MergeMember> Members)
    {
        /// <summary>Какой номер логично оставить главным — самый свежий активный. Это ПОДСКАЗКА в
        /// отчёте, а не решение: человек вправе выбрать другой.</summary>
        public MergeMember? Suggested => Members.OrderByDescending(m => m.SwVersion).FirstOrDefault();
    }

    public sealed record Result(
        IReadOnlyList<LegacyLayoutVersion> LegacyLayout,
        IReadOnlyList<MergeCandidate> Candidates)
    {
        public string Summary =>
            $"версий в режиме совместимости: {LegacyLayout.Count}; "
            + $"групп, похожих на исполнения одной прошивки: {Candidates.Count} "
            + $"(записей в них: {Candidates.Sum(c => c.Members.Count)})";
    }

    /// <summary>Осмотреть базу.</summary>
    /// <param name="records">Все записи версий с именами (Database.GetAllFwVersionsWithNames).</param>
    /// <param name="isNewLayout">Проверка «у версии есть своя Прошивка\» — обычно
    /// VersionLayout.IsNewLayout. Отдельным параметром, чтобы осмотр можно было проверить тестом, не
    /// раскладывая файлы, и чтобы недоступная шара не превращалась в «всё старое».</param>
    public static Result Scan(IEnumerable<FwVersionRecord> records, Func<string, bool> isNewLayout)
    {
        var all = records?.ToList() ?? new List<FwVersionRecord>();

        var legacy = all
            // ОПЦ сюда не берём: это разовые сборки под конкретный шкаф, исполнений у них не бывает,
            // а перестройка их раскладки — вопрос к мигратору диска, не к этому инструменту.
            .Where(r => !r.IsOpc && !r.Archived && !FwStatus.IsRolledBack(r.Status))
            .Where(r => !string.IsNullOrWhiteSpace(r.DiskPath) && !isNewLayout(r.DiskPath))
            .Select(r => new LegacyLayoutVersion(r.Id ?? 0, Title(r), r.DiskPath))
            .ToList();

        var candidates = all
            .Where(r => !r.IsOpc && !r.Archived && !FwStatus.IsRolledBack(r.Status))
            .GroupBy(r => (r.SubtypeId, r.ControllerId, r.HwVersion))
            .Where(g => g.Select(r => r.SwVersion).Distinct().Count() > 1)
            // Группа интересна, только если исполнение У КОГО-ТО заполнено: соседние номера без
            // исполнений — это обычная линейка версий, и сводить там нечего.
            .Where(g => g.Any(r => FwExecution.Normalize(r.Execution).Length > 0))
            .Select(g => new MergeCandidate(
                GroupTitle(g.First()),
                g.OrderByDescending(r => r.SwVersion)
                    .Select(r => new MergeMember(
                        r.Id ?? 0, r.SwVersion, r.VersionRaw, FwExecution.Normalize(r.Execution),
                        r.DiskPath, FwStatus.IsDisabled(r.Status) == false))
                    .ToList()))
            .OrderBy(c => c.Title, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        return new Result(legacy, candidates);
    }

    /// <summary>Отчёт для человека — он же журнал осмотра: что нашли и что ПРЕДЛАГАЕМ. Отдельно от
    /// показа на экране, потому что ровно этот текст сохраняется в файл: разговор «почему эти две
    /// записи считаются одной прошивкой» должен опираться на то, что видел человек, а не на то, что
    /// он запомнил.</summary>
    public static string Report(Result result, DateTime now)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Осмотр старой раскладки исполнений — {now:dd.MM.yyyy HH:mm}");
        sb.AppendLine(result.Summary);
        sb.AppendLine();
        sb.AppendLine("НИ ОДИН ФАЙЛ И НИ ОДНА ЗАПИСЬ ЭТИМ ОСМОТРОМ НЕ ИЗМЕНЕНЫ.");
        sb.AppendLine();

        sb.AppendLine("1. ВЕРСИИ В РЕЖИМЕ СОВМЕСТИМОСТИ (файлы лежат прямо в папке версии)");
        sb.AppendLine("   Исполнение такой версии завести негде: его папка живёт внутри «Прошивка\\».");
        sb.AppendLine("   Перестроить их умеет штатная перестройка диска — Настройки → «Перестроить диск».");
        if (result.LegacyLayout.Count == 0) sb.AppendLine("   — таких версий нет.");
        foreach (var v in result.LegacyLayout) sb.AppendLine($"   • {v.Title}  ←  {v.DiskPath}");
        sb.AppendLine();

        sb.AppendLine("2. ГРУППЫ, ПОХОЖИЕ НА ИСПОЛНЕНИЯ ОДНОЙ ПРОШИВКИ");
        sb.AppendLine("   Это ПРЕДПОЛОЖЕНИЕ машины: один шкаф, один контроллер, одно железо, разные");
        sb.AppendLine("   номера ПО, и где-то заполнено исполнение. Свести их автоматически нельзя —");
        sb.AppendLine("   у записей разные файлы, и машина не знает, какие из них одна прошивка.");
        if (result.Candidates.Count == 0) sb.AppendLine("   — таких групп нет.");
        foreach (var c in result.Candidates)
        {
            sb.AppendLine();
            sb.AppendLine($"   {c.Title}");
            foreach (var m in c.Members)
            {
                var execution = m.Execution.Length == 0 ? FwExecution.MainDisplayName : m.Execution;
                sb.AppendLine($"      • sw {m.SwVersion}  {m.VersionRaw}  исполнение: {execution}");
                sb.AppendLine($"        {m.DiskPath}");
            }
            if (c.Suggested is { } s)
                sb.AppendLine($"      Предложение: оставить главной sw {s.SwVersion} ({s.VersionRaw}), "
                              + "остальные положить в неё папками исполнений — ПОСЛЕ вашего подтверждения.");
        }
        return sb.ToString();
    }

    private static string Title(FwVersionRecord r) =>
        $"{r.GroupName} {r.SubtypeName} {r.CtrlName} {r.VersionRaw}".Replace("  ", " ").Trim();

    private static string GroupTitle(FwVersionRecord r) =>
        $"{r.GroupName} / {r.SubtypeName} / {r.CtrlName} / hw {r.HwVersion}".Trim();
}
