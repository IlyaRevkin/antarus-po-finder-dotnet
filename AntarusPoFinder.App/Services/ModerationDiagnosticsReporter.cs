using System;
using System.IO;
using System.Text;
using AntarusPoFinder.Core.Services;

namespace AntarusPoFinder.App.Services;

/// <summary>Отправить снимок очереди модерации — сам, без участия человека.
///
/// <b>Зачем.</b> Жалоба: «в модерации прошивок не реальное значение их количества, у кого-то 2–3, у
/// меня 20+». Чинить вслепую я уже пробовал и ошибся: предположил сиротские записи, а тест доказал,
/// что таких быть не может. Данных с чужой машины нет и не будет — садиться за неё и переписывать
/// цифры руками никто не станет. Значит, цифры должна прислать сама программа.
///
/// <b>Почему снимок уходит даже когда всё сходится.</b> Главная догадка теперь другая: у разных
/// людей РАЗНЫЕ НАБОРЫ версий в очереди, то есть отметка «выведена из модерации» не доезжает. Это
/// видно только сравнением двух машин — а значит снимок нужен и с той, где «2–3», и с той, где
/// «20+». Со стороны человека это по-прежнему ноль действий.
///
/// <b>Почему не чаще раза в сутки.</b> Очередь модерации открывают по многу раз на дню; снимок на
/// каждое открытие утопил бы редкий интересный случай в сотне одинаковых. Отметка о последней
/// отправке — строго местная: она про эту машину, а не про контору.</summary>
public static class ModerationDiagnosticsReporter
{
    /// <summary>Ключ отметки «когда отправляли в прошлый раз». Местный — см. SkipSettingsKeys.</summary>
    public const string LastSentKey = "moderation_diag_sent_at";

    /// <summary>Положить снимок в ту же очередь, из которой уходят журналы заливок: отправкой
    /// занимается общий фоновый проход (LoaderDiagnostics.FlushAsync), второго канала заводить
    /// незачем.</summary>
    public static void ReportOnce(AppServices services, int listRows)
    {
        try
        {
            var today = DateTime.Now.ToString("yyyy-MM-dd");
            if (string.Equals(services.Cfg.Get(LastSentKey), today, StringComparison.Ordinal)) return;

            var snapshot = new ModerationDiagnostics.Snapshot
            {
                At = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                Machine = Environment.MachineName,
                User = services.CurrentUserName,
                Role = services.Cfg.CurrentRole(),
                AppVersion = AppUpdateService.CurrentVersionText,
                Badge = services.Db.GetUnreleasedFwVersionsCount(),
                ListRows = listRows,
                Slices = services.Db.ModerationSlices(),
                Versions = services.Db.ModerationVersionNumbers(),
            };

            var dir = LoaderDiagnostics.QueueFolder(ConfigService.AppData);
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, ModerationDiagnostics.FileNameFor(snapshot));
            File.WriteAllText(path, ModerationDiagnostics.Serialize(snapshot), new UTF8Encoding(false));
            LoaderDiagnostics.Trim(dir);

            services.Cfg.Set(LastSentKey, today);
        }
        catch (Exception)
        {
            // Диагностика не имеет права мешать работе со списком — ради него человек сюда и зашёл.
        }
    }
}
