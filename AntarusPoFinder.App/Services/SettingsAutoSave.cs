namespace AntarusPoFinder.App.Services;

/// <summary>Правила автосохранения полей в Настройках и на странице «Сетевые диски»: кнопок
/// «Сохранить» там больше нет — текстовые поля сохраняются по уходу фокуса и по Enter, галочки,
/// радиокнопки и выбор пути — сразу по изменению.
///
/// Два правила, из-за которых это отдельный класс, а не пара строк в обработчике:
/// 1) не сохранять и не писать в статус-строку, если значение не изменилось (иначе каждый уход
///    фокуса с нетронутого поля сыпал бы «сохранено» в нижнюю строку);
/// 2) числовые поля при мусорном вводе не показывают модальное окно (по уходу фокуса это особенно
///    навязчиво) — поле возвращается к сохранённому значению, а причина уходит в статус-строку.
///
/// Логика чистая (без WPF) намеренно — так её проверяет тест, а не только живой клик.</summary>
internal static class SettingsAutoSave
{
    /// <summary>Изменилось ли текстовое значение по сравнению с сохранённым. Обрезка пробелов —
    /// как при самом сохранении: « C:\Диск » и «C:\Диск» это одно и то же значение.</summary>
    public static bool TextChanged(string? typed, string? stored) =>
        !string.Equals((typed ?? "").Trim(), (stored ?? "").Trim(), StringComparison.Ordinal);

    /// <summary>То же для путей: на Windows регистр в пути значения не имеет, и «D:\ПО» после
    /// «d:\по» — не повод перезаписывать настройку и сообщать об этом оператору.</summary>
    public static bool PathChanged(string? typed, string? stored) =>
        !string.Equals((typed ?? "").Trim(), (stored ?? "").Trim(), StringComparison.OrdinalIgnoreCase);

    /// <summary>Результат разбора числового поля: сохранять ли (Save), что именно (Value) и что
    /// сказать в статус-строке (Message; пустая — не говорить ничего, значение не менялось).</summary>
    public readonly record struct NumberEdit(bool Save, int Value, bool Invalid, string Message);

    /// <summary>Разбор числового поля Настроек. min — минимально допустимое значение (0 там, где
    /// ноль означает «отключено», 1 там, где ноль бессмыслен). invalidMessage — что показать при
    /// мусорном вводе; вызывающий по Invalid=true возвращает в поле сохранённое значение.</summary>
    public static NumberEdit ParseNumber(string? typed, int stored, int min, string invalidMessage)
    {
        var text = (typed ?? "").Trim();
        if (!int.TryParse(text, out var value) || value < min)
            return new NumberEdit(false, stored, true, invalidMessage);
        if (value == stored)
            return new NumberEdit(false, stored, false, "");
        return new NumberEdit(true, value, false, "");
    }
}
