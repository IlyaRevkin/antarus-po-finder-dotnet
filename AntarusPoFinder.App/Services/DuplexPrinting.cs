using System.Collections.Concurrent;
using System.IO;
using System.Printing;
using System.Text;
using System.Threading.Tasks;
using AntarusPoFinder.Core.Services;

namespace AntarusPoFinder.App.Services;

/// <param name="PrinterName">Куда ушло задание; null — принтер определить не удалось.</param>
/// <param name="DuplexApplied">Удалось ли проставить нужный режим печати (буклет для паспорта либо
/// обычная двусторонняя для инструкции). false — печать всё равно состоялась, но настройками принтера
/// как есть.</param>
public readonly record struct DuplexPrintOutcome(string? PrinterName, bool DuplexApplied);

/// <summary>Печать документа в одном из двух режимов (см. <see cref="PrintTicketXml"/>): паспорт —
/// буклетом (две страницы на лист, переворот по короткому краю), инструкция — обычным листом с
/// двусторонней печатью (переворот по длинному краю).
///
/// Требование простое: паспорт печатается буклетом, с разворотом относительно КОРОТКОГО края, а
/// инструкция — обычным листом с двусторонней печатью. Отправка файла ассоциацией
/// («напечатать этот PDF») никаких настроек не несёт — печатается тем, что стоит у принтера сейчас, и
/// каждый раз приходилось лезть в настройки печати руками.
///
/// Как это делается: PDF мы не рисуем сами (его печатает Word/просмотрщик через ассоциацию), поэтому
/// задать параметры «своему» заданию напрямую нельзя — их неоткуда взять. Зато можно выставить их у
/// САМОЙ ОЧЕРЕДИ печати (PrintTicket пользователя): программа, которую запустит ассоциация, стартует
/// уже с ними. Правка тикета — в <see cref="PrintTicketXml"/> (там же разобрано, почему XML).
///
/// Прежний тикет обязательно возвращается на место: чужие задания печатать двусторонними мы не
/// подписывались. Возврат отложенный — не раньше, чем задание ушло из очереди: печать поднимает
/// стороннюю программу, и вернув настройки сразу, мы вернули бы их ДО того, как она их прочитала.
/// Программу закрыли раньше, чем задание допечаталось, — возврат делается на выходе из процесса
/// (см. <see cref="Pending"/>).</summary>
public static class DuplexPrinting
{
    /// <summary>Очереди, у которых сейчас стоит наш тикет, и что у них было до этого. Нужен на
    /// случай, когда программу закрыли, пока задание ещё печаталось.</summary>
    private static readonly ConcurrentDictionary<string, string> Pending = new();

    private static int _exitHookInstalled;

    /// <summary>Сколько ждём появления задания в очереди. Ассоциация успевает поднять Word или
    /// просмотрщик PDF далеко не мгновенно, особенно первым запуском.</summary>
    private static readonly TimeSpan AppearTimeout = TimeSpan.FromSeconds(30);

    /// <summary>Сколько ждём, пока очередь опустеет, прежде чем вернуть настройки в любом случае.</summary>
    private static readonly TimeSpan DrainTimeout = TimeSpan.FromMinutes(3);

    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1);

    /// <summary>ПАСПОРТ: печать буклетом (две страницы на лист, переворот по короткому краю). Печать
    /// состоится в любом случае: не вышло с настройками — уйдёт как есть, вызывающий скажет человеку.</summary>
    public static DuplexPrintOutcome PrintPassportBooklet(string path) =>
        PrintWith(path, PrintTicketXml.ApplyPassportBooklet, IsPassportBooklet);

    /// <summary>ИНСТРУКЦИЯ: обычный лист с двусторонней печатью (переворот по длинному краю, одна
    /// страница на лист). Как и у паспорта, печать идёт в любом случае.</summary>
    public static DuplexPrintOutcome PrintInstructionDuplex(string path) =>
        PrintWith(path, PrintTicketXml.ApplyInstructionDuplex, IsInstructionDuplex);

    /// <summary>Буклет паспорта уже выставлен: короткий край И две страницы на лист.</summary>
    private static bool IsPassportBooklet(string? ticket) =>
        PrintTicketXml.DuplexOption(ticket) == PrintTicketXml.TwoSidedShortEdge
        && PrintTicketXml.PagesPerSheet(ticket) == 2;

    /// <summary>Режим инструкции уже выставлен: длинный край И одна страница на лист.</summary>
    private static bool IsInstructionDuplex(string? ticket) =>
        PrintTicketXml.DuplexOption(ticket) == PrintTicketXml.TwoSidedLongEdge
        && PrintTicketXml.PagesPerSheet(ticket) == 1;

    private static DuplexPrintOutcome PrintWith(string path, Func<string?, string> transform,
        Func<string?, bool> alreadyApplied)
    {
        var outcome = Apply(transform, alreadyApplied);
        PrintableDocActions.Print(path);
        if (outcome.DuplexApplied && outcome.PrinterName is { } queue) RestoreWhenDone(queue);
        return outcome;
    }

    private static DuplexPrintOutcome Apply(Func<string?, string> transform, Func<string?, bool> alreadyApplied)
    {
        try
        {
            using var server = new LocalPrintServer();
            using var queue = server.DefaultPrintQueue;
            if (queue is null) return new DuplexPrintOutcome(null, false);

            var current = ReadTicket(queue);
            // Уже стоит нужный режим (человек выставил сам или прошлое задание не успело вернуть) —
            // ничего не меняем и, главное, не запоминаем «прежнее» состояние: иначе возврат записал бы
            // наш же тикет как чужой и после следующей печати всё осталось бы в этом режиме навсегда.
            if (alreadyApplied(current))
                return new DuplexPrintOutcome(queue.FullName, true);

            if (!WriteTicket(queue, transform(current)))
                return new DuplexPrintOutcome(queue.FullName, false);

            if (current is not null) Pending[queue.FullName] = current;
            InstallExitHook();
            return new DuplexPrintOutcome(queue.FullName, true);
        }
        catch (Exception)
        {
            // Нет принтеров вовсе, спулер остановлен, нет прав на очередь — печать это не отменяет.
            return new DuplexPrintOutcome(null, false);
        }
    }

    // ── Возврат настроек ─────────────────────────────────────────────────────────────────────

    private static void RestoreWhenDone(string queueName)
    {
        if (!Pending.ContainsKey(queueName)) return;
        _ = Task.Run(async () =>
        {
            try
            {
                if (await WaitForJobsAsync(queueName, want => want > 0, AppearTimeout))
                    await WaitForJobsAsync(queueName, want => want == 0, DrainTimeout);
                Restore(queueName);
            }
            catch (Exception)
            {
                // Очередь исчезла/переименовалась — возвращать нечего и некуда.
                Pending.TryRemove(queueName, out _);
            }
        });
    }

    private static async Task<bool> WaitForJobsAsync(string queueName, Func<int, bool> until, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (JobCount(queueName) is { } jobs && until(jobs)) return true;
            await Task.Delay(PollInterval).ConfigureAwait(false);
        }
        return false;
    }

    private static int? JobCount(string queueName)
    {
        try
        {
            using var server = new LocalPrintServer();
            using var queue = server.GetPrintQueue(queueName);
            queue.Refresh();
            return queue.NumberOfJobs;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static void Restore(string queueName)
    {
        if (!Pending.TryRemove(queueName, out var ticket)) return;
        try
        {
            using var server = new LocalPrintServer();
            using var queue = server.GetPrintQueue(queueName);
            WriteTicket(queue, ticket);
        }
        catch (Exception)
        {
            // Уже не наша забота: настройки останутся нашими до следующей ручной правки принтера.
        }
    }

    /// <summary>Программу закрыли, пока задание ещё печаталось, — вернуть настройки принтера
    /// напоследок. Иначе человек, ничего не подозревая, продолжил бы печатать всё двусторонним.</summary>
    private static void InstallExitHook()
    {
        if (Interlocked.Exchange(ref _exitHookInstalled, 1) != 0) return;
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            foreach (var queueName in Pending.Keys) Restore(queueName);
        };
    }

    // ── PrintTicket ↔ XML ────────────────────────────────────────────────────────────────────

    private static string? ReadTicket(PrintQueue queue)
    {
        try
        {
            // Refresh обязателен: очередь отдаёт пользовательский тикет из снимка, сделанного при её
            // получении, и до перечитывания на его месте лежит ЗАВОДСКОЙ (подробно — в
            // PrinterPageProbe). Здесь это опаснее, чем в подстановке полей: прочитанный тикет мы
            // потом возвращаем на место как «настройки человека», то есть заводскими затирали бы то,
            // что он выставил себе сам.
            queue.Refresh();
            var ticket = queue.UserPrintTicket ?? queue.DefaultPrintTicket;
            if (ticket is null) return null;
            using var stream = ticket.GetXmlStream();
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static bool WriteTicket(PrintQueue queue, string xml)
    {
        try
        {
            using var stream = new MemoryStream(Encoding.UTF8.GetBytes(xml));
            queue.UserPrintTicket = new PrintTicket(stream);
            queue.Commit();
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }
}
