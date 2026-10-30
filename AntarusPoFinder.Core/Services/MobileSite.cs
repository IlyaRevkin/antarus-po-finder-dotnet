using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using AntarusPoFinder.Core.Data;

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

        if (trimmed.StartsWith("v/", StringComparison.Ordinal) &&
            int.TryParse(trimmed[2..], NumberStyles.Integer, CultureInfo.InvariantCulture, out var id))
            return CardPage(db, id, linkBase);

        return new Page(404, Shell("Не найдено", $"""
            <p class="muted">Такой страницы нет.</p>
            <p><a class="back" href="{Esc(linkBase)}/">← к поиску</a></p>
            """));
    }

    // ── Поиск ────────────────────────────────────────────────────────────────

    private static string SearchPage(Database db, string q, string linkBase)
    {
        var body = new StringBuilder();
        body.Append($"""
            <form method="get" action="{Esc(linkBase)}/">
              <input class="q" type="search" name="q" value="{Esc(q)}" placeholder="Шкаф, контроллер, версия">
              <button class="go" type="submit">Найти</button>
            </form>
            """);

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

    // ── Разметка ─────────────────────────────────────────────────────────────

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
