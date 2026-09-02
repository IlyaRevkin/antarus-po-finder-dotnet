namespace AntarusPoFinder.Core.Services;

/// <summary>Три повода положить в папку «Инструкция» страницу, которую увидит заказчик, наведя
/// телефон на наклейку. Раньше повод был один («в разработке»), и это оказалось враньём в двух
/// случаях из трёх.
///
/// <list type="bullet">
/// <item><description><see cref="InDevelopment"/> — документ ЕЩЁ не написан, но будет. Страница живёт
/// по тому же пути, по которому потом ляжет настоящая инструкция, и уходит, как только та
/// появится.</description></item>
/// <item><description><see cref="NotPlanned"/> — документа НЕ БУДЕТ вовсе (рациональные шкафы).
/// Обещать «скоро допишем» здесь нельзя — это ожидание, которое никогда не сбудется. Такая страница
/// ОДНА на всех и не привязана ни к типу, ни к подтипу, ни к контроллеру: см.
/// <see cref="InstructionStub.SharedNotPlannedFileName"/>.</description></item>
/// <item><description><see cref="ServiceNote"/> — документ ЕСТЬ, и страница кладётся РЯДОМ с ним, а
/// не вместо: «если что-то непонятно или инструкция устарела — звоните». Единственный вид, который
/// сосуществует с настоящей инструкцией.</description></item>
/// </list>
///
/// Во всех трёх есть контакты сервиса (<see cref="ServiceContacts"/>) — это и было смыслом затеи:
/// куда бы человек ни попал по наклейке, он должен увидеть, кому звонить.</summary>
public enum StubKind
{
    InDevelopment = 0,
    NotPlanned = 1,
    ServiceNote = 2,
}

public static class StubKinds
{
    /// <summary>Все три — в порядке, в котором их показывает редактор макета.</summary>
    public static readonly StubKind[] All = { StubKind.InDevelopment, StubKind.NotPlanned, StubKind.ServiceNote };

    /// <summary>Как вид называется человеку.</summary>
    public static string Label(this StubKind kind) => kind switch
    {
        StubKind.NotPlanned => "Инструкции не будет",
        StubKind.ServiceNote => "Дополнение к инструкции",
        _ => "Инструкция в разработке",
    };

    /// <summary>Страница лежит ВМЕСТО инструкции, а не рядом с ней. От этого зависит всё поведение
    /// уборки: такую страницу надо убрать, как только настоящий документ появился, а
    /// <see cref="StubKind.ServiceNote"/> — наоборот, только тогда и кладётся.</summary>
    public static bool ReplacesInstruction(this StubKind kind) => kind != StubKind.ServiceNote;

    /// <summary>Короткое имя для метки в файле. Не <c>ToString()</c>: имя в метке — это формат на
    /// диске, и переименование члена перечисления не должно превращать все уже лежащие заглушки в
    /// «неопознанные» (а значит — в перерисовку сотен файлов на сетевом диске).</summary>
    public static string Tag(this StubKind kind) => kind switch
    {
        StubKind.NotPlanned => "none",
        StubKind.ServiceNote => "note",
        _ => "dev",
    };

    /// <summary>Разбор метки. Неизвестное (в том числе метка БЕЗ вида — так писались заглушки до
    /// появления трёх видов) — «в разработке»: именно им всё лежащее на дисках и является.</summary>
    public static StubKind FromTag(string? tag) => tag switch
    {
        "none" => StubKind.NotPlanned,
        "note" => StubKind.ServiceNote,
        _ => StubKind.InDevelopment,
    };
}
