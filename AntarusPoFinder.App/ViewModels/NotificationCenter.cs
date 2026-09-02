using System.Collections.ObjectModel;
using System.Linq;
using AntarusPoFinder.Core.Data;
using AntarusPoFinder.Core.Domain;
using AntarusPoFinder.Core.Services;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AntarusPoFinder.App.ViewModels;

/// <summary>История уведомлений и счётчик на колокольчике — одним местом, поверх таблицы
/// notifications.
///
/// Появился по тикету kiselyov.a («практически всегда уведомления остаются пустыми; счётчик
/// убавлять ТОЛЬКО на прочитанных; историю сохранять; удалять поштучно»). До него всё это было
/// размазано: список — ObservableCollection прямо в MainWindowViewModel (в памяти, до перезапуска),
/// счётчик — отдельное число, которое обнулялось по факту ОТКРЫТИЯ окна, а признак «прочитано»
/// ставился всему списку разом при его закрытии.
///
/// Правила, ради которых это один класс:
/// <list type="bullet">
/// <item>база — источник правды, коллекция её зеркалит; после перезапуска история на месте;</item>
/// <item>счётчик СЧИТАЕТСЯ по коллекции, а не накапливается отдельным числом — рассинхронизироваться
/// с содержимым он теперь не может в принципе;</item>
/// <item>в счётчик идут только непрочитанные и только тех категорий, которым это разрешено
/// (ConfigService.IsNotificationCategoryCountedUnread). Категорию переключили — счётчик
/// пересчитается, для этого есть <see cref="Refresh"/>.</item>
/// </list>
///
/// Потокобезопасности здесь нет и не задумано: зовётся только из потока интерфейса, как и прежний
/// код в MainWindowViewModel.</summary>
public partial class NotificationCenter : ObservableObject
{
    /// <summary>Сколько уведомлений держим. Было 100 на память одного запуска; теперь история живёт
    /// между запусками, и на этом же числе она перестала бы доживать до конца недели. Верхнюю
    /// границу всё равно оставляем: список, который нельзя прокрутить до конца, — не история.</summary>
    public const int HistoryLimit = 200;

    private readonly Database _db;
    private readonly ConfigService _cfg;

    public NotificationCenter(Database db, ConfigService cfg)
    {
        _db = db;
        _cfg = cfg;
        foreach (var stored in _db.GetNotifications(HistoryLimit))
            History.Add(new NotificationEntry(stored));
    }

    /// <summary>Вся история, свежие сверху. Прочитанные из неё НЕ вычищаются — см.
    /// NotificationEntry.IsRead: именно скрытие прочитанных и делало окно пустым.</summary>
    public ObservableCollection<NotificationEntry> History { get; } = new();

    /// <summary>Число на колокольчике. Считается, а не хранится.</summary>
    public int UnreadCount => History.Count(e => !e.IsRead && _cfg.IsNotificationCategoryCountedUnread(e.Category));

    /// <summary>Пересчитать счётчик — после того как снаружи поменяли настройку категорий.</summary>
    public void Refresh() => OnPropertyChanged(nameof(UnreadCount));

    /// <summary>Записать уведомление. Повтор того же текста поднимает существующую строку наверх и
    /// увеличивает её счётчик повторов (правило живёт в Database.SaveNotification — там же, где сама
    /// история, иначе после перезапуска повтор заводил бы вторую строку).</summary>
    public NotificationEntry Add(string text, NotificationCategory category, Action? reopen = null, bool reopenIsModal = false)
    {
        var saved = _db.SaveNotification(text, category, DateTime.Now, HistoryLimit);
        var existing = History.FirstOrDefault(e => e.Id == saved.Id);

        NotificationEntry entry;
        if (existing is not null)
        {
            existing.When = saved.When;
            existing.Repeats = saved.Repeats;
            existing.IsRead = false;
            existing.IsNew = true;
            // Действие «Показать» обновляем только если пришло новое: у повтора баннера оно живое, а
            // у обычного ShowStatus его нет вовсе — и затирать им прежнее нельзя.
            if (reopen is not null)
            {
                existing.Reopen = reopen;
                existing.ReopenIsModal = reopenIsModal;
            }
            var at = History.IndexOf(existing);
            if (at > 0) History.Move(at, 0);
            entry = existing;
        }
        else
        {
            entry = new NotificationEntry(saved, reopen, reopenIsModal);
            History.Insert(0, entry);
            while (History.Count > HistoryLimit)
                History.RemoveAt(History.Count - 1);
        }

        Refresh();
        return entry;
    }

    /// <summary>Пометить одно уведомление прочитанным — зовётся, когда строка ДЕЙСТВИТЕЛЬНО показана
    /// человеку в окне истории (см. NotificationHistoryWindow.NotificationRow_Loaded), а не по факту
    /// открытия окна. Ровно этого требует тикет: «убирать из счётчика количество ТОЛЬКО
    /// прочитанные».</summary>
    public void MarkRead(NotificationEntry entry)
    {
        if (entry.IsRead) return;
        entry.IsRead = true;
        _db.MarkNotificationRead(entry.Id);
        Refresh();
    }

    /// <summary>«Отметить все прочитанными» — явная кнопка. Именно кнопка, а не побочный эффект
    /// открытия окна: разгрести счётчик одним движением надо уметь, но это решение человека.</summary>
    public void MarkAllRead()
    {
        var changed = false;
        foreach (var entry in History)
        {
            if (!entry.IsRead)
            {
                entry.IsRead = true;
                changed = true;
            }
            // Пометку «новое» снимаем тоже: человек нажал «всё прочитано» — значит и подсветке
            // новизны взяться неоткуда (см. NotificationEntry.IsNew).
            entry.IsNew = false;
        }
        if (!changed) return;
        _db.MarkAllNotificationsRead();
        Refresh();
    }

    /// <summary>Удалить одно уведомление.</summary>
    public void Delete(NotificationEntry entry)
    {
        History.Remove(entry);
        _db.DeleteNotification(entry.Id);
        Refresh();
    }

    public void Clear()
    {
        History.Clear();
        _db.ClearNotifications();
        Refresh();
    }
}
