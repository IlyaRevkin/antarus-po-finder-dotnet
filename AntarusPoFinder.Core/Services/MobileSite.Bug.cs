using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using AntarusPoFinder.Core.Data;
using AntarusPoFinder.Core.Domain;

namespace AntarusPoFinder.Core.Services;

/// <summary>ЕДИНСТВЕННОЕ, ЧТО ТЕЛЕФОН УМЕЕТ ЗАПИСАТЬ: жалоба на прошивку («указать баг в ней» из
/// тикета Ильи).
///
/// <b>Почему это выделено отдельно и написано осторожнее остального.</b> Всё прочее в мобильном
/// доступе — чтение: худшее, что делает кривой запрос, — это некрасивая страница. Здесь из чужой
/// сети в рабочую базу попадает ЗАПИСЬ, которая потом уедет к коллегам и в хранилище. Поэтому
/// рамки заданы жёстко и в одном месте:
/// <list type="bullet">
/// <item><description>завести можно РОВНО тикет типа «баг прошивки» (<see cref="TicketType.FwBug"/>)
///       и ничего больше: ни смены статуса, ни реплики, ни правки прошивки. Тип не приходит из
///       запроса вовсе — он прошит здесь, и подменить его нечем;</description></item>
/// <item><description>жалоба обязана быть привязана к СУЩЕСТВУЮЩЕЙ версии из базы: тикет «баг
///       прошивки» без прошивки — это жалоба неизвестно на что, разбирать её
///       некому;</description></item>
/// <item><description>текст ограничен <see cref="MaxBugChars"/> знаками, а размер тела запроса и
///       частота отправки — транспортом (MobileAccessServer): база не должна ни распухнуть, ни
///       встать от потока с чужого устройства;</description></item>
/// <item><description>тикет заводится ТЕМ ЖЕ путём, что и в программе (TicketSyncService.CreateTicket
///       через переданный <c>create</c>), — иначе он остался бы виден только на этой машине и не
///       уехал бы ни в очередь на диск, ни в автовыгрузку.</description></item>
/// </list>
///
/// <b>Кто автор.</b> Автором остаётся владелец сессии Finder, чьим токеном открыта страница, —
/// именно он, а не «никто», отвечает за то, что заведено с его телефона. Строку автора при этом НЕ
/// переписываем пометкой про телефон, хотя соблазн есть: по точному совпадению с ней наладчик видит
/// «свои» тикеты и может закрыть ошибочно заведённый (TicketVisibility.CanModerate), и жалоба,
/// поданная с телефона, иначе переставала бы быть его собственной ровно в тот момент, когда он
/// вернулся к компьютеру её поправить. Пометка уходит в текст (<see cref="PhoneMark"/>) — там её
/// видно всем и везде, куда тикет приедет.</summary>
public static partial class MobileSite
{
    /// <summary>Потолок длины жалобы. Две тысячи знаков — это полстраницы текста, больше с телефона
    /// у шкафа никто не наберёт; а предел нужен затем, чтобы в базу нельзя было влить роман.</summary>
    public const int MaxBugChars = 2000;

    /// <summary>Всё, что нужно, чтобы завести тикет, — и ничего сверх того. Отдельной записью,
    /// чтобы приём с телефона не получал на руки ни базу, ни сервисы приложения: он может завести
    /// ровно то, что здесь описано.</summary>
    public sealed record BugDraft(int FwVersionId, string FwSyncId, string FwLabel, string Severity, string Text);

    /// <summary>Бланк жалобы. Версия подставлена и НЕ редактируется с телефона: выбирать прошивку из
    /// списка на экране у шкафа — верный способ выбрать соседнюю, а сюда приходят с её карточки.</summary>
    /// <param name="error">Что не так с прошлой попыткой. Текст и критичность при этом возвращаются
    /// в поля: заставить человека набирать жалобу заново на экранной клавиатуре — значит не получить
    /// её вовсе.</param>
    public static Page BugFormPage(Database db, int fwVersionId, string linkBase,
        string error, string draftText, string draftSeverity)
    {
        var label = BugLabel(db, fwVersionId);
        if (label is null)
            return new Page(404, Shell("Баг прошивки", $"""
                <p class="muted">Жалоба заводится на конкретную версию, а этой в базе нет. Откройте
                версию поиском и нажмите «Сообщить о баге прошивки» на её карточке.</p>
                <p><a class="back" href="{Esc(linkBase)}/">← к поиску</a></p>
                """));

        var severity = FwBugSeverity.Normalize(draftSeverity);
        if (severity.Length == 0) severity = FwBugSeverity.Major;

        var body = new StringBuilder();
        body.Append("<h1>Баг прошивки</h1>");
        body.Append($"""<p class="muted">{Esc(label)}</p>""");
        if (error.Length > 0) body.Append($"""<p class="err">{Esc(error)}</p>""");

        body.Append($"""<form method="post" action="{Esc(linkBase)}/bug">""");
        body.Append($"""<input type="hidden" name="v" value="{fwVersionId}">""");

        // Критичность — те же три уровня и теми же словами, что в программе (FwBugSeverity): жалоба
        // с телефона попадает в тот же список, и четвёртого уровня «с телефона» там быть не должно.
        body.Append("""<div class="sevs">""");
        foreach (var (id, text) in FwBugSeverity.All)
            body.Append($"""
                <label><input type="radio" name="sev" value="{Esc(id)}"
                {(string.Equals(id, severity, StringComparison.Ordinal) ? " checked" : "")}> {Esc(text)}</label>
                """);
        body.Append("</div>");

        body.Append($"""
            <textarea name="text" maxlength="{MaxBugChars}"
              placeholder="Что происходит, на каком шкафу, при каких условиях">{Esc(draftText)}</textarea>
            <button class="send" type="submit">Отправить</button>
            </form>
            <p class="muted">Жалоба попадёт в тот же список, что и заведённые с компьютера, —
            программист увидит её во вкладке «Баги прошивок».</p>
            """);
        body.Append($"""<p><a class="back" href="{Esc(linkBase)}/v/{fwVersionId}">← к версии</a></p>""");
        return new Page(200, Shell("Баг прошивки", body.ToString()));
    }

    /// <summary>Приём заполненного бланка. Разбирает тело запроса, проверяет всё, что можно
    /// проверить без приложения, и только потом зовёт <paramref name="create"/> — настоящее
    /// заведение тикета (TicketSyncService.CreateTicket).
    ///
    /// Второй реализации заведения тикета здесь нет и быть не должно: этот метод НИЧЕГО не пишет в
    /// базу сам. Иначе тикет с телефона не попал бы ни в очередь на сетевой диск, ни в автовыгрузку,
    /// и остался бы виден только на одной машине — ровно та ошибка, от которой заведена единственная
    /// точка входа в TicketSyncService.</summary>
    /// <param name="body">Тело POST-запроса как пришло (<c>v=1&amp;sev=major&amp;text=…</c>).</param>
    /// <param name="who">Чьим доступом заведено — имя сессии Finder, раздающей страницу. Уходит в
    /// текст пометкой (см. <see cref="PhoneMark"/>), чтобы в списке не появлялись тикеты ниоткуда.</param>
    /// <param name="create">Заводит тикет и говорит, получилось ли. false — приложение по какой-то
    /// причине отказало; человеку в этом случае показывается его же текст, а не «отправлено».</param>
    public static Page SubmitBug(Database db, string? body, string linkBase, string who,
        Func<BugDraft, bool> create)
    {
        var form = ParseQuery(body);
        var id = Int(form.GetValueOrDefault("v", ""));
        var severity = FwBugSeverity.Normalize(form.GetValueOrDefault("sev", ""));
        var text = (form.GetValueOrDefault("text", "") ?? "").Trim();

        var label = BugLabel(db, id);
        if (label is null)
            return BugFormPage(db, id, linkBase, "", text, severity);

        if (text.Length == 0)
            return BugFormPage(db, id, linkBase,
                "Опишите, что не так, — жалоба без текста программисту ничего не говорит.", text, severity);

        if (text.Length > MaxBugChars)
            return BugFormPage(db, id, linkBase,
                $"Слишком длинно: {text.Length} знаков при пределе {MaxBugChars}. Коротко: что, где и когда.",
                text[..MaxBugChars], severity);

        if (severity.Length == 0) severity = FwBugSeverity.Major;

        var draft = new BugDraft(id, db.GetFwVersionSyncId(id), label, severity, PhoneMark(text, who));
        if (!create(draft))
            return BugFormPage(db, id, linkBase,
                "Не удалось завести тикет — попробуйте ещё раз или заведите его с компьютера.", text, severity);

        return new Page(200, Shell("Баг прошивки", $"""
            <h1>Баг прошивки</h1>
            <p class="ok">Жалоба заведена. Она уехала туда же, куда и заведённые с компьютера.</p>
            <p class="muted">{Esc(label)}</p>
            <p><a class="action" href="{Esc(linkBase)}/v/{id}">К версии</a></p>
            <p><a class="back" href="{Esc(linkBase)}/">← к поиску</a></p>
            """));
    }

    /// <summary>Пометка «заведено с телефона» — в САМ текст жалобы, а не в поле автора (почему
    /// именно так — в описании класса). Токен в пометку не попадает: тикет уезжает к коллегам и в
    /// хранилище, а токен — это пропуск к базе на чьём-то компьютере.</summary>
    public static string PhoneMark(string text, string who)
    {
        var mark = string.IsNullOrWhiteSpace(who)
            ? "— заведено с телефона через мобильный доступ"
            : $"— заведено с телефона через мобильный доступ {who.Trim()}";
        return text + "\n\n" + mark;
    }

    /// <summary>Подпись прошивки для тикета — тем же способом, что и жучок на карточке в программе
    /// (<see cref="FwBugLabel"/>), чтобы одна и та же версия в списке багов называлась одинаково,
    /// откуда бы жалобу ни завели. null — версии в базе нет.</summary>
    private static string? BugLabel(Database db, int fwVersionId)
    {
        if (fwVersionId <= 0) return null;
        var row = db.GetFwVersionById(fwVersionId);
        if (row is null) return null;

        var names = db.GetFwVersionNames(fwVersionId);
        var name = names is null
            ? ""
            : string.Join(" ", new[] { names.Value.GroupName, names.Value.SubtypeName, names.Value.ControllerName }
                .Where(p => !string.IsNullOrWhiteSpace(p)));
        return FwBugLabel.Build(name, row.VersionRaw, row.Execution);
    }

    private static int Int(string? s) =>
        int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : 0;
}
