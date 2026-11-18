using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AntarusPoFinder.Core.Domain;

/// <summary>ПОЧЕМУ СТРОКИ НЕТ В ВЫДАЧЕ — поэтапно и числами.
///
/// <b>Откуда это взялось.</b> Несколько месяцев повторяется одно и то же: залили новую версию
/// прошивки — она не находится; помогает только залить её ЕЩЁ РАЗ, дубликатом. Важное уточнение
/// Ильи: «я пытался отключать только, не помогало, только дубль помогал» — то есть выключение
/// прежних версий случай НЕ лечит. Значит новая версия не проигрывает старой при схлопывании
/// (там выключенная старая просто ушла бы), а вовсе не доезжает до выдачи.
///
/// Разбирать такое по рассказу невозможно: поиск молчит. Он либо показывает строку, либо нет, и в
/// обоих случаях выглядит одинаково. Поэтому каждый поиск теперь может сказать, на каком шаге и по
/// какой причине строка выпала:
///
/// <list type="number">
/// <item>сколько строк дал индекс (<see cref="IndexRows"/>);</item>
/// <item>сколько отсеяли фильтры (<see cref="DroppedByFilters"/>);</item>
/// <item>сколько спрятало правило «слова по запросу» и КАКИМ ИМЕННО словом (<see cref="HiddenByWord"/>);</item>
/// <item>сколько не совпало с запросом вовсе (<see cref="DroppedByQuery"/>);</item>
/// <item>сколько выбыло при схлопывании и почему (<see cref="Collapsed"/>);</item>
/// <item>что осталось (<see cref="Shown"/>).</item>
/// </list>
///
/// <b>Чего здесь НЕТ и почему.</b> Записи уезжают наружу тихим каналом диагностики, поэтому в них
/// только то, по чему прошивку опознают: номер версии, подтип, контроллер, причина, слово-виновник
/// и ИМЯ поля, в котором слово нашлось. Ни путей на диске, ни имён файлов, ни серийников шкафов,
/// ни номеров заявок — и САМОГО ЗАПРОСА тоже нет: по номеру заявки и по серийнику ищут постоянно,
/// то есть строка запроса ровно та же тайна, что и поля, которые мы отсюда и выбрасываем. От
/// запроса остаются число слов и режим — этого хватает, чтобы понять, что случай про обычный
/// поиск, а не про пустой с фильтрами.</summary>
public sealed class SearchTrace
{
    /// <summary>Сколько строк попало в список подробностей. Записи читают глазами; полная опись
    /// выдачи на широком запросе («1») — это тысячи строк, в которых разбирать нечего. Примеров
    /// хватает первых двух десятков.</summary>
    public const int MaxDetails = 20;

    /// <summary>Режим поиска: «обычный», «точный», «пустой запрос». Нужен, чтобы не разбирать
    /// случай не тем правилом.</summary>
    [JsonPropertyName("mode")] public string Mode { get; set; } = "";

    /// <summary>Сколько слов было в запросе. Сами слова — см. рассуждение в заголовке класса.</summary>
    [JsonPropertyName("queryWords")] public int QueryWords { get; set; }

    [JsonPropertyName("filtered")] public bool Filtered { get; set; }
    [JsonPropertyName("showAllVersions")] public bool ShowAllVersions { get; set; }

    /// <summary>Сколько слов-исключений действовало на этот поиск (сами слова попадают в запись
    /// только как виновники конкретных спрятанных строк).</summary>
    [JsonPropertyName("onDemandWords")] public int OnDemandWords { get; set; }

    [JsonPropertyName("indexRows")] public int IndexRows { get; set; }
    [JsonPropertyName("passedFilters")] public int PassedFilters { get; set; }
    [JsonPropertyName("passedWords")] public int PassedWords { get; set; }
    [JsonPropertyName("matchedQuery")] public int MatchedQuery { get; set; }
    [JsonPropertyName("shown")] public int Shown { get; set; }

    [JsonPropertyName("droppedByFilters")] public int DroppedByFilters => IndexRows - PassedFilters;
    [JsonPropertyName("hiddenByWordCount")] public int HiddenByWordCount { get; set; }
    [JsonPropertyName("droppedByQuery")] public int DroppedByQuery => Math.Max(0, PassedWords - MatchedQuery);
    [JsonPropertyName("collapsedCount")] public int CollapsedCount { get; set; }

    /// <summary>Строки, спрятанные правилом «слова по запросу», с виновным словом.</summary>
    [JsonPropertyName("hiddenByWord")] public List<SearchTraceRow> HiddenByWord { get; set; } = new();

    /// <summary>Строки, выбывшие при схлопывании, с причиной.</summary>
    [JsonPropertyName("collapsed")] public List<SearchTraceRow> Collapsed { get; set; } = new();

    /// <summary>ГЛАВНЫЙ ПРИЗНАК разбираемого случая: при схлопывании выбыла версия, которая СВЕЖЕЕ
    /// показанной. Обычное схлопывание (свежая вытеснила прежние) происходит на каждом втором
    /// поиске и интереса не представляет вовсе; а вот «показана старая, свежая выброшена» — это
    /// ровно то, на что жалуются. Сравнение по дате сборки (dt_str), а не по номеру: номера живут
    /// внутри своей hw-линейки и между линейками несравнимы, а «залита позже» — факт общий.</summary>
    [JsonPropertyName("fresherLost")] public bool FresherLost { get; set; }

    public bool AnythingHidden => HiddenByWordCount > 0 || FresherLost;

    public void NoteHiddenByWord(SearchTraceRow row)
    {
        HiddenByWordCount++;
        if (HiddenByWord.Count < MaxDetails) HiddenByWord.Add(row);
    }

    public void NoteCollapsed(SearchTraceRow row)
    {
        CollapsedCount++;
        if (Collapsed.Count < MaxDetails) Collapsed.Add(row);
    }

    /// <summary>Какие слова спрятали хоть что-то — для подсказки в выдаче («скрыто 2 версии по
    /// слову „Рх“»). Считается по списку примеров, а не по счётчику: слово у них одно и то же,
    /// а обрезка списка на двадцатой строке набор слов не меняет.</summary>
    public IReadOnlyList<string> HidingWords =>
        HiddenByWord.Select(r => r.Word).Where(w => !string.IsNullOrEmpty(w))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList()!;

    /// <summary>ОТПЕЧАТОК СЛУЧАЯ — «это та же беда или уже другая».
    ///
    /// По нему запись не отправляется второй раз (см. SearchTraceLedger). Считается от того, ЧТО
    /// спрятано и почему, а не от того, кто и когда искал: один и тот же человек повторит тот же
    /// запрос десять раз подряд, пока не поверит, что прошивки нет, — и все десять раз это один и
    /// тот же случай, присылать который десятикратно значит сделать канал нечитаемым.</summary>
    public string Fingerprint()
    {
        var sb = new StringBuilder();
        sb.Append(Mode).Append('|').Append(FresherLost ? '1' : '0').Append('|');
        foreach (var row in HiddenByWord.Concat(Collapsed)
                     .Select(r => $"{r.Reason}~{r.Word}~{r.Field}~{r.Subtype}~{r.Controller}~{r.Version}")
                     .OrderBy(s => s, StringComparer.Ordinal))
            sb.Append(row).Append(';');

        var bytes = System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()));
        return Convert.ToHexString(bytes, 0, 8).ToLowerInvariant();
    }

    private static readonly JsonSerializerOptions Json = new()
    {
        // Кириллица читаемой: записи смотрят глазами не реже, чем программой (как у слепка диска).
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = true,
    };

    /// <summary>Запись целиком — то, что уезжает. Имя машины и время добавляются здесь, а не
    /// хранятся в самой трассе: трасса — про поиск, а не про того, кто искал.</summary>
    public string ToJson(string machine, string appVersion, DateTime takenAtUtc) =>
        JsonSerializer.Serialize(new SearchTraceReport
        {
            Machine = machine,
            AppVersion = appVersion,
            TakenAt = takenAtUtc.ToString("O"),
            Case = Fingerprint(),
            Search = this,
        }, Json);
}

/// <summary>Одна строка, не попавшая в выдачу. Только опознавательное — см. рассуждение «чего
/// здесь нет» в <see cref="SearchTrace"/>.</summary>
public sealed record SearchTraceRow(
    [property: JsonPropertyName("version")] string Version,
    [property: JsonPropertyName("subtype")] string Subtype,
    [property: JsonPropertyName("controller")] string Controller,
    [property: JsonPropertyName("reason")] string Reason,
    [property: JsonPropertyName("word")] string? Word = null,
    [property: JsonPropertyName("field")] string? Field = null);

/// <summary>Причины выбытия — одними и теми же словами в записи, в подсказке и в тестах.</summary>
public static class SearchDropReasons
{
    public const string OnDemandWord = "слово по запросу";
    public const string Superseded = "заменена более свежей";
    public const string LostByRank = "проиграла по рангу";
    public const string DuplicateConfig = "дубль конфигурации";
}

/// <summary>Обёртка записи: трасса плюс «кто и когда».</summary>
public sealed class SearchTraceReport
{
    [JsonPropertyName("machine")] public string Machine { get; set; } = "";
    [JsonPropertyName("appVersion")] public string AppVersion { get; set; } = "";
    [JsonPropertyName("takenAt")] public string TakenAt { get; set; } = "";
    [JsonPropertyName("case")] public string Case { get; set; } = "";
    [JsonPropertyName("search")] public SearchTrace? Search { get; set; }
}
