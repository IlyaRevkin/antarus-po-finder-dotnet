using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Sockets;
using System.Text;
using AntarusPoFinder.App.Services;
using AntarusPoFinder.Core.Data;
using AntarusPoFinder.Core.Domain;
using AntarusPoFinder.Core.Services;
using AntarusPoFinder.Tests.TestHelpers;
using Xunit;

namespace AntarusPoFinder.Tests;

/// <summary>ЖАЛОБА НА ПРОШИВКУ С ТЕЛЕФОНА («указать баг в ней» из тикета Ильи) — первая и
/// единственная запись, которую мобильный доступ принимает.
///
/// Проверяется ровно то, что без телефона иначе не проверишь ничем, и ровно то, чем эта запись
/// опасна: что принимается ТОЛЬКО жалоба и только с токеном, что она не может ни прийти без текста,
/// ни быть длиной в роман, ни сыпаться потоком, и что заведение идёт через переданную точку входа
/// (в приложении это TicketSyncService.CreateTicket), а не мимо неё.</summary>
public class MobileBugReportTests
{
    private const string Base = "/tok";

    private static int SeedVersion(Database db)
    {
        var group = db.GetAllEquipmentGroups().First(g => g.Name == "НГР");
        var subtype = db.GetSubtypesForGroup(group.Id!.Value).First(s => s.Name == "КНС");
        var mod = db.GetAllModifications().First(m => m.ControllerName == "SMH4");

        return db.AddFwVersion(new FwVersionRecord
        {
            SubtypeId = subtype.Id!.Value,
            ControllerId = mod.ControllerId,
            EqPrefix = group.Prefix,
            SubPrefix = subtype.Prefix,
            HwVersion = mod.HwVersion,
            SwVersion = 1,
            DtStr = "20260101_0000",
            VersionRaw = "2.1.0001.0001.20260101_0000",
            Filename = "fw.psl",
            Status = FwStatus.Active,
        });
    }

    private static string Body(int id, string text, string sev = "major") =>
        $"v={id}&sev={sev}&text={Uri.EscapeDataString(text)}";

    // ── Бланк и разбор ───────────────────────────────────────────────────────

    [Fact]
    public void Card_OffersTheBugForm_AndTheFormKnowsItsVersion()
    {
        using var dbFile = new TempDb();
        using var db = new Database(dbFile.Path);
        var id = SeedVersion(db);

        Assert.Contains($"{Base}/bug?v={id}", MobileSite.Render(db, $"v/{id}", "", Base).Html);

        var form = MobileSite.Render(db, "bug", $"?v={id}", Base);
        Assert.Equal(200, form.Status);
        Assert.Contains("method=\"post\"", form.Html);
        Assert.Contains($"value=\"{id}\"", form.Html);
        // Критичность — та же, что в программе, а не своя шкала «с телефона».
        foreach (var (sev, _) in FwBugSeverity.All) Assert.Contains($"value=\"{sev}\"", form.Html);
    }

    /// <summary>Жалоба без прошивки — это жалоба неизвестно на что: разбирать её некому.</summary>
    [Fact]
    public void Form_ForUnknownVersion_Is404()
    {
        using var dbFile = new TempDb();
        using var db = new Database(dbFile.Path);

        Assert.Equal(404, MobileSite.Render(db, "bug", "?v=999999", Base).Status);
        Assert.Equal(404, MobileSite.Render(db, "bug", "", Base).Status);
    }

    [Fact]
    public void Submit_CreatesExactlyOneFwBug_WithVersionAndSeverity()
    {
        using var dbFile = new TempDb();
        using var db = new Database(dbFile.Path);
        var id = SeedVersion(db);
        var drafts = new List<MobileSite.BugDraft>();

        var page = MobileSite.SubmitBug(db, Body(id, "Насос не запускается по сухому ходу", "critical"),
            Base, "Иванов", d => { drafts.Add(d); return true; });

        Assert.Equal(200, page.Status);
        var draft = Assert.Single(drafts);
        Assert.Equal(id, draft.FwVersionId);
        Assert.Equal(FwBugSeverity.Critical, draft.Severity);
        Assert.Contains("Насос не запускается", draft.Text);
        // Подпись прошивки — слепок на момент жалобы, иначе на другой машине тикет не опознать.
        Assert.Contains("2.1.0001.0001", draft.FwLabel);
    }

    /// <summary>Пометка «с телефона» обязана быть в самом тикете — иначе в списке появится жалоба
    /// ниоткуда. И при этом она НЕ в поле автора: по точному совпадению автора наладчик видит свои
    /// тикеты и может закрыть ошибочный (TicketVisibility.CanModerate).</summary>
    [Fact]
    public void Submit_MarksTheTicketAsFiledFromAPhone_ByWhoseAccess()
    {
        using var dbFile = new TempDb();
        using var db = new Database(dbFile.Path);
        var id = SeedVersion(db);
        MobileSite.BugDraft? draft = null;

        MobileSite.SubmitBug(db, Body(id, "мелочь в описании режима"), Base, "Иванов",
            d => { draft = d; return true; });

        Assert.Contains("с телефона", draft!.Text);
        Assert.Contains("Иванов", draft.Text);
    }

    /// <summary>Токен в пометку попадать не должен: тикет уезжает к коллегам и в хранилище, а токен —
    /// это пропуск к базе на чьём-то компьютере.</summary>
    [Fact]
    public void PhoneMark_DoesNotCarryTheToken()
    {
        var marked = MobileSite.PhoneMark("текст", "Иванов");
        Assert.DoesNotContain("tok", marked);
        Assert.Contains("текст", marked);
    }

    [Fact]
    public void Submit_WithoutText_CreatesNothing_AndKeepsWhatWasTyped()
    {
        using var dbFile = new TempDb();
        using var db = new Database(dbFile.Path);
        var id = SeedVersion(db);
        var created = 0;

        var page = MobileSite.SubmitBug(db, Body(id, "   "), Base, "Иванов", _ => { created++; return true; });

        Assert.Equal(0, created);
        Assert.Equal(200, page.Status);
        Assert.Contains("Опишите", page.Html);
    }

    [Fact]
    public void Submit_TooLongText_IsRefused_AndSaysSo()
    {
        using var dbFile = new TempDb();
        using var db = new Database(dbFile.Path);
        var id = SeedVersion(db);
        var created = 0;

        var page = MobileSite.SubmitBug(db, Body(id, new string('я', MobileSite.MaxBugChars + 1)),
            Base, "Иванов", _ => { created++; return true; });

        Assert.Equal(0, created);
        Assert.Contains("Слишком длинно", page.Html);
    }

    /// <summary>Версию в бланке подставляет сама страница, но прийти может что угодно — и жалоба на
    /// несуществующую прошивку не должна заводиться.</summary>
    [Fact]
    public void Submit_ForUnknownVersion_CreatesNothing()
    {
        using var dbFile = new TempDb();
        using var db = new Database(dbFile.Path);
        var created = 0;

        var page = MobileSite.SubmitBug(db, Body(999999, "жалоба в никуда"), Base, "Иванов",
            _ => { created++; return true; });

        Assert.Equal(0, created);
        Assert.Equal(404, page.Status);
    }

    /// <summary>Приложение отказало (база занята, диск отвалился) — на телефоне это обязано
    /// выглядеть отказом, а не «отправлено»: иначе человек к жалобе больше не вернётся.</summary>
    [Fact]
    public void Submit_WhenTheAppRefuses_SaysSo_AndKeepsTheText()
    {
        using var dbFile = new TempDb();
        using var db = new Database(dbFile.Path);
        var id = SeedVersion(db);

        var page = MobileSite.SubmitBug(db, Body(id, "авария по перегреву"), Base, "Иванов", _ => false);

        Assert.Contains("Не удалось", page.Html);
        Assert.Contains("авария по перегреву", page.Html);
    }

    /// <summary>Текст жалобы возвращается в поле ввода при ошибке — то есть попадает в разметку
    /// напрямую, как и всё остальное, что пришло с провода.</summary>
    [Fact]
    public void Submit_EscapesWhatCameFromThePhone()
    {
        using var dbFile = new TempDb();
        using var db = new Database(dbFile.Path);
        var id = SeedVersion(db);

        var page = MobileSite.SubmitBug(db, Body(id, "<script>alert(1)</script>"), Base, "Иванов", _ => false);

        Assert.DoesNotContain("<script>", page.Html);
        Assert.Contains("&lt;script&gt;", page.Html);
    }

    // ── Транспорт: что сервер вообще соглашается принять ─────────────────────

    /// <summary>Один запрос на соединение, как и шлёт браузер (Connection: close в ответе).</summary>
    private static string Request(int port, string raw)
    {
        using var client = new TcpClient("127.0.0.1", port);
        using var stream = client.GetStream();
        var bytes = Encoding.UTF8.GetBytes(raw);
        stream.Write(bytes, 0, bytes.Length);
        stream.Flush();

        var buffer = new byte[64 * 1024];
        var total = 0;
        int n;
        while (total < buffer.Length && (n = stream.Read(buffer, total, buffer.Length - total)) > 0)
            total += n;
        return Encoding.UTF8.GetString(buffer, 0, total);
    }

    private static string Post(int port, string path, string body) =>
        Request(port, $"POST {path} HTTP/1.1\r\nHost: x\r\nContent-Type: application/x-www-form-urlencoded\r\n" +
                      $"Content-Length: {Encoding.UTF8.GetByteCount(body)}\r\nConnection: close\r\n\r\n{body}");

    private static MobileAccessServer StartServer(List<string> accepted, bool acceptWrites = true) =>
        new(0, "tok",
            (path, _) => new MobileSite.Page(200, "страница " + MobileSite.Esc(path)),
            null,
            acceptWrites
                ? body =>
                {
                    accepted.Add(body);
                    return (new MobileSite.Page(200, "принято"), true);
                }
            : null);

    [Fact]
    public void Post_WithTheRightToken_ReachesTheBugHandler()
    {
        var accepted = new List<string>();
        using var server = StartServer(accepted);

        var answer = Post(server.Port, "/tok/bug", "v=1&text=hi");

        Assert.Contains("200", answer);
        Assert.Equal("v=1&text=hi", Assert.Single(accepted));
    }

    /// <summary>Главное про запись: без токена не принимается НИЧЕГО. Сервер слушает локальную сеть,
    /// и сосед по Wi-Fi не должен иметь возможности завести тикет в чужой базе.</summary>
    [Fact]
    public void Post_WithoutTheToken_IsRefused_AndNothingIsCreated()
    {
        var accepted = new List<string>();
        using var server = StartServer(accepted);

        Assert.Contains("403", Post(server.Port, "/чужой/bug", "v=1&text=hi"));
        Assert.Contains("403", Post(server.Port, "/bug", "v=1&text=hi"));
        Assert.Empty(accepted);
    }

    /// <summary>«С телефона можно завести баг» не должно превратиться в «с телефона можно
    /// что-нибудь ещё»: любой другой адрес POST-ом — отказ, даже с верным токеном.</summary>
    [Fact]
    public void Post_ToAnyOtherAddress_IsRefused()
    {
        var accepted = new List<string>();
        using var server = StartServer(accepted);

        Assert.Contains("405", Post(server.Port, "/tok/", "x=1"));
        Assert.Contains("405", Post(server.Port, "/tok/v/1", "x=1"));
        Assert.Contains("405", Post(server.Port, "/tok/p", "x=1"));
        Assert.Empty(accepted);
    }

    /// <summary>Сервер, поднятый без приёма жалоб, обязан остаться читающим.</summary>
    [Fact]
    public void Post_WhenWritesAreNotWiredUp_IsRefused()
    {
        var accepted = new List<string>();
        using var server = StartServer(accepted, acceptWrites: false);

        Assert.Contains("405", Post(server.Port, "/tok/bug", "v=1&text=hi"));
    }

    /// <summary>Другие способы что-то изменить (PUT/DELETE) не появились заодно с приёмом.</summary>
    [Fact]
    public void OtherWriteMethods_StayRefused()
    {
        var accepted = new List<string>();
        using var server = StartServer(accepted);

        var answer = Request(server.Port, "DELETE /tok/bug HTTP/1.1\r\nHost: x\r\nConnection: close\r\n\r\n");

        Assert.Contains("405", answer);
        Assert.Empty(accepted);
    }

    /// <summary>Чтение приёмом записи не задето.</summary>
    [Fact]
    public void Get_StillWorks()
    {
        var accepted = new List<string>();
        using var server = StartServer(accepted);

        Assert.Contains("страница", Request(server.Port, "GET /tok/p HTTP/1.1\r\nHost: x\r\nConnection: close\r\n\r\n"));
    }

    /// <summary>Длинное тело не читается вовсе — предел проверяется по заявленной длине, до
    /// выделения памяти под неё.</summary>
    [Fact]
    public void Post_WithHugeBody_IsRefused_WithoutReachingTheHandler()
    {
        var accepted = new List<string>();
        using var server = StartServer(accepted);

        var answer = Post(server.Port, "/tok/bug", "text=" + new string('a', 20 * 1024));

        Assert.Contains("413", answer);
        Assert.Empty(accepted);
    }

    /// <summary>Поток жалоб с одного устройства упирается в паузу: база не должна ни распухнуть, ни
    /// встать от того, что кто-то держит палец на кнопке.</summary>
    [Fact]
    public void Post_Repeated_IsThrottledAfterTheFirstAcceptedTicket()
    {
        var accepted = new List<string>();
        using var server = StartServer(accepted);

        Assert.Contains("200", Post(server.Port, "/tok/bug", "v=1&text=первая"));
        Assert.Contains("429", Post(server.Port, "/tok/bug", "v=1&text=вторая"));
        Assert.Contains("429", Post(server.Port, "/tok/bug", "v=1&text=третья"));
        Assert.Single(accepted);
    }

    /// <summary>А неудачная попытка счётчик не тратит: человек, отправивший пустой бланк и тут же
    /// исправивший его, — единственный, кто здесь ведёт себя правильно.</summary>
    [Fact]
    public void Post_ThatCreatedNothing_DoesNotSpendTheQuota()
    {
        var accepted = new List<string>();
        using var server = new MobileAccessServer(0, "tok",
            (path, _) => new MobileSite.Page(200, MobileSite.Esc(path)), null,
            body =>
            {
                accepted.Add(body);
                // Пустой текст — тикет не заведён, страница с ошибкой.
                var created = body.Contains("text=") && !body.EndsWith("text=", StringComparison.Ordinal);
                return (new MobileSite.Page(200, created ? "принято" : "опишите"), created);
            });

        Assert.Contains("опишите", Post(server.Port, "/tok/bug", "v=1&text="));
        Assert.Contains("принято", Post(server.Port, "/tok/bug", "v=1&text=есть"));
        Assert.Equal(2, accepted.Count);
    }
}
