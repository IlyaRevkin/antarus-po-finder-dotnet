using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Threading.Tasks;

namespace AntarusPoFinder.Core.Loader;

/// <summary>Итог проверки связи с ПЛК. <paramref name="Reachable"/>=false — это НЕ повод отменять
/// заливку самой программой: наладчик может знать лучше (адрес временный, ПЛК ещё грузится).</summary>
public sealed record PlcLinkResult(bool Reachable, string Message, long ElapsedMs);

/// <summary>Есть ли связь с контроллером ДО того, как запускать заливку. Смысл ровно один: не ждать
/// минуту ради «CONNECTION_FAILED» из-за невоткнутого шнурка или чужого адреса — это самая частая и
/// самая обидная потеря времени в поле.
///
/// Проверяем TCP-портом SSH (22): именно по SSH Loader и работает с ПЛК после подключения
/// (docs/loader/LOADER_AUTOMATION_ARCHITECTURE.md, PlcConnectionResolver), поэтому открытый порт —
/// куда более точный признак, чем ICMP-ping, который в цеховых сетях режут пачками. Если 22-й
/// закрыт, но ping проходит — сообщаем и это: «адрес отвечает, но SSH закрыт» — другая проблема и
/// другое лечение.</summary>
public static class PlcLinkCheck
{
    public const int SshPort = 22;

    public static async Task<PlcLinkResult> CheckAsync(string? ip, int timeoutMs, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(ip))
            return new PlcLinkResult(false, "Адрес ПЛК не задан.", 0);

        var host = ip.Trim();
        var started = Environment.TickCount64;

        var tcp = await TryTcpAsync(host, SshPort, timeoutMs, ct);
        var elapsed = Environment.TickCount64 - started;
        if (tcp) return new PlcLinkResult(true, $"Связь есть: {host}, порт {SshPort} отвечает.", elapsed);

        var pinged = await TryPingAsync(host, timeoutMs, ct);
        elapsed = Environment.TickCount64 - started;
        return pinged
            ? new PlcLinkResult(false, $"{host} отвечает на ping, но порт {SshPort} (SSH) закрыт — " +
                "ПЛК ещё загружается либо по этому адресу другое устройство.", elapsed)
            : new PlcLinkResult(false, $"{host} не отвечает — проверьте кабель, адрес и выбранный сетевой адаптер.", elapsed);
    }

    private static async Task<bool> TryTcpAsync(string host, int port, int timeoutMs, CancellationToken ct)
    {
        try
        {
            using var client = new TcpClient();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(timeoutMs);
            await client.ConnectAsync(host, port, timeout.Token);
            return client.Connected;
        }
        catch (Exception)
        {
            // Недоступный хост, отказ в соединении, отмена по таймауту — для нас всё это одно и то
            // же «связи нет»; разбирать причины тут нечего, следующим шагом идёт ping.
            return false;
        }
    }

    private static async Task<bool> TryPingAsync(string host, int timeoutMs, CancellationToken ct)
    {
        try
        {
            using var ping = new Ping();
            var reply = await ping.SendPingAsync(host, timeoutMs);
            return reply.Status == IPStatus.Success;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>Сетевые адаптеры машины, годные для подключения к шкафу: включённые, не loopback и
    /// не туннели. Имена — те же, что видит наладчик в «Сетевых подключениях», поэтому по ним же он
    /// и выбирает переходник USB-Ethernet.</summary>
    public static IReadOnlyList<string> Adapters()
    {
        try
        {
            return NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.NetworkInterfaceType != NetworkInterfaceType.Loopback
                            && n.NetworkInterfaceType != NetworkInterfaceType.Tunnel)
                .OrderByDescending(n => n.OperationalStatus == OperationalStatus.Up)
                .ThenBy(n => n.Name, StringComparer.CurrentCultureIgnoreCase)
                .Select(n => n.Name)
                .Distinct(StringComparer.CurrentCultureIgnoreCase)
                .ToList();
        }
        catch (Exception)
        {
            return Array.Empty<string>();
        }
    }

    /// <summary>Адрес, на который сейчас настроен этот адаптер, — подсказка в настройках: увидев
    /// «192.168.1.5», наладчик сразу понимает, тот ли переходник выбран. Пусто — адреса нет.</summary>
    public static string AdapterAddress(string adapterName)
    {
        try
        {
            var nic = NetworkInterface.GetAllNetworkInterfaces()
                .FirstOrDefault(n => string.Equals(n.Name, adapterName, StringComparison.CurrentCultureIgnoreCase));
            var ipv4 = nic?.GetIPProperties().UnicastAddresses
                .FirstOrDefault(a => a.Address.AddressFamily == AddressFamily.InterNetwork);
            return ipv4?.Address.ToString() ?? "";
        }
        catch (Exception)
        {
            return "";
        }
    }
}
