namespace AntarusPoFinder.Core.Domain;

/// <summary>Одно уведомление, каким оно лежит в базе (таблица notifications).
///
/// До этого история уведомлений жила ТОЛЬКО в памяти окна — ObservableCollection в
/// MainWindowViewModel, — и умирала вместе с процессом. Тикет: «Сохранять историю уведомлений».
///
/// Машинная запись, в общий конфиг не уезжает: уведомление — след того, что произошло на ЭТОМ
/// компьютере («структура диска создана», «проверка обновлений не удалась»), и у коллеги оно
/// означало бы неправду.</summary>
public class StoredNotification
{
    /// <summary>rowid. Он же опознавалка при удалении по одному и при схлопывании повтора.</summary>
    public long Id { get; set; }

    public string Text { get; set; } = "";

    /// <summary>Время ПОСЛЕДНЕГО появления: повтор поднимает запись наверх, а не заводит новую.</summary>
    public DateTime When { get; set; }

    public NotificationCategory Category { get; set; }

    /// <summary>Сколько раз пришло ровно это же сообщение — см. Database.SaveNotification.</summary>
    public int Repeats { get; set; } = 1;

    /// <summary>Человек это уведомление уже видел в окне истории. Только прочитанные уходят из
    /// счётчика на колокольчике — ровно то, о чём тикет: «убирать из счётчика количество ТОЛЬКО
    /// прочитанные».</summary>
    public bool IsRead { get; set; }
}
