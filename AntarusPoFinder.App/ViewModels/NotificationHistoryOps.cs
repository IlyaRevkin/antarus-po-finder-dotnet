using System.Collections.Generic;

namespace AntarusPoFinder.App.ViewModels;

/// <summary>Схлопывание повторов в истории уведомлений — вынесено из
/// <see cref="MainWindowViewModel"/> отдельно, чтобы правило можно было проверить тестом без
/// поднятия WPF-приложения.</summary>
public static class NotificationHistoryOps
{
    /// <summary>Если такое же сообщение в истории уже есть — поднимает его наверх, обновляет время
    /// и увеличивает счётчик повторов; возвращает true, и новую строку заводить не надо.
    ///
    /// Прежнее правило сравнивало только САМУЮ ВЕРХНЮЮ запись. Для подряд идущих повторов
    /// (оператор трижды нажал одну кнопку) этого хватало, но не для залипшей фоновой ошибки: между
    /// её повторениями успевают лечь другие сообщения синхронизации, она каждый раз оказывается не
    /// на вершине и заводит новую строку. Так одна ошибка, срабатывающая на каждом тике приёма
    /// конфига, за рабочий день дала «под 500 уведомлений» и вытеснила из истории всё остальное.</summary>
    public static bool CollapseRepeat(IList<NotificationEntry> history, string text, Action? reopen, DateTime now)
    {
        for (var i = 0; i < history.Count; i++)
        {
            if (history[i].Text != text) continue;

            var bumped = history[i] with
            {
                When = now,
                Reopen = reopen ?? history[i].Reopen,
                Repeats = history[i].Repeats + 1,
                // Случилось снова — значит это снова новость, и в «прочитанных» запись залипнуть
                // не должна (см. NotificationEntry.IsRead).
                IsRead = false,
            };
            history.RemoveAt(i);
            history.Insert(0, bumped);
            return true;
        }
        return false;
    }
}
