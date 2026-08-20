using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;

namespace AntarusPoFinder.App.Services;

/// <summary>Единственное место, через которое приложение пишет в буфер обмена.
///
/// Буфер обмена в Windows — общий ресурс на всю систему, и открыт он может быть только у одного
/// процесса за раз. Менеджеры буфера, Word, браузер, удалённый сеанс — все они держат его на доли
/// секунды, и попавший в этот промежуток Clipboard.SetText бросает COMException
/// (CLIPBRD_E_CANT_OPEN, 0x800401D0). Из тикета: оператор нажал «копировать имя» на карточке
/// поиска и получил отчёт о сбое вместо скопированного номера версии.
///
/// Поэтому здесь две вещи, которых не было ни в одной из прежних копий этого кода. Первая —
/// попытка не одна: занятость почти всегда мгновенная, и второй заход через полста миллисекунд
/// проходит. Вторая — отказ возвращается значением, а не исключением: вызывающий сам решает,
/// сказать ли «скопировано» или «буфер занят», и ни один из них не имеет права уронить программу
/// на жесте, который человек просто повторит.</summary>
public static class ClipboardSafe
{
    /// <summary>Сколько раз пробуем. Пять попыток по 50 мс — это четверть секунды в худшем случае,
    /// незаметно для нажатия кнопки и с запасом перекрывает типичную занятость буфера.</summary>
    private const int Attempts = 5;
    private const int DelayMs = 50;

    /// <summary>Кладёт текст в буфер обмена. true — получилось.</summary>
    public static bool TrySetText(string? text)
    {
        if (string.IsNullOrEmpty(text)) return false;

        for (var attempt = 1; attempt <= Attempts; attempt++)
        {
            try
            {
                Clipboard.SetText(text);
                return true;
            }
            catch (ExternalException)
            {
                // Буфер занят другим процессом. COMException (CLIPBRD_E_CANT_OPEN из тикета) —
                // наследник ExternalException, так что ловится здесь же; более широкий catch взят
                // намеренно, потому что WPF заворачивает часть отказов буфера в базовый тип.
            }

            // После последней попытки спать незачем — только задержим ответ вызывающему.
            if (attempt < Attempts) Thread.Sleep(DelayMs);
        }
        return false;
    }
}
