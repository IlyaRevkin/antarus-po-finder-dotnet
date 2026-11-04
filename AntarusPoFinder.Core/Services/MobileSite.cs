using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using AntarusPoFinder.Core.Data;
using AntarusPoFinder.Core.Domain;

namespace AntarusPoFinder.Core.Services;

/// <summary>ЧТО ВИДИТ ТЕЛЕФОН СЕРВИСНИКА: разметка страниц мобильного доступа и разбор адреса.
///
/// <b>Зачем.</b> Тикет Ильи: «сделать мобильную версию, чтобы сервис с телефона мог посмотереть к
/// примеру параметры пч упп, найти прошивку и указать баг в ней, посмотреть входа выхода, актуальную
/// версию прошивки». В шкафу на объекте ноутбук держать негде, а база прошивок лежит в профиле
/// пользователя и на конторском диске — снаружи до неё хода нет. Разбор того, почему страницу
/// раздаёт САМ Finder по локальной сети, а не хостинг, — в docs/mobile-access-plan.md.
///
/// <b>Почему разметка в Core, а сокет — в приложении.</b> Чтобы страницу можно было проверять
/// тестами без сети и без WPF, и чтобы при смене транспорта (если доступ однажды вынесут за пределы
/// офиса) переписывать пришлось ровно транспорт. Здесь нет ни одного обращения к сокету, к файловой
/// системе и к интерфейсу — на входе база, путь и строка запроса, на выходе HTML.
///
/// <b>База читается НЕ из потока сервера.</b> <see cref="Database"/> держит одно соединение SQLite
/// без блокировок, поэтому <see cref="Render"/> обязан звать тот же поток, который работает с базой
/// в приложении (страница заворачивает вызов в Dispatcher). Навязать это отсюда нельзя — отметка
/// затем, чтобы следующий вызывающий не решил, что сюда можно ходить из пула потоков.</summary>
public static class MobileSite
{
    /// <summary>Сколько находок показывать. На телефоне длинный список бесполезен — листать его в
    /// шкафу некому, уточнить запрос дешевле.</summary>
    public const int MaxResults = 30;

    /// <summary>Ответ страницы. Код нужен ровно для 404 — всё остальное отдаётся как 200 со
    /// страницей, объясняющей, что не нашлось: ошибка HTTP на телефоне выглядит как «сломалось», а
    /// «ничего не найдено» — как нормальный ответ на запрос.</summary>
    public sealed record Page(int Status, string Html);

    /// <summary>Разобрать путь после токена и отдать страницу.</summary>
    /// <param name="path">Хвост адреса БЕЗ токена и без query: «» или «v/17».</param>
    /// <param name="queryString">То, что после «?», как пришло с провода.</param>
    /// <param name="linkBase">Префикс, с которого начинаются все ссылки страницы («/&lt;токен&gt;»).
    /// Токен не константа и меняется от машины к машине, зашивать его в разметку здесь нельзя.</param>
    public static Page Render(Database db, string? path, string? queryString, string linkBase)
    {
        var trimmed = (path ?? "").Trim('/');
        var query = ParseQuery(queryString);

        if (trimmed.Length == 0)
            return new Page(200, SearchPage(db, query.GetValueOrDefault("q", ""), linkBase));

        // Параметры ПЧ/УПП живут отдельным адресом, а не вторым режимом того же: закладка на
        // телефоне одна, а наладчик у шкафа приходит либо за прошивкой, либо за настройками, и
        // возвращаться хочет туда же, откуда ушёл.
        if (trimmed == "p")
            return new Page(200, ParamSearchPage(db, query.GetValueOrDefault("q", ""), linkBase));

        if (trimmed.StartsWith("v/", StringComparison.Ordinal) &&
            int.TryParse(trimmed[2..], NumberStyles.Integer, CultureInfo.InvariantCulture, out var id))
            return CardPage(db, id, linkBase);

        if (trimmed.StartsWith("t/", StringComparison.Ordinal) &&
            int.TryParse(trimmed[2..], NumberStyles.Integer, CultureInfo.InvariantCulture, out var tableId))
            return ParamTablePage(db, tableId, query.GetValueOrDefault("f", ""),
                query.GetValueOrDefault("q", ""), linkBase);

        return new Page(404, Shell("Не найдено", $"""
            <p class="muted">Такой страницы нет.</p>
            <p><a class="back" href="{Esc(linkBase)}/">← к поиску</a></p>
            """));
    }

    // ── Поиск ────────────────────────────────────────────────────────────────

    private static string SearchPage(Database db, string q, string linkBase)
    {
        var body = new StringBuilder();
        body.Append(Nav(linkBase, q, paramMode: false));
        body.Append(SearchForm($"{Esc(linkBase)}/", q, "Шкаф, контроллер, версия"));

        if (string.IsNullOrWhiteSpace(q))
        {
            body.Append("""<p class="muted">Введите, что искать, — так же, как в программе на компьютере.</p>""");
            return Shell("Поиск прошивки", body.ToString());
        }

        var hits = SearchService.Search(db, q);
        if (hits.Count == 0)
        {
            body.Append("""<p class="muted">Ничего не нашлось.</p>""");
            return Shell("Поиск прошивки", body.ToString());
        }

        body.Append($"""<p class="muted">Найдено: {hits.Count}</p><ul class="list">""");
        foreach (var hit in hits.Take(MaxResults))
        {
            var marks = new StringBuilder();
            if (hit.IsOpc) marks.Append("""<span class="mark opc">ОПЦ</span>""");
            if (!string.IsNullOrWhiteSpace(hit.Execution))
                marks.Append($"""<span class="mark">{Esc(hit.Execution)}</span>""");

            body.Append($"""
                <li><a href="{Esc(linkBase)}/v/{hit.FwVersionId}">
                <span class="name">{Esc(hit.Name)}</span>
                <span class="ver">{Esc(hit.VersionRaw)}</span>{marks}
                </a></li>
                """);
        }
        body.Append("</ul>");
        if (hits.Count > MaxResults)
            body.Append($"""<p class="muted">Показаны первые {MaxResults} — уточните запрос.</p>""");

        return Shell("Поиск прошивки", body.ToString());
    }

    // ── Карточка версии ──────────────────────────────────────────────────────

    private static Page CardPage(Database db, int id, string linkBase)
    {
        var row = db.GetFwVersionById(id);
        if (row is null)
            return new Page(404, Shell("Версия не найдена", $"""
                <p class="muted">Такой версии в базе нет.</p>
                <p><a class="back" href="{Esc(linkBase)}/">← к поиску</a></p>
                """));

        var names = db.GetFwVersionNames(id);
        var title = names is null
            ? row.VersionRaw
            : $"{names.Value.SubtypeName} {names.Value.ControllerName}".Trim();

        // «Актуальная версия» — главное, ради чего карточку и открывают с телефона. Считается НЕ по
        // номеру: текущей может быть отмеченная руками версия, а у разных исполнений и HW текущая
        // своя. Единственное место, где это правило живёт, — FwHistoryStatus, и считать его заново
        // здесь было бы вторым, расходящимся ответом на тот же вопрос.
        var history = db.GetFwVersionsHistory(row.SubtypeId, row.ControllerId);
        var labels = FwHistoryStatus.Labels(history);
        var status = "";
        string? currentRaw = null;
        var currentId = 0;
        for (var i = 0; i < history.Count; i++)
        {
            if (history[i].Id == id) status = labels[i];
            if (currentRaw is null && FwHistoryStatus.IsCurrent(labels[i]))
            {
                currentRaw = history[i].VersionRaw;
                currentId = history[i].Id ?? 0;
            }
        }

        var body = new StringBuilder();
        body.Append($"""<h1>{Esc(title)}</h1><p class="big">{Esc(row.VersionRaw)}</p>""");

        if (status.Length > 0)
            body.Append($"""
                <p><span class="badge {(FwHistoryStatus.IsCurrent(status) ? "ok" : "warn")}">{Esc(status)}</span></p>
                """);

        if (currentRaw is not null && currentId != id)
            body.Append($"""
                <p class="notice">Актуальная сейчас — <a href="{Esc(linkBase)}/v/{currentId}">{Esc(currentRaw)}</a></p>
                """);

        if (row.IsOpc)
            body.Append("""
                <p class="notice opc">Разовая прошивка под конкретный шкаф (ОПЦ) — другим шкафам не ставится.</p>
                """);

        var rows = new List<(string Key, string Value)>();
        if (names is not null)
        {
            rows.Add(("Оборудование", names.Value.GroupName));
            rows.Add(("Контроллер", names.Value.ControllerName));
        }
        if (!string.IsNullOrWhiteSpace(row.Execution)) rows.Add(("Исполнение", row.Execution));
        rows.Add(("Железо", $"HW {row.HwVersion}"));
        if (!string.IsNullOrWhiteSpace(row.DtStr)) rows.Add(("Дата версии", row.DtStr));
        if (!string.IsNullOrWhiteSpace(row.UploadDate)) rows.Add(("Загружена", row.UploadDate));
        if (!string.IsNullOrWhiteSpace(row.AuthorName)) rows.Add(("Загрузил", row.AuthorName));
        if (row.IsOpc && !string.IsNullOrWhiteSpace(row.RequestNum)) rows.Add(("Заявка", row.RequestNum));
        if (row.IsOpc && !string.IsNullOrWhiteSpace(row.CabinetSn)) rows.Add(("Шкаф №", row.CabinetSn));
        if (!string.IsNullOrWhiteSpace(row.Tags)) rows.Add(("Теги", row.Tags));

        body.Append("<dl>");
        foreach (var (key, value) in rows)
            body.Append($"<dt>{Esc(key)}</dt><dd>{Esc(value)}</dd>");
        body.Append("</dl>");

        if (!string.IsNullOrWhiteSpace(row.Description))
            body.Append($"""<h2>Описание</h2><p>{Esc(row.Description)}</p>""");

        body.Append($"""<p><a class="back" href="{Esc(linkBase)}/">← к поиску</a></p>""");
        return new Page(200, Shell(title, body.ToString()));
    }

    // ── Параметры ПЧ/УПП ─────────────────────────────────────────────────────
    //
    // Жалоба Ильи дословно: «я моб версию посмотрел, там нет поиска параметров упп ПЧ, а это
    // важно». Важно потому, что прошивку с телефона смотрят изредка («актуальная ли?»), а
    // настройки частотника — каждый раз, когда стоят у шкафа с отвёрткой.

    /// <summary>Сколько найденных документов показывать. Меньше, чем прошивок: у каждого документа
    /// под заголовком идут ещё и совпавшие строки, и тридцать таких карточек — экран, который
    /// листают дольше, чем набирают уточнённый запрос.</summary>
    public const int MaxTables = 15;

    /// <summary>Сколько совпавших строк показать прямо в выдаче. Ровно затем, зачем они показаны и
    /// в программе: ответ «нашёлся документ „Задание Modbus“» без строки, из-за которой он нашёлся,
    /// заставляет открывать документ и искать глазами заново.</summary>
    public const int MaxHitRows = 4;

    private static string ParamSearchPage(Database db, string q, string linkBase)
    {
        var body = new StringBuilder();
        body.Append(Nav(linkBase, q, paramMode: true));
        body.Append(SearchForm($"{Esc(linkBase)}/p", q, "P0-10, частота, шкаф"));

        if (string.IsNullOrWhiteSpace(q))
        {
            body.Append("""
                <p class="muted">Код настройки («P0-10»), название параметра или шкаф — так же, как
                в программе на компьютере.</p>
                """);
            return Shell("Параметры ПЧ/УПП", body.ToString());
        }

        // ТОТ ЖЕ движок, что и в программе: SearchView.PerformParamTablesSearch зовёт ровно это.
        // Второй реализации поиска здесь быть не должно — наладчик ищет «P0-10» и по названию
        // параметра, и правило «строки, совпавшие со всеми словами запроса» (BestMatchingRows)
        // живёт в Database. Поправка на раскладку — общая (SearchService), без вопроса «это точно
        // оно?»: на телефоне спрашивать некого, а подмена срабатывает только когда как набрано не
        // нашлось ничего.
        var hits = SearchService.SearchWithLayoutFallback(q, false, (query, exact) =>
            db.SearchParamTablesByTokens(
                SearchService.Normalize(query).Split(' ', StringSplitOptions.RemoveEmptyEntries), exact));

        if (hits.Count == 0)
        {
            body.Append("""<p class="muted">Ничего не нашлось.</p>""");
            return Shell("Параметры ПЧ/УПП", body.ToString());
        }

        body.Append($"""<p class="muted">Найдено документов: {hits.Count}</p><ul class="list">""");
        foreach (var hit in hits.Take(MaxTables))
        {
            var head = hit.Table.Manufacturer.Length > 0
                ? $"{hit.Table.Name} [{hit.Table.Manufacturer}]"
                : hit.Table.Name;

            body.Append($"""<li><a href="{Esc(linkBase)}/t/{hit.Table.Id}">""");
            body.Append($"""<span class="name">{Esc(head)}</span>""");
            if (hit.Subtypes.Length > 0)
                body.Append($"""<span class="sub">{Esc(hit.Subtypes)}</span>""");
            foreach (var row in hit.Rows.Take(MaxHitRows))
                body.Append($"""
                    <span class="hitrow"><b>{Esc(row.Code)}</b> {Esc(row.Title)}
                    <i>{Esc(row.Kind == ParamRowKind.Note ? "" : row.ValueDisplay)}</i></span>
                    """);
            if (hit.Rows.Count > MaxHitRows)
                body.Append($"""<span class="sub">…и ещё {hit.Rows.Count - MaxHitRows} строк</span>""");
            body.Append("</a></li>");
        }
        body.Append("</ul>");
        if (hits.Count > MaxTables)
            body.Append($"""<p class="muted">Показаны первые {MaxTables} — уточните запрос.</p>""");

        return Shell("Параметры ПЧ/УПП", body.ToString());
    }

    /// <summary>Сам документ: строки последней живой редакции с отбором по назначению.</summary>
    /// <param name="purpose">Токен назначения из адреса («commissioning», «service», пусто — все).
    /// В адресе токен, а не подпись: подпись — это текст для человека, он может и поменяться, а
    /// закладка на «мой набор» переживать правку текста обязана.</param>
    /// <param name="back">Запрос, с которого сюда пришли, — чтобы «← к поиску» вернуло к выдаче, а
    /// не к пустому полю: на телефоне набирать «P0-10» заново дороже всего остального.</param>
    private static Page ParamTablePage(Database db, int tableId, string purpose, string back, string linkBase)
    {
        var table = db.GetParamTable(tableId);
        if (table is null || table.DeletedAt.Length > 0)
            return new Page(404, Shell("Документ не найден", $"""
                <p class="muted">Такой таблицы параметров в базе нет.</p>
                <p><a class="back" href="{Esc(linkBase)}/p">← к поиску параметров</a></p>
                """));

        var title = table.Name.Length > 0 ? table.Name : "Таблица параметров";
        var all = db.LatestParamTableRows(tableId);

        // Подпись отбора ищется по токену среди ОПЦИЙ ПРОГРАММЫ, а не собирается здесь: и список
        // пунктов, и правило отбора («неразмеченная строка видна всегда») живут в ParamPurposes и
        // проверены тестами. Неизвестный токен в адресе — это «все строки», а не ошибка: ссылку
        // правят руками, и падать из-за опечатки странице незачем.
        var option = ParamPurposes.Options()
            .FirstOrDefault(o => string.Equals(ParamPurposes.TokenOf(o), purpose, StringComparison.Ordinal))
            ?? ParamPurposes.AnyLabel;
        var shown = ParamPurposes.Filter(all, option);
        var ordered = ParamTableEditing.Ordered(shown, db.GetParamGroupOrder());
        var blocks = ParamTableView.Blocks(ordered);

        var body = new StringBuilder();
        body.Append($"""<h1>{Esc(title)}</h1>""");

        var head = new List<string>();
        if (table.Manufacturer.Length > 0) head.Add(table.Manufacturer);
        var subtypes = string.Join(", ", ParamTableBinding.Of(db, table).Links.Select(l => l.Display));
        if (subtypes.Length > 0) head.Add(subtypes);
        if (head.Count > 0) body.Append($"""<p class="muted">{Esc(string.Join(" · ", head))}</p>""");

        // Отбор — ссылками, а не выпадающим списком: список на телефоне требует два касания и
        // перекрывает содержимое, а здесь это три слова в ряд, и выбранное видно без открывания.
        body.Append("""<div class="tabs">""");
        foreach (var opt in ParamPurposes.Options())
        {
            var token = ParamPurposes.TokenOf(opt);
            var href = $"{Esc(linkBase)}/t/{tableId}?f={Esc(token)}" +
                       (back.Length > 0 ? "&amp;q=" + Esc(Uri.EscapeDataString(back)) : "");
            var cls = string.Equals(opt, option, StringComparison.Ordinal) ? "tab on" : "tab";
            body.Append($"""<a class="{cls}" href="{href}">{Esc(opt)}</a>""");
        }
        body.Append("</div>");

        var unmarked = ParamPurposes.Unmarked(all);
        if (ParamPurposes.TokenOf(option).Length > 0 && unmarked > 0)
            body.Append($"""
                <p class="muted">Без разметки: {unmarked} — показаны тоже, иначе параметр остался бы
                невыставленным.</p>
                """);

        if (ordered.Count == 0)
            body.Append("""<p class="muted">В этой редакции строк нет.</p>""");

        var group = "";
        var block = -1;
        for (var i = 0; i < ordered.Count; i++)
        {
            var row = ordered[i];
            if (!string.Equals(row.GroupName, group, StringComparison.OrdinalIgnoreCase))
            {
                group = row.GroupName;
                if (group.Length > 0) body.Append($"<h2>{Esc(group)}</h2>");
            }
            if (blocks[i].Index != block)
            {
                block = blocks[i].Index;
                // Применимость и условие — подзаголовок над строками, а не поле каждой из них: так
                // они записаны в исходнике, и так их не потеряешь. «Только для ПЧ №1», пропущенное
                // на телефоне, — это параметр, выставленный не тому аппарату.
                if (!blocks[i].IsPlain) body.Append($"""<p class="blk">{Esc(blocks[i].Title)}</p>""");
            }
            body.Append(ParamRowCard(row));
        }

        body.Append($"""
            <p><a class="back" href="{Esc(linkBase)}/p{(back.Length > 0 ? "?q=" + Esc(Uri.EscapeDataString(back)) : "")}">← к поиску параметров</a></p>
            """);
        return new Page(200, Shell(title, body.ToString()));
    }

    /// <summary>ОДНА СТРОКА ПАРАМЕТРА НА УЗКОМ ЭКРАНЕ — карточкой, а не ячейками таблицы.
    ///
    /// <b>Почему не таблица.</b> В документе у строки десять столбцов (группа, код, название,
    /// значение, заводское, единица, назначение, применимость, условие, описание). На экране
    /// шириной 360 px это 36 px на столбец — не читается вообще, а горизонтальная прокрутка
    /// отрывает значение от кода: человек у шкафа держит телефон одной рукой, второй — отвёртку,
    /// и возить экран вбок ему нечем. Четыре из десяти столбцов при этом сняты ещё до показа:
    /// группа, применимость и условие стали заголовками (выше), единица приклеена к значению
    /// (ParamTableRow.ValueDisplay).
    ///
    /// <b>Что осталось и как уложено.</b> Три яруса сверху вниз, по убыванию того, ради чего
    /// строку читают:
    /// <list type="number">
    /// <item>КОД слева и ЗНАЧЕНИЕ справа — на одной строке и оба крупные. Это и есть вопрос
    ///       («что в P0-10?») и ответ на него; по коду же и ведут пальцем, пролистывая;</item>
    /// <item>название параметра — обычным текстом, с переносом: оно длинное и в ячейку не влезло
    ///       бы никогда;</item>
    /// <item>приглушённой строкой — заводское значение и назначение, то есть справка, которую
    ///       читают, только если первые два яруса не ответили.</item>
    /// </list>
    /// Описание идёт отдельным абзацем под ними: в программе оно приклеено к названию через тире
    /// (ParamTableView.Tail), но там строка длиной в экран, а здесь это второй-третий перенос —
    /// название растворилось бы в нём.
    ///
    /// <b>Пояснение (Kind=Note) показывается текстом без кода и значения</b> — у него их нет, и
    /// пустая рамка на месте значения читалась бы как «ничего не выставлять».</summary>
    private static string ParamRowCard(ParamTableRow row)
    {
        if (row.Kind == ParamRowKind.Note)
        {
            var note = row.Description.Length > 0 ? row.Description : row.Title;
            return $"""<p class="note">{Esc(note)}</p>""";
        }

        var sb = new StringBuilder();
        sb.Append("""<div class="prow">""");
        sb.Append($"""
            <div class="top"><span class="code">{Esc(row.Code)}</span>
            <span class="val">{Esc(row.ValueDisplay)}</span></div>
            """);
        if (row.Title.Length > 0) sb.Append($"""<div class="ttl">{Esc(row.Title)}</div>""");

        var marks = new List<string>();
        if (!string.IsNullOrWhiteSpace(row.Factory)) marks.Add("заводское: " + row.Factory);
        var purposeLabel = ParamPurposes.Label(row.Purpose);
        if (purposeLabel.Length > 0) marks.Add(purposeLabel);
        if (marks.Count > 0) sb.Append($"""<div class="sub">{Esc(string.Join(" · ", marks))}</div>""");

        if (!string.IsNullOrWhiteSpace(row.Description))
            sb.Append($"""<div class="desc">{Esc(row.Description)}</div>""");
        sb.Append("</div>");
        return sb.ToString();
    }

    // ── Разметка ─────────────────────────────────────────────────────────────

    /// <summary>Переключатель между прошивками и параметрами. Запрос переносится в ссылку: человек
    /// набрал слово не в том разделе, и заставлять его набирать то же самое заново на экранной
    /// клавиатуре — худшее, что можно сделать с поиском на телефоне.</summary>
    private static string Nav(string linkBase, string q, bool paramMode)
    {
        var tail = string.IsNullOrWhiteSpace(q) ? "" : "?q=" + Esc(Uri.EscapeDataString(q));
        return $"""
            <div class="tabs">
            <a class="tab{(paramMode ? "" : " on")}" href="{Esc(linkBase)}/{tail}">Прошивки</a>
            <a class="tab{(paramMode ? " on" : "")}" href="{Esc(linkBase)}/p{tail}">Параметры ПЧ/УПП</a>
            </div>
            """;
    }

    private static string SearchForm(string action, string q, string placeholder) => $"""
        <form method="get" action="{action}">
          <input class="q" type="search" name="q" value="{Esc(q)}" placeholder="{Esc(placeholder)}">
          <button class="go" type="submit">Найти</button>
        </form>
        """;


    /// <summary>Обёртка страницы. Стили внутри документа и ни одного внешнего файла — намеренно:
    /// телефон в цеху держит связь только с раздающим компьютером, интернета за ним может не быть
    /// вовсе, а отдельная раздача css — это ещё один маршрут, который нечем окупить на двух
    /// страницах. Тёмная палитра не выбор вкуса: экран смотрят в шкафу и на улице, и светлый фон
    /// там слепит сильнее, чем помогает.</summary>
    private static string Shell(string title, string body) => $$"""
        <!doctype html><html lang="ru"><head><meta charset="utf-8">
        <meta name="viewport" content="width=device-width, initial-scale=1">
        <title>{{Esc(title)}} — Antarus ПО Finder</title>
        <style>
        body{font-family:-apple-system,Segoe UI,Roboto,sans-serif;margin:0;padding:16px;
             background:#14161a;color:#e8eaed;font-size:17px;line-height:1.45}
        h1{font-size:21px;margin:0 0 4px}
        h2{font-size:17px;margin:20px 0 6px;color:#9aa0a6}
        .big{font-size:26px;font-weight:600;margin:0 0 12px;color:#8ab4f8}
        .muted{color:#9aa0a6}
        form{display:flex;gap:8px;margin:0 0 16px}
        .q{flex:1;min-width:0;padding:12px;font-size:17px;border-radius:10px;border:1px solid #3c4043;
           background:#1e2126;color:#e8eaed}
        .go{padding:12px 16px;font-size:17px;border:0;border-radius:10px;background:#8ab4f8;
            color:#14161a;font-weight:600}
        ul.list{list-style:none;margin:0;padding:0}
        ul.list li{margin:0 0 8px}
        ul.list a{display:block;padding:12px;border-radius:10px;background:#1e2126;color:#e8eaed;
                  text-decoration:none}
        .name{display:block;font-weight:600}
        .ver{display:inline-block;margin-top:4px;color:#8ab4f8}
        .mark{display:inline-block;margin:4px 0 0 8px;padding:1px 7px;border-radius:7px;
              background:#3c4043;color:#e8eaed;font-size:13px}
        .mark.opc{background:#b26a00;color:#fff}
        .badge{display:inline-block;padding:3px 10px;border-radius:8px;font-size:15px}
        .badge.ok{background:#1e4620;color:#b7f0b9}
        .badge.warn{background:#3c4043;color:#e8eaed}
        .notice{padding:10px 12px;border-radius:10px;background:#1e2126}
        .notice.opc{background:#4a2f00;color:#ffd79a}
        dl{display:grid;grid-template-columns:auto 1fr;gap:4px 14px;margin:14px 0}
        dt{color:#9aa0a6}
        dd{margin:0;overflow-wrap:anywhere}
        a{color:#8ab4f8}
        .back{display:inline-block;margin-top:18px}
        .tabs{display:flex;gap:8px;margin:0 0 14px;flex-wrap:wrap}
        .tab{padding:8px 12px;border-radius:999px;background:#1e2126;color:#9aa0a6;
             text-decoration:none;font-size:15px}
        .tab.on{background:#8ab4f8;color:#14161a;font-weight:600}
        .sub{display:block;margin-top:4px;color:#9aa0a6;font-size:15px}
        .hitrow{display:block;margin-top:6px;font-size:15px;color:#c8ccd1}
        .hitrow b{font-family:ui-monospace,Consolas,monospace;color:#e8eaed}
        .hitrow i{font-style:normal;color:#8ab4f8}
        .blk{margin:14px 0 6px;padding:6px 10px;border-radius:8px;background:#2a2016;color:#ffd79a;
             font-size:15px}
        .note{margin:10px 0;padding:10px 12px;border-radius:10px;background:#1a1d21;color:#c8ccd1}
        /* Карточка параметра. Крупный код слева и крупное значение справа — см. ParamRowCard о том,
           почему строку документа на телефоне показывают так, а не ячейками. */
        .prow{margin:0 0 8px;padding:10px 12px;border-radius:10px;background:#1e2126}
        .prow .top{display:flex;justify-content:space-between;align-items:baseline;gap:10px}
        .prow .code{font-family:ui-monospace,Consolas,monospace;font-size:18px;font-weight:600}
        .prow .val{font-size:19px;font-weight:600;color:#8ab4f8;text-align:right;
                   overflow-wrap:anywhere}
        .prow .ttl{margin-top:2px}
        .prow .desc{margin-top:4px;color:#9aa0a6;font-size:15px}
        </style></head><body>{{body}}</body></html>
        """;

    /// <summary>Экранирование ВСЕГО, что идёт из базы и из строки запроса. Описания прошивок, теги и
    /// имена пишут руками, и одна угловая скобка в описании ломала бы страницу; а строка запроса,
    /// которая возвращается в поле ввода, без этого — прямая дыра под чужой скрипт.</summary>
    public static string Esc(string? s) => string.IsNullOrEmpty(s)
        ? ""
        : s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;")
           .Replace("\"", "&quot;").Replace("'", "&#39;");

    /// <summary>Разбор query-строки. Свой, а не HttpUtility.ParseQueryString: Core — библиотека без
    /// System.Web, а нужен ровно один параметр. «+» как пробел — то, что шлёт обычная GET-форма.</summary>
    public static Dictionary<string, string> ParseQuery(string? queryString)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (string.IsNullOrEmpty(queryString)) return result;

        foreach (var pair in queryString.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var idx = pair.IndexOf('=');
            var key = idx < 0 ? pair : pair[..idx];
            var value = idx < 0 ? "" : pair[(idx + 1)..];
            // Битая процентная последовательность приходит от кого угодно (телефон не единственный,
            // кто может постучаться) и не должна ронять страницу — такой параметр просто пропускаем.
            try { result[Decode(key)] = Decode(value); }
            catch (UriFormatException) { }
        }
        return result;
    }

    private static string Decode(string s) => Uri.UnescapeDataString(s.Replace('+', ' '));
}
