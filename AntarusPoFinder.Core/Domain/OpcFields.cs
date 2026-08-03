namespace AntarusPoFinder.Core.Domain;

/// <summary>Правила одной галочки «ОПЦ» в форме загрузки прошивки.
///
/// Раньше галочек было две — «ОПЦ заявка» и «ОПЦ SN», каждая открывала своё поле, и обе можно было
/// включить, не заполнив ни одного поля: версия уезжала в папку «ОПЦ» вообще без признака, для какого
/// шкафа она собиралась. Теперь галочка одна: включили — открылись оба поля (серийный номер шкафа и
/// номер заявки), и заполнить нужно хотя бы одно, можно оба. Поле «sw» (галочка «не увеличивать
/// версию ПО») при включённой ОПЦ не задаётся вовсе — см. <see cref="SwVersionChoiceApplies"/>.
///
/// Логика вынесена отдельно от WPF-формы, чтобы её проверял тест, а не только живой клик.</summary>
public static class OpcFields
{
    /// <summary>Единственная причина отказа: галочка включена, но ни одно из двух полей не заполнено.</summary>
    public const string BothEmptyError =
        "Для ОПЦ-версии заполните хотя бы одно поле: серийный номер шкафа или номер заявки (можно оба). " +
        "По ним потом видно, для какого именно шкафа собиралась эта прошивка.";

    /// <summary>null — всё в порядке; иначе готовый текст сообщения оператору.</summary>
    public static string? Validate(bool opcEnabled, string? cabinetSn, string? requestNum)
    {
        if (!opcEnabled) return null;
        if (!string.IsNullOrWhiteSpace(cabinetSn) || !string.IsNullOrWhiteSpace(requestNum)) return null;
        return BothEmptyError;
    }

    public static bool IsValid(bool opcEnabled, string? cabinetSn, string? requestNum) =>
        Validate(opcEnabled, cabinetSn, requestNum) is null;

    /// <summary>Учитывать ли выбор «не увеличивать версию ПО (sw)». Для ОПЦ-версии — нет: у неё
    /// собственный, разовый шкаф, и sw в форме не задаётся (галочка при включённой ОПЦ прячется),
    /// поэтому случайно оставшийся включённым флажок не должен молча утащить номер назад.</summary>
    public static bool SwVersionChoiceApplies(bool opcEnabled) => !opcEnabled;
}
