using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AntarusPoFinder.Core.Data;
using AntarusPoFinder.Core.Domain;
using AntarusPoFinder.Core.Services;
using AntarusPoFinder.Tests.TestHelpers;
using Xunit;

namespace AntarusPoFinder.Tests;

/// <summary>Двусторонний обмен тикетами через хранилище (TicketStorageSync).
///
/// Просьба владельца дословно: «сделай синхру тикетов через хранилище, чтобы ты их мог закрывать и
/// они соответственно с хранилища подтягивались, а то сложно так отслеживать что открыто что нет».
/// До этого в хранилище уезжал РАЗОВЫЙ архив: снимок на минуту выгрузки, без обратной дороги —
/// закрытый снаружи тикет в конторе продолжал висеть открытым.
///
/// Здесь проверяется ровно то, чего не видно ни в живом прогоне, ни глазами: что обмен СЛИВАЕТ, а
/// не затирает, что он сходится при встречных правках, и что повторный проход не ходит в сеть
/// впустую. Настоящего бакета и настоящих ключей тут нет и быть не должно — хранилище подставное.</summary>
public class TicketStorageSyncTests
{
    // ── Подставное хранилище ─────────────────────────────────────────────────

    /// <summary>Бакет в памяти. Считает запросы: «не скачивать то, что не менялось» — это
    /// поведение, а не оптимизация, и проверяется оно только счётчиком.</summary>
    private sealed class FakeStorage : ITicketStorage
    {
        private readonly Dictionary<string, string> _objects = new(StringComparer.Ordinal);
        private readonly Dictionary<string, int> _versions = new(StringComparer.Ordinal);

        public int Gets { get; private set; }
        public int Puts { get; private set; }
        public string? ListError { get; set; }
        public string? PutError { get; set; }
        /// <summary>Ключи, чтение которых должно падать (битый объект/оборванная сеть).</summary>
        public HashSet<string> UnreadableKeys { get; } = new(StringComparer.Ordinal);

        public IReadOnlyDictionary<string, string> Objects => _objects;

        public void Seed(string key, string json) => Write(key, json);

        public void Remove(string key) { _objects.Remove(key); _versions.Remove(key); }

        private void Write(string key, string json)
        {
            _objects[key] = json;
            _versions[key] = _versions.TryGetValue(key, out var v) ? v + 1 : 1;
        }

        public Task<TicketStorageListing> ListAsync(string relativePrefix, CancellationToken ct = default)
        {
            if (ListError is not null) return Task.FromResult(TicketStorageListing.Fail(ListError));
            var entries = _objects
                .Where(p => p.Key.StartsWith(relativePrefix, StringComparison.Ordinal))
                // ETag у настоящего бакета — отпечаток тела; здесь номер записи, что для обмена то
                // же самое: он меняется тогда и только тогда, когда объект переписали.
                .Select(p => new TicketStorageEntry(p.Key, p.Value.Length, null, _versions[p.Key].ToString()))
                .ToList();
            return Task.FromResult(TicketStorageListing.Success(entries));
        }

        public Task<(string? Json, string? Error)> GetAsync(string relativeKey, CancellationToken ct = default)
        {
            Gets++;
            if (UnreadableKeys.Contains(relativeKey)) return Task.FromResult<(string?, string?)>((null, "обрыв связи"));
            return Task.FromResult(_objects.TryGetValue(relativeKey, out var json)
                ? ((string?)json, (string?)null)
                : (null, null)); // объекта нет — законный ответ, не сбой
        }

        public Task<string?> PutAsync(string relativeKey, string json, CancellationToken ct = default)
        {
            Puts++;
            if (PutError is not null) return Task.FromResult<string?>(PutError);
            Write(relativeKey, json);
            return Task.FromResult<string?>(null);
        }
    }

    private static Ticket NewTicket(string id, string text = "не печатается наклейка",
        string status = TicketStatus.Open, string at = "2026-09-10T10:00:00.000", string role = "naladchik") => new()
    {
        Id = id,
        Type = TicketType.Bug,
        Text = text,
        Status = status,
        CreatedBy = "ivanov",
        CreatedByRole = role,
        CreatedAt = at,
        UpdatedAt = at,
    };

    private static string StateKey(string id) => $"{TicketStorageSync.StateFolder}/{id}.json";

    // ── Выкладка накопленного ────────────────────────────────────────────────

    /// <summary>Первый же обмен выкладывает ВСЕ тикеты, которые на машине уже лежат. Без этого
    /// канал начинался бы с пустого бакета, и шестнадцать открытых тем, ради которых он и делался,
    /// не увидел бы никто, пока кто-нибудь не заведёт семнадцатую.</summary>
    [Fact]
    public async Task FirstSync_UploadsEveryTicketTheMachineAlreadyHas()
    {
        using var file = new TempDb();
        using var db = new Database(file.Path);
        db.InsertTicketIfMissing(NewTicket("t1"));
        db.InsertTicketIfMissing(NewTicket("t2", "не находит прошивку"));
        var storage = new FakeStorage();

        var result = await TicketStorageSync.RunAsync(db, storage);

        Assert.True(result.Ok);
        Assert.Equal(2, result.Pushed);
        Assert.Equal(0, result.Failed);
        Assert.Contains(StateKey("t1"), storage.Objects.Keys);
        Assert.Contains(StateKey("t2"), storage.Objects.Keys);
    }

    /// <summary>Второй проход по тем же данным не выкладывает ничего и не скачивает ничего сверх
    /// одного подтверждающего чтения на объект: отпечаток тела уже запомнен.</summary>
    [Fact]
    public async Task SecondSync_WithNothingChanged_IsQuiet()
    {
        using var file = new TempDb();
        using var db = new Database(file.Path);
        db.InsertTicketIfMissing(NewTicket("t1"));
        var storage = new FakeStorage();

        await TicketStorageSync.RunAsync(db, storage);
        await TicketStorageSync.RunAsync(db, storage); // подтверждает отпечаток только что выложенного
        var gets = storage.Gets;
        var puts = storage.Puts;

        var third = await TicketStorageSync.RunAsync(db, storage);

        Assert.True(third.Quiet);
        Assert.Equal(gets, storage.Gets);
        Assert.Equal(puts, storage.Puts);
    }

    // ── Приём ────────────────────────────────────────────────────────────────

    /// <summary>Главное, ради чего всё затевалось: тикет закрыли в хранилище — на машине он стал
    /// закрытым.</summary>
    [Fact]
    public async Task ClosingATicketInStorage_ReachesTheMachine()
    {
        using var file = new TempDb();
        using var db = new Database(file.Path);
        db.InsertTicketIfMissing(NewTicket("t1"));
        var storage = new FakeStorage();
        await TicketStorageSync.RunAsync(db, storage);

        // Так это и делается снаружи: положить тот же объект со статусом «закрыт» и более поздней
        // отметкой времени.
        storage.Seed(StateKey("t1"), TicketStorageSync.Serialize(
            NewTicket("t1", status: TicketStatus.Closed, at: "2026-09-11T09:00:00.000")));

        var result = await TicketStorageSync.RunAsync(db, storage);

        Assert.Equal(1, result.Pulled);
        Assert.Equal(TicketStatus.Closed, db.GetTickets().Single(t => t.Id == "t1").Status);
    }

    /// <summary>Тикет, заведённый только в хранилище (машина его никогда не видела), приезжает
    /// целиком.</summary>
    [Fact]
    public async Task ATicketThatOnlyExistsInStorage_ArrivesWhole()
    {
        using var file = new TempDb();
        using var db = new Database(file.Path);
        var storage = new FakeStorage();
        storage.Seed(StateKey("t9"), TicketStorageSync.Serialize(NewTicket("t9", "тема из хранилища")));

        await TicketStorageSync.RunAsync(db, storage);

        var arrived = Assert.Single(db.GetTickets());
        Assert.Equal("t9", arrived.Id);
        Assert.Equal("тема из хранилища", arrived.Text);
    }

    /// <summary>Устаревшее состояние из хранилища НЕ переоткрывает тикет, закрытый на машине, — и
    /// машина тут же дописывает в бакет своё, более свежее. Иначе две машины бесконечно
    /// переоткрывали бы друг другу один тикет.</summary>
    [Fact]
    public async Task StaleStorageState_DoesNotReopenALocallyClosedTicket_AndIsOverwritten()
    {
        using var file = new TempDb();
        using var db = new Database(file.Path);
        db.InsertTicketIfMissing(NewTicket("t1"));
        var storage = new FakeStorage();
        await TicketStorageSync.RunAsync(db, storage);

        db.ApplyTicketStatusIfNewer("t1", TicketStatus.Closed, "2026-09-11T12:00:00.000");
        // В бакет тем временем лёг чужой снимок с более ранней отметкой времени.
        storage.Seed(StateKey("t1"), TicketStorageSync.Serialize(
            NewTicket("t1", status: TicketStatus.Open, at: "2026-09-11T08:00:00.000")));

        await TicketStorageSync.RunAsync(db, storage);

        Assert.Equal(TicketStatus.Closed, db.GetTickets().Single().Status);
        var inBucket = TicketStorageSync.TryParse(storage.Objects[StateKey("t1")])!;
        Assert.Equal(TicketStatus.Closed, inBucket.Status);
    }

    /// <summary>Текст тикета из хранилища не переписывает местный. Тикет после создания правят
    /// одним способом — сменой статуса; приедь сюда чужой текст, на двух машинах разошлись бы две
    /// редакции одной жалобы.</summary>
    [Fact]
    public async Task IncomingText_DoesNotOverwriteTheLocalOne()
    {
        using var file = new TempDb();
        using var db = new Database(file.Path);
        db.InsertTicketIfMissing(NewTicket("t1", "исходный текст"));
        var storage = new FakeStorage();
        storage.Seed(StateKey("t1"), TicketStorageSync.Serialize(
            NewTicket("t1", "подменённый текст", TicketStatus.Closed, "2026-09-11T09:00:00.000")));

        await TicketStorageSync.RunAsync(db, storage);

        var local = db.GetTickets().Single();
        Assert.Equal("исходный текст", local.Text);
        Assert.Equal(TicketStatus.Closed, local.Status);
    }

    // ── Слияние двух машин ───────────────────────────────────────────────────

    /// <summary>Две машины правят РАЗНЫЕ тикеты между обменами — выживают оба изменения. У снимка
    /// целым файлом (обмен конфигом) это было бы невозможно: тот, кто записал вторым, стёр бы
    /// чужое.</summary>
    [Fact]
    public async Task TwoMachines_ChangingDifferentTickets_BothChangesSurvive()
    {
        using var fileA = new TempDb();
        using var fileB = new TempDb();
        using var dbA = new Database(fileA.Path);
        using var dbB = new Database(fileB.Path);
        var storage = new FakeStorage();

        dbA.InsertTicketIfMissing(NewTicket("t1"));
        dbB.InsertTicketIfMissing(NewTicket("t2", "вторая тема"));
        await TicketStorageSync.RunAsync(dbA, storage);
        await TicketStorageSync.RunAsync(dbB, storage);
        await TicketStorageSync.RunAsync(dbA, storage);

        // Обе машины закрывают СВОЙ тикет, не зная о чужом действии.
        dbA.ApplyTicketStatusIfNewer("t1", TicketStatus.Closed, "2026-09-11T12:00:00.000");
        dbB.ApplyTicketStatusIfNewer("t2", TicketStatus.InProgress, "2026-09-11T12:00:01.000");

        await TicketStorageSync.RunAsync(dbA, storage);
        await TicketStorageSync.RunAsync(dbB, storage);
        await TicketStorageSync.RunAsync(dbA, storage);

        Assert.Equal(TicketStatus.Closed, dbA.GetTickets().Single(t => t.Id == "t1").Status);
        Assert.Equal(TicketStatus.InProgress, dbA.GetTickets().Single(t => t.Id == "t2").Status);
        Assert.Equal(TicketStatus.Closed, dbB.GetTickets().Single(t => t.Id == "t1").Status);
        Assert.Equal(TicketStatus.InProgress, dbB.GetTickets().Single(t => t.Id == "t2").Status);
    }

    /// <summary>Две машины правят ОДИН тикет. Побеждает более поздняя отметка времени, и обе машины
    /// приходят к ней же — даже если в бакет последним лёг проигравший.</summary>
    [Fact]
    public async Task TwoMachines_ChangingTheSameTicket_ConvergeOnTheLatestTimestamp()
    {
        using var fileA = new TempDb();
        using var fileB = new TempDb();
        using var dbA = new Database(fileA.Path);
        using var dbB = new Database(fileB.Path);
        var storage = new FakeStorage();

        dbA.InsertTicketIfMissing(NewTicket("t1"));
        await TicketStorageSync.RunAsync(dbA, storage);
        await TicketStorageSync.RunAsync(dbB, storage);

        dbA.ApplyTicketStatusIfNewer("t1", TicketStatus.Closed, "2026-09-11T15:00:00.000");
        dbB.ApplyTicketStatusIfNewer("t1", TicketStatus.InProgress, "2026-09-11T14:00:00.000");

        // Проигравший (B) кладёт своё ПОСЛЕ победителя — в бакете временно остаётся старшее
        // состояние.
        await TicketStorageSync.RunAsync(dbA, storage);
        await TicketStorageSync.RunAsync(dbB, storage);
        // Ещё по проходу каждому: A видит чужое устаревшее состояние и возвращает своё, B его
        // забирает.
        await TicketStorageSync.RunAsync(dbA, storage);
        await TicketStorageSync.RunAsync(dbB, storage);

        Assert.Equal(TicketStatus.Closed, dbA.GetTickets().Single().Status);
        Assert.Equal(TicketStatus.Closed, dbB.GetTickets().Single().Status);
        Assert.Equal(TicketStatus.Closed, TicketStorageSync.TryParse(storage.Objects[StateKey("t1")])!.Status);
    }

    // ── Автоотчёты о сбоях ───────────────────────────────────────────────────

    /// <summary>Автоотчёты возятся, но ОТДЕЛЬНОЙ папкой: вопрос владельца — «что открыто, что нет»,
    /// и семь стектрейсов среди живых тем его забивают. Ровно так же они разведены и в самой
    /// программе — спрятаны за галку, а не выброшены.</summary>
    [Fact]
    public async Task CrashReports_GoToTheirOwnFolder_AwayFromLiveTopics()
    {
        using var file = new TempDb();
        using var db = new Database(file.Path);
        db.InsertTicketIfMissing(NewTicket("t1"));
        db.InsertTicketIfMissing(NewTicket("crash1",
            $"{TicketAutoReports.TextPrefix}\nSystem.NullReferenceException", role: TicketAutoReports.SystemRole));
        var storage = new FakeStorage();

        await TicketStorageSync.RunAsync(db, storage);

        Assert.Contains($"{TicketStorageSync.StateFolder}/t1.json", storage.Objects.Keys);
        Assert.Contains($"{TicketStorageSync.CrashFolder}/crash1.json", storage.Objects.Keys);
        Assert.DoesNotContain($"{TicketStorageSync.StateFolder}/crash1.json", storage.Objects.Keys);
    }

    /// <summary>Закрыть автоотчёт можно тем же способом, что и живую тему: правила у обеих папок
    /// одинаковые.</summary>
    [Fact]
    public async Task ACrashReport_CanBeClosedFromStorage()
    {
        using var file = new TempDb();
        using var db = new Database(file.Path);
        var crash = NewTicket("crash1", $"{TicketAutoReports.TextPrefix}\nSystem.IO.IOException",
            role: TicketAutoReports.SystemRole);
        db.InsertTicketIfMissing(crash);
        var storage = new FakeStorage();
        await TicketStorageSync.RunAsync(db, storage);

        crash.Status = TicketStatus.Closed;
        crash.UpdatedAt = "2026-09-11T09:00:00.000";
        storage.Seed($"{TicketStorageSync.CrashFolder}/crash1.json", TicketStorageSync.Serialize(crash));

        await TicketStorageSync.RunAsync(db, storage);

        Assert.Equal(TicketStatus.Closed, db.GetTickets().Single().Status);
    }

    // ── Пропажи, мусор, обрывы ───────────────────────────────────────────────

    /// <summary>Объект стёрли в бакете руками (страница «Хранилище» это умеет). Удалением тикета
    /// это НЕ считается — тикеты в этой программе не удаляются вовсе, — поэтому он выкладывается
    /// заново.</summary>
    [Fact]
    public async Task AnObjectDeletedInTheBucket_IsUploadedAgain()
    {
        using var file = new TempDb();
        using var db = new Database(file.Path);
        db.InsertTicketIfMissing(NewTicket("t1"));
        var storage = new FakeStorage();
        await TicketStorageSync.RunAsync(db, storage);
        storage.Remove(StateKey("t1"));

        var result = await TicketStorageSync.RunAsync(db, storage);

        Assert.Equal(1, result.Pushed);
        Assert.Contains(StateKey("t1"), storage.Objects.Keys);
        Assert.Single(db.GetTickets());
    }

    /// <summary>Битый (или недочитанный) объект считается сбоем, но не срывает проход: остальные
    /// тикеты приезжают, а счётчик сбоев уходит наружу — молчащий обмен уже однажды стоил дней
    /// поисков (см. автообновление и автоотправку конфига).</summary>
    [Fact]
    public async Task AnUnreadableObject_IsCounted_ButTheRestStillArrives()
    {
        using var file = new TempDb();
        using var db = new Database(file.Path);
        var storage = new FakeStorage();
        storage.Seed(StateKey("good"), TicketStorageSync.Serialize(NewTicket("good")));
        storage.Seed(StateKey("bad"), "{ это не json");
        storage.Seed(StateKey("offline"), TicketStorageSync.Serialize(NewTicket("offline")));
        storage.UnreadableKeys.Add(StateKey("offline"));

        var result = await TicketStorageSync.RunAsync(db, storage);

        Assert.True(result.Ok);
        Assert.Equal(2, result.Failed);
        Assert.Contains(db.GetTickets(), t => t.Id == "good");
    }

    /// <summary>Битый объект перечитывается на следующем проходе, а не помечается разобранным:
    /// иначе исправленный кем-то файл никогда бы не доехал.</summary>
    [Fact]
    public async Task AnUnreadableObject_IsRetriedNextTime()
    {
        using var file = new TempDb();
        using var db = new Database(file.Path);
        var storage = new FakeStorage();
        storage.Seed(StateKey("t1"), "{ это не json");

        await TicketStorageSync.RunAsync(db, storage);
        storage.Seed(StateKey("t1"), TicketStorageSync.Serialize(NewTicket("t1")));
        await TicketStorageSync.RunAsync(db, storage);

        Assert.Single(db.GetTickets());
    }

    /// <summary>Хранилище не ответило — это ошибка прохода целиком, а не «всё в порядке, ничего не
    /// приехало». Местные тикеты при этом целы.</summary>
    [Fact]
    public async Task WhenTheBucketIsUnreachable_TheOutcomeSaysSo()
    {
        using var file = new TempDb();
        using var db = new Database(file.Path);
        db.InsertTicketIfMissing(NewTicket("t1"));
        var storage = new FakeStorage { ListError = "хранилище не ответило вовремя" };

        var result = await TicketStorageSync.RunAsync(db, storage);

        Assert.False(result.Ok);
        Assert.Equal("хранилище не ответило вовремя", result.Error);
        Assert.Equal(0, storage.Puts);
        Assert.Single(db.GetTickets());
    }

    /// <summary>Не удалось положить — тикет остаётся неотправленным и уходит на следующем проходе.
    /// Запомни мы его как выложенный, он не уехал бы уже никогда.</summary>
    [Fact]
    public async Task AFailedUpload_IsRetriedOnTheNextPass()
    {
        using var file = new TempDb();
        using var db = new Database(file.Path);
        db.InsertTicketIfMissing(NewTicket("t1"));
        var storage = new FakeStorage { PutError = "хранилище не ответило вовремя" };

        var first = await TicketStorageSync.RunAsync(db, storage);
        Assert.Equal(1, first.Failed);
        Assert.Empty(storage.Objects);

        storage.PutError = null;
        var second = await TicketStorageSync.RunAsync(db, storage);

        Assert.Equal(1, second.Pushed);
        Assert.Contains(StateKey("t1"), storage.Objects.Keys);
    }

    /// <summary>Разовая выгрузка архивом лежит в той же папке верхнего уровня и обменом не
    /// трогается — ни как тикет, ни как мусор.</summary>
    [Fact]
    public async Task TheOneOffExportArchive_IsLeftAlone()
    {
        using var file = new TempDb();
        using var db = new Database(file.Path);
        var storage = new FakeStorage();
        storage.Seed("tickets/tickets_20260824_1530_deadbeef.zip", "PK...");

        var result = await TicketStorageSync.RunAsync(db, storage);

        Assert.True(result.Ok);
        Assert.Equal(0, result.Pulled);
        Assert.Equal(0, storage.Gets);
        Assert.Empty(db.GetTickets());
    }

    // ── Разбор объекта, который правили руками ───────────────────────────────

    /// <summary>Объект правит человек (или другая программа), поэтому разбор терпимый: без
    /// отметки правки берётся время создания, незнакомый статус — «Открыт». Потерять тикет из виду
    /// хуже, чем показать лишний.</summary>
    [Fact]
    public void TryParse_FillsInWhatAHandWrittenObjectOmits()
    {
        var ticket = TicketStorageSync.TryParse("""
            { "id": "t1", "text": "правлено руками", "createdAt": "2026-09-01T08:00:00.000" }
            """);

        Assert.NotNull(ticket);
        Assert.Equal("2026-09-01T08:00:00.000", ticket!.UpdatedAt);
        Assert.Equal(TicketStatus.Open, ticket.Status);
        Assert.Equal(TicketType.Other, ticket.Type);
    }

    /// <summary>«resolved», «закрыт» — не наши слова, но намерение однозначное. Иначе закрытие,
    /// написанное чужой рукой, молча ничего не делало бы.</summary>
    [Theory]
    [InlineData("closed")]
    [InlineData("Closed")]
    [InlineData("resolved")]
    [InlineData("закрыт")]
    public void TryParse_UnderstandsTheUsualWordsForClosed(string written)
    {
        var ticket = TicketStorageSync.TryParse($$"""
            { "id": "t1", "status": "{{written}}", "createdAt": "2026-09-01T08:00:00.000" }
            """);

        Assert.Equal(TicketStatus.Closed, ticket!.Status);
    }

    [Fact]
    public void TryParse_OnGarbage_ReturnsNullInsteadOfThrowing()
    {
        Assert.Null(TicketStorageSync.TryParse("<html>вход в панель</html>"));
        Assert.Null(TicketStorageSync.TryParse("""{ "text": "без идентификатора" }"""));
    }

    /// <summary>Кириллица в файле остаётся кириллицей: эти объекты читают и правят глазами.</summary>
    [Fact]
    public void Serialize_KeepsCyrillicReadable()
    {
        var json = TicketStorageSync.Serialize(NewTicket("t1", "не печатается наклейка"));

        Assert.Contains("не печатается наклейка", json);
        Assert.DoesNotContain("\\u04", json);
    }
}

/// <summary>Перевод между ключом обмена («tickets/state/…») и настоящим ключом объекта в бакете.
/// Бакет у предприятия общий, и его часть отделена ПРЕФИКСОМ из настроек — промахнись перевод, и
/// тикеты легли бы в чужую часть бакета (либо не нашлись бы в своей). Настоящей сети здесь нет,
/// ключи выдуманные: подставляется обработчик HTTP.</summary>
public class TicketStorageKeyMappingTests
{
    private static S3Settings Settings(string prefix) =>
        new("https://s3.twcstorage.ru", "amperus", "ru-1", prefix, "AK", "SK",
            "https://fs.elitacompany.ru", true);

    private sealed class Recorder : System.Net.Http.HttpMessageHandler
    {
        private readonly string _body;
        public Recorder(string body = "") => _body = body;
        public List<(string Method, string Url)> Seen { get; } = new();

        protected override Task<System.Net.Http.HttpResponseMessage> SendAsync(
            System.Net.Http.HttpRequestMessage request, CancellationToken ct)
        {
            Seen.Add((request.Method.Method, request.RequestUri!.AbsoluteUri));
            return Task.FromResult(new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new System.Net.Http.StringContent(_body),
            });
        }
    }

    [Fact]
    public async Task Put_WritesUnderTheConfiguredPrefix()
    {
        var recorder = new Recorder();
        var storage = new S3TicketStorage(Settings("antarus"), new S3Client(new System.Net.Http.HttpClient(recorder)));

        await storage.PutAsync("tickets/state/9b1c-42.json", "{}");

        var (method, url) = Assert.Single(recorder.Seen);
        Assert.Equal("PUT", method);
        Assert.Equal("https://s3.twcstorage.ru/amperus/antarus/tickets/state/9b1c-42.json", url);
    }

    /// <summary>Префикса нет — ключ идёт как есть, без лишнего слеша в начале.</summary>
    [Fact]
    public async Task Put_WithoutAPrefix_WritesAtTheRoot()
    {
        var recorder = new Recorder();
        var storage = new S3TicketStorage(Settings(""), new S3Client(new System.Net.Http.HttpClient(recorder)));

        await storage.PutAsync("tickets/state/9b1c-42.json", "{}");

        Assert.Equal("https://s3.twcstorage.ru/amperus/tickets/state/9b1c-42.json", Assert.Single(recorder.Seen).Url);
    }

    /// <summary>Обратный перевод: хостинг отдаёт ключи ПОЛНОСТЬЮ, с префиксом, а слияние работает с
    /// относительными. Не сними мы префикс — ни один объект не был бы опознан как тикет, и обмен
    /// молча выкладывал бы дубликаты на каждом проходе.</summary>
    [Fact]
    public async Task List_StripsThePrefixBack()
    {
        const string body = """
            <?xml version="1.0" encoding="UTF-8"?>
            <ListBucketResult xmlns="http://s3.amazonaws.com/doc/2006-03-01/">
              <Name>amperus</Name>
              <IsTruncated>false</IsTruncated>
              <Contents>
                <Key>antarus/tickets/state/9b1c-42.json</Key>
                <Size>310</Size>
                <ETag>"7d8f"</ETag>
              </Contents>
              <Contents>
                <Key>antarus/instructions/i.pdf</Key>
                <Size>10</Size>
              </Contents>
            </ListBucketResult>
            """;
        var recorder = new Recorder(body);
        var storage = new S3TicketStorage(Settings("antarus"), new S3Client(new System.Net.Http.HttpClient(recorder)));

        var listing = await storage.ListAsync("tickets/");

        Assert.True(listing.Ok);
        Assert.Contains(listing.Entries, e => e.Key == "tickets/state/9b1c-42.json" && e.ETag == "7d8f");
        Assert.True(TicketStorageSync.IsTicketObject("tickets/state/9b1c-42.json"));
        // Запрошено ровно под своей папкой, а не по всему бакету.
        Assert.Contains("prefix=antarus%2Ftickets%2F", Assert.Single(recorder.Seen).Url);
    }

    /// <summary>Ключей нет — обмен молча не делается, ровно как выкладка инструкций (это ШТАТНОЕ
    /// состояние, см. S3Settings). Никаких запросов в сеть при этом не уходит.</summary>
    [Fact]
    public async Task WithoutCredentials_NothingIsRequested()
    {
        var recorder = new Recorder();
        var settings = new S3Settings("https://s3.twcstorage.ru", "amperus", "ru-1", "", "", "",
            "https://fs.elitacompany.ru", true);
        var storage = new S3TicketStorage(settings, new S3Client(new System.Net.Http.HttpClient(recorder)));

        Assert.False(storage.CanSync);
        Assert.False((await storage.ListAsync("tickets/")).Ok);
        Assert.Empty(recorder.Seen);
    }
}
