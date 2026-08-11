using AntarusPoFinder.App.ViewModels;
using AntarusPoFinder.Core.Domain;

namespace AntarusPoFinder.App;

/// <summary>WPF equivalent of the Python pages' <c>self._mw</c> back-reference — cross-cutting
/// operations a page needs to trigger on the main window/shell without owning the shell itself.</summary>
public interface IAppHost
{
    /// <summary>category defaults to General so existing call sites keep working unchanged — pass
    /// an explicit category where it's known (see NotificationCategory) so the user's Настройки →
    /// Уведомления filter can actually apply to it. If the category is disabled, the message is
    /// fully suppressed: no status-bar flash, no history entry (see MainWindowViewModel.ShowStatus).</summary>
    /// <summary>reopen (необязателен) — действие кнопки «Показать» у записи в истории уведомлений:
    /// напр. перейти на страницу «Тикеты». Без него запись без кнопки, как и раньше.</summary>
    void ShowStatus(string message, int ms = 4000, NotificationCategory category = NotificationCategory.General, Action? reopen = null);

    /// <summary>Открывает индикатор фоновой работы внизу окна (рядом с «Диск: …») до Dispose.
    /// Обязателен для всего, что ходит на сетевой диск дольше мгновения: страница сама может
    /// оставаться отзывчивой, но пользователю нужно видеть, что программа занята, а не «висит» —
    /// см. BusyTracker и MainWindowViewModel.RunSyncAsync.</summary>
    IBusyScope BeginBusy(string text);

    /// <summary>То же для СИНХРОНИЗАЦИИ (конфиг, тикеты, приём прошивок с диска, отправка на диск):
    /// крутит пилюлю синхры в статус-строке и НЕ занимает индикатор фоновой работы выше. Разделение
    /// сделано по жалобе «отделить анимацию синхронизации конфига от анимации поиска»: раньше обе
    /// работы делили одну полоску, и во время поиска снизу мог висеть текст про синхру.
    /// stage — что именно синхронизируется, попадает в подсказку пилюли.</summary>
    IDisposable BeginSync(string stage);

    /// <summary>Итог синхронизации: isError=true оставляет пилюлю в состоянии «ошибка» с этим текстом
    /// в подсказке, успех её гасит. Для фоновых тиков это делает сама оболочка; страницам нужно для
    /// ручной синхронизации, иначе пилюля показывала бы «всё хорошо» после неудачной кнопки.</summary>
    void NoteSyncResult(string? message, bool isError);

    void ReloadSidebarApps();
    void SwitchRole(string role);
    void Navigate(string pageId);

    /// <summary>Restarts the administrator auto-push timer against the current
    /// config_push_interval_min setting — called from NetworkSyncView right after it's saved, so the
    /// change takes effect immediately instead of waiting for the next role switch.</summary>
    void RefreshConfigSync();

    /// <summary>Called right after the root disk path is changed (NetworkSyncView.SaveRoot_Click):
    /// creates the folder structure on the new path if it's missing AND refreshes the footer disk
    /// indicator immediately. Without this the "Диск: …" footer only recomputed on the periodic
    /// RunSync tick (every sync_interval_min, or never when that's 0), so saving a valid path left
    /// the footer contradicting the "Путь сохранён" toast for minutes, and a fresh machine's disk
    /// stayed empty until the first upload happened to create the tree.</summary>
    void OnRootPathChanged();

    /// <summary>Recomputes the footer disk indicator now — call after writing to the disk (e.g. a
    /// successful firmware upload) so the availability/file count isn't stale until the next
    /// periodic RunSync tick.</summary>
    void RefreshDiskStatus();

    /// <summary>Отправляет общий справочник (иерархия, производители ПЧ/УПП, теги, расширения) на
    /// сетевой диск сразу после того, как администратор его изменил — см.
    /// MainWindowViewModel.PushCatalogChange. what — что именно поменялось, попадает в статус-строку.
    /// subjectKey — к какому объекту относится правка (для правок прошивки — её FwVersionId в виде
    /// строки), чтобы карточка выдачи точечно показала «правки этой прошивки ещё не на диске»; пусто —
    /// правка без привязки к конкретной прошивке (тип/подтип/контроллер и т.п.).</summary>
    void PushCatalogChange(string what, string subjectKey = "");

    /// <summary>Пометить показанную выдачу поиска устаревшей — вызывается после локальных изменений
    /// данных прошивок (загрузка, откат), чтобы при следующем заходе на «Поиск» выдача обновилась.
    /// Кэш поиска специально НЕ обновляется на каждом переключении вкладок (см. SearchView), поэтому
    /// такие правки нужно объявлять явно, иначе оператор увидит выдачу без только что загруженного.</summary>
    void InvalidateSearchResults();

    /// <summary>Страницу «Тикеты» открыли и показали (в т.ч. после фонового приёма новых событий в
    /// TicketsView.Activate) — сдвинуть watermark «до какого тикета уже видели» и погасить бейдж на
    /// пункте меню. Вызывается самим TicketsView, чтобы пометка «просмотрено» ставилась ПОСЛЕ приёма
    /// новых тикетов с диска, а не до него.</summary>
    void OnTicketsViewed();
}
