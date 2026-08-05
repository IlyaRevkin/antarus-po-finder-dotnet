using System;

namespace AntarusPoFinder.Core.Services;

/// <summary>Реквизиты хранилища на хостинге — куда Finder кладёт копию инструкции, чтобы ссылка под
/// QR-кодом на наклейке открывалась с телефона откуда угодно, а не только из офисной сети (решение
/// Ивана Герасимова от 05.08.2026: «размещаться будет на хостинге, а не на внутреннем ресурсе»).
///
/// Разделение полей не случайное и повторяет то, как выданы реквизиты:
///   • <see cref="Endpoint"/>/<see cref="Bucket"/>/<see cref="Region"/>/<see cref="Prefix"/> — КУДА
///     класть. Одинаковы для всего предприятия, поэтому синхронизируются между машинами;
///   • <see cref="AccessKey"/>/<see cref="SecretKey"/> — ЧЕМ подписывать. Задаются администратором
///     один раз и синхронизируются на все машины, которым положено выкладывать (см.
///     ConfigSyncService.SkipSettingsKeys — ключей там больше нет): иначе выложить инструкцию мог бы
///     только тот, кто вписал ключ у себя. Secret при этом и в БД, и в общем конфиге лежит
///     ЗАШИФРОВАННЫМ (см. ConfigService.S3SecretKey), Access Key ID — не секрет и едет строкой;
///   • <see cref="WebUrl"/> — под каким адресом файл потом виден снаружи. Это ОТДЕЛЬНЫЙ адрес
///     (fs.elitacompany.ru), а не адрес хранилища: класть и читать — разные точки входа, и путать
///     их нельзя, иначе в QR уйдёт ссылка, которую сможет открыть только владелец ключей.
///
/// Ключей на 05.08.2026 ещё нет — Иван обещал прислать файл secrets позже. Поэтому всё устроено
/// так, чтобы незаполненные ключи были ШТАТНЫМ состоянием: <see cref="CanPublish"/> false,
/// выкладка молча не делается, всё остальное работает ровно как раньше. Когда ключи придут, их
/// достаточно вписать в Настройки — переустановка и правка кода не потребуются.</summary>
public sealed record S3Settings(
    string Endpoint,
    string Bucket,
    string Region,
    string Prefix,
    string AccessKey,
    string SecretKey,
    string WebUrl,
    bool Enabled)
{
    /// <summary>Адрес хранилища задан настолько, что запрос вообще есть куда отправить.</summary>
    public bool HasAddress =>
        !string.IsNullOrWhiteSpace(Endpoint) && !string.IsNullOrWhiteSpace(Bucket);

    /// <summary>Ключи выданы. Отделено от <see cref="HasAddress"/> намеренно: до присылки secrets
    /// адрес уже известен и заполнен по умолчанию, а ключей нет — и интерфейс должен показывать
    /// именно это («осталось вписать ключи»), а не «ничего не настроено».</summary>
    public bool HasCredentials =>
        !string.IsNullOrWhiteSpace(AccessKey) && !string.IsNullOrWhiteSpace(SecretKey);

    /// <summary>Можно ли прямо сейчас выкладывать файлы. Выключатель отдельно от ключей, чтобы
    /// выкладку можно было приостановить, не стирая реквизиты.</summary>
    public bool CanPublish => Enabled && HasAddress && HasCredentials;

    /// <summary>Ключ объекта в бакете для пути файла относительно корня диска прошивок. Раскладка в
    /// бакете ПОВТОРЯЕТ раскладку на диске — так же, как её повторяет третий диск (см.
    /// InstructionDiskResolver): ничего не «маппится» таблицей, а значит переименование подтипа или
    /// контроллера на диске автоматически действует и на хостинге. Именно поэтому ссылка под QR
    /// («веб-адрес + путь относительно корня», см. LabelLinkBuilder) и указывает ровно на тот файл,
    /// который сюда положили.</summary>
    public string KeyFor(string relativePath)
    {
        var normalized = (relativePath ?? "").Replace('\\', '/').Trim('/');
        var prefix = (Prefix ?? "").Replace('\\', '/').Trim('/');
        return prefix.Length > 0 ? prefix + "/" + normalized : normalized;
    }
}
