using System.Net.Http;
using System.Net.Sockets;
using System.Threading.Tasks;
using AntarusPoFinder.Core.Services;

namespace AntarusPoFinder.App.Services;

/// <summary>Состояние одной проверки. «Не настроено» — отдельно от «недоступно» намеренно: вход по
/// учётной записи может быть просто не настроен, и красить это в красный — значит приучить смотреть
/// мимо реальных отказов.</summary>
public enum ConnectionState { Checking, Ok, Failed, NotConfigured }

/// <param name="Title">Что проверяли — «Контроллер домена (LDAP)», «Сервер проверки входа», …</param>
/// <param name="Target">Куда ходили: путь, адрес, репозиторий. Пусто, если проверять было нечего.</param>
/// <param name="Details">Человекочитаемый результат/причина — то, что читает наладчик и что уходит
/// в буфер обмена (текст пересылают, а не пересказывают).</param>
public record ConnectionCheckResult(string Title, ConnectionState State, string Target, string Details);

/// <summary>Проверка ЦЕЛИ ВХОДА — домена или веб-сервера, смотря какой способ выбран, — и общие
/// примитивы «сделать с таймаутом» / «отвечает ли хост». Единственный потребитель сегодня —
/// SelfCheckProbe, собирающий снимок машины для «Проверки компьютера».
///
/// Проверка обязана уложиться в таймаут и не бросать: под IPsec туннель поднимается ПОЗЖЕ старта
/// приложения, поэтому «сети нет» — штатное состояние, которое надо показать, а не авария, на
/// которой можно повиснуть.
///
/// Раньше здесь жили ещё и проверки папок, источников обновлений и сборка отчёта — они переехали
/// туда, где стали разбором с причинами: SelfCheckProbe (сбор) и SelfCheckAnalyzer/SelfCheckReport
/// в Core (выводы и текст).</summary>
public static class ConnectionStatusService
{
    /// <summary>Сколько ждём каждый источник. 6 секунд — компромисс: обычная шара/домен в локальной
    /// сети отвечают за десятки миллисекунд, а неотвечающая — не отвечает вовсе, и ждать её дольше
    /// смысла нет (первое обращение к SMB после поднятия IPsec бывает медленным, но не настолько).</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(6);

    /// <summary>Порт LDAP контроллера домена — проверяем именно достижимость порта, а не бинд:
    /// у экрана состояния нет (и не должно быть) ни логина, ни пароля пользователя.</summary>
    private const int LdapPort = 389;

    // ── Вход (контроллер домена / веб-сервер проверки пароля) ────────────────

    /// <summary>Проверяет ровно то, к чему полезет вход при текущем «способе проверки»
    /// (ConfigService.AdAuthMode): ldap — порт контроллера домена, http — адрес веб-сервера,
    /// both — оба, и достаточно любого (ровно так же ведёт себя CombinedAdCredentialValidator:
    /// LDAP, а при недоступном домене — HTTP). Пароль/логин не нужны и не запрашиваются.</summary>
    public static async Task<ConnectionCheckResult> CheckAuthTargetAsync(ConfigService cfg, TimeSpan timeout,
        Func<string, int, bool>? tcpProbe = null, Func<string, string?>? httpProbe = null)
    {
        var mode = cfg.AdAuthMode();
        var domain = cfg.Get("ad_domain");
        var httpUrl = cfg.AdHttpUrl();

        if (mode == "http")
            return await CheckHttpAuthAsync(httpUrl, timeout, httpProbe);

        var ldap = await CheckLdapAsync(domain, timeout, tcpProbe);
        if (mode != "both") return ldap;

        if (ldap.State == ConnectionState.Ok)
            return ldap with { Title = "Вход по AD (домен, запасной путь — веб-сервер)", Details = ldap.Details + "; веб-сервер не проверялся — он нужен только при недоступном домене" };

        var http = await CheckHttpAuthAsync(httpUrl, timeout, httpProbe);
        var state = http.State == ConnectionState.Ok ? ConnectionState.Ok : ConnectionState.Failed;
        return new ConnectionCheckResult(
            "Вход по AD (домен, запасной путь — веб-сервер)",
            state,
            $"{ldap.Target} / {http.Target}",
            $"домен: {ldap.Details}; веб-сервер: {http.Details}");
    }

    private static Task<ConnectionCheckResult> CheckLdapAsync(string domain, TimeSpan timeout, Func<string, int, bool>? tcpProbe)
    {
        const string title = "Контроллер домена (LDAP)";
        if (string.IsNullOrWhiteSpace(domain))
            return Task.FromResult(new ConnectionCheckResult(title, ConnectionState.NotConfigured, "", "домен не задан в настройках"));

        var target = $"{domain.Trim()}:{LdapPort}";
        return RunWithTimeoutAsync(
            () =>
            {
                try
                {
                    var ok = tcpProbe is not null ? tcpProbe(domain.Trim(), LdapPort) : TryTcpConnect(domain.Trim(), LdapPort, timeout);
                    return ok
                        ? new ConnectionCheckResult(title, ConnectionState.Ok, target, "доступен")
                        : new ConnectionCheckResult(title, ConnectionState.Failed, target,
                            "недоступен — нет сети до домена (под IPsec это нормально, пока туннель не поднялся)");
                }
                catch (Exception ex)
                {
                    return new ConnectionCheckResult(title, ConnectionState.Failed, target, $"недоступен: {ex.Message}");
                }
            },
            timeout,
            () => new ConnectionCheckResult(title, ConnectionState.Failed, target, $"не ответил за {timeout.TotalSeconds:0} с"));
    }

    private static Task<ConnectionCheckResult> CheckHttpAuthAsync(string url, TimeSpan timeout, Func<string, string?>? httpProbe)
    {
        const string title = "Сервер проверки входа (HTTP)";
        if (string.IsNullOrWhiteSpace(url))
            return Task.FromResult(new ConnectionCheckResult(title, ConnectionState.NotConfigured, "", "адрес сервера не задан в настройках"));

        var target = url.Trim();
        return RunWithTimeoutAsync(
            () =>
            {
                try
                {
                    var problem = httpProbe is not null ? httpProbe(target) : TryHttpReach(target, timeout);
                    return problem is null
                        ? new ConnectionCheckResult(title, ConnectionState.Ok, target, "доступен")
                        : new ConnectionCheckResult(title, ConnectionState.Failed, target, $"недоступен: {problem}");
                }
                catch (Exception ex)
                {
                    return new ConnectionCheckResult(title, ConnectionState.Failed, target, $"недоступен: {ex.Message}");
                }
            },
            timeout,
            () => new ConnectionCheckResult(title, ConnectionState.Failed, target, $"не ответил за {timeout.TotalSeconds:0} с"));
    }

    // ── Внутреннее ───────────────────────────────────────────────────────────

    /// <summary>Выполняет блокирующую проверку в фоне и возвращает результат таймаута, если она не
    /// успела. Сама «зависшая» операция при этом остаётся висеть в пуле потоков — прервать
    /// системный вызов к неотвечающей шаре нельзя в принципе, — но вызывающий (интерфейс) её больше
    /// не ждёт, а это единственное, что требуется. Internal — тестам нужен прямой доступ, чтобы
    /// проверить именно соблюдение таймаута.</summary>
    internal static async Task<ConnectionCheckResult> RunWithTimeoutAsync(
        Func<ConnectionCheckResult> work, TimeSpan timeout, Func<ConnectionCheckResult> onTimeout)
    {
        var task = Task.Run(work);
        var finished = await Task.WhenAny(task, Task.Delay(timeout));
        if (finished != task) return onTimeout();
        try { return await task; }
        catch (Exception ex) { return onTimeout() with { Details = ex.Message }; }
    }

    /// <summary>Проверка «отвечает ли хост» с жёстким таймаутом.
    ///
    /// ⚠️ Брошенную по таймауту задачу ОБЯЗАТЕЛЬНО надо «наблюсти». Недостижимый или
    /// нерезолвящийся хост (домена нет в DNS — обычное дело на машине вне домена и штатная
    /// ситуация, пока поднимается IPsec-туннель) роняет ConnectAsync уже ПОСЛЕ того, как истёк
    /// таймаут и мы ушли дальше. Если это исключение никто не прочитал, оно прилетает с потока
    /// финализатора в TaskScheduler.UnobservedTaskException, а тот в этом приложении показывает
    /// модальное «Произошла ошибка» и заводит тикет о сбое. То есть экран диагностики падал ровно
    /// в том случае, ради которого он и сделан. Отсюда же и Dispose только после завершения
    /// задачи: закрытый на ходу сокет даёт второе такое же исключение.</summary>
    internal static bool TryTcpConnect(string host, int port, TimeSpan timeout)
    {
        var client = new TcpClient();
        var connect = client.ConnectAsync(host, port);

        bool finished;
        try { finished = connect.Wait(timeout); }
        catch (Exception) { finished = true; } // упала в срок — Wait уже прочитал исключение

        if (!finished)
        {
            _ = connect.ContinueWith(
                t =>
                {
                    _ = t.Exception; // прочитать = «наблюсти»
                    try { client.Dispose(); } catch { /* закрываем как получится */ }
                },
                TaskContinuationOptions.ExecuteSynchronously);
            return false;
        }

        var connected = connect.IsCompletedSuccessfully && client.Connected;
        client.Dispose();
        return connected;
    }

    /// <summary>null — сервер ответил (любым кодом, включая 401: для нас это «жив и требует
    /// авторизации», ровно как и для HttpAdCredentialValidator). Иначе — текст проблемы.</summary>
    private static string? TryHttpReach(string url, TimeSpan timeout)
    {
        try
        {
            using var handler = new HttpClientHandler { AllowAutoRedirect = false };
            using var client = new HttpClient(handler) { Timeout = timeout };
            using var response = client.Send(new HttpRequestMessage(HttpMethod.Head, url), CancellationToken.None);
            return null;
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }
}
