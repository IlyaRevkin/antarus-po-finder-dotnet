using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using AntarusPoFinder.App.Services;
using AntarusPoFinder.Core.Data;
using AntarusPoFinder.Core.Infrastructure;
using AntarusPoFinder.Core.Services;
using AntarusPoFinder.Tests.TestHelpers;
using Xunit;

namespace AntarusPoFinder.Tests;

/// <summary>Выкладка инструкций в хранилище на хостинге (решение Ивана Герасимова от 05.08.2026:
/// файлы размещаются на хостинге, а на вебдиск кладётся КОПИЯ, а не ярлык).
///
/// Проверяются три разных обещания, и все три одинаково важны:
///   • подпись запроса считается ровно так, как её считает сервер, — иначе единственным сообщением
///     об ошибке будет «SignatureDoesNotMatch» от чужой машины, по которому ничего не отладить;
///   • пока ключи не выданы, всё работает как раньше и молчит — это ШТАТНОЕ состояние, а не поломка
///     (ключи Иван обещал прислать позже, вписываются в Настройки без обновления программы);
///   • неудача выкладки не отменяет загрузку версии: файл к этому моменту уже лежит на диске.</summary>
public class S3PublishingTests
{
    /// <summary>Эталонный пример из документации AWS («Signature Version 4 test suite», GET Object с
    /// заголовком Range). Ключи в нём — публичные примерные, а не чьи-то настоящие. Смысл теста
    /// именно в чужом эталоне: свои же собственные вычисления сверять не с чем.</summary>
    private const string ExampleAccessKey = "AKIAIOSFODNN7EXAMPLE";
    private const string ExampleSecretKey = "wJalrXUtnFEMI/K7MDENG/bPxRfiCYEXAMPLEKEY";

    private static readonly DateTimeOffset ExampleWhen =
        new(2013, 5, 24, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Signer_MatchesTheReferenceExampleFromAwsDocumentation()
    {
        var headers = new Dictionary<string, string>
        {
            ["host"] = "examplebucket.s3.amazonaws.com",
            ["range"] = "bytes=0-9",
            ["x-amz-content-sha256"] = AwsV4Signer.EmptyPayloadHash,
            ["x-amz-date"] = "20130524T000000Z",
        };

        var canonical = AwsV4Signer.CanonicalRequest("GET", "/test.txt", "", headers,
            AwsV4Signer.EmptyPayloadHash);

        var auth = AwsV4Signer.AuthorizationHeader(ExampleAccessKey, ExampleSecretKey, ExampleWhen,
            "us-east-1", "s3", headers, canonical);

        Assert.Contains("Signature=f0e8bdb87c964420e857bd35b5d6ed310bd44f0170aba48dd91039c6036bdb41", auth);
        Assert.Contains($"Credential={ExampleAccessKey}/20130524/us-east-1/s3/aws4_request", auth);
        Assert.Contains("SignedHeaders=host;range;x-amz-content-sha256;x-amz-date", auth);
    }

    /// <summary>Кириллица в пути — не экзотика, а норма для этого диска («ПО/НГР/…»), и подпись
    /// обязана считаться от ЗАКОДИРОВАННОГО пути, тогда как ссылка под QR-кодом оставляет буквы
    /// буквами (см. LabelLinkBuilder). Слеши при этом остаются разделителями, а не превращаются
    /// в %2F, — иначе сервер увидит один объект с косыми чертами в имени вместо папок.</summary>
    [Fact]
    public void CanonicalPath_EncodesCyrillicAndSpaces_ButKeepsSlashes()
    {
        var path = AwsV4Signer.CanonicalPath("amperus/ПО/инструкция 2.pdf");

        Assert.Equal("/amperus/%D0%9F%D0%9E/%D0%B8%D0%BD%D1%81%D1%82%D1%80%D1%83%D0%BA%D1%86%D0%B8%D1%8F%202.pdf", path);
        Assert.Equal("%2A%28%29", AwsV4Signer.UriEncode("*()"));   // «почти незарезервированные» — тоже проценты
        Assert.Equal("-_.~", AwsV4Signer.UriEncode("-_.~"));       // а эти четыре остаются собой
    }

    // ── Состояния настройки ───────────────────────────────────────────────────

    private static S3Settings Settings(string access = "AK", string secret = "SK", bool enabled = true) =>
        new("https://s3.twcstorage.ru", "amperus", "ru-1", "", access, secret,
            "https://fs.elitacompany.ru", enabled);

    /// <summary>Главное состояние на день сдачи работы: адрес известен, ключей ещё нет. Оно обязано
    /// отличаться от «ничего не настроено» — интерфейс говорит «осталось вписать ключи», выкладчик
    /// не создаётся вовсе, и загрузка версии идёт ровно как до появления хостинга.</summary>
    [Fact]
    public void WithoutKeys_NothingIsPublished_AndItIsNotAnError()
    {
        var pending = Settings(access: "", secret: "");

        Assert.True(pending.HasAddress);
        Assert.False(pending.HasCredentials);
        Assert.False(pending.CanPublish);
        Assert.Null(InstructionPublisher.For(pending));

        // И выключатель отдельно от ключей: реквизиты остаются, выкладка приостановлена.
        Assert.False(InstructionPublisher.For(Settings(enabled: false)) is not null);
        Assert.NotNull(InstructionPublisher.For(Settings()));
    }

    /// <summary>Раскладка в бакете повторяет раскладку на диске — от этого зависит, что ссылка под
    /// QR-кодом (веб-адрес + путь относительно корня) указывает ровно на выложенный файл.</summary>
    [Fact]
    public void KeyFor_RepeatsTheDiskLayout_AndRespectsThePrefix()
    {
        Assert.Equal("PO/PZH/SMH5/Instrukciya/i.pdf",
            Settings().KeyFor(@"ПО\ПЖ\SMH5\Инструкция\и.pdf"));

        var withPrefix = Settings() with { Prefix = "finder" };
        Assert.Equal("finder/PO/i.pdf", withPrefix.KeyFor(@"\ПО\и.pdf"));
    }

    // ── Запрос целиком ────────────────────────────────────────────────────────

    /// <summary>Адресация путём, а не поддоменом бакета: у части хостингов нет сертификата на
    /// «имя-бакета.домен», и запрос падал бы на проверке сертификата ещё до подписи.</summary>
    [Fact]
    public void BuildRequest_UsesPathStyleAddressing_AndSignsTheBody()
    {
        var content = new byte[] { 1, 2, 3 };
        var request = S3Client.BuildRequest(Settings(), HttpMethod.Put, "ПО/и.pdf", content,
            "application/pdf", ExampleWhen);

        // Именно AbsoluteUri: по проводу уходит закодированный путь, а ToString() раскодирует его
        // обратно для показа — и подпись, посчитанная от закодированного пути, сошлась бы только с
        // первым из двух.
        Assert.Equal("https://s3.twcstorage.ru/amperus/%D0%9F%D0%9E/%D0%B8.pdf", request.RequestUri!.AbsoluteUri);
        Assert.Equal(AwsV4Signer.Sha256Hex(content),
            Assert.Single(request.Headers.GetValues("x-amz-content-sha256")));
        Assert.Equal("20130524T000000Z", Assert.Single(request.Headers.GetValues("x-amz-date")));

        var auth = Assert.Single(request.Headers.GetValues("Authorization"));
        // Content-Type сознательно не подписан: прокси по дороге, поправивший его, ломал бы подпись.
        Assert.Contains("SignedHeaders=host;x-amz-content-sha256;x-amz-date", auth);
        Assert.Equal("application/pdf", request.Content!.Headers.ContentType!.MediaType);
    }

    [Fact]
    public void PublicUrl_IsTheWebAddress_NotTheStorageOne()
    {
        // Класть и читать — разные точки входа: перепутать их значит положить в QR ссылку, которую
        // откроет только владелец ключей.
        // Кириллица остаётся буквами: ссылку читает человек с наклейки, и она же уходит в QR-код,
        // где каждый лишний знак — это лишние клетки (см. LabelLinkBuilder.EscapeSegment).
        Assert.Equal("https://fs.elitacompany.ru/ПО/и.pdf",
            S3Client.PublicUrl(Settings(), "ПО/и.pdf"));

        // Веб-адрес не задан — остаётся адрес хранилища: убедиться, что файл долетел, он позволяет,
        // но в QR такой ссылке не место (о чём и говорит строка состояния в настройках).
        Assert.StartsWith("https://s3.twcstorage.ru/amperus/",
            S3Client.PublicUrl(Settings() with { WebUrl = "" }, "и.pdf"));
    }

    // ── Выкладка ──────────────────────────────────────────────────────────────

    /// <summary>Подставной хостинг: запоминает, что и куда клали, и отвечает тем, чем велено. Живой
    /// сети в тестах нет и быть не должно — проверяется поведение программы, а не доступность
    /// Timeweb.</summary>
    private sealed class FakeStorage : HttpMessageHandler
    {
        private readonly HttpStatusCode _status;
        private readonly string _body;

        public FakeStorage(HttpStatusCode status = HttpStatusCode.OK, string body = "")
        {
            _status = status;
            _body = body;
        }

        public List<(string Url, long Length)> Puts { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var bytes = request.Content is null
                ? Array.Empty<byte>()
                : await request.Content.ReadAsByteArrayAsync(cancellationToken);
            Puts.Add((request.RequestUri!.ToString(), bytes.Length));
            return new HttpResponseMessage(_status) { Content = new StringContent(_body) };
        }
    }

    private static InstructionPublisher PublisherOver(FakeStorage storage, S3Settings? settings = null) =>
        new(settings ?? Settings(), new S3Client(new HttpClient(storage)));

    [Fact]
    public void Publish_SendsTheFile_AndReturnsTheAddressForTheQrCode()
    {
        using var first = new TempRoot();
        var folder = Path.Combine(first.Path, "ПО", "ПЖ", "SMH5", "Инструкция");
        Directory.CreateDirectory(folder);
        var file = Path.Combine(folder, "инструкция_2.1.pdf");
        File.WriteAllText(file, "документ");

        var storage = new FakeStorage();
        var warnings = new List<string>();

        var url = PublisherOver(storage).Publish(file, file, first.Path, warnings);

        Assert.Empty(warnings);
        Assert.Equal("https://fs.elitacompany.ru/PO/PZH/SMH5/Instrukciya/instrukciya_2.1.pdf", url);

        var put = Assert.Single(storage.Puts);
        Assert.StartsWith("https://s3.twcstorage.ru/amperus/", put.Url);
        Assert.Equal(File.ReadAllBytes(file).Length, put.Length);
    }

    /// <summary>Ключ считается от пути на ПЕРВОМ диске, а не от того места, где файл физически лежит:
    /// на третьем диске у него другой корень, а у коллеги третий диск подключён под другой буквой.
    /// Иначе наклейка, напечатанная на одной машине, вела бы не туда, куда выложила другая.</summary>
    [Fact]
    public void Publish_ComputesTheKeyFromTheFirstDiskPath_EvenWhenTheFileLivesOnTheThirdOne()
    {
        using var first = new TempRoot();
        using var third = new TempRoot();

        var actual = Path.Combine(third.Path, "ПО", "Инструкция", "и.pdf");
        Directory.CreateDirectory(Path.GetDirectoryName(actual)!);
        File.WriteAllText(actual, "документ");
        var onFirstDisk = Path.Combine(first.Path, "ПО", "Инструкция", "и.pdf");

        var storage = new FakeStorage();
        var url = PublisherOver(storage).Publish(actual, onFirstDisk, first.Path, new List<string>());

        Assert.Equal("https://fs.elitacompany.ru/PO/Instrukciya/i.pdf", url);
        Assert.Single(storage.Puts);
    }

    /// <summary>Инструкция папкой (постраничные сканы) выкладывается пофайлово с сохранением
    /// вложенности: каталогов у бакета нет, «папка» в нём — это общий префикс ключа.</summary>
    [Fact]
    public void Publish_Folder_SendsEveryFileKeepingTheNesting()
    {
        using var first = new TempRoot();
        var folder = Path.Combine(first.Path, "ПО", "Инструкция", "сканы");
        Directory.CreateDirectory(Path.Combine(folder, "стр"));
        File.WriteAllText(Path.Combine(folder, "01.png"), "a");
        File.WriteAllText(Path.Combine(folder, "стр", "02.png"), "b");

        var storage = new FakeStorage();
        var warnings = new List<string>();
        var url = PublisherOver(storage).Publish(folder, folder, first.Path, warnings);

        Assert.Empty(warnings);
        Assert.Equal(2, storage.Puts.Count);
        Assert.Contains(storage.Puts, p => p.Url.EndsWith("/01.png", StringComparison.Ordinal));
        Assert.Contains(storage.Puts, p => p.Url.Contains("/str/02.png", StringComparison.Ordinal));
        Assert.NotNull(url);
    }

    /// <summary>Случайно выбранная папка на тысячу файлов не должна превращать загрузку версии в
    /// получасовое ожидание — и о том, что выкладки не было, обязана сказать вслух.</summary>
    [Fact]
    public void Publish_HugeFolder_IsRefusedOutLoud_NotTruncatedSilently()
    {
        using var first = new TempRoot();
        var folder = Path.Combine(first.Path, "Инструкция", "сканы");
        Directory.CreateDirectory(folder);
        for (var i = 0; i <= InstructionPublisher.MaxFilesPerFolder; i++)
            File.WriteAllText(Path.Combine(folder, $"{i}.png"), "x");

        var storage = new FakeStorage();
        var warnings = new List<string>();

        Assert.Null(PublisherOver(storage).Publish(folder, folder, first.Path, warnings));
        Assert.Empty(storage.Puts);
        Assert.Contains(warnings, w => w.Contains("на хостинг не выкладываем", StringComparison.Ordinal));
    }

    /// <summary>Хостинг ответил отказом — это ПРЕДУПРЕЖДЕНИЕ, а не исключение: файл к этому моменту
    /// уже лежит на диске и версия уже создана, отменять их из-за недоступного хостинга нельзя.
    /// И текст должен говорить, что чинить, а не «403».</summary>
    [Fact]
    public void Publish_RefusedByTheHost_WarnsInPlainWords_AndDoesNotThrow()
    {
        using var first = new TempRoot();
        var file = Path.Combine(first.Path, "и.pdf");
        File.WriteAllText(file, "документ");

        var storage = new FakeStorage(HttpStatusCode.Forbidden,
            "<Error><Code>SignatureDoesNotMatch</Code><Message>…</Message></Error>");
        var warnings = new List<string>();

        Assert.Null(PublisherOver(storage).Publish(file, file, first.Path, warnings));
        Assert.Contains(warnings, w => w.Contains("Secret Access Key", StringComparison.Ordinal));
    }

    /// <summary>Файл вне диска прошивок — считать его адрес на хостинге не от чего, и выкладывать
    /// «куда-нибудь» нельзя: ссылка под QR всё равно указывала бы не туда.</summary>
    [Fact]
    public void Publish_FileOutsideTheFirmwareDisk_IsNotPublished()
    {
        using var first = new TempRoot();
        using var elsewhere = new TempRoot();
        var file = Path.Combine(elsewhere.Path, "и.pdf");
        File.WriteAllText(file, "документ");

        var storage = new FakeStorage();
        var warnings = new List<string>();

        Assert.Null(PublisherOver(storage).Publish(file, file, first.Path, warnings));
        Assert.Empty(storage.Puts);
        Assert.Contains(warnings, w => w.Contains("вне диска прошивок", StringComparison.Ordinal));
    }

    // ── Хранение реквизитов ───────────────────────────────────────────────────

    /// <summary>Secret хранится в базе ЗАШИФРОВАННЫМ (глазами не читается), а вот в общий конфиг ключи
    /// хостинга теперь СИНХРОНИЗИРУЮТСЯ: администратор вписывает их один раз, и они доезжают до всех,
    /// кому положено выкладывать (иначе выложить инструкцию мог бы только тот, кто вписал ключ у себя —
    /// «это не дело»). Поэтому s3_access_key/s3_secret_key в SkipSettingsKeys больше НЕТ. Адрес/бакет/
    /// регион и «дублировать ли копией» — синхронизировались и раньше.</summary>
    [Fact]
    public void TheSecret_IsStoredEncrypted_AndTheKeysSyncToEveryMachine()
    {
        using var dbFile = new TempDb();
        using var db = new Database(dbFile.Path);
        var cfg = new ConfigService(db);

        cfg.SetS3SecretKey("очень-секретный-ключ");
        Assert.Equal("очень-секретный-ключ", cfg.S3SecretKey());
        Assert.DoesNotContain("очень-секретный-ключ", cfg.Get("s3_secret_key"));

        // Стёртый ключ — снова пусто, а не «зашифрованная пустота».
        cfg.SetS3SecretKey("");
        Assert.Equal("", cfg.S3SecretKey());
        Assert.Equal("", cfg.Get("s3_secret_key"));

        var skipped = ConfigSyncSkipKeys.Read();
        Assert.DoesNotContain("s3_access_key", skipped);
        Assert.DoesNotContain("s3_secret_key", skipped);
        Assert.DoesNotContain("s3_endpoint", skipped);
        Assert.DoesNotContain("s3_bucket", skipped);
    }

    /// <summary>Round-trip: администратор на машине A вписывает ключи хостинга, и после экспорта/приёма
    /// они оказываются на машине B — секрет расшифровывается там в исходное значение. Плюс два
    /// доказательства совместимости и безопасности: в снимке на диске секрет лежит ТОЛЬКО шифротекстом
    /// (открытым текстом его там нет), а Access Key ID — как обычная строка.</summary>
    [Fact]
    public void S3Keys_TravelToAnotherMachine_AndTheSecretArrivesDecryptable()
    {
        using var m = new TwoMachines();
        m.SetSharedRoot();
        var root = m.Root.Path;

        m.CfgA.SetS3AccessKey("AKIA-EXAMPLE-ID");
        m.CfgA.SetS3SecretKey("очень-секретный-ключ");

        ConfigSyncService.Export(m.SvcA, root, "profileA");

        // Снимок на диске: секрет — шифротекстом (открытого значения нет вовсе), access key — строкой.
        var bytes = File.ReadAllBytes(ConfigSyncService.ConfigPathFor(root));
        var json = ConfigFileCrypto.TryDecrypt(bytes)!;
        Assert.DoesNotContain("очень-секретный-ключ", json);
        var payload = System.Text.Json.Nodes.JsonNode.Parse(json)!.AsObject();
        Assert.StartsWith("enc:", (string?)payload["s3_secret_key"]);
        Assert.Equal("AKIA-EXAMPLE-ID", (string?)payload["s3_access_key"]);

        // Машина B забирает обновление и применяет.
        var update = ConfigSyncService.CheckForUpdate(m.SvcB, out var err);
        Assert.True(err is null, err);
        Assert.NotNull(update);
        ConfigSyncService.Apply(m.SvcB, update!.ConfigPath, root);

        Assert.Equal("AKIA-EXAMPLE-ID", m.CfgB.S3AccessKey());
        Assert.Equal("очень-секретный-ключ", m.CfgB.S3SecretKey());
        // На B он тоже лежит зашифрованным, а не открытым текстом.
        Assert.DoesNotContain("очень-секретный-ключ", m.CfgB.Get("s3_secret_key"));
        Assert.True(m.CfgB.S3().HasCredentials);
    }

    /// <summary>Ключ, вписанный в базу руками (или сохранённый версией программы без шифрования),
    /// обязан читаться как есть: иначе однажды настроенная выкладка отвалилась бы после обновления.</summary>
    [Fact]
    public void AKeyWrittenByHand_KeepsWorking()
    {
        using var dbFile = new TempDb();
        using var db = new Database(dbFile.Path);
        var cfg = new ConfigService(db);

        cfg.Set("s3_secret_key", "вписан-руками");

        Assert.Equal("вписан-руками", cfg.S3SecretKey());
        Assert.True(cfg.S3().HasCredentials || string.IsNullOrEmpty(cfg.S3AccessKey()));
    }

    // ── Связка с укладкой на диск ─────────────────────────────────────────────

    /// <summary>Полный путь укладки: документ ложится рядом с прошивкой на первом диске, второй
    /// экземпляр уходит на хостинг. Ровно то, о чём просил Иван: «дублируем информацию, а не
    /// ярлыками занимаемся».</summary>
    [Fact]
    public void Copy_PlacesTheDocumentNextToFirmware_AndPublishesIt()
    {
        using var first = new TempRoot();
        using var source = new TempRoot();

        var src = Path.Combine(source.Path, "исходник.pdf");
        File.WriteAllText(src, "документ");
        var folder = Path.Combine(first.Path, "ПО", "ПЖ", "SMH5", "Инструкция");

        var storage = new FakeStorage();
        var warnings = new List<string>();

        var placement = InstructionStorage.Copy(src, folder, first.Path, warnings,
            "2.1.0042.0001", PublisherOver(storage));

        Assert.Empty(warnings);
        Assert.True(File.Exists(placement.ActualPath));                       // рядом с прошивкой
        Assert.True(File.Exists(placement.StoredPath));
        Assert.Single(storage.Puts);                                          // хостинг
        Assert.StartsWith("https://fs.elitacompany.ru/", placement.PublishedUrl);
        // Имя каноническое на обоих экземплярах — по нему же строится ссылка под QR-кодом.
        Assert.EndsWith("инструкция_2.1.0042.0001.pdf", placement.StoredPath);
        Assert.EndsWith("/instrukciya_2.1.0042.0001.pdf", placement.PublishedUrl);
    }

    /// <summary>Хостинг не настроен (ключи ещё не выданы) — укладка на диски обязана пройти ровно
    /// так же, как до появления этой возможности, и молча.</summary>
    [Fact]
    public void Copy_WithoutAPublisher_WorksExactlyAsBefore()
    {
        using var first = new TempRoot();
        using var source = new TempRoot();

        var src = Path.Combine(source.Path, "исходник.pdf");
        File.WriteAllText(src, "документ");
        var folder = Path.Combine(first.Path, "ПО", "Инструкция");
        var warnings = new List<string>();

        var placement = InstructionStorage.Copy(src, folder, first.Path, warnings,
            "2.1.0042.0001", publisher: null);

        Assert.True(File.Exists(placement.StoredPath));
        Assert.Null(placement.PublishedUrl);
        Assert.Empty(warnings);
    }

    /// <summary>Хостинг недоступен — версия всё равно создаётся: файл уже на диске, а причина
    /// уезжает в предупреждения рядом с остальными.</summary>
    [Fact]
    public void Copy_PublishingFails_TheVersionStillLands()
    {
        using var first = new TempRoot();
        using var source = new TempRoot();

        var src = Path.Combine(source.Path, "исходник.pdf");
        File.WriteAllText(src, "документ");
        var folder = Path.Combine(first.Path, "ПО", "Инструкция");
        var warnings = new List<string>();

        var storage = new FakeStorage(HttpStatusCode.ServiceUnavailable);
        var placement = InstructionStorage.Copy(src, folder, first.Path, warnings,
            "2.1.0042.0001", PublisherOver(storage));

        Assert.True(File.Exists(placement.StoredPath));
        Assert.Null(placement.PublishedUrl);
        Assert.Single(warnings);
    }

    /// <summary>Секрет не должен уезжать в текст только оттого, что запись где-то подставили в
    /// строку. В S3Settings он лежит уже расшифрованным (ConfigService.S3()), а печатается запись
    /// целиком по умолчанию — то есть достаточно одного «{settings}» в предупреждении, в журнале
    /// страницы «Хранилище» или в тикете, чтобы ключ на запись во весь бакет предприятия оказался
    /// открытым текстом там, где его прочитает кто угодно.</summary>
    [Fact]
    public void S3Settings_ToString_DoesNotRevealTheSecret()
    {
        var settings = new S3Settings("https://s3.twcstorage.ru", "amperus", "ru-1", "po",
            ExampleAccessKey, ExampleSecretKey, "https://fs.elitacompany.ru", true);

        var text = settings.ToString();

        Assert.DoesNotContain(ExampleSecretKey, text);
        Assert.DoesNotContain("wJalrXUtnFEMI", text);
        // Всё остальное остаётся видимым — иначе запись стала бы бесполезна для разбора «почему не
        // выкладывается», ради которого её в строку и подставляют.
        Assert.Contains("amperus", text);
        Assert.Contains(ExampleAccessKey, text);
    }

    /// <summary>«Ключ не задан» и «ключ задан, но скрыт» — разные состояния, и по печати записи их
    /// обязано быть видно: именно этим отличается «ещё не настроили» от «настроили, но не пускает».</summary>
    [Fact]
    public void S3Settings_ToString_StillTellsWhetherTheKeyIsSet()
    {
        var without = new S3Settings("https://s3", "amperus", "ru-1", "", "", "", "", false);

        Assert.Contains("не задан", without.ToString());
    }

    // ── Страница «обратитесь в сервис» внутри выложенного документа ──────────

    /// <summary>Подставной хостинг, сохраняющий ТЕЛО запроса на диск: только так можно посмотреть, что
    /// именно уехало наверх, — а весь смысл вшивания в том, каким документ оказался в бакете.</summary>
    private sealed class BodyCapturingStorage : HttpMessageHandler
    {
        private readonly string _folder;
        public List<string> Saved { get; } = new();

        public BodyCapturingStorage(string folder) => _folder = folder;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var bytes = request.Content is null
                ? Array.Empty<byte>()
                : await request.Content.ReadAsByteArrayAsync(cancellationToken);
            var path = Path.Combine(_folder, $"uploaded-{Saved.Count}.pdf");
            File.WriteAllBytes(path, bytes);
            Saved.Add(path);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("") };
        }
    }

    /// <summary>Рисовальщик страницы: отдаёт настоящий одностраничный PDF — сшивать пустышку нельзя.</summary>
    private sealed class PdfPageWriter : IInstructionStubWriter
    {
        public void Write(string path, string text) => Write(path, StubKind.ServiceNote, null);

        public void Write(string path, StubKind kind, string? versionRaw)
        {
            using var doc = new PdfSharp.Pdf.PdfDocument();
            var page = doc.AddPage();
            using (var gfx = PdfSharp.Drawing.XGraphics.FromPdfPage(page))
                gfx.DrawRectangle(PdfSharp.Drawing.XBrushes.White, 0, 0, page.Width.Point, page.Height.Point);
            doc.Save(path);
        }
    }

    private static string MakePdf(string path, int pages)
    {
        using var doc = new PdfSharp.Pdf.PdfDocument();
        for (var i = 0; i < pages; i++)
        {
            var page = doc.AddPage();
            using var gfx = PdfSharp.Drawing.XGraphics.FromPdfPage(page);
            gfx.DrawRectangle(PdfSharp.Drawing.XBrushes.White, 0, 0, page.Width.Point, page.Height.Point);
        }
        doc.Save(path);
        return path;
    }

    /// <summary>Выложенная инструкция приезжает в бакет со страницей сервиса в конце — и сколько раз
    /// её ни перезаливай, страница остаётся ОДНА. Это и есть то, ради чего всё затевалось: заказчик
    /// открывает по QR один файл, и телефон сервиса должен быть в нём же.</summary>
    [Fact]
    public void PublishedInstruction_CarriesTheServicePage_AndRepublishingDoesNotAddASecond()
    {
        using var first = new TempRoot();
        var folder = Path.Combine(first.Path, "ПО", "ПЖ", "SMH5", "Инструкция");
        Directory.CreateDirectory(folder);
        var doc = MakePdf(Path.Combine(folder, "инструкция_2.1.pdf"), 3);

        var storage = new BodyCapturingStorage(first.Path);
        var publisher = new InstructionPublisher(Settings(), new S3Client(new HttpClient(storage)),
            pdf: null, stubs: new PdfPageWriter());
        var warnings = new List<string>();

        publisher.Publish(doc, doc, first.Path, warnings);
        publisher.Publish(doc, doc, first.Path, warnings);

        Assert.Empty(warnings);
        Assert.Equal(2, storage.Saved.Count);
        foreach (var uploaded in storage.Saved)
        {
            Assert.Equal(4, ServicePageStitcher.PageCount(uploaded));
            Assert.Equal(1, ServicePageStitcher.CountStitchedPages(uploaded));
        }

        // Оригинал на диске не тронут — вшивание идёт во временную копию.
        Assert.Equal(3, ServicePageStitcher.PageCount(doc));
        Assert.Equal(0, ServicePageStitcher.CountStitchedPages(doc));
    }

    /// <summary>В саму заглушку страница сервиса не вшивается: телефон на ней уже напечатан, и вторая
    /// такая же страница выглядела бы поломкой.</summary>
    [Fact]
    public void PublishedStub_DoesNotGetASecondServicePage()
    {
        using var first = new TempRoot();
        var folder = Path.Combine(first.Path, "ПО", "ПЖ", "SMH5", "Инструкция");
        Directory.CreateDirectory(folder);

        var stub = InstructionStub.PathFor(folder, "2.1");
        MakePdf(stub, 1);
        File.AppendAllText(stub, "\n" + InstructionStub.Marker + " kind=dev stamp=abc\n");
        Assert.True(InstructionStub.IsStub(stub));

        var storage = new BodyCapturingStorage(first.Path);
        var publisher = new InstructionPublisher(Settings(), new S3Client(new HttpClient(storage)),
            pdf: null, stubs: new PdfPageWriter());

        publisher.Publish(stub, stub, first.Path, new List<string>());

        var uploaded = Assert.Single(storage.Saved);
        Assert.Equal(0, ServicePageStitcher.CountStitchedPages(uploaded));
    }

    // ── Выкладка с потока интерфейса ─────────────────────────────────────────

    /// <summary>Подставной хостинг, отвечающий ПО-НАСТОЯЩЕМУ асинхронно: продолжение после
    /// <c>SendAsync</c> достаётся уже другому потоку. Обычный FakeStorage выше для этой проверки не
    /// годится — он отвечает так быстро, что задача успевает завершиться синхронно, продолжение
    /// никуда не ставится, и взаимная блокировка не воспроизводится.</summary>
    private sealed class SlowStorage : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            await Task.Delay(20, cancellationToken).ConfigureAwait(false);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("") };
        }
    }

    /// <summary>Поток с очередью, которую НИКТО не разбирает, — так выглядит поток интерфейса WPF,
    /// пока он стоит внутри синхронного вызова: диспетчер жив, но занят и сообщений не обрабатывает.</summary>
    private sealed class NeverPumpedContext : SynchronizationContext
    {
        public override void Post(SendOrPostCallback d, object? state) { /* очередь не разбирается */ }
        public override void Send(SendOrPostCallback d, object? state) =>
            throw new InvalidOperationException("сюда попадать не должны");
    }

    /// <summary>Выкладка, вызванная С ПОТОКА ИНТЕРФЕЙСА, обязана завершаться.
    ///
    /// Это регрессия на живую жалобу: «когда в модерации прошивки указываю инструкцию, всё зависает
    /// и инструкция не публикуется». Сохранение модерации зовёт выкладку синхронно
    /// (<see cref="IInstructionPublisher"/> — обычный метод, внутри GetAwaiter().GetResult()), и пока
    /// в S3Client не было ConfigureAwait(false), продолжение после запроса вставало в очередь
    /// диспетчера — а диспетчер в этот момент стоял на GetResult() и очередь не разбирал. Окно
    /// замирало навсегда, файл на хостинг не уезжал, и на его место потом вставала заглушка.
    ///
    /// Проверка идёт на отдельном потоке с таким же «непрокачиваемым» контекстом: при возврате
    /// поломки тест не зависнет, а честно упадёт по времени ожидания.</summary>
    [Fact]
    public void Publish_FromAUiLikeThread_DoesNotDeadlock()
    {
        using var first = new TempRoot();
        var folder = Path.Combine(first.Path, "ПО", "ПЖ", "SMH5", "Инструкция");
        Directory.CreateDirectory(folder);
        var file = Path.Combine(folder, "инструкция_2.1.pdf");
        File.WriteAllText(file, new string('x', 64 * 1024));

        var publisher = new InstructionPublisher(Settings(), new S3Client(new HttpClient(new SlowStorage())));
        var warnings = new List<string>();
        string? url = null;
        var finished = new ManualResetEventSlim(false);

        var thread = new Thread(() =>
        {
            SynchronizationContext.SetSynchronizationContext(new NeverPumpedContext());
            try { url = publisher.Publish(file, file, first.Path, warnings); }
            finally { finished.Set(); }
        }) { IsBackground = true };
        thread.Start();

        Assert.True(finished.Wait(TimeSpan.FromSeconds(30)),
            "выкладка не вернулась — в S3Client снова потерян ConfigureAwait(false), " +
            "и сохранение модерации будет вешать окно намертво");
        Assert.Equal("https://fs.elitacompany.ru/PO/PZH/SMH5/Instrukciya/instrukciya_2.1.pdf", url);
        Assert.Empty(warnings);
    }
}
