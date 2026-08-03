using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AntarusPoFinder.Core.Services;

namespace AntarusPoFinder.App.Services;

/// <summary>Состояние одной проверки. «Не настроено» — отдельно от «недоступно» намеренно: второй
/// диск или папка обновлений могут быть просто не заданы, и красить это в красный — значит приучить
/// смотреть мимо реальных отказов.</summary>
public enum ConnectionState { Checking, Ok, Failed, NotConfigured }

/// <param name="Title">Что проверяли — «Корень сетевого диска», «Контроллер домена», …</param>
/// <param name="Target">Куда ходили: путь, адрес, репозиторий. Пусто, если проверять было нечего.</param>
/// <param name="Details">Человекочитаемый результат/причина — то, что читает наладчик и что уходит
/// в буфер обмена (текст пересылают, а не пересказывают).</param>
public record ConnectionCheckResult(string Title, ConnectionState State, string Target, string Details);

/// <summary>Проверки для экрана «Состояние подключения» (см. ConnectionStatusDialog). Каждая
/// обязана уложиться в таймаут и не бросать: на отвалившейся SMB-шаре даже Directory.Exists отвечает
/// секундами, а под IPsec туннель поднимается ПОЗЖЕ старта приложения, поэтому «сети нет» — это
/// штатное состояние, которое надо показать, а не авария, на которой можно повиснуть.
///
/// Живёт в App (не в Core), потому что опирается на AppUpdateService — App-овый. Ничего WPF-ного
/// здесь нет специально: логика проверок покрыта тестами без окна.</summary>
public static class ConnectionStatusService
{
    /// <summary>Сколько ждём каждый источник. 6 секунд — компромисс: обычная шара/домен в локальной
    /// сети отвечают за десятки миллисекунд, а неотвечающая — не отвечает вовсе, и ждать её дольше
    /// смысла нет (первое обращение к SMB после поднятия IPsec бывает медленным, но не настолько).</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(6);

    /// <summary>Порт LDAP контроллера домена — проверяем именно достижимость порта, а не бинд:
    /// у экрана состояния нет (и не должно быть) ни логина, ни пароля пользователя.</summary>
    private const int LdapPort = 389;

    // ── Папки (сетевой диск, второй диск) ────────────────────────────────────

    /// <summary><paramref name="probe"/> — шов для тестов: подменяет реальный поход на диск. В бою
    /// всегда null (используется Directory.Exists + чтение содержимого).</summary>
    public static Task<ConnectionCheckResult> CheckFolderAsync(string title, string? path, TimeSpan timeout, Func<string, bool>? probe = null)
    {
        if (string.IsNullOrWhiteSpace(path))
            return Task.FromResult(new ConnectionCheckResult(title, ConnectionState.NotConfigured, "", "путь не задан в настройках"));

        var target = path.Trim();
        return RunWithTimeoutAsync(
            () =>
            {
                try
                {
                    var exists = probe is not null ? probe(target) : Directory.Exists(target);
                    return exists
                        ? new ConnectionCheckResult(title, ConnectionState.Ok, target, "доступен")
                        : new ConnectionCheckResult(title, ConnectionState.Failed, target,
                            "недоступен: сетевой диск не подключён, путь не существует или нет прав");
                }
                catch (Exception ex)
                {
                    return new ConnectionCheckResult(title, ConnectionState.Failed, target, $"недоступен: {ex.Message}");
                }
            },
            timeout,
            () => new ConnectionCheckResult(title, ConnectionState.Failed, target,
                $"не ответил за {timeout.TotalSeconds:0} с — диск/сеть не отвечают"));
    }

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

    // ── Обновления ───────────────────────────────────────────────────────────

    /// <summary>Одна строка на оба источника сразу: папка и GitHub, с найденными версиями. Красная,
    /// только если недоступны ОБА — то есть новые версии на эту машину не придут никак (это и есть
    /// «молчаливая» поломка, ради видимости которой экран затевался). Если папка отвалилась, но
    /// GitHub жив (или наоборот) — это предупреждение в тексте, но не отказ.</summary>
    public static async Task<ConnectionCheckResult> CheckUpdateSourcesAsync(string? folderPath, TimeSpan timeout)
    {
        const string title = "Источник обновлений";
        var report = await AppUpdateService.ProbeSourcesAsync(folderPath, timeout);
        var details = AppUpdateService.DescribeSources(report).Replace("\n", "; ");
        var target = report.Folder.Configured ? report.Folder.Location : "GitHub";
        return new ConnectionCheckResult(title,
            report.EffectiveSource is null ? ConnectionState.Failed : ConnectionState.Ok,
            target, details);
    }

    /// <summary>Все проверки разом — то, что дёргает окно и кнопка «Проверить снова». Пункты
    /// запускаются параллельно: последовательный запуск на трёх неотвечающих целях складывал бы три
    /// таймаута подряд.</summary>
    public static async Task<IReadOnlyList<ConnectionCheckResult>> CheckAllAsync(ConfigService cfg, TimeSpan timeout)
    {
        var root = CheckFolderAsync("Корень сетевого диска", cfg.RootPath(), timeout);
        var second = CheckFolderAsync("Второй диск", cfg.SecondDiskPath(), timeout);
        var auth = CheckAuthTargetAsync(cfg, timeout);
        var updates = CheckUpdateSourcesAsync(cfg.EffectiveAppUpdatePath(), timeout);

        await Task.WhenAll(root, second, auth, updates);
        return new[] { root.Result, second.Result, auth.Result, updates.Result };
    }

    // ── Отчёт ────────────────────────────────────────────────────────────────

    /// <summary>Текст для буфера обмена: то, что Илья пересылает как есть. Дата и версия — сверху,
    /// иначе через день непонятно, к какому моменту относится присланный кусок.</summary>
    public static string BuildReport(IEnumerable<ConnectionCheckResult> results)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Состояние подключения — {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"Antarus ПО Finder {AppUpdateService.CurrentVersionText}, компьютер {Environment.MachineName}, пользователь Windows {Environment.UserName}");
        sb.AppendLine();
        foreach (var r in results)
        {
            sb.AppendLine($"{StateLabel(r.State)} {r.Title}");
            if (!string.IsNullOrEmpty(r.Target)) sb.AppendLine($"    адрес: {r.Target}");
            sb.AppendLine($"    {r.Details}");
        }
        return sb.ToString().TrimEnd();
    }

    public static string StateLabel(ConnectionState state) => state switch
    {
        ConnectionState.Ok => "[ ОК ]",
        ConnectionState.Failed => "[ НЕТ ]",
        ConnectionState.NotConfigured => "[ — ]",
        _ => "[ ... ]",
    };

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
