using System.Net;
using System.Net.Mail;
using AntarusPoFinder.Core.Domain;

namespace AntarusPoFinder.Core.Services;

/// <summary>Настройки почтового сервера, через который уходят дубли уведомлений.</summary>
public sealed record SmtpSettings(
    string Host, int Port, bool UseSsl, string User, string Password, string From, bool Enabled)
{
    /// <summary>Хватает ли настроек, чтобы вообще пытаться отправить. Пустой хост — штатное
    /// состояние (почта просто не настроена), а не ошибка: ровно так же, как пустые ключи хостинга
    /// молча выключают выкладку, а не сыплют предупреждениями при каждом действии.</summary>
    public bool CanSend => Enabled && Host.Trim().Length > 0 && From.Trim().Length > 0;
}

/// <summary>Дублирование уведомлений на почту. Помечено бетой намеренно — владелец так и просил:
/// «не только задел, если получится уже рабочее сделать можно сделать, но как бету».
///
/// Что здесь СОЗНАТЕЛЬНО не сделано: очереди с повторной отправкой нет. Не доехало письмо — об этом
/// говорится сразу и один раз, а событие остаётся в обычных уведомлениях внутри программы, где оно
/// и было. Очередь, копящая недоставленное, — это отдельное хозяйство со своими бедами (разросшийся
/// хвост, письма недельной давности, приезжающие пачкой после починки сервера), и заводить его
/// вслепую, не увидев, как почта поведёт себя в конторе, значит чинить то, что ещё не ломалось.
///
/// Почта — ДУБЛЬ, а не замена: уведомление всегда остаётся в программе. Это главное правило здесь,
/// из-за него и молчаливый отказ при ненастроенном сервере безопасен.</summary>
public static class EmailNotifier
{
    /// <summary>Отправляет одно письмо. Возвращает пустую строку при успехе, иначе — человеческое
    /// описание беды (для строки состояния и для кнопки «Проверить отправку»).</summary>
    public static string Send(SmtpSettings s, IReadOnlyList<string> to, string subject, string body)
    {
        if (!s.CanSend) return "Почта не настроена.";
        if (to.Count == 0) return "";

        try
        {
            using var client = new SmtpClient(s.Host.Trim(), s.Port) { EnableSsl = s.UseSsl };
            // Пустой логин — сервер без авторизации (внутренний релей конторы). Подставлять сюда
            // пустые учётные данные нельзя: часть серверов на этом отказывает вместо анонимной
            // отправки, которая на самом деле разрешена.
            if (s.User.Trim().Length > 0)
                client.Credentials = new NetworkCredential(s.User.Trim(), s.Password);

            using var msg = new MailMessage { From = new MailAddress(s.From.Trim()), Subject = subject, Body = body };
            foreach (var addr in to)
            {
                // Группа AD адресом письма быть не может — её раскрывает почтовый сервер, если у
                // группы есть свой адрес. Здесь просто пропускаем всё, что не похоже на ящик:
                // падать из-за строки «Программисты» значило бы не отправить письмо и остальным.
                if (EmailRouting.LooksLikeMailbox(addr)) msg.To.Add(addr);
            }
            if (msg.To.Count == 0) return "Среди получателей нет ни одного почтового адреса.";

            client.Send(msg);
            return "";
        }
        catch (SmtpException ex) { return "Почтовый сервер отказал: " + ex.Message; }
        catch (Exception ex) { return "Не удалось отправить письмо: " + ex.Message; }
    }
}
