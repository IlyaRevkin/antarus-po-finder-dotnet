using System.Text;

namespace AntarusPoFinder.Core.Services;

/// <summary>Раскладка слепков диска в бакете и правила их хранения.
///
/// Отдельно от <see cref="DiskSnapshot"/> и от выкладки: имена объектов и «сколько держать» — это
/// решение, которое читают и люди, и код по ту сторону, и менять его на ходу нельзя.</summary>
public static class DiskSnapshotStorage
{
    /// <summary>Своя верхняя папка, не вперемешку с тикетами и инструкциями: у слепков другой смысл
    /// жизни (их перетирают), другая частота и другой объём.</summary>
    public const string RootFolder = "disk";

    /// <summary>Сколько датированных слепков держать на машину. Две недели ежесуточных — этого
    /// хватает, чтобы ответить «когда это сломалось», и не превращает папку в свалку. «Последний»
    /// лежит отдельно и не считается.</summary>
    public const int KeepDated = 14;

    /// <summary>Всегда самый свежий, под постоянным именем. Нужен затем, чтобы читающей стороне не
    /// приходилось сперва листать бакет: один заранее известный адрес — и слепок на руках.</summary>
    public static string LatestKey(string machine) => $"{RootFolder}/{SanitizeMachine(machine)}/latest.json.gz";

    /// <summary>Датированная копия того же слепка. Время в имени — UTC: машины стоят в одном
    /// часовом поясе сегодня, но складывать в одну папку снимки с разными представлениями о
    /// «сейчас» — это готовая путаница в порядке.</summary>
    public static string DatedKey(string machine, DateTime takenAtUtc) =>
        $"{RootFolder}/{SanitizeMachine(machine)}/{takenAtUtc:yyyyMMdd_HHmmss}.json.gz";

    public static string MachineFolder(string machine) => $"{RootFolder}/{SanitizeMachine(machine)}/";

    /// <summary>Имя машины в ключе бакета. Кириллица, пробелы и точки в именах объектов рано или
    /// поздно превращаются в разъехавшиеся адреса (ровно поэтому у выкладки инструкций есть
    /// TranslitMap), а имя компьютера человек задаёт как хочет. Поэтому здесь — только латиница,
    /// цифры, дефис и подчёркивание; всё прочее становится дефисом. Пустое имя — «unknown»: слепок
    /// без имени машины бесполезен, но терять его из-за этого глупо.</summary>
    public static string SanitizeMachine(string? machine)
    {
        var s = (machine ?? "").Trim().ToLowerInvariant();
        var sb = new StringBuilder(s.Length);
        foreach (var ch in s)
        {
            if (ch is >= 'a' and <= 'z' || ch is >= '0' and <= '9' || ch == '-' || ch == '_') sb.Append(ch);
            else sb.Append('-');
        }
        var result = sb.ToString().Trim('-');
        return result.Length == 0 ? "unknown" : result;
    }

    /// <summary>Какие объекты удалить, чтобы датированных осталось не больше <paramref name="keep"/>.
    ///
    /// «Последний» (latest.json.gz) не трогается никогда — он не датированный и не участвует в
    /// очереди. Сортировка по ИМЕНИ, а не по времени объекта в бакете: имя мы задали сами и оно
    /// отражает момент снятия слепка, тогда как время объекта — это момент загрузки, и у
    /// перезалитого вручную файла они разъезжаются.</summary>
    public static IReadOnlyList<string> ToPrune(IEnumerable<string> keys, int keep = KeepDated)
    {
        var dated = keys
            .Where(k => !k.EndsWith("/latest.json.gz", StringComparison.Ordinal))
            .Where(k => k.EndsWith(".json.gz", StringComparison.Ordinal))
            .OrderByDescending(k => k, StringComparer.Ordinal)
            .ToList();

        return dated.Count <= Math.Max(0, keep)
            ? Array.Empty<string>()
            : dated.Skip(Math.Max(0, keep)).ToList();
    }
}
