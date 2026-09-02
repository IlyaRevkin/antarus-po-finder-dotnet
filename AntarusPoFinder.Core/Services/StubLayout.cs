using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AntarusPoFinder.Core.Services;

/// <summary>Как выглядит страница-заглушка (см. <see cref="InstructionStub"/> и <see cref="StubKind"/>).
///
/// Заглушка — не техническая затычка, а то, что реально увидит заказчик, наведя телефон на наклейку
/// на шкафу: наклейку клеят задолго до того, как инструкцию допишут (а для рациональных шкафов
/// инструкции не будет вовсе), и по постоянной ссылке открывается ровно эта страница. Поэтому её
/// текст и вид — вопрос не разработки, а оформления, и решаться он должен в программе, а не правкой
/// кода.
///
/// Все размеры заданы в ДОЛЯХ ширины страницы, а не в пунктах: страница рисуется картинкой при 150
/// точках на дюйм (см. InstructionStubWriter), и доли переживают смену разрешения, а пункты пришлось
/// бы пересчитывать. Проценты в интерфейсе — те же доли, умноженные на сто.
///
/// Настройка ОБЩАЯ и синхронизируется: заглушки на хостинг кладут разные машины, и выглядеть они
/// обязаны одинаково — иначе у заказчиков окажутся страницы разного вида по одному и тому же поводу.</summary>
public sealed record StubLayout
{
    /// <summary>Подстановка номера версии в тексте. Именно подстановкой, а не отдельным полем:
    /// человек сам решает, писать «Версия 1.0.0005.0001» или «ПО 1.0.0005.0001» и нужно ли это вообще.</summary>
    public const string VersionPlaceholder = "{версия}";

    /// <summary>Подстановка блока контактов сервиса (<see cref="ServiceContacts.Block"/>). Ровно по
    /// той же причине, что и подстановка версии, плюс одна своя: контакты обязаны быть во ВСЕХ ТРЁХ
    /// видах заглушки, а телефон меняется целиком и сразу. Через подстановку правка делается в одном
    /// поле и доезжает до всех трёх страниц; вписанный руками в каждый макет телефон разъехался бы на
    /// первом же изменении.</summary>
    public const string ServicePlaceholder = "{сервис}";

    /// <summary>Какому поводу принадлежит этот макет. Хранится в самом макете, а не только ключом в
    /// наборе: по нему считается метка вида в файле заглушки (см. <see cref="InstructionStub"/>).</summary>
    public StubKind Kind { get; init; } = StubKind.InDevelopment;

    public string Title { get; init; } = InstructionStub.Text;

    public string Hint { get; init; } =
        "Документ ещё не готов. Файл заменится настоящей инструкцией, как только её приложат к этой версии.";

    /// <summary>Блок «куда звонить». Отдельной строкой, а не внутри <see cref="Hint"/>: у него свой
    /// размер, своё место на странице и своё правило — он не должен исчезать ни у одного из трёх
    /// видов.</summary>
    public string Contacts { get; init; } = ServicePlaceholder;

    /// <summary>Нижняя строка — обычно кто выпустил и к чему это относится. Пусто — не печатается.</summary>
    public string Footer { get; init; } = VersionPlaceholder;

    public double TitleSize { get; init; } = 0.06;
    public double HintSize { get; init; } = 0.022;
    public double ContactsSize { get; init; } = 0.02;
    public double FooterSize { get; init; } = 0.018;

    /// <summary>Рамка по краю страницы: на печати помогает увидеть, что лист не обрезан.</summary>
    public bool ShowFrame { get; init; }

    /// <summary>Серый тон подсказки и подписи, 0 — чёрный, 255 — белый. Заголовок всегда чёрный:
    /// он обязан читаться и на плохой печати, и с экрана телефона под углом.</summary>
    public int MutedTone { get; init; } = 0x66;

    public static StubLayout Default { get; } = new();

    /// <summary>Вид по умолчанию для каждого из трёх поводов. Тексты разные не для красоты: обещать
    /// «скоро допишем» там, где документа не будет никогда, — то же самое враньё, от которого
    /// заглушка и должна избавлять, а рядом с готовой инструкцией такая фраза противоречит лежащему
    /// в двух сантиметрах файлу.</summary>
    public static StubLayout DefaultFor(StubKind kind) => kind switch
    {
        StubKind.NotPlanned => new StubLayout
        {
            Kind = StubKind.NotPlanned,
            Title = "Инструкции по этому шкафу нет",
            Hint = "Отдельного руководства на такой шкаф не выпускается. " +
                   "По работе, настройке и обслуживанию проще всего спросить у сервисной службы — " +
                   "там подскажут по вашему объекту, а не по общей бумаге.",
            Footer = "",
        },
        StubKind.ServiceNote => new StubLayout
        {
            Kind = StubKind.ServiceNote,
            Title = "Остались вопросы?",
            Hint = "Инструкция к этому шкафу лежит рядом. Если в ней что-то непонятно, шкаф собран " +
                   "иначе или документ устарел — позвоните, разберёмся вместе.",
            Footer = VersionPlaceholder,
        },
        _ => Default,
    };

    /// <summary>Текст с подставленными номером версии и контактами сервиса. Версия неизвестна (общая
    /// папка контроллера) — подстановка убирается вместе с лишними пробелами, а не оставляет
    /// «Версия {версия}».</summary>
    public string Fill(string? text, string? versionRaw, string? contacts = null)
    {
        if (string.IsNullOrEmpty(text)) return "";
        var version = (versionRaw ?? "").Trim();
        return text
            .Replace(VersionPlaceholder, version, StringComparison.Ordinal)
            .Replace(ServicePlaceholder, (contacts ?? ServiceContacts.Block).Trim(), StringComparison.Ordinal)
            .Trim();
    }

    /// <summary>Разбор сохранённого вида. Битый JSON — вид по умолчанию, а не исключение: испорченная
    /// настройка не должна оставлять папку «Инструкция» вовсе без заглушки.</summary>
    public static StubLayout Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return Default;
        try { return JsonSerializer.Deserialize<StubLayout>(json) ?? Default; }
        catch (Exception) { return Default; }
    }

    public string ToJson() => JsonSerializer.Serialize(this);

    /// <summary>Ограничение вменяемости: размер шрифта в долях ширины страницы. Ноль сделал бы текст
    /// невидимым, а половина ширины — нечитаемой кашей; и то и другое человек способен ввести
    /// случайно, а увидит результат уже на наклеенном шкафу.</summary>
    public StubLayout Sane() => this with
    {
        Title = string.IsNullOrWhiteSpace(Title) ? DefaultFor(Kind).Title : Title.Trim(),
        TitleSize = Clamp(TitleSize, 0.02, 0.15),
        HintSize = Clamp(HintSize, 0.01, 0.08),
        ContactsSize = Clamp(ContactsSize, 0.01, 0.08),
        FooterSize = Clamp(FooterSize, 0.008, 0.06),
        MutedTone = Math.Clamp(MutedTone, 0, 200),
    };

    private static double Clamp(double value, double min, double max) =>
        double.IsFinite(value) ? Math.Clamp(value, min, max) : min;

    /// <summary>Отпечаток макета — двенадцать шестнадцатеричных знаков от SHA-256 по всем полям,
    /// влияющим на картинку, плюс по подставляемым контактам.
    ///
    /// <b>Зачем именно отпечаток, а не время правки.</b> Готовый PDF заглушки лежит на диске и на
    /// хостинге; пока в нём нечем было отличить «нарисован по нынешнему макету» от «нарисован по
    /// прошлому», перерисовывать его было не по чему — отсюда и жалоба «меняю макет, а заглушки
    /// прежние, хоть перезаливай, хоть удаляй и заливай»: перезаливка гоняла на хостинг тот же самый
    /// устаревший файл с диска. Время правки для этой роли не годится: макет живёт не файлом, а
    /// строкой в общих настройках, которую синхронизация переписывает на каждой машине по своим
    /// причинам, — сравнение со временем перерисовывало бы все заглушки на диске после каждой
    /// синхронизации. Отпечаток же меняется ровно тогда, когда меняется КАРТИНКА: правка, не влияющая
    /// на вид, не трогает ни одного файла на сетевом диске, а любая влияющая перерисовывает каждый
    /// ровно один раз.
    ///
    /// Двенадцати знаков (48 бит) достаточно: это не защита от подделки, а сравнение «то же самое или
    /// нет» между двумя редакциями одного макета.</summary>
    public string Stamp(string? contacts = null)
    {
        var sane = Sane();
        // Собирается вручную, а не сериализацией: у JSON нет обещания стабильного порядка полей между
        // версиями рантайма, а перестановка полей молча перерисовала бы все заглушки на диске.
        var material = string.Join('\u001F',
            ((int)sane.Kind).ToString(),
            sane.Title, sane.Hint, sane.Contacts, sane.Footer,
            sane.TitleSize.ToString("R"), sane.HintSize.ToString("R"),
            sane.ContactsSize.ToString("R"), sane.FooterSize.ToString("R"),
            sane.ShowFrame ? "1" : "0", sane.MutedTone.ToString(),
            (contacts ?? ServiceContacts.Block).Trim());

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(material));
        return Convert.ToHexString(hash, 0, 6).ToLowerInvariant();
    }
}

/// <summary>Три макета — по одному на каждый повод (<see cref="StubKind"/>) — и общий на всех блок
/// контактов сервиса.
///
/// Контакты вынесены из макетов НАРУЖУ намеренно: телефон сервиса один, и когда он меняется, он
/// меняется сразу на всех трёх страницах. Внутри макетов стоит подстановка
/// <see cref="StubLayout.ServicePlaceholder"/>, а значение живёт здесь — правка в одном поле.</summary>
public sealed record StubLayoutSet
{
    public StubLayout InDevelopment { get; init; } = StubLayout.DefaultFor(StubKind.InDevelopment);
    public StubLayout NotPlanned { get; init; } = StubLayout.DefaultFor(StubKind.NotPlanned);
    public StubLayout ServiceNote { get; init; } = StubLayout.DefaultFor(StubKind.ServiceNote);

    /// <summary>Блок «куда звонить», подставляемый во все три макета. Пусто — берётся зашитый в
    /// программу (<see cref="ServiceContacts.Block"/>): телефон, поменявшийся между релизами, можно
    /// вписать сюда, не дожидаясь обновления.</summary>
    public string ServiceContacts { get; init; } = "";

    public static StubLayoutSet Default { get; } = new();

    /// <summary>Действующие контакты: вписанные человеком либо зашитые в программу.</summary>
    [JsonIgnore]
    public string Contacts =>
        string.IsNullOrWhiteSpace(ServiceContacts) ? Services.ServiceContacts.Block : ServiceContacts.Trim();

    public StubLayout For(StubKind kind) => kind switch
    {
        StubKind.NotPlanned => NotPlanned,
        StubKind.ServiceNote => ServiceNote,
        _ => InDevelopment,
    };

    public StubLayoutSet With(StubKind kind, StubLayout layout) => kind switch
    {
        StubKind.NotPlanned => this with { NotPlanned = layout with { Kind = StubKind.NotPlanned } },
        StubKind.ServiceNote => this with { ServiceNote = layout with { Kind = StubKind.ServiceNote } },
        _ => this with { InDevelopment = layout with { Kind = StubKind.InDevelopment } },
    };

    /// <summary>Отпечаток макета этого вида вместе с общими контактами — то, по чему заглушка на
    /// диске понимает, что её пора перерисовать (см. <see cref="StubLayout.Stamp"/>).</summary>
    public string Stamp(StubKind kind) => For(kind).Stamp(Contacts);

    public StubLayoutSet Sane() => new()
    {
        InDevelopment = (InDevelopment with { Kind = StubKind.InDevelopment }).Sane(),
        NotPlanned = (NotPlanned with { Kind = StubKind.NotPlanned }).Sane(),
        ServiceNote = (ServiceNote with { Kind = StubKind.ServiceNote }).Sane(),
        ServiceContacts = (ServiceContacts ?? "").Trim(),
    };

    public string ToJson() => JsonSerializer.Serialize(this);

    /// <summary>Разбор набора. <paramref name="legacySingleLayout"/> — значение прежней настройки
    /// <c>stub_layout</c>, в которой лежал ОДИН макет «в разработке»: пока набора ещё нет, подхватываем
    /// подогнанный человеком вид оттуда, а не сбрасываем его на умолчание. Настройки эти живут в
    /// общем синхронизируемом конфиге, и обнулить чужую подгонку обновлением программы нельзя.</summary>
    public static StubLayoutSet Parse(string? json, string? legacySingleLayout = null)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return string.IsNullOrWhiteSpace(legacySingleLayout)
                ? Default
                : (Default with { InDevelopment = StubLayout.Parse(legacySingleLayout) }).Sane();
        }

        try { return (JsonSerializer.Deserialize<StubLayoutSet>(json) ?? Default).Sane(); }
        catch (Exception) { return Default; }
    }
}
