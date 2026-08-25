using System.Text.RegularExpressions;
using AntarusPoFinder.Core.Domain;

namespace AntarusPoFinder.Core.Services;

/// <summary>Разбор ТЕКСТОВОГО файла параметров ПЧ/УПП — того самого, который до сих пор открывали
/// блокнотом — в строки таблицы.
///
/// Без импорта вся затея не стоит ничего: накопленное за годы осталось бы снаружи, а таблицу
/// пришлось бы набивать заново с нуля. Поэтому разбор ПРОЩАЕТ ВСЁ: неизвестную строку он не
/// выбрасывает, а кладёт пояснением, и человек правит результат в предпросмотре ДО сохранения.
///
/// <b>Три вида скобок в исходнике означают три РАЗНЫЕ вещи</b>, и это суть формата, а не украшение:
/// <list type="bullet">
/// <item><c>=====[Настройка ШУ]</c> — СЕКЦИЯ, из неё получается группа таблицы;</item>
/// <item><c>-----[Для ПЧ от 18,5 кВт]</c> — ПОДГРУППА ПО УСЛОВИЮ: строка нужна не всегда
///       (ParamTableRow.AppliesWhen);</item>
/// <item><c>&lt;&lt;&lt;[Только для ПЧ №1]</c> … <c>&gt;&gt;&gt;</c> — ПРИМЕНИМОСТЬ к конкретному
///       аппарату (ParamTableRow.Applicability).</item>
/// </list>
/// Свалить всё три в плоский список — значит дать наладчику выставить параметр не тому частотнику.
/// Именно поэтому применимость и условие — свойства СТРОКИ, а не заголовки внутри таблицы.</summary>
public static class ParamTextParser
{
    /// <summary>Результат разбора. Warnings — не ошибки, а то, на что стоит взглянуть в
    /// предпросмотре: строка без кода, значение вида «(?)», не опознанная секция.</summary>
    public record ParseResult(string Title, List<ParamTableRow> Rows, List<string> Warnings);

    // «=====[Настройка ШУ]» — два и больше «=», дальше заголовок в квадратных скобках. Скобки
    // необязательны: попадаются строки «====== Двигатель ======».
    private static readonly Regex SectionRe = new(@"^\s*={2,}\s*(?:\[(?<t>[^\]]*)\]|(?<t>[^=\[\]]+?))\s*=*\s*$", RegexOptions.Compiled);

    // «----------------[Для ПЧ от 18,5 кВт]». Три и больше дефисов, чтобы не спутать с «- Мощность».
    private static readonly Regex ConditionRe = new(@"^\s*-{3,}\s*(?:\[(?<t>[^\]]*)\]|(?<t>[^-\[\]]+?))\s*-*\s*$", RegexOptions.Compiled);

    private static readonly Regex ApplyOpenRe = new(@"^\s*<{2,}\s*(?:\[(?<t>[^\]]*)\]|(?<t>[^<\[\]]+?))\s*$", RegexOptions.Compiled);
    private static readonly Regex ApplyCloseRe = new(@"^\s*>{2,}\s*$", RegexOptions.Compiled);

    /// <summary>Код настройки в начале строки. Широкий намеренно — у девяти производителей девять
    /// написаний: «P0-02», «PD-01», «F0.00», «b1-01», «H1-01», «1-20» (Danfoss/VEDA — вообще без
    /// букв). Общее у всех: необязательные буквы, необязательные цифры, разделитель «-» или «.» и
    /// цифры после него.
    ///
    /// Проверка «дальше пробел или скобка» обязательна и ловит именно то, на чём наивный вариант
    /// ломался: «8-N-1» (формат данных в описании другого параметра) без неё уезжало бы в код.</summary>
    private static readonly Regex CodeRe = new(@"^(?<code>[A-Za-z]{0,3}[0-9]{0,3}[-.][0-9]{1,3}[A-Za-z]?)(?=[\s(]|$)", RegexOptions.Compiled);

    /// <summary>Значение в скобках сразу за кодом. Пробел перед скобкой встречается ровно так же
    /// часто, как его отсутствие («PD-01 (3)» и «P0-02(2)» — соседние строки одного файла).</summary>
    private static readonly Regex ValueRe = new(@"^\s*\((?<v>[^)]*)\)", RegexOptions.Compiled);

    public static ParseResult Parse(string text)
    {
        var rows = new List<ParamTableRow>();
        var warnings = new List<string>();
        var title = "";

        var section = "";
        var group = ParamGroupCatalog.Main;
        var appliesWhen = "";
        var applicability = "";
        var sortOrder = 0;
        var seenSection = false;

        var lines = (text ?? "").Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');

        // Есть ли в файле вообще секции — решается ДО разбора, потому что от этого зависит судьба
        // самой первой содержательной строки. В живом файле это заголовок документа
        // («ESQ-230 - КПЧ(Задание Modbus) Новая серия ПЧ 2025г»), и он, как назло, разбирается
        // регулярным выражением кода: «ESQ-230» — буквы, дефис, цифры, ровно как «P0-02». Отличить
        // заголовок от параметра по нему самому нельзя; отличает его ПОЛОЖЕНИЕ — до первой секции.
        // Секций нет вовсе (кто-то вёл файл сплошным списком) — заголовка тоже нет, и первая строка
        // остаётся обычным параметром, а не пропадает в названии документа.
        var hasSections = lines.Any(l => SectionRe.IsMatch(l.TrimEnd()));

        foreach (var raw in lines)
        {
            var line = raw.TrimEnd();
            if (line.Trim().Length == 0) continue;
            // Строка-разделитель без единой буквы («=====», «*****», «-----»): ни секция, ни
            // параметр, ни пояснение. Молча пропускаем — иначе она осела бы пояснением в таблице.
            if (!line.Any(char.IsLetterOrDigit)) continue;

            var m = SectionRe.Match(line);
            if (m.Success)
            {
                section = m.Groups["t"].Value.Trim();
                group = ParamGroupCatalog.Suggest(section);
                // Условие живёт ДО КОНЦА СЕКЦИИ и сбрасывается новой секцией: «Для 55 Гц» в конце
                // «Настройки ШУ» к идущему следом «Двигателю» отношения не имеет.
                appliesWhen = "";
                seenSection = true;
                if (group == ParamGroupCatalog.Other && section.Length > 0)
                    warnings.Add($"Секция «{section}» в справочнике групп не опознана — предложено «{ParamGroupCatalog.Other}».");
                continue;
            }

            m = ConditionRe.Match(line);
            if (m.Success)
            {
                appliesWhen = m.Groups["t"].Value.Trim();
                continue;
            }

            m = ApplyOpenRe.Match(line);
            if (m.Success)
            {
                applicability = m.Groups["t"].Value.Trim();
                continue;
            }

            if (ApplyCloseRe.IsMatch(line))
            {
                applicability = "";
                continue;
            }

            // Заголовок документа — первая содержательная строка ДО первой секции (см. hasSections
            // выше). Дальше такие строки уже пояснения, а не название.
            if (hasSections && !seenSection && rows.Count == 0 && title.Length == 0)
            {
                title = line.Trim();
                continue;
            }

            var body = line.TrimStart();
            var indented = line.Length != body.Length;

            var code = CodeRe.Match(body);
            if (!code.Success)
            {
                // Строка с отступом сразу под параметром — это ПРОДОЛЖЕНИЕ его описания, а не новая
                // строка таблицы: «P0-19 - Единица времени…» и следующей строкой с отступом
                // «По умолчанию 1 (0=1с, 1=0.1с, 2=0.01с)». Оторви её — и параметр останется без
                // единственного пояснения, ради которого его и записали.
                if (indented && rows.Count > 0)
                {
                    var prev = rows[^1];
                    prev.Description = prev.Description.Length == 0
                        ? body.Trim()
                        : prev.Description + " " + body.Trim();
                    continue;
                }

                // Не код и не продолжение — указание наладчику простым текстом («В ПЛК выставить
                // частоту 55Гц»). Оно ОБЯЗАНО остаться: без него подгруппа «Для 55 Гц» теряет смысл.
                rows.Add(new ParamTableRow
                {
                    SortOrder = sortOrder++,
                    Kind = ParamRowKind.Note,
                    GroupName = group,
                    Title = body.Trim(),
                    ValueState = ParamValueState.Set,
                    Applicability = applicability,
                    AppliesWhen = appliesWhen,
                });
                continue;
            }

            var rest = body[code.Length..];
            var value = "";
            var state = ParamValueState.OnSite;

            var v = ValueRe.Match(rest);
            if (v.Success)
            {
                rest = rest[v.Length..];
                var inner = v.Groups["v"].Value.Trim();
                if (inner == "?")
                {
                    state = ParamValueState.Ask;
                    warnings.Add($"{code.Groups["code"].Value}: значение помечено «?» — уточнить по ПЛК.");
                }
                else
                {
                    value = inner;
                    state = ParamValueState.Set;
                }
            }

            // Тире между значением и названием то есть, то нет — «P5-22 (00001) Выбор логики…»
            // написано без него, соседние строки с ним. Отсутствие тире не повод потерять строку.
            rest = rest.TrimStart();
            if (rest.StartsWith('-') || rest.StartsWith('–') || rest.StartsWith('—'))
                rest = rest[1..].TrimStart();

            var (name, description) = SplitNameAndDescription(rest.Trim());

            rows.Add(new ParamTableRow
            {
                SortOrder = sortOrder++,
                Kind = ParamRowKind.Param,
                GroupName = group,
                Code = code.Groups["code"].Value,
                Title = name,
                Value = value,
                ValueState = state,
                Description = description,
                Applicability = applicability,
                AppliesWhen = appliesWhen,
            });
        }

        if (applicability.Length > 0)
            warnings.Add($"Блок «{applicability}» не закрыт строкой «>>>» — применимость проставлена до конца файла.");

        return new ParseResult(title, rows, warnings);
    }

    /// <summary>Разделить хвост строки на НАЗВАНИЕ и ОПИСАНИЕ. Разделителем служит либо « - », либо
    /// «: », но только когда левая часть похожа на название: без двоеточий, без запятых и не длиннее
    /// разумного.
    ///
    /// Оговорка не косметическая. «P1-37 - Автонастройка: 1 - без вращения, 2 - с вращением»: наивное
    /// деление по первому « - » дало бы название «Автонастройка: 1» и описание «без вращения, 2 - с
    /// вращением» — бессмыслицу в самой заметной колонке таблицы. С проверкой левая часть первого
    /// « - » («Автонастройка: 1») отвергается из-за двоеточия, и делит уже «: » — название
    /// «Автонастройка», описание «1 - без вращения, 2 - с вращением».
    ///
    /// Ошибиться разбор всё равно может — на то в импорте и есть предпросмотр с правкой.</summary>
    internal static (string Name, string Description) SplitNameAndDescription(string rest)
    {
        if (rest.Length == 0) return ("", "");

        foreach (var separator in new[] { " - ", " – ", " — " })
        {
            var at = rest.IndexOf(separator, StringComparison.Ordinal);
            if (at > 0 && LooksLikeName(rest[..at]))
                return (rest[..at].Trim(), rest[(at + separator.Length)..].Trim());
        }

        var colon = rest.IndexOf(": ", StringComparison.Ordinal);
        if (colon > 0 && LooksLikeName(rest[..colon]))
            return (rest[..colon].Trim(), rest[(colon + 2)..].Trim());

        return (rest, "");
    }

    private static bool LooksLikeName(string candidate)
    {
        var text = candidate.Trim();
        return text.Length > 0
               && text.Length <= 60
               && !text.Contains(':')
               && !text.Contains(',');
    }
}
