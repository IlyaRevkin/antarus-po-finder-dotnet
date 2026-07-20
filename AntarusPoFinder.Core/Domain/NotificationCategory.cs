using System.Linq;

namespace AntarusPoFinder.Core.Domain;

/// <summary>Coarse taxonomy for AddNotification/ShowStatus calls (see MainWindowViewModel/IAppHost) —
/// lets the user opt individual kinds of chatter in/out from Настройки без отключения уведомлений
/// целиком. Derived by grouping the ~70 real call sites across the app by what they're actually about
/// (see git history for the audit), not by which file happens to raise them.</summary>
public enum NotificationCategory
{
    /// <summary>Everything that doesn't fit a more specific bucket below — roles/passwords/quick
    /// apps/theme, "скопировано в буфер" toasts, etc. Always the implicit default for callers that
    /// don't pass a category, so nothing silently stops being reported just because this feature
    /// was added.</summary>
    General,

    /// <summary>Сетевой диск/конфиг: путь диска, папка осмотра/второй диск, интервалы синхронизации,
    /// ручной и автоматический экспорт/импорт конфига, пересоздание структуры папок на диске.</summary>
    Sync,

    /// <summary>Прошивки и параметры ПЧ/УПП: загрузка, модерация, теги, резервы номеров, откат/
    /// дублирование версий, авто- и ручное обновление локального кэша прошивок.</summary>
    FirmwareAndParams,

    /// <summary>Вкладка «Осмотр»: скан, приём фото по QR, удаление файлов, очистка папки осмотра.</summary>
    Inspection,

    /// <summary>Иерархия оборудования (Настройки → Иерархия): типы/подтипы шкафов, контроллеры и их
    /// модификации, разрешённые расширения файлов.</summary>
    Hierarchy,

    /// <summary>Обновления самого приложения — стартовая и периодическая фоновая проверка новой
    /// версии (см. Раунд с self-update таймером), настройки автообновления.</summary>
    AppUpdates,
}

public static class NotificationCategoryInfo
{
    public static readonly (NotificationCategory Category, string Label)[] All =
    {
        (NotificationCategory.General, "Общие"),
        (NotificationCategory.Sync, "Синхронизация и сетевой диск"),
        (NotificationCategory.FirmwareAndParams, "Прошивки и параметры"),
        (NotificationCategory.Inspection, "Осмотр"),
        (NotificationCategory.Hierarchy, "Иерархия оборудования"),
        (NotificationCategory.AppUpdates, "Обновления приложения"),
    };

    public static string Label(NotificationCategory category) => All.First(x => x.Category == category).Label;
}
