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
/// Именно поэтому применимость и условие — свойства СТРОКИ, а не заголовки внутри таблицы.
///
/// ⚠️ <b>Правила ниже выведены не из головы, а из прогона по всему накопленному на диске</b>
/// (≈90 файлов в «Прочее\!Файлы параметров ПЧ»). До этого прогона разбор находил НОЛЬ параметров в
/// 16 файлах из 93 — целые семейства (INNOVERT, ABB, Vacon) написаны в форме, которой прежний
/// разбор не знал вовсе. Отсюда и широта: сноски звёздочкой, кириллические двойники латинских букв
/// в кодах, код без разделителя, табуляция вместо тире.</summary>
public static class ParamTextParser
{
    /// <summary>Результат разбора. Warnings — не ошибки, а то, на что стоит взглянуть в
    /// предпросмотре: строка без кода, значение вида «(?)», не опознанная секция.</summary>
    public record ParseResult(string Title, List<ParamTableRow> Rows, List<string> Warnings);

    // ── Кириллические двойники латинских букв ────────────────────────────────────────────────

    /// <summary>Буквы, которые в кириллице и латинице выглядят ОДИНАКОВО. В живых файлах коды
    /// набраны то так, то этак — «С00.16» у VEDA написано русской «С», «Р2-28» русской «Р», а в
    /// самом частотнике это латинские C00.16 и P2-28. Разбор их обязан и узнавать, и приводить к
    /// латинице: код — это то, что человек ВБИВАЕТ в аппарат, и русская буква там означает, что
    /// набрать его по таблице нельзя.</summary>
    private const string CyrillicLookalikes = "АВЕКМНОРСТУХавекмнорстух";
    private const string LatinLookalikes    = "ABEKMHOPCTYXabekmhopctyx";

    /// <summary>Латиница плюс её кириллические двойники — из чего может состоять код.</summary>
    private const string CodeLetter = "[A-Za-z" + CyrillicLookalikes + "]";

    internal static string ToLatinCode(string code)
    {
        var chars = code.ToCharArray();
        for (var i = 0; i < chars.Length; i++)
        {
            var at = CyrillicLookalikes.IndexOf(chars[i]);
            if (at >= 0) chars[i] = LatinLookalikes[at];
        }
        return new string(chars);
    }

    // ── Разметка файла ───────────────────────────────────────────────────────────────────────

    // «=====[Настройка ШУ]» — два и больше «=», дальше заголовок в квадратных скобках. Скобки
    // необязательны: попадаются строки «====== Двигатель ======».
    private static readonly Regex SectionRe = new(@"^\s*={2,}\s*(?:\[(?<t>[^\]]*)\]|(?<t>[^=\[\]]+?))\s*=*\s*$", RegexOptions.Compiled);

    /// <summary>Секция, отбитая ТОЛЬКО квадратными скобками: «[Мотор]». Так написаны файлы
    /// INNOVERT — решёток из «=» там нет вовсе, и без этого правила «Мотор» оседал пояснением, а
    /// все его параметры уходили в «Основные настройки» вместе с половиной файла.</summary>
    private static readonly Regex BracketSectionRe = new(@"^\s*\[(?<t>[^\[\]]+)\]\s*$", RegexOptions.Compiled);

    // «----------------[Для ПЧ от 18,5 кВт]». Три и больше дефисов, чтобы не спутать с «- Мощность».
    private static readonly Regex ConditionRe = new(@"^\s*-{3,}\s*(?:\[(?<t>[^\]]*)\]|(?<t>[^-\[\]]+?))\s*-*\s*$", RegexOptions.Compiled);

    private static readonly Regex ApplyOpenRe = new(@"^\s*<{2,}\s*(?:\[(?<t>[^\]]*)\]|(?<t>[^<\[\]]+?))\s*$", RegexOptions.Compiled);
    private static readonly Regex ApplyCloseRe = new(@"^\s*>{2,}\s*$", RegexOptions.Compiled);

    /// <summary>Хвостовые «&gt;&gt;&gt;» у открывающей строки. Блок бывает написан В ОДНУ строку:
    /// «&lt;&lt;&lt;&lt;&lt; Для схем без HL1, HL2 - (11) Отсутствие сухого хода &gt;&gt;&gt;&gt;&gt;».
    /// Прежний разбор принимал такую строку за ОТКРЫТИЕ блока — и применимость (вместе с прилипшими
    /// к ней «&gt;&gt;&gt;&gt;&gt;») растекалась до конца файла, помечая чужие строки чужим
    /// аппаратом.</summary>
    private static readonly Regex ApplyTailRe = new(@"\s*>{2,}\s*$", RegexOptions.Compiled);

    /// <summary>Пояснение к сноске: «* - при использовании AO вместо ModBus», «** - Для прошивок…».
    /// Живёт обычно в самом низу файла, а помеченные им строки — наверху, поэтому сноски
    /// собираются ОТДЕЛЬНЫМ проходом до разбора.</summary>
    private static readonly Regex FootnoteRe = new(@"^\s*(?<m>\*{1,3})\s*[-–—:]\s*(?<t>.+)$", RegexOptions.Compiled);

    /// <summary>Условие в скобках ПЕРЕД кодом: «(если требуется) U0-15 (55) - Максимальная частота».
    /// Без него вся строка уходила пояснением, то есть параметр терялся совсем.</summary>
    private static readonly Regex LeadingParenRe = new(@"^\((?<t>[^()]{1,60})\)[\s\t]+", RegexOptions.Compiled);

    /// <summary>Код настройки в начале строки. Широкий намеренно — у девяти производителей девять
    /// написаний, и каждое из перечисленных ниже взято с живого файла:
    /// <list type="bullet">
    /// <item><c>P0-02</c>, <c>PD-01</c>, <c>F0.00</c>, <c>b1-01</c>, <c>U0.18</c>, <c>P 17.2</c>
    ///       (Vacon пишет код с пробелом) — буквы, необязательные цифры, разделитель, цифры;</item>
    /// <item><c>1-20</c> — Danfoss/VEDA вообще без букв;</item>
    /// <item><c>Pa00</c>, <c>PC09</c>, <c>Pd25</c> — INNOVERT пишет БЕЗ разделителя;</item>
    /// <item><c>1611</c>, <c>9905</c> — ABB пишет четырьмя цифрами и только ими.</item>
    /// </list>
    /// Две последние формы и были причиной «файл разобрался, а параметров ноль»: без них
    /// не читались ВСЕ файлы INNOVERT и ABB.
    ///
    /// Проверка «дальше пробел, скобка или двоеточие» обязательна и ловит именно то, на чём наивный
    /// вариант ломался: «8-N-1» (формат данных в описании другого параметра) без неё уезжало бы в
    /// код, а «4-20мА» и «0-10В» — становились бы кодами «4-20» и «0-10».
    ///
    /// Порядок ветвей значим: форма с разделителем проверяется РАНЬШЕ формы без него, иначе «P0-02»
    /// прочиталось бы как «P0» + мусор.</summary>
    private static readonly Regex CodeRe = new(
        @"^(?<code>"
        + CodeLetter + @"{1,3}\s?[0-9]{0,3}[-.][0-9]{1,3}" + CodeLetter + @"?"
        + @"|[0-9]{1,3}[-.][0-9]{1,3}" + CodeLetter + @"?"
        + @"|" + CodeLetter + @"{1,3}[0-9]{2,4}"
        + @"|[0-9]{4}"
        + @")(?=[\s(:]|$)", RegexOptions.Compiled);

    /// <summary>Значение в скобках сразу за кодом. Пробел перед скобкой встречается ровно так же
    /// часто, как его отсутствие («PD-01 (3)» и «P0-02(2)» — соседние строки одного файла).</summary>
    private static readonly Regex ValueRe = new(@"^\s*\((?<v>[^)]*)\)", RegexOptions.Compiled);

    /// <summary>Число и единица одной строкой: «0.5 сек», «10 Bar», «2 сек». Единица в файлах
    /// пишется В СКОБКАХ ВМЕСТЕ со значением, а столбец «Ед.» до этого не заполнялся вовсе — ни
    /// разбором, ни чем-либо ещё.</summary>
    private static readonly Regex ValueWithUnitRe = new(@"^(?<n>[-+]?[0-9]+(?:[.,][0-9]+)?)\s+(?<u>\S{1,10})$", RegexOptions.Compiled);

    /// <summary>Единица, приписанная к НАЗВАНИЮ через запятую: «Максимальная частота, Гц»,
    /// «Номинальный ток двигателя, А». Список закрытый намеренно: без него в единицу уезжал бы
    /// любой хвост после последней запятой.</summary>
    private static readonly string[] KnownUnits =
    {
        "Гц", "кГц", "В", "кВ", "А", "мА", "кВт", "Вт", "с", "сек", "мс", "мин", "ч",
        "%", "об/мин", "Bar", "бар", "МПа", "кПа", "Ом", "кОм", "Нм",
    };

    private static readonly Regex TrailingUnitRe = new(@"^(?<t>.+?),\s*(?<u>[^\s,]{1,7})$", RegexOptions.Compiled);

    /// <summary>Значение-заполнитель: не число, а указание СНЯТЬ его на объекте. «(По шильду)»,
    /// «(Настраивается по месту)» — так написана добрая половина параметров двигателя. Прежде они
    /// попадали в таблицу как обычное значение, то есть наладчик читал «выставить „По шильду“».</summary>
    private static readonly string[] OnSiteHints = { "по месту", "на месте", "шильд", "настраивается" };

    // ── Разбор ───────────────────────────────────────────────────────────────────────────────

    public static ParseResult Parse(string text)
    {
        var rows = new List<ParamTableRow>();
        var warnings = new List<string>();
        var title = "";

        var lines = (text ?? "").Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');

        // Пояснения к сноскам собираются ДО разбора: помеченные строки стоят наверху файла, а сами
        // пояснения — под ними, и на один проход их не хватает.
        var footnotes = CollectFootnotes(lines);

        // Есть ли в файле секции и есть ли в нём вообще параметры — решается ДО разбора, потому что
        // от этого зависит судьба самой первой содержательной строки. В живом файле это заголовок
        // документа («ESQ-230 - КПЧ(Задание Modbus) Новая серия ПЧ 2025г»), и он, как назло,
        // разбирается регулярным выражением кода: «ESQ-230» — буквы, дефис, цифры, ровно как
        // «P0-02». Отличить заголовок от параметра по нему самому нельзя; отличает его ПОЛОЖЕНИЕ —
        // до первой секции.
        var hasSections = lines.Any(l => SectionRe.IsMatch(l.TrimEnd()) || BracketSectionRe.IsMatch(l.TrimEnd()));
        // Файлов без единой решётки на диске большинство, и у них заголовок тоже есть («Vacon 20 -
        // КПЧ», «Innovert ТГР», «M740 НГР X»). Их признак другой — см. LooksLikeDocumentTitle.
        var hasParams = lines.Any(HasCode);
        var mainPrefix = DominantCodePrefix(lines);

        var section = "";
        var group = ParamGroupCatalog.Main;
        var appliesWhen = "";
        var applicability = "";
        var sortOrder = 0;
        var seenSection = false;
        var missingFootnotes = new HashSet<string>();

        for (var index = 0; index < lines.Length; index++)
        {
            var line = lines[index].TrimEnd();
            if (line.Trim().Length == 0) continue;

            // Закрывающая «>>>» проверяется ПЕРВОЙ, до отсева украшений: букв и цифр в ней нет ни
            // одной, и вместе с «=====» её отсеивало как украшение — применимость («Только для
            // ПЧ №1») после этого молча растекалась до конца файла, помечая чужие строки чужим
            // аппаратом. Ровно то, ради предотвращения чего применимость и заведена.
            if (ApplyCloseRe.IsMatch(line))
            {
                applicability = "";
                continue;
            }

            // Строка-разделитель без единой буквы («=====», «*****», «-----», «_____»): ни секция,
            // ни параметр, ни пояснение. Молча пропускаем — иначе она осела бы пояснением.
            if (!line.Any(char.IsLetterOrDigit)) continue;

            // Пояснение к сноске уже собрано первым проходом и в таблицу отдельной строкой не идёт:
            // его текст стоит в столбце «Когда нужно» у тех строк, к которым оно относится.
            if (FootnoteRe.IsMatch(line)) continue;

            var m = SectionRe.Match(line);
            if (!m.Success) m = BracketSectionRe.Match(line);
            if (m.Success)
            {
                section = m.Groups["t"].Value.Trim();
                group = GroupFor(section, warnings);
                // Условие живёт ДО КОНЦА СЕКЦИИ и сбрасывается новой секцией: «Для 55 Гц» в конце
                // «Настройки ШУ» к идущему следом «Двигателю» отношения не имеет.
                appliesWhen = "";
                seenSection = true;
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
                var inner = ApplyTailRe.Replace(m.Groups["t"].Value.Trim(), "").Trim();
                if (ApplyTailRe.IsMatch(line))
                {
                    // Блок открыли и закрыли одной строкой — это не «дальше идут строки такого-то
                    // аппарата», а замечание само по себе («для схем без HL1, HL2 поставьте 11»).
                    // Пометить им следующие строки значило бы соврать про них.
                    rows.Add(NewNote(inner, group, applicability, appliesWhen, sortOrder++));
                }
                else applicability = inner;
                continue;
            }

            var body = line.TrimStart();
            var indented = line.Length != body.Length;

            // Сноска («*U0-11 (2) - …») и условие в скобках («(если требуется) U0-15 …») стоят
            // ПЕРЕД кодом и до разбора должны быть сняты — иначе код не находится вовсе.
            var probe = body;
            var marker = TakeFootnoteMarker(ref probe);
            var leadingCondition = TakeLeadingCondition(ref probe);

            var code = CodeRe.Match(probe);

            // Заголовок документа. Двумя правилами: в файле с секциями — первая содержательная
            // строка до первой секции (заголовок там похож на код и иначе не отличим), в файле без
            // секций — первая строка БЕЗ кода.
            if (title.Length == 0 && rows.Count == 0
                && (hasSections ? !seenSection : hasParams && LooksLikeDocumentTitle(probe, code, mainPrefix)))
            {
                title = body.Trim();
                continue;
            }

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

                // Заголовок раздела, написанный простым текстом: «Параметры электродвигателя»,
                // «Общие настройки:», «Master:». Так отбита большая часть накопленных файлов, и без
                // этого правила ВЕСЬ файл оказывался одной группой «Основные настройки» — то самое
                // «визуально нет разделения разделов».
                if (LooksLikeBareHeading(lines, index, body))
                {
                    section = body.Trim().TrimEnd(':').Trim();
                    // Про свою группу здесь молчим: сообщение ниже уже говорит о ней, и два подряд
                    // про одну и ту же строку читаются как две разные беды.
                    group = GroupFor(section, null);
                    appliesWhen = "";
                    seenSection = true;
                    warnings.Add($"Строка «{section}» принята за раздел таблицы (группа «{group}»). Если это пояснение, а не заголовок — верните её строкой.");
                    continue;
                }

                // Не код, не продолжение и не заголовок — указание наладчику простым текстом («В ПЛК
                // выставить частоту 55Гц»). Оно ОБЯЗАНО остаться: без него подгруппа «Для 55 Гц»
                // теряет смысл.
                rows.Add(NewNote(body.Trim(), group, applicability, appliesWhen, sortOrder++));
                continue;
            }

            var rest = probe[code.Length..];
            var value = "";
            var unit = "";
            var state = ParamValueState.OnSite;
            var codeText = ToLatinCode(code.Groups["code"].Value);

            var found = TakeBracketValue(ref rest, ref value, ref state, codeText, warnings);
            if (!found)
            {
                // Тире между кодом и значением то есть, то нет — снимаем и пробуем скобку ещё раз:
                // «P 1.1 -<tab>(По шильду)» именно так и написано.
                rest = StripLeadingDash(rest.TrimStart());
                found = TakeBracketValue(ref rest, ref value, ref state, codeText, warnings);
            }
            if (!found) TakeTabValue(ref rest, ref value, ref state);

            // Тире между значением и названием то есть, то нет — «P5-22 (00001) Выбор логики…»
            // написано без него, соседние строки с ним. Отсутствие тире не повод потерять строку.
            rest = StripLeadingDash(rest.TrimStart());

            var (name, description) = SplitNameAndDescription(rest.Trim());
            SplitTrailingUnit(ref name, ref unit);
            SplitValueUnit(ref value, ref unit);

            // Заполнитель вместо значения — это «снимается по месту», а не значение «По шильду».
            if (state == ParamValueState.Set && LooksLikePlaceholder(value))
            {
                if (description.Length == 0) description = value;
                value = "";
                state = ParamValueState.OnSite;
            }

            var rowCondition = Combine(appliesWhen, FootnoteText(marker, footnotes, missingFootnotes), leadingCondition);

            rows.Add(new ParamTableRow
            {
                SortOrder = sortOrder++,
                Kind = ParamRowKind.Param,
                GroupName = group,
                Code = codeText,
                Title = name,
                Value = value,
                Unit = unit,
                ValueState = state,
                Description = description,
                Applicability = applicability,
                AppliesWhen = rowCondition,
            });
        }

        if (applicability.Length > 0)
            warnings.Add($"Блок «{applicability}» не закрыт строкой «>>>» — применимость проставлена до конца файла.");
        foreach (var missing in missingFootnotes)
            warnings.Add($"Строки помечены сноской «{missing}», а пояснения к ней в файле нет — посмотрите, не потерялось ли оно.");

        return new ParseResult(title, rows, warnings);
    }

    /// <summary>Какую группу дать секции. Справочник знает — берём его написание; не знает — берём
    /// НАЗВАНИЕ САМОЙ СЕКЦИИ, как оно написано в файле.
    ///
    /// ⚠️ Раньше незнакомая секция уезжала в «Прочее», и это оказалось прямой жалобой владельца:
    /// «визуально нет разделения разделов, как в том же txt». В накопленных файлах разделы названы
    /// по-своему («Калибровка», «Master», «Меню настроек Fieldbus»), и свалив их все в одну группу,
    /// таблица теряла ровно то, ради чего исходник и был читаемым. Своё имя группы это возвращает:
    /// заголовок в таблице совпадает с заголовком в txt.
    ///
    /// Порядок показа от этого не страдает: новая группа заводится в справочнике (см.
    /// ParamTableEditing.SaveRevision) и встаёт перед «Сбросом до заводских», а переставить её
    /// человек может в Настройках → Иерархия → «Группы параметров ПЧ/УПП».</summary>
    private static string GroupFor(string section, List<string>? warnings)
    {
        var suggested = ParamGroupCatalog.Suggest(section);
        if (suggested != ParamGroupCatalog.Other || section.Length == 0) return suggested;

        warnings?.Add($"Раздел «{section}» в справочнике групп не значится — заведена группа с таким же названием. Проверьте её место в Настройках.");
        return section;
    }

    /// <summary>Буквенная приставка, с которой начинается большинство кодов файла («U» у M740, «P» у
    /// Innovance, пустая у ABB). Нужна ровно одному правилу — отличить заголовок документа от
    /// параметра, см. <see cref="LooksLikeDocumentTitle"/>.</summary>
    private static string DominantCodePrefix(string[] lines)
    {
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in lines.Skip(1))
        {
            var probe = line.TrimStart();
            if (probe.Length == 0) continue;
            TakeFootnoteMarker(ref probe);
            TakeLeadingCondition(ref probe);
            var m = CodeRe.Match(probe);
            if (!m.Success) continue;
            var prefix = new string(m.Groups["code"].Value.TakeWhile(char.IsLetter).ToArray());
            counts[prefix] = counts.GetValueOrDefault(prefix) + 1;
        }
        return counts.Count == 0 ? "" : counts.OrderByDescending(p => p.Value).First().Key;
    }

    /// <summary>Первая содержательная строка файла БЕЗ секций — это заголовок документа или всё-таки
    /// параметр?
    ///
    /// Просто «нет кода» не годится: «M740 НГР X» и «ESQ-230 - КПЧ…» разбираются регулярным
    /// выражением кода ровно как параметр — модель аппарата написана в той же форме, что и код
    /// настройки. Поэтому признаков два, и оба про то, чем заголовок ОТЛИЧАЕТСЯ от параметра:
    /// у него нет значения в скобках, и его «код» не похож на коды остального файла («M740» против
    /// «U0-04», «U1-00», «U2-15»).</summary>
    private static bool LooksLikeDocumentTitle(string probe, Match code, string mainPrefix)
    {
        if (!code.Success) return true;
        if (ValueRe.IsMatch(probe[code.Length..])) return false;

        var prefix = new string(code.Groups["code"].Value.TakeWhile(char.IsLetter).ToArray());
        return !string.Equals(prefix, mainPrefix, StringComparison.OrdinalIgnoreCase);
    }

    private static ParamTableRow NewNote(string text, string group, string applicability, string appliesWhen, int order) => new()
    {
        SortOrder = order,
        Kind = ParamRowKind.Note,
        GroupName = group,
        Title = text,
        ValueState = ParamValueState.Set,
        Applicability = applicability,
        AppliesWhen = appliesWhen,
    };

    private static Dictionary<string, string> CollectFootnotes(string[] lines)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var raw in lines)
        {
            var m = FootnoteRe.Match(raw.TrimEnd());
            if (!m.Success) continue;
            var text = m.Groups["t"].Value.Trim();
            if (text.Length == 0) continue;
            result[m.Groups["m"].Value] = text;
        }
        return result;
    }

    private static string FootnoteText(string marker, IReadOnlyDictionary<string, string> footnotes,
        ISet<string> missing)
    {
        if (marker.Length == 0) return "";
        if (footnotes.TryGetValue(marker, out var text)) return text;
        missing.Add(marker);
        // Пояснения не нашлось — сама пометка всё равно остаётся видимой: «строка не для всех»
        // важнее, чем «не знаем, для кого именно».
        return marker;
    }

    private static string TakeFootnoteMarker(ref string probe)
    {
        var stars = 0;
        while (stars < probe.Length && probe[stars] == '*') stars++;
        // Больше трёх звёзд подряд — это украшение («*****»), а не сноска.
        if (stars == 0 || stars > 3 || stars == probe.Length) return "";
        var rest = probe[stars..].TrimStart();
        if (rest.Length == 0) return "";
        probe = rest;
        return new string('*', stars);
    }

    private static string TakeLeadingCondition(ref string probe)
    {
        var m = LeadingParenRe.Match(probe);
        if (!m.Success) return "";
        var rest = probe[m.Length..];
        // Скобка перед кодом — условие; скобка перед текстом («(ПАРАМЕТРЫ С ШИЛЬДА ДВИГАТЕЛЯ)») —
        // обычное пояснение, и раскулачивать его нельзя.
        if (!CodeRe.IsMatch(rest)) return "";
        probe = rest;
        return m.Groups["t"].Value.Trim();
    }

    private static bool TakeBracketValue(ref string rest, ref string value, ref string state,
        string code, List<string> warnings)
    {
        var v = ValueRe.Match(rest);
        if (!v.Success) return false;

        rest = rest[v.Length..];
        var inner = v.Groups["v"].Value.Trim();
        if (inner == "?")
        {
            state = ParamValueState.Ask;
            warnings.Add($"{code}: значение помечено «?» — уточнить по ПЛК.");
        }
        else
        {
            value = inner;
            state = ParamValueState.Set;
        }
        return true;
    }

    /// <summary>Значение, отбитое от описания ТАБУЛЯЦИЕЙ: «P 17.2 - 0&lt;tab&gt;Скрыть часть
    /// параметров - выкл». Так написан весь Vacon, и файл у него сам объявляет этот формат первой
    /// строкой («Параметр - Значение &lt;tab&gt; Параметр - Описание настройки»).
    ///
    /// Именно табуляция, а не «два пробела подряд»: «Максимальное входное  напряжение» — обычная
    /// опечатка с двойным пробелом, и по ней половина названия уехала бы в значение.</summary>
    private static bool TakeTabValue(ref string rest, ref string value, ref string state)
    {
        var tab = rest.IndexOf('\t');
        if (tab <= 0) return false;

        var left = rest[..tab].Trim();
        var right = rest[(tab + 1)..].Trim();
        if (left.Length == 0 || left.Length > 24 || right.Length == 0) return false;

        value = left;
        state = ParamValueState.Set;
        rest = right;
        return true;
    }

    private static string StripLeadingDash(string rest) =>
        rest.StartsWith('-') || rest.StartsWith('–') || rest.StartsWith('—')
            ? rest[1..].TrimStart()
            : rest;

    private static void SplitValueUnit(ref string value, ref string unit)
    {
        if (unit.Length > 0 || value.Length == 0) return;
        var m = ValueWithUnitRe.Match(value);
        if (!m.Success) return;
        value = m.Groups["n"].Value;
        unit = m.Groups["u"].Value;
    }

    private static void SplitTrailingUnit(ref string name, ref string unit)
    {
        if (unit.Length > 0 || name.Length == 0) return;
        var m = TrailingUnitRe.Match(name);
        if (!m.Success) return;
        var candidate = m.Groups["u"].Value;
        if (!KnownUnits.Any(u => string.Equals(u, candidate, StringComparison.OrdinalIgnoreCase))) return;
        name = m.Groups["t"].Value.Trim();
        unit = candidate;
    }

    private static bool LooksLikePlaceholder(string value) =>
        value.Length > 0 && OnSiteHints.Any(h => value.Contains(h, StringComparison.OrdinalIgnoreCase));

    private static string Combine(params string[] parts) =>
        string.Join("; ", parts.Where(p => !string.IsNullOrWhiteSpace(p)).Select(p => p.Trim()).Distinct());

    internal static bool HasCode(string line)
    {
        var probe = line.TrimStart();
        if (probe.Length == 0) return false;
        TakeFootnoteMarker(ref probe);
        TakeLeadingCondition(ref probe);
        return CodeRe.IsMatch(probe);
    }

    /// <summary>Похожа ли строка на заголовок раздела, написанный простым текстом.
    ///
    /// Осторожность здесь дороже полноты: принятая за заголовок строка ИСЧЕЗАЕТ из таблицы, и если
    /// это было указание наладчику — оно потеряно. Поэтому признаков два, и оба сильные:
    /// либо строка кончается двоеточием («Общие настройки:», «Master:»), либо она стоит ОТДЕЛЬНО
    /// (перед ней пусто или украшение) и сразу за ней идёт параметр. Плюс общие оговорки: коротко,
    /// не предложение (нет точки/запятой на конце), не начинается со скобки.
    ///
    /// О каждом таком решении разбор говорит вслух (warnings) — человек видит его в предпросмотре
    /// до сохранения.</summary>
    private static bool LooksLikeBareHeading(string[] lines, int index, string body)
    {
        var text = body.Trim();
        if (text.Length == 0 || text.Length > 60) return false;
        if (text.StartsWith('(') || text.StartsWith('*')) return false;
        if (text.EndsWith('.') || text.EndsWith(',') || text.EndsWith(';') || text.EndsWith(')')) return false;
        if (text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length > 6) return false;

        if (text.EndsWith(':')) return true;

        // Перед заголовком — пустая строка, украшение или начало файла. Смотрим ИМЕННО на
        // предыдущую строку, а не на предыдущую содержательную: «отбит пустой строкой» — это и есть
        // весь признак, а через пропуск пустых он превращается в «где-то выше есть текст», то есть
        // не выполняется никогда.
        var before = index == 0 ? null : lines[index - 1].Trim();
        if (before is not null && before.Length > 0 && before.Any(char.IsLetterOrDigit)) return false;

        // А сразу за ним — параметр: заголовок без параметров под ним заголовком не бывает.
        var after = NextMeaningful(lines, index);
        return after is not null && HasCode(after);
    }

    private static string? NextMeaningful(string[] lines, int index)
    {
        for (var i = index + 1; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            if (line.Length == 0) continue;
            if (!line.Any(char.IsLetterOrDigit)) continue;
            return line;
        }
        return null;
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
