namespace AntarusPoFinder.App.Services;

/// <summary>Правила «поиск обновляется по мере ввода» — вынесены из SearchView отдельно, чтобы их
/// проверял тест, а не только живой набор текста.
///
/// Кнопка «Найти» и Enter никуда не делись: они запускают ровно тот же поиск немедленно, без
/// ожидания паузы, и только они разрешают вопрос про раскладку клавиатуры (см. SearchView.
/// LayoutFallbackAllowed) — модальное окно посреди набора текста было бы худшим, что может сделать
/// «динамичный» поиск.</summary>
internal static class SearchLiveQuery
{
    /// <summary>Пауза после последнего нажатия клавиши, после которой запускается поиск. 300 мс —
    /// быстрее, чем осознанная пауза в наборе, и достаточно, чтобы «НГР» не искалось трижды (Н, НГ,
    /// НГР) по дороге.</summary>
    public const int DebounceMs = 300;

    /// <summary>Режим «Схемы» — единственный, где выдача берётся не из локальной базы, а обходом
    /// второго диска (сетевая шара, бывает под 400 ГБ и минуты обхода, см. SearchView.
    /// PerformSchemasSearchAsync). Гонять его по каждой паузе в наборе нельзя — там поиск
    /// по-прежнему запускается Enter'ом и кнопкой «Найти».</summary>
    public static bool AutoSearchApplies(bool schemasMode) => !schemasMode;

    /// <summary>Есть ли что искать. Пустой запрос без фильтров ничего не ищет — вызывающий вместо
    /// этого очищает выдачу, иначе после стирания текста на экране висели бы результаты запроса,
    /// которого в поле уже нет.</summary>
    public static bool HasSomethingToSearch(string? query, bool hasFilters) =>
        !string.IsNullOrWhiteSpace(query) || hasFilters;

    /// <summary>Нужно ли перезапускать поиск после правки текста. Смысл сравнения — обрезанный текст:
    /// добавленный в конце пробел выдачу не меняет, а перезапуск стоил бы обхода диска в карточках.</summary>
    public static bool QueryChanged(string? typed, string? lastSearched) =>
        !string.Equals((typed ?? "").Trim(), (lastSearched ?? "").Trim(), StringComparison.Ordinal);
}
