using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AntarusPoFinder.Core.Data;
using AntarusPoFinder.Core.Domain;
using AntarusPoFinder.Core.Services;
using Xunit;

namespace AntarusPoFinder.Tests;

/// <summary>СЕАНСЫ РАБОТЫ ПРОГРАММЫ. Просьба дословно: «сделай вкладку где можно сессию смотреть и
/// доступ супер админа, чтобы я мог управлять сессиями всеми через него, а обычный пользователь
/// только свои сессии залогиненые видит (так можно будет убивать старые версии или те кто мусор
/// записывают)».
///
/// Проверяем ровно то, на чём это может молча развалиться:
/// <list type="bullet">
/// <item>что считается живым сеансом, а что протухшим (протухший обязан ПОКАЗЫВАТЬСЯ, а не
///   исчезать: исчезнувший сеанс неотличим от «никто не работал»);</item>
/// <item>видимость: свои/все;</item>
/// <item>право завершать — только у администратора;</item>
/// <item>слияние между машинами: просьба завершить не должна теряться под чужой свежей отметкой
///   жизни — это главный случай, ради которого поля сливаются по отдельности;</item>
/// <item>выход не прерывает заливку в ПЛК. Это не про удобство: обрыв оставляет контроллер без
///   рабочей прошивки, и такой баг в этом проекте уже был — с автоперезапуском на обновление.</item>
/// </list></summary>
public class AppSessionsTests
{
    private static string Stamp(DateTime dt) => dt.ToString("yyyy-MM-dd HH:mm:ss.fffffff");

    private static AppSession Session(
        string id = "s1",
        string login = "revkin.i",
        string machine = "PC-1",
        string version = "2.2.5.0",
        DateTime? beat = null,
        string endedAt = "",
        string endedBy = "",
        string closedAt = "")
        => new()
        {
            SessionId = id,
            OriginId = "origin-" + id,
            MachineName = machine,
            UserLogin = login,
            AppVersion = version,
            Channel = "рабочая",
            StartedAt = Stamp(DateTime.Now.AddHours(-1)),
            HeartbeatAt = Stamp(beat ?? DateTime.Now),
            EndedAt = endedAt,
            EndedBy = endedBy,
            ClosedAt = closedAt,
        };

    // ── Живой или нет ──────────────────────────────────────────────────────

    [Fact]
    public void СвежаяОтметка_СеансРаботает()
    {
        var now = DateTime.Now;
        var s = Session(beat: now.AddMinutes(-2));

        Assert.True(AppSessions.IsFresh(s, now));
        Assert.Equal(AppSessionState.Alive, AppSessions.StateOf(s, now));
        Assert.Equal("работает", AppSessions.StateLabel(s, now));
    }

    /// <summary>Протухший показывается завершённым, но ОТДЕЛЬНОЙ подписью: «не отвечает» — это
    /// «выключили по питанию / увезли ноутбук», а не «человек вышел». Строка при этом остаётся в
    /// списке: молча пропавший сеанс неотличим от того, которого не было.</summary>
    [Fact]
    public void ОтметкаПротухла_НеОтвечает_НоСтрокаОстаётся()
    {
        var now = DateTime.Now;
        var s = Session(beat: now - AppSessions.StaleAfter - TimeSpan.FromMinutes(1));

        Assert.False(AppSessions.IsFresh(s, now));
        Assert.Equal(AppSessionState.Stale, AppSessions.StateOf(s, now));
        Assert.False(AppSessions.IsLive(s, now));
        Assert.Equal("не отвечает", AppSessions.StateLabel(s, now));
    }

    /// <summary>Пустая отметка — не «живой с незапамятных времён», а «не знаем». Так выглядит
    /// строка со старой версии программы, и считать её живой значило бы завести вечный сеанс,
    /// который некому завершить.</summary>
    [Fact]
    public void ПустаяОтметка_НеСчитаетсяЖивой()
    {
        var s = Session();
        s.HeartbeatAt = "";

        Assert.False(AppSessions.IsFresh(s, DateTime.Now));
    }

    [Fact]
    public void ПомеченНаЗавершение_НоЕщёЖив_ЭтоЗавершается()
    {
        var now = DateTime.Now;
        var s = Session(beat: now.AddMinutes(-1), endedAt: Stamp(now.AddMinutes(-2)), endedBy: "revkin.i");

        Assert.Equal(AppSessionState.Ending, AppSessions.StateOf(s, now));
        Assert.True(AppSessions.IsLive(s, now));
        Assert.Contains("завершается", AppSessions.StateLabel(s, now));
    }

    /// <summary>Машину выключили раньше, чем она успела прочитать просьбу. Исход тот же — сеанса
    /// нет, — и показывать его «не отвечает» значило бы заставить администратора гадать, сработала
    /// его команда или нет.</summary>
    [Fact]
    public void ПомеченНаЗавершение_ИПротух_ЭтоЗавершён()
    {
        var now = DateTime.Now;
        var s = Session(beat: now - AppSessions.StaleAfter - TimeSpan.FromMinutes(5),
                        endedAt: Stamp(now.AddHours(-1)), endedBy: "revkin.i");

        Assert.Equal(AppSessionState.Closed, AppSessions.StateOf(s, now));
        Assert.Contains("завершён", AppSessions.StateLabel(s, now));
    }

    // ── Кто что видит ──────────────────────────────────────────────────────

    [Fact]
    public void Наладчик_ВидитТолькоСвоиСеансы()
    {
        var all = new[]
        {
            Session(id: "a", login: "revkin.i", machine: "PC-1"),
            Session(id: "b", login: "petrov", machine: "PC-2"),
            Session(id: "c", login: "revkin.i", machine: "PC-7"),
        };

        var visible = AppSessions.Visible(all, "naladchik", "revkin.i");

        Assert.Equal(2, visible.Count);
        Assert.All(visible, s => Assert.Equal("revkin.i", s.UserLogin));
        // Две машины — и обе его: «свой» определяется логином, а не компьютером.
        Assert.Contains(visible, s => s.MachineName == "PC-7");
    }

    [Fact]
    public void Администратор_ВидитВсе()
    {
        var all = new[]
        {
            Session(id: "a", login: "revkin.i"),
            Session(id: "b", login: "petrov"),
        };

        Assert.Equal(2, AppSessions.Visible(all, "administrator", "revkin.i").Count);
    }

    /// <summary>Логин приезжает и нормализованным (AD), и как имя учётки Windows — «Naladka3» и
    /// «naladka3» это один человек, и не узнав его, программа спрятала бы от него его же сеанс.</summary>
    [Fact]
    public void СвойСеанс_УзнаётсяБезОглядкиНаРегистр()
    {
        var all = new[] { Session(id: "a", login: "Naladka3") };

        Assert.Single(AppSessions.Visible(all, "programmer", "naladka3"));
    }

    /// <summary>Пустой логин своим не считается НИКОГДА: иначе человек с пустым «кто действует»
    /// увидел бы все безымянные сеансы конторы.</summary>
    [Fact]
    public void ПустойЛогин_НеСчитаетсяСвоим()
    {
        var all = new[] { Session(id: "a", login: "") };

        Assert.Empty(AppSessions.Visible(all, "naladchik", ""));
    }

    // ── Право завершать ────────────────────────────────────────────────────

    [Fact]
    public void ЗавершатьМожетТолькоАдминистратор()
    {
        var now = DateTime.Now;
        var s = Session(id: "b", login: "petrov", beat: now);

        Assert.False(AppSessions.CanTerminate("naladchik", s, "my-session", now));
        Assert.False(AppSessions.CanTerminate("programmer", s, "my-session", now));
        Assert.Contains("только администратор", AppSessions.TerminationRefusal("naladchik", s, "my-session", now));

        Assert.True(AppSessions.CanTerminate("administrator", s, "my-session", now));
    }

    [Fact]
    public void СвойСобственныйСеанс_ЗавершатьНеПредлагаем()
    {
        var now = DateTime.Now;
        var s = Session(id: "mine", beat: now);

        var refusal = AppSessions.TerminationRefusal("administrator", s, "mine", now);

        Assert.NotNull(refusal);
        Assert.Contains("ваш собственный", refusal);
    }

    /// <summary>Протухший не завершаем: программы там давно нет, отметка никому не доедет, и строка
    /// навсегда осталась бы «завершается» — список врал бы о сделанном.</summary>
    [Fact]
    public void НеотвечающийСеанс_ЗавершатьНечего()
    {
        var now = DateTime.Now;
        var s = Session(id: "b", login: "petrov", beat: now - AppSessions.StaleAfter - TimeSpan.FromMinutes(1));

        Assert.Contains("не отвечает", AppSessions.TerminationRefusal("administrator", s, "mine", now));
    }

    [Fact]
    public void ПовторноеЗавершение_Отклоняется()
    {
        var now = DateTime.Now;
        var s = Session(id: "b", login: "petrov", beat: now, endedAt: Stamp(now.AddMinutes(-1)), endedBy: "revkin.i");

        Assert.Contains("уже помечен", AppSessions.TerminationRefusal("administrator", s, "mine", now));
    }

    // ── Выход ждёт долгую операцию ─────────────────────────────────────────

    /// <summary>⚠️ ПРО ОКИРПИЧИВАНИЕ КОНТРОЛЛЕРА. Завершение сеанса — такой же принудительный выход,
    /// как автообновление, и прерывать им заливку в ПЛК нельзя: контроллер останется без рабочей
    /// прошивки. Правило берётся то же самое, что у перезапуска (LongOperationRules), а не своя
    /// копия списка — иначе новая долгая операция, забытая во втором списке, вернула бы тот баг.</summary>
    [Fact]
    public void ЗаливкаВПлк_ОткладываетВыход()
    {
        var active = new[]
        {
            new LongOperation(LongOperationKind.PlcDeploy, LongOperationSubject.Folder(@"C:\ПО\A"),
                "Загрузка в ПЛК: ТГР 2.0", DateTime.Now),
        };

        var reason = AppSessions.ShutdownDelayReason(active);

        Assert.NotNull(reason);
        Assert.Contains("загрузка в плк", reason);
        Assert.Equal(LongOperationRules.RestartBlockReason(active), reason);
    }

    [Fact]
    public void СборкаLfsИЗаменаФайла_ТожеОткладываютВыход()
    {
        foreach (var kind in new[] { LongOperationKind.LfsBuild, LongOperationKind.FirmwareReplace,
                                     LongOperationKind.DiskRebuild, LongOperationKind.LegacyImport })
        {
            var active = new[] { new LongOperation(kind, "", "работа", DateTime.Now) };
            Assert.NotNull(AppSessions.ShutdownDelayReason(active));
        }
    }

    /// <summary>А вот слепок диска и чистка выход НЕ откладывают: первый только читает, вторая
    /// удаляет уже ненужное поштучно. Откладывать ради них значило бы не отключить человека вовсе —
    /// слепок снимается по расписанию и может идти когда угодно.</summary>
    [Fact]
    public void ЧтениеДискаИЧистка_ВыходНеОткладывают()
    {
        foreach (var kind in new[] { LongOperationKind.DiskSnapshot, LongOperationKind.DiskCleanup })
        {
            var active = new[] { new LongOperation(kind, "", "работа", DateTime.Now) };
            Assert.Null(AppSessions.ShutdownDelayReason(active));
        }
    }

    [Fact]
    public void НичегоНеИдёт_ВыходитСразу()
    {
        Assert.Null(AppSessions.ShutdownDelayReason(Array.Empty<LongOperation>()));
    }

    // ── Слияние между машинами ─────────────────────────────────────────────

    /// <summary>ГЛАВНЫЙ СЛУЧАЙ. Администратор в 10:00 пометил сеанс на завершение; коллега про это
    /// ещё не знает и в 10:01 отметился живым. Сливай мы «целиком по свежести» — победила бы копия
    /// коллеги, и просьба завершить исчезла бы бесследно, а администратор решил бы, что кнопка не
    /// работает.</summary>
    [Fact]
    public void СвежаяОтметкаЖизни_НеСтираетПросьбуЗавершить()
    {
        var baseTime = new DateTime(2026, 11, 17, 10, 0, 0);
        var withEnd = Session(id: "s", beat: baseTime.AddMinutes(-5),
                              endedAt: Stamp(baseTime), endedBy: "revkin.i");
        var stillAlive = Session(id: "s", beat: baseTime.AddMinutes(1));

        var merged = AppSessions.Merge(withEnd, stillAlive);

        Assert.Equal(Stamp(baseTime), merged.EndedAt);
        Assert.Equal("revkin.i", merged.EndedBy);
        Assert.Equal(Stamp(baseTime.AddMinutes(1)), merged.HeartbeatAt);
    }

    /// <summary>То же самое с другой стороны: входящая строка приехала к тому, кто ещё не знает о
    /// завершении. Порядок слияния не должен ничего менять.</summary>
    [Fact]
    public void ПросьбаЗавершить_ПриезжаетНаЧужуюМашину()
    {
        var baseTime = new DateTime(2026, 11, 17, 10, 0, 0);
        var mineAlive = Session(id: "s", beat: baseTime.AddMinutes(1));
        var incomingEnded = Session(id: "s", beat: baseTime.AddMinutes(-5),
                                    endedAt: Stamp(baseTime), endedBy: "revkin.i");

        var merged = AppSessions.Merge(mineAlive, incomingEnded);

        Assert.Equal(Stamp(baseTime), merged.EndedAt);
        Assert.Equal("revkin.i", merged.EndedBy);
        Assert.Equal(Stamp(baseTime.AddMinutes(1)), merged.HeartbeatAt);
    }

    /// <summary>Завершение ЛИПКОЕ: снять его нельзя ничем. Это и есть ответ на «синхронизация не
    /// переносит удаления» — завершение сделано добавлением поля, а не удалением строки, и значит,
    /// доезжает обычным обменом.</summary>
    [Fact]
    public void ЗавершениеНеСнимаетсяПустойКопией()
    {
        var baseTime = new DateTime(2026, 11, 17, 10, 0, 0);
        var ended = Session(id: "s", beat: baseTime, endedAt: Stamp(baseTime), endedBy: "revkin.i");
        var clean = Session(id: "s", beat: baseTime.AddHours(2));

        Assert.NotEmpty(AppSessions.Merge(ended, clean).EndedAt);
        Assert.NotEmpty(AppSessions.Merge(clean, ended).EndedAt);
    }

    /// <summary>Две машины пометили сеанс — берём РАННЕЕ решение вместе с его подписью: иначе имя
    /// завершившего разошлось бы со временем, стоящим рядом с ним.</summary>
    [Fact]
    public void ДваЗавершения_ПобеждаетРаннееВместеСПодписью()
    {
        var baseTime = new DateTime(2026, 11, 17, 10, 0, 0);
        var first = Session(id: "s", beat: baseTime, endedAt: Stamp(baseTime), endedBy: "revkin.i");
        var second = Session(id: "s", beat: baseTime, endedAt: Stamp(baseTime.AddMinutes(5)), endedBy: "petrov");

        var merged = AppSessions.Merge(second, first);

        Assert.Equal(Stamp(baseTime), merged.EndedAt);
        Assert.Equal("revkin.i", merged.EndedBy);
    }

    [Fact]
    public void НовыйСеанс_ПриезжаетЦеликом()
    {
        var incoming = Session(id: "новый", login: "petrov", machine: "PC-9");

        var merged = AppSessions.Merge(null, incoming);

        Assert.Equal("новый", merged.SessionId);
        Assert.Equal("petrov", merged.UserLogin);
        Assert.Equal("PC-9", merged.MachineName);
    }

    // ── То же, но через базу ───────────────────────────────────────────────

    private static string NewTempDb() => Path.Combine(Path.GetTempPath(), $"antarus_sessions_{Guid.NewGuid():N}.db");

    [Fact]
    public void База_ЗаводитСеансОтмечаетИЗакрывает()
    {
        var path = NewTempDb();
        try
        {
            using var db = new Database(path);
            db.StartSession(new AppSession
            {
                SessionId = "s1", MachineName = "PC-1", UserLogin = "revkin.i",
                AppVersion = "2.2.5.0", Channel = "рабочая",
            });

            var created = db.FindSession("s1")!;
            Assert.Equal("PC-1", created.MachineName);
            Assert.NotEmpty(created.HeartbeatAt);
            Assert.Empty(created.EndedAt);
            Assert.Empty(created.ClosedAt);

            db.HeartbeatSession("s1", "revkin.i");
            Assert.True(string.CompareOrdinal(db.FindSession("s1")!.HeartbeatAt, created.HeartbeatAt) >= 0);

            db.CloseSession("s1");
            Assert.NotEmpty(db.FindSession("s1")!.ClosedAt);
        }
        finally { TempDbFiles.Delete(path); }
    }

    /// <summary>Повторная просьба завершить не сдвигает момент решения вперёд — иначе слияние
    /// выбирало бы между двумя разными «раньше», и подпись разъехалась бы со временем.</summary>
    [Fact]
    public void База_ПовторнаяПросьбаЗавершить_НеСдвигаетОтметку()
    {
        var path = NewTempDb();
        try
        {
            using var db = new Database(path);
            db.StartSession(new AppSession { SessionId = "s1", UserLogin = "petrov" });

            db.RequestEndSession("s1", "revkin.i");
            var first = db.FindSession("s1")!;

            db.RequestEndSession("s1", "кто-то другой");
            var second = db.FindSession("s1")!;

            Assert.Equal(first.EndedAt, second.EndedAt);
            Assert.Equal("revkin.i", second.EndedBy);
        }
        finally { TempDbFiles.Delete(path); }
    }

    /// <summary>Завершённый сеанс не отмечается живым дальше: HeartbeatSession не трогает строки с
    /// проставленным closed_at, иначе закрытая программа «оживала» бы чужой отметкой.</summary>
    [Fact]
    public void База_ЗакрытыйСеансНеОживает()
    {
        var path = NewTempDb();
        try
        {
            using var db = new Database(path);
            db.StartSession(new AppSession { SessionId = "s1", UserLogin = "petrov" });
            db.CloseSession("s1");
            var closedBeat = db.FindSession("s1")!.HeartbeatAt;

            db.HeartbeatSession("s1", "petrov");

            Assert.Equal(closedBeat, db.FindSession("s1")!.HeartbeatAt);
        }
        finally { TempDbFiles.Delete(path); }
    }

    /// <summary>Слияние через базу — тот же главный случай, что и выше, но уже с чтением-записью:
    /// приехавшая со своей машины свежая отметка не должна стирать нашу просьбу завершить.</summary>
    [Fact]
    public void База_СлияниеСохраняетПросьбуЗавершить()
    {
        var path = NewTempDb();
        try
        {
            using var db = new Database(path);
            db.StartSession(new AppSession { SessionId = "s1", UserLogin = "petrov", MachineName = "PC-2" });
            db.RequestEndSession("s1", "revkin.i");

            // Так выглядит строка, приехавшая с машины коллеги: он отметился живым и про завершение
            // ещё не слышал.
            db.MergeSessions(new[]
            {
                new AppSession
                {
                    SessionId = "s1", UserLogin = "petrov", MachineName = "PC-2",
                    HeartbeatAt = Stamp(DateTime.Now.AddMinutes(10)),
                },
            });

            var merged = db.FindSession("s1")!;
            Assert.NotEmpty(merged.EndedAt);
            Assert.Equal("revkin.i", merged.EndedBy);
        }
        finally { TempDbFiles.Delete(path); }
    }

    /// <summary>Уборка чистит только давно закрытое. Это чистка СВОЕЙ базы: удаления общий конфиг не
    /// переносит, и порог взят большой нарочно — чтобы срок истёк у всех примерно разом и строку
    /// некому было вернуть.</summary>
    [Fact]
    public void База_УборкаНеТрогаетСвежиеСеансы()
    {
        var path = NewTempDb();
        try
        {
            using var db = new Database(path);
            db.StartSession(new AppSession { SessionId = "свежий", UserLogin = "petrov" });
            db.MergeSessions(new[]
            {
                new AppSession
                {
                    SessionId = "древний", UserLogin = "petrov",
                    HeartbeatAt = Stamp(DateTime.Now - AppSessions.KeepClosedFor - TimeSpan.FromDays(1)),
                    ClosedAt = Stamp(DateTime.Now - AppSessions.KeepClosedFor - TimeSpan.FromDays(1)),
                },
            });

            db.PruneOldSessions(DateTime.Now);

            var left = db.GetSessions().Select(s => s.SessionId).ToList();
            Assert.Contains("свежий", left);
            Assert.DoesNotContain("древний", left);
        }
        finally { TempDbFiles.Delete(path); }
    }
}
