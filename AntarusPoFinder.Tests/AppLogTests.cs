using System;
using System.IO;
using System.Linq;
using AntarusPoFinder.Core.Services;
using Xunit;

namespace AntarusPoFinder.Tests;

/// <summary>ЕДИНЫЙ ЖУРНАЛ ПРИЛОЖЕНИЯ: формат строки, ротация и отсечение секретов.
///
/// Проверяется здесь не «пишется ли вообще» (это видно и глазами), а три свойства, от которых
/// зависит, будет ли журнал полезен через полгода:
///
/// • <b>одна строка — одно событие</b>. Журнал читают по SSH через grep; стек исключения в десять
///   строк превратил бы «сколько у нас падений» в неразбериху, а девять строк беды — в невидимые;
/// • <b>ротация</b>. Журнал, который растёт вечно, — это бомба на диске у человека, который о нём
///   не знает;
/// • <b>секреты не уезжают</b>. Журнал уходит наружу сам, и проверка перед отправкой — последнее,
///   что отделяет описание работы программы от утечки реквизитов.</summary>
public class AppLogTests
{
    // ── Формат строки ────────────────────────────────────────────────────────────────────────

    /// <summary>Колонки на своих местах и в том порядке, в каком их разбирают: время, уровень,
    /// подсистема, текст.</summary>
    [Fact]
    public void Строка_журнала_начинается_со_времени_уровня_и_подсистемы()
    {
        var line = AppLogFormat.Line(new DateTime(2026, 11, 11, 14, 20, 3, 123),
            AppLogLevel.Error, "disk", "обход сорвался");

        Assert.StartsWith("2026-11-11 14:20:03.123 ERROR disk", line, StringComparison.Ordinal);
        Assert.EndsWith("обход сорвался", line, StringComparison.Ordinal);

        // Разбор по пробелам (так его и читают awk'ом) должен давать ровно те же четыре поля.
        var parts = line.Split(' ', 5, StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal("ERROR", parts[2]);
        Assert.Equal("disk", parts[3]);
    }

    /// <summary>Перевод строки внутри события запрещён: иначе одно падение выглядело бы как десять
    /// событий, а «grep ERROR» показывал бы только первую строку стека.</summary>
    [Fact]
    public void Стек_исключения_укладывается_в_одну_строку()
    {
        var text = "упало\r\n   в методе А\n   в методе Б";

        var line = AppLogFormat.Line(DateTime.Now, AppLogLevel.Error, "app", text);

        Assert.DoesNotContain('\n', line);
        Assert.DoesNotContain('\r', line);
        Assert.Contains("\\n   в методе А", line, StringComparison.Ordinal);
    }

    /// <summary>Свёрнутое должно разворачиваться обратно — иначе стек в журнале нечитаем. Обратная
    /// косая черта в путях (<c>C:\new</c>) не должна при этом превращаться в перевод строки.</summary>
    [Fact]
    public void Свёрнутый_текст_разворачивается_обратно_вместе_с_путями()
    {
        var text = "не найдено C:\\new\\файл.psl\nпробовали ещё раз";

        var restored = AppLogFormat.Unescape(AppLogFormat.Escape(text));

        Assert.Equal(text, restored);
    }

    /// <summary>Подсистема — колонка, по которой отбирают строки. Пробел или кириллица в ней
    /// разъехались бы с разбором и с набором запроса по SSH.</summary>
    [Theory]
    [InlineData("Сетевой диск")]
    [InlineData("  DISK  ")]
    [InlineData("")]
    public void Подсистема_приводится_к_латинице_без_пробелов(string given)
    {
        var area = AppLogFormat.Area(given);

        Assert.DoesNotContain(' ', area);
        Assert.All(area, ch => Assert.True(ch is >= 'a' and <= 'z' or >= '0' and <= '9' or '-' or '.',
            $"в подсистеме «{area}» оказался знак «{ch}»"));
    }

    // ── Ротация ──────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Переросший_потолок_журнал_требует_ротации()
    {
        Assert.True(AppLogRotation.NeedsRoll(1_000_000, 1_000_000));
        Assert.False(AppLogRotation.NeedsRoll(999_999, 1_000_000));
    }

    /// <summary>Имя архива разбирается обратно во время — на этом держится и уборка, и порядок
    /// отправки.</summary>
    [Fact]
    public void Время_архива_читается_из_его_имени()
    {
        var at = new DateTime(2026, 11, 11, 3, 4, 5);

        var name = AppLogRotation.RolledName(at);

        Assert.Equal("app-20261111-030405.log", name);
        Assert.Equal(at, AppLogRotation.StampOf(name));
        Assert.Null(AppLogRotation.StampOf("app.log"));
        Assert.Null(AppLogRotation.StampOf("заметка.log"));
    }

    /// <summary>Лишние по числу удаляются, а самые свежие остаются — беда ближе к последнему
    /// файлу, чем к первому.</summary>
    [Fact]
    public void Ротация_оставляет_заданное_число_самых_свежих()
    {
        var now = new DateTime(2026, 11, 11);
        var names = Enumerable.Range(1, 10)
            .Select(d => AppLogRotation.RolledName(now.AddDays(-d)))
            .ToList();

        var doomed = AppLogRotation.ToDelete(names, now, keepFiles: 3, keepDays: 0);

        Assert.Equal(7, doomed.Count);
        Assert.DoesNotContain(AppLogRotation.RolledName(now.AddDays(-1)), doomed);
        Assert.Contains(AppLogRotation.RolledName(now.AddDays(-10)), doomed);
    }

    /// <summary>Срок важнее числа: жалоба приходит «на прошлой неделе было», и неделя должна
    /// лежать целиком, а месячной давности — уйти, сколько бы файлов ни накопилось.</summary>
    [Fact]
    public void Ротация_удаляет_состарившееся_даже_если_файлов_мало()
    {
        var now = new DateTime(2026, 11, 11);
        var names = new[]
        {
            AppLogRotation.RolledName(now.AddDays(-2)),
            AppLogRotation.RolledName(now.AddDays(-40)),
        };

        var doomed = AppLogRotation.ToDelete(names, now, keepFiles: 8, keepDays: 30);

        Assert.Single(doomed);
        Assert.Equal(AppLogRotation.RolledName(now.AddDays(-40)), doomed[0]);
    }

    /// <summary>Чужой файл в папке журналов не наш, и удалять его нельзя: туда мог что-нибудь
    /// положить человек.</summary>
    [Fact]
    public void Чужие_файлы_в_папке_журналов_не_удаляются()
    {
        var doomed = AppLogRotation.ToDelete(
            new[] { "важное.txt", "app-20200101-000000.log" }, new DateTime(2026, 11, 11),
            keepFiles: 0, keepDays: 1);

        Assert.Equal(new[] { "app-20200101-000000.log" }, doomed);
    }

    // ── Запись на диск ───────────────────────────────────────────────────────────────────────

    /// <summary>Переросший потолок журнал откладывается в архив сам, по ходу записи, а не по
    /// расписанию: расписание на машине, которую выключают, не наступает.</summary>
    [Fact]
    public void Журнал_сам_откладывается_в_архив_когда_перерос_потолок()
    {
        WithTempLog(() =>
        {
            AppLog.MaxBytes = 300;
            for (var i = 0; i < 20; i++) AppLog.Info("test", new string('я', 40));

            Assert.NotEmpty(AppLog.Archives());
            Assert.True(new FileInfo(AppLog.CurrentPath).Length < 2000,
                "текущий журнал должен был смениться, а не расти дальше");
        });
    }

    /// <summary>Отправка начинается с ротации: слать файл, в который прямо сейчас пишут, значит
    /// однажды отправить его обрезанным посреди события.</summary>
    [Fact]
    public void Ротация_перед_отправкой_уносит_написанное_в_архив()
    {
        WithTempLog(() =>
        {
            AppLog.Error("test", "вот это и надо отправить");

            var archive = AppLog.Roll();

            Assert.NotNull(archive);
            Assert.Contains("вот это и надо отправить", File.ReadAllText(archive!), StringComparison.Ordinal);
            Assert.False(File.Exists(AppLog.CurrentPath), "текущий журнал должен был уехать целиком");
            Assert.Null(AppLog.Roll());   // откладывать больше нечего
        });
    }

    // ── Отсечение секретов ───────────────────────────────────────────────────────────────────

    /// <summary>Шифрованное значение из общего конфига в журнал попасть не должно, а если попало —
    /// журнал не уезжает. Та же проверка, что у слепка диска, тем же кодом.</summary>
    [Fact]
    public void Журнал_с_шифрованным_значением_не_отправляется()
    {
        var refusal = DiskSnapshotSafety.RefusalForText(
            "2026-11-11 14:20:03.123 WARN  store    ключ enc:AQID не подошёл");

        Assert.NotNull(refusal);
        Assert.Contains("enc:", refusal!, StringComparison.Ordinal);
    }

    [Fact]
    public void Журнал_с_паролем_рядом_со_значением_не_отправляется()
    {
        Assert.NotNull(DiskSnapshotSafety.RefusalForText("подключаюсь: password=qwerty123"));
        Assert.NotNull(DiskSnapshotSafety.RefusalForText("s3 token: abcdef"));
        Assert.NotNull(DiskSnapshotSafety.RefusalForText("-----BEGIN RSA PRIVATE KEY-----"));
    }

    /// <summary>А вот ЧЕСТНОЕ сообщение об ошибке с теми же словами уезжать обязано. Отказ
    /// касается всей пачки: запрети мы «пароль не подошёл», и одна такая строка заткнула бы журнал
    /// машины целиком — молча и до тех пор, пока кто-нибудь не хватится.</summary>
    [Theory]
    [InlineData("пароль не подошёл, вход отклонён")]
    [InlineData("смена пароля отменена пользователем")]
    [InlineData("the credentials supplied are not complete")]
    [InlineData("разбор sequence: ожидался номер")]
    [InlineData("не найдено C:\\Users\\ilia\\AppData\\Local\\token-cache")]
    public void Обычные_сообщения_об_ошибках_отправке_не_мешают(string line)
    {
        Assert.Null(DiskSnapshotSafety.RefusalForText(line));
    }

    /// <summary>Пачка проверяется ЦЕЛИКОМ: секрет в одном куске останавливает отправку всех.</summary>
    [Fact]
    public void Секрет_в_одном_куске_останавливает_всю_пачку()
    {
        var whole = string.Join("\n", new[]
        {
            "2026-11-11 10:00:00.000 INFO  app      старт",
            "2026-11-11 10:00:01.000 WARN  sync     s3Secret: AQIDBAU=",
        });

        Assert.NotNull(DiskSnapshotSafety.RefusalForText(whole));
    }

    // ── Пачка для отправки ───────────────────────────────────────────────────────────────────

    /// <summary>Отправлять нечего — не отправляем вовсе. Пустой архив раз в сутки с каждой машины
    /// это ровно тот мусорный поток, которого и боялись.</summary>
    [Fact]
    public async System.Threading.Tasks.Task Пустой_журнал_никуда_не_уезжает()
    {
        await WithTempLogAsync(async () =>
        {
            var result = await AppLogDrop.FlushAsync("ILIA-PC", "https://example.invalid/upload", "код");

            Assert.True(result.Ok);
            Assert.Equal(0, result.Files);
        });
    }

    /// <summary>Пачка собирается от свежих к старым и упирается в потолок размера: если всё не
    /// влезает, уехать должно то, что ближе к беде.</summary>
    [Fact]
    public void Пачка_набирается_со_свежих_и_не_перерастает_потолок()
    {
        WithTempLog(() =>
        {
            var names = new[] { "app-20261111-100000.log", "app-20261110-100000.log", "app-20261109-100000.log" };
            foreach (var name in names)
                File.WriteAllText(Path.Combine(AppLog.Folder, name), new string('x', 400));

            var batch = AppLogDrop.TakeBatch(AppLog.Archives(), maxBytes: 900);

            Assert.Equal(2, batch.Count);
            Assert.Equal(names[0], Path.GetFileName(batch[0]));
        });
    }

    /// <summary>Пачка распаковывается обратно по кускам, и имена кусков сохраняются: по ним видно,
    /// за какое время журнал.</summary>
    [Fact]
    public void Пачка_упаковывается_и_читается_обратно()
    {
        var body = AppLogDrop.Pack(new[]
        {
            ("app-20261111-100000.log", "первая строка"),
            ("app-20261110-100000.log", "вторая строка"),
        });

        using var zip = new System.IO.Compression.ZipArchive(new MemoryStream(body));
        Assert.Equal(2, zip.Entries.Count);
        using var reader = new StreamReader(zip.GetEntry("app-20261111-100000.log")!.Open());
        Assert.Equal("первая строка", reader.ReadToEnd());
    }

    /// <summary>Имя архива: время ПЕРВЫМ и только латиница — в папке diag/ лежит вперемешку всё со
    /// всех машин, и сортировка по имени должна совпадать с сортировкой по времени.</summary>
    [Fact]
    public void Имя_пачки_начинается_со_времени_и_отличимо_от_слепка()
    {
        var name = AppLogDrop.FileName("ПК-Ильи", new DateTime(2026, 11, 11, 3, 4, 5));

        Assert.StartsWith("20261111_030405_log_", name, StringComparison.Ordinal);
        Assert.EndsWith(".zip", name, StringComparison.Ordinal);
        Assert.All(name, ch => Assert.True(ch < 128, $"в имени «{name}» оказалась кириллица"));
    }

    /// <summary>Не чаще раза в сутки: журнал нужен для разбора после жалобы, а не для наблюдения
    /// в реальном времени.</summary>
    [Fact]
    public void Журнал_уезжает_не_чаще_раза_в_сутки()
    {
        var now = new DateTime(2026, 11, 11, 12, 0, 0, DateTimeKind.Utc);

        Assert.False(AppLogDrop.Due(now.AddHours(-23), now));
        Assert.True(AppLogDrop.Due(now.AddHours(-25), now));
        Assert.True(AppLogDrop.Due(DateTime.MinValue, now), "первый раз отправлять надо всегда");
    }

    // ── Оснастка ─────────────────────────────────────────────────────────────────────────────

    /// <summary>Журнал на время проверки переезжает во временную папку и включается: на прогоне
    /// он заглушен целиком (см. AssemblyInfo), чтобы тесты не писали в профиль того, кто их
    /// запустил.</summary>
    private static void WithTempLog(Action body) =>
        WithTempLogAsync(() => { body(); return System.Threading.Tasks.Task.CompletedTask; })
            .GetAwaiter().GetResult();

    private static async System.Threading.Tasks.Task WithTempLogAsync(Func<System.Threading.Tasks.Task> body)
    {
        var folder = Path.Combine(Path.GetTempPath(), "antarus_log_" + Guid.NewGuid().ToString("N"));
        var savedFolder = AppLog.Folder;
        var savedMax = AppLog.MaxBytes;
        Directory.CreateDirectory(folder);
        AppLog.Folder = folder;
        AppLog.Enabled = true;
        try { await body(); }
        finally
        {
            AppLog.Enabled = false;
            AppLog.Folder = savedFolder;
            AppLog.MaxBytes = savedMax;
            try { Directory.Delete(folder, recursive: true); } catch (IOException) { }
        }
    }
}
