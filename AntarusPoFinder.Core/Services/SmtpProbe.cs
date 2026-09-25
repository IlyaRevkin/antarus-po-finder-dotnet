using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace AntarusPoFinder.Core.Services;

/// <summary>Что ответил почтовый сервер на приветствие: имя, порт, умеет ли STARTTLS и требует ли
/// вход.</summary>
public sealed record SmtpProbeResult(
    string Host, int Port, string Banner, bool StartTls, bool NeedsAuth,
    string CertificateSubject, string? Error)
{
    public bool Ok => Error is null;
}

/// <summary>Опрос почтового сервера — то же самое, что Илья делал руками в PowerShell: открыть
/// соединение, прочитать баннер, сказать EHLO и посмотреть, что сервер умеет.
///
/// Заведено по его просьбе: «добавь для почты кнопку, которая автоматически вытащит сервер SMTP».
/// Смысл не в экономии трёх строк, а в том, что реквизиты почты в конторе никто не помнит наизусть,
/// и выяснять их приходилось консолью — то есть не тому человеку, который настраивает программу.
///
/// Здесь НЕТ отправки письма и НЕТ пароля: опрос отвечает на вопрос «этот адрес вообще почтовый
/// сервер и на каком порту» — и только. Пароль спрашивается отдельно и после, когда уже видно,
/// что сервер отвечает: иначе непонятно, в чём беда — в адресе, в порте или в пароле.</summary>
public static class SmtpProbe
{
    /// <summary>Порты по порядку разумности: 587 (submission, самый частый), 25 (внутренний релей),
    /// 465 (TLS сразу, без STARTTLS). Перебираются, пока какой-нибудь не ответит баннером.</summary>
    public static readonly int[] CommonPorts = [587, 25, 465];

    public static SmtpProbeResult Probe(string host, int port, int timeoutMs = 5000)
    {
        host = (host ?? "").Trim();
        if (host.Length == 0) return Fail(host, port, "Не указан адрес сервера.");

        try
        {
            using var tcp = new TcpClient();
            if (!tcp.ConnectAsync(host, port).Wait(timeoutMs))
                return Fail(host, port, $"Сервер {host} не ответил на порту {port} за {timeoutMs / 1000} с.");

            using var stream = tcp.GetStream();
            stream.ReadTimeout = timeoutMs;
            stream.WriteTimeout = timeoutMs;
            using var reader = new StreamReader(stream, Encoding.ASCII);
            using var writer = new StreamWriter(stream, Encoding.ASCII) { NewLine = "\r\n", AutoFlush = true };

            var banner = reader.ReadLine() ?? "";
            if (!banner.StartsWith("220", StringComparison.Ordinal))
                return Fail(host, port, $"Это не похоже на почтовый сервер — он ответил: {banner}");

            writer.WriteLine("EHLO antarus-po-finder");

            // Ответ на EHLO многострочный: строки «250-ВОЗМОЖНОСТЬ», последняя — «250 ВОЗМОЖНОСТЬ».
            // Читать до первой строки «250 » (с пробелом) обязательно: остановись мы на первой
            // строке вообще — не узнали бы ни про STARTTLS, ни про AUTH.
            var caps = new List<string>();
            string? line;
            while ((line = reader.ReadLine()) is not null)
            {
                caps.Add(line);
                if (line.StartsWith("250 ", StringComparison.Ordinal)) break;
            }

            var startTls = caps.Any(c => c.Contains("STARTTLS", StringComparison.OrdinalIgnoreCase));
            var needsAuth = caps.Any(c => c.Contains("AUTH", StringComparison.OrdinalIgnoreCase));
            var certificate = "";

            if (startTls)
            {
                writer.WriteLine("STARTTLS");
                var tlsReply = reader.ReadLine() ?? "";
                if (tlsReply.StartsWith("220", StringComparison.Ordinal))
                {
                    try
                    {
                        // Сертификат НЕ проверяется: у внутреннего сервера конторы он подписан
                        // собственным центром (в ответе Ильи — «Issuer: CN=CA-ELITA»), и строгая
                        // проверка забраковала бы работающий сервер. Здесь это безопасно: мы только
                        // СМОТРИМ, кому он выписан, и ничего не отправляем.
                        using var ssl = new SslStream(stream, leaveInnerStreamOpen: true, (_, _, _, _) => true);
                        ssl.AuthenticateAsClient(host);
                        if (ssl.RemoteCertificate is { } raw)
                            certificate = new X509Certificate2(raw).Subject;
                    }
                    catch (Exception ex)
                    {
                        // Не смогли поднять TLS — это не повод считать опрос неудачным: адрес и порт
                        // мы уже выяснили, а именно они и нужны.
                        certificate = "TLS не поднялся: " + ex.Message;
                    }
                }
            }

            return new SmtpProbeResult(host, port, banner.Trim(), startTls, needsAuth, certificate, null);
        }
        catch (Exception ex)
        {
            return Fail(host, port, $"Не удалось опросить {host}:{port} — {ex.Message}");
        }
    }

    /// <summary>Опрос по нескольким портам подряд. Возвращает первый ответивший — или последнюю
    /// беду, если не ответил никто.</summary>
    public static SmtpProbeResult ProbeCommonPorts(string host, int timeoutMs = 5000)
    {
        SmtpProbeResult last = Fail(host, 0, "Не указан адрес сервера.");
        foreach (var port in CommonPorts)
        {
            last = Probe(host, port, timeoutMs);
            if (last.Ok) return last;
        }
        return last;
    }

    /// <summary>Откуда взять адрес сервера, когда человек его не знает: домен из почтового адреса
    /// («ivanov@elitacompany.com» → «elitacompany.com») и обычные для почты имена перед ним.
    ///
    /// Догадка, а не знание: список нарочно короткий и начинается с самого адреса домена — угадать
    /// десятью попытками нельзя, а тянуть время перебором человек не станет.</summary>
    public static List<string> GuessHosts(string fromAddress)
    {
        var at = (fromAddress ?? "").LastIndexOf('@');
        if (at < 0 || at == fromAddress!.Length - 1) return [];

        var domain = fromAddress[(at + 1)..].Trim().Trim('>').ToLowerInvariant();
        if (domain.Length == 0) return [];

        return ["mail." + domain, "smtp." + domain, domain, "exchange." + domain];
    }

    private static SmtpProbeResult Fail(string host, int port, string error) =>
        new(host, port, "", false, false, "", error);
}
