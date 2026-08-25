namespace AntarusPoFinder.Core.Domain;

/// <summary>Справочник ГРУПП параметров: фиксированный список с порядком показа.
///
/// Почему справочник, а не свободный текст в строке: через полгода в базе оказались бы «Двигатель»,
/// «Мотор», «Настройки двигателя» и «двигатель» — четыре группы про одно и то же, и осмысленный
/// порядок («сперва основные значения, сброс до заводских в конце») перестал бы существовать.
/// Устройство — дословно как у fw_attachment_kinds: имя + sort_order, засев разовой миграцией.
///
/// <b>Порядок задан смыслом работы наладчика, а не алфавитом:</b> сперва то, что выставляют всегда,
/// потом связь и вводы-выводы, потом «может понадобиться, если что-то не работает», потом двигатель
/// и его разгон, защиты, прочее — и сброс до заводских ПОСЛЕДНИМ. Сброс в середине списка означал бы,
/// что человек, идущий по таблице сверху вниз, в какой-то момент обнулит всё, что уже выставил.</summary>
public static class ParamGroupCatalog
{
    public const string Main = "Основные настройки";
    public const string Communication = "Связь";
    public const string InputsOutputs = "Входы и выходы";
    public const string Troubleshooting = "Может понадобиться, если что-то не работает";
    public const string Motor = "Двигатель";
    public const string Ramps = "Разгон и торможение";
    public const string Protections = "Защиты";
    public const string Other = "Прочее";
    public const string FactoryReset = "Сброс до заводских";

    /// <summary>Стартовый набор с порядком. Шаг между номерами крупный намеренно: свою группу
    /// администратор вставляет МЕЖДУ готовыми, не переписывая весь список.</summary>
    public static readonly (string Name, int SortOrder)[] Defaults =
    {
        (Main, 10),
        (Communication, 20),
        (InputsOutputs, 30),
        (Troubleshooting, 40),
        (Motor, 50),
        (Ramps, 60),
        (Protections, 70),
        (Other, 900),
        // Заведомо последняя: между «Прочим» и ею оставлено место на девять своих групп, и всё равно
        // сброс окажется ниже любой из них.
        (FactoryReset, 1000),
    };

    /// <summary>Ключевые слова → группа. По ним разбор текстового файла ПРЕДЛАГАЕТ группу для секции
    /// («=====[Настройка ШУ]» → «Основные настройки»), а человек в предпросмотре соглашается или
    /// правит. Автоматически завести новую группу разбор не может — в этом весь смысл справочника.
    ///
    /// Порядок проверки — сверху вниз, первое совпадение побеждает: «сброс до заводских» проверяется
    /// раньше «связи», иначе «Сброс параметров связи» ушёл бы не туда.</summary>
    private static readonly (string Keyword, string Group)[] Hints =
    {
        ("заводск", FactoryReset),
        ("сброс", FactoryReset),
        ("двигател", Motor),
        ("мотор", Motor),
        ("разгон", Ramps),
        ("торможен", Ramps),
        ("замедлен", Ramps),
        ("защит", Protections),
        ("авари", Protections),
        ("связ", Communication),
        ("modbus", Communication),
        ("модбас", Communication),
        ("протокол", Communication),
        ("вход", InputsOutputs),
        ("выход", InputsOutputs),
        ("не работает", Troubleshooting),
        ("неисправ", Troubleshooting),
        ("диагност", Troubleshooting),
        ("шу", Main),
        ("основн", Main),
    };

    /// <summary>Какую группу предложить для секции с таким заголовком. Ничего не узнали — «Прочее»:
    /// молча придумывать новую группу нельзя, а терять строку тем более.
    ///
    /// ⚠️ Сравнение идёт в .NET с игнором регистра, а НЕ через COLLATE NOCASE: у SQLite NOCASE
    /// сворачивает только латиницу, а тут всё кириллическое (см. CLAUDE.md и Database.FileKey).</summary>
    public static string Suggest(string? sectionTitle)
    {
        var text = (sectionTitle ?? "").Trim();
        if (text.Length == 0) return Main;

        foreach (var (keyword, group) in Hints)
            if (text.Contains(keyword, StringComparison.OrdinalIgnoreCase))
                return group;

        return Other;
    }

    /// <summary>Место группы в порядке показа, когда справочник под рукой. Незнакомая группа (её
    /// удалили из справочника уже после того, как ревизию сохранили) уходит В КОНЕЦ, но перед
    /// сбросом до заводских — она всё ещё должна читаться, просто без своего места.</summary>
    public static int OrderOf(string? groupName, IReadOnlyDictionary<string, int> catalog)
    {
        var name = (groupName ?? "").Trim();
        if (name.Length == 0) return int.MaxValue - 1;
        return catalog.TryGetValue(name, out var order) ? order : 990;
    }
}
