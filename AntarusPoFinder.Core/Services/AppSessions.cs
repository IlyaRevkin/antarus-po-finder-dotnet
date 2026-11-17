using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using AntarusPoFinder.Core.Domain;

namespace AntarusPoFinder.Core.Services;

/// <summary>ПРАВИЛА СЕАНСОВ — кто считается живым, кто чей сеанс видит, кому можно завершать и что
/// происходит со строкой при слиянии конфигов.
///
/// Вынесено отдельной статикой без состояния по той же причине, что и
/// <see cref="LongOperationRules"/>: это ровно та часть, которую надо уметь проверить тестом, не
/// поднимая ни базы, ни окна. Всё, что знает про SQLite, живёт в Database.AppSessions.cs; всё, что
/// знает про таймеры и выход из программы, — в приложении.</summary>
public static class AppSessions
{
    /// <summary>Как часто программа отмечается живой. Несколько минут, а не секунды: отметка — это
    /// запись в общий конфиг, который ездит по сетевой шаре, и бить по ней чаще, чем ходит сама
    /// синхронизация, бессмысленно — всё равно доедет не раньше.</summary>
    public static readonly TimeSpan HeartbeatInterval = TimeSpan.FromMinutes(3);

    /// <summary>Через сколько МОЛЧАНИЯ сеанс считается протухшим.
    ///
    /// Заметно больше интервала отметки, и это не запас «на всякий случай»: отметка пишется локально,
    /// а до чужой машины едет общим конфигом раз в sync_interval_min — свежесть чужой строки всегда
    /// отстаёт на один обмен. Возьми порог поменьше — и все чужие сеансы висели бы протухшими, то
    /// есть список, ради которого всё затевалось, показывал бы «никого нет».</summary>
    public static readonly TimeSpan StaleAfter = TimeSpan.FromMinutes(30);

    /// <summary>Разбор отметки времени. Формат тот же, в котором их пишет база
    /// (Database.NowIso/NowIsoPrecise) — «yyyy-MM-dd HH:mm:ss[.fffffff]», сортируемый как строка и
    /// разбираемый инвариантной культурой. Пусто/мусор — null, и это честное «не знаем»: такую
    /// строку нельзя считать ни свежей, ни протухшей по ошибке округления.</summary>
    public static DateTime? ParseStamp(string? stamp) =>
        DateTime.TryParse(stamp, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt)
            ? dt
            : null;

    /// <summary>Подаёт ли сеанс признаки жизни прямо сейчас. Пустая отметка — НЕ подаёт: так
    /// выглядит строка, приехавшая со старой версии программы, и считать её живой значило бы
    /// показывать вечный сеанс, который некому завершить.</summary>
    public static bool IsFresh(AppSession session, DateTime now) =>
        ParseStamp(session.HeartbeatAt) is { } beat && now - beat < StaleAfter;

    /// <summary>Состояние строки на данный момент. Порядок проверок важен: сперва «вышел» (это
    /// факт), потом «просили завершить» (это намерение, которое ещё исполняется), и только потом
    /// свежесть отметки.</summary>
    public static AppSessionState StateOf(AppSession session, DateTime now)
    {
        if (!string.IsNullOrEmpty(session.ClosedAt)) return AppSessionState.Closed;
        if (!IsFresh(session, now))
            // Протух и при этом был помечен на завершение — всё равно «завершён»: машину выключили
            // раньше, чем она успела дочитать просьбу, и исход тот же самый.
            return string.IsNullOrEmpty(session.EndedAt) ? AppSessionState.Stale : AppSessionState.Closed;
        return string.IsNullOrEmpty(session.EndedAt) ? AppSessionState.Alive : AppSessionState.Ending;
    }

    public static bool IsLive(AppSession session, DateTime now) =>
        StateOf(session, now) is AppSessionState.Alive or AppSessionState.Ending;

    public static string StateLabel(AppSession session, DateTime now) => StateOf(session, now) switch
    {
        AppSessionState.Alive => "работает",
        AppSessionState.Ending => string.IsNullOrWhiteSpace(session.EndedBy)
            ? "завершается"
            : $"завершается — {session.EndedBy}",
        AppSessionState.Closed => string.IsNullOrEmpty(session.EndedAt)
            ? "закрыт"
            : $"завершён — {(session.EndedBy.Length > 0 ? session.EndedBy : "администратор")}",
        _ => "не отвечает",
    };

    /// <summary>Роль, которой видны ЧУЖИЕ сеансы. Одна строка на всё приложение, чтобы «кто
    /// главный администратор» не разъехалось между списком и кнопкой завершения.</summary>
    public const string AdminRole = "administrator";

    public static bool IsAdmin(string? role) =>
        string.Equals(role, AdminRole, StringComparison.Ordinal);

    /// <summary>Что показывать человеку. Администратор видит всё; остальные — ТОЛЬКО свои сеансы.
    ///
    /// «Свой» — это совпадение логина, а не машины: один и тот же человек за день садится за две
    /// машины, и оба его сеанса — его. Сравнение регистронезависимое, потому что логин в базу
    /// попадает и нормализованным (AppUserAuthService.NormalizeAdLogin), и как есть — именем учётки
    /// Windows (AppServices.CurrentUserName), а «Naladka3» и «naladka3» это один человек.</summary>
    public static List<AppSession> Visible(IEnumerable<AppSession> all, string? role, string ownLogin)
    {
        var rows = all ?? Enumerable.Empty<AppSession>();
        if (!IsAdmin(role))
            rows = rows.Where(s => SameUser(s.UserLogin, ownLogin));
        // Свежие сверху: список читают сверху вниз и ищут в нём того, кто мешает прямо сейчас.
        return rows.OrderByDescending(s => s.HeartbeatAt, StringComparer.Ordinal).ToList();
    }

    public static bool SameUser(string? left, string? right) =>
        !string.IsNullOrWhiteSpace(left) &&
        string.Equals(left.Trim(), (right ?? "").Trim(), StringComparison.OrdinalIgnoreCase);

    /// <summary>Почему этот сеанс завершить нельзя. null — можно, кнопка живая.
    ///
    /// Текст готов к показу: отказ без причины человек читает как «не работает кнопка».</summary>
    public static string? TerminationRefusal(string? role, AppSession session, string ownSessionId, DateTime now)
    {
        if (!IsAdmin(role))
            return "Завершать чужие сеансы может только администратор.";

        if (string.Equals(session.SessionId, ownSessionId, StringComparison.Ordinal))
            return "Это ваш собственный сеанс — закройте программу обычным способом.";

        return StateOf(session, now) switch
        {
            AppSessionState.Closed => "Сеанс уже завершён.",
            // Протухший не завершаем намеренно: программы там давно нет, отметка никому не доедет,
            // а строка навсегда осталась бы «завершается» — то есть список врал бы о сделанном.
            AppSessionState.Stale => "Сеанс и так не отвечает — завершать нечего.",
            AppSessionState.Ending => "Сеанс уже помечен на завершение, идёт выход.",
            _ => null,
        };
    }

    public static bool CanTerminate(string? role, AppSession session, string ownSessionId, DateTime now) =>
        TerminationRefusal(role, session, ownSessionId, now) is null;

    /// <summary>⚠️ ПОЧЕМУ ВЫХОД ОТКЛАДЫВАЕТСЯ. null — выходить можно прямо сейчас.
    ///
    /// Завершение сеанса — это такой же принудительный выход из программы, как автообновление, и
    /// грабли у него ровно те же, на которые уже наступали: «автоматический перезапуск Finder для
    /// обновления происходит при заливке прошивки в контроллер, что может привести к окирпичиванию
    /// контроллера». Поэтому правило здесь не своё, а ТО ЖЕ САМОЕ
    /// (<see cref="LongOperationRules.RestartBlockReason"/>): пока идёт операция, обрыв которой
    /// оставляет контроллер или файл на диске недописанным, сеанс помечен завершающимся, но
    /// программа не выходит — дожидается окончания и выходит следующим тиком.
    ///
    /// Два правила вместо одного здесь были бы худшим вариантом: завести новую долгую операцию и
    /// забыть вписать её во второй список — значит вернуть ровно тот баг.</summary>
    public static string? ShutdownDelayReason(IEnumerable<LongOperation> active) =>
        LongOperationRules.RestartBlockReason(active);

    /// <summary>Что сказать человеку, которому администратор завершил сеанс.</summary>
    public static string TerminationNotice(AppSession session)
    {
        var who = string.IsNullOrWhiteSpace(session.EndedBy) ? "Администратор" : session.EndedBy;
        return $"{who} завершил ваш сеанс работы. Программа закроется — войдите заново.";
    }

    /// <summary>То же, но когда выход отложен долгой операцией: человек должен понимать, что
    /// программа не зависла и что обрывать операцию руками не надо.</summary>
    public static string TerminationDeferredNotice(AppSession session, string reason)
    {
        var who = string.IsNullOrWhiteSpace(session.EndedBy) ? "Администратор" : session.EndedBy;
        return $"{who} завершил ваш сеанс, но программа закроется не сейчас: {reason}. " +
               "Дождитесь окончания — обрывать операцию нельзя.";
    }

    /// <summary>СЛИЯНИЕ ОДНОГО СЕАНСА, ПРИЕХАВШЕГО С ЧУЖОЙ МАШИНЫ, со своей копией той же строки.
    /// <paramref name="mine"/> = null — такой строки у нас ещё нет, берём входящую целиком.
    ///
    /// Поля сливаются ПООТДЕЛЬНОСТИ, а не «побеждает строка посвежее», и это главное решение здесь.
    /// Разберём именно тот случай, ради которого всё и делалось:
    /// <list type="number">
    /// <item>администратор в 10:00 просит завершить сеанс коллеги — у него в строке появляется
    ///   ended_at = 10:00;</item>
    /// <item>коллега про это ещё не знает и продолжает отмечаться живым: в ЕГО копии строки
    ///   heartbeat_at = 10:01, а ended_at пустой;</item>
    /// <item>строки встречаются в общем конфиге.</item>
    /// </list>
    /// Сливай мы «целиком по свежести» — победила бы копия коллеги (его отметка новее), и просьба
    /// завершить бесследно исчезла бы. Поэтому: отметка жизни берётся МАКСИМАЛЬНАЯ, а завершение —
    /// ЛИПКОЕ: раз проставленное, оно не снимается ничем и никогда. Поставленное дважды берётся
    /// РАННЕЕ — это момент решения, а не момент, когда новость доехала.
    ///
    /// Так «завершение» оказывается добавлением данных, а не удалением, и доезжает до чужой машины
    /// обычным обменом конфига — тем самым, который удаления не переносит (см. CLAUDE.md и
    /// Database.ConfigExchange).</summary>
    public static AppSession Merge(AppSession? mine, AppSession incoming)
    {
        if (mine is null) return Copy(incoming);

        return new AppSession
        {
            SessionId = mine.SessionId,
            // Неизменные приметы сеанса: заполняем из входящей, только если своя пуста. Перебивать
            // их незачем — сеанс один, и машину с версией знает лучше всех та копия, что его завела.
            OriginId = Prefer(mine.OriginId, incoming.OriginId),
            MachineName = Prefer(mine.MachineName, incoming.MachineName),
            UserLogin = Prefer(mine.UserLogin, incoming.UserLogin),
            AppVersion = Prefer(mine.AppVersion, incoming.AppVersion),
            Channel = Prefer(mine.Channel, incoming.Channel),
            StartedAt = Earliest(mine.StartedAt, incoming.StartedAt),

            HeartbeatAt = Latest(mine.HeartbeatAt, incoming.HeartbeatAt),

            // Липкие поля — см. разбор выше.
            EndedAt = Earliest(mine.EndedAt, incoming.EndedAt),
            EndedBy = string.IsNullOrEmpty(mine.EndedAt) ? Prefer(incoming.EndedBy, mine.EndedBy)
                    : string.IsNullOrEmpty(incoming.EndedAt) ? mine.EndedBy
                    // Обе машины пометили сеанс — «кто завершил» берём у той, чьё решение раньше:
                    // иначе подпись разъехалась бы с отметкой времени рядом с ней.
                    : string.CompareOrdinal(mine.EndedAt, incoming.EndedAt) <= 0 ? mine.EndedBy : incoming.EndedBy,
            ClosedAt = Earliest(mine.ClosedAt, incoming.ClosedAt),
        };
    }

    private static AppSession Copy(AppSession s) => new()
    {
        SessionId = s.SessionId,
        OriginId = s.OriginId,
        MachineName = s.MachineName,
        UserLogin = s.UserLogin,
        AppVersion = s.AppVersion,
        Channel = s.Channel,
        StartedAt = s.StartedAt,
        HeartbeatAt = s.HeartbeatAt,
        EndedAt = s.EndedAt,
        EndedBy = s.EndedBy,
        ClosedAt = s.ClosedAt,
    };

    private static string Prefer(string? first, string? second) =>
        !string.IsNullOrWhiteSpace(first) ? first : second ?? "";

    /// <summary>Из двух отметок — та, что позже. Пустая проигрывает всегда: «не знаем» не может
    /// быть свежее любого известного времени.</summary>
    private static string Latest(string? left, string? right)
    {
        var a = left ?? ""; var b = right ?? "";
        if (a.Length == 0) return b;
        if (b.Length == 0) return a;
        return string.CompareOrdinal(a, b) >= 0 ? a : b;
    }

    /// <summary>Из двух отметок — та, что раньше. Пустая здесь тоже проигрывает, но по другой
    /// причине: пусто означает «события не было», и оно не должно отменять уже случившееся.</summary>
    private static string Earliest(string? left, string? right)
    {
        var a = left ?? ""; var b = right ?? "";
        if (a.Length == 0) return b;
        if (b.Length == 0) return a;
        return string.CompareOrdinal(a, b) <= 0 ? a : b;
    }

    /// <summary>Сколько держим закрытые сеансы, прежде чем убрать строку из своей базы.
    ///
    /// Уборка МЕСТНАЯ, и строка может вернуться с чужой машины — ровно как у «Подключений»
    /// (Database.ForgetParticipant): удаления общий конфиг не переносит. Поэтому порог большой и
    /// одинаковый у всех: когда срок вышел у всех разом, возвращать строку становится некому.
    /// Меньший порог означал бы бесконечное воскрешение чужих старых сеансов.</summary>
    public static readonly TimeSpan KeepClosedFor = TimeSpan.FromDays(60);
}
