using System.Globalization;

namespace AntarusPoFinder.Core.Services;

/// <summary>Где на этикетке стоит код относительно текста.
///
/// «Сам» — прежнее и единственное до сих пор поведение: код слева, текст колонкой справа, а если
/// колонке не остаётся ширины — код сверху, текст под ним. Остальные значения ставят код туда, куда
/// сказали: на разных наклейках и разных шкафах удобно по-разному, а единственная зашитая раскладка
/// была ровно тем, на что и жаловались.</summary>
public enum QrPlacement
{
    Auto,
    Left,
    Right,
    Above,
    Below,
}

/// <summary>Где печатается подпись назначения («Руководство по эксплуатации»).
///
/// «Сам» — там, где она ничего не отнимает у кода: первой строкой в области текста. «Сверху»/«Снизу»
/// — своей полосой во всю ширину, над всем содержимым или под ним: так подпись читается первой, но
/// её полоса забирает высоту у кода. Выбор за тем, кто клеит: на крупной наклейке она ничего не
/// портит, на мелкой — уводит код к пределу читаемости.</summary>
public enum HeadlinePlacement
{
    Auto,
    Top,
    Bottom,
}

/// <summary>Как выровнена подпись назначения в своей строке.</summary>
public enum HeadlineAlignment
{
    Center,
    Left,
    Right,
}

/// <summary>Поворот всего макета на наклейке.
///
/// Нужен рулонным принтерам: на ленте наклейка едет узкой стороной вперёд, и напечатанное «как
/// смотрит макет» оказывается лежащим на боку относительно того, как наклейку клеят на шкаф. Сам
/// физический размер наклейки при этом НЕ меняется (её режет высечка, а не мы) — поворачивается
/// только содержимое: макет считается на перевёрнутой стороне и кладётся на ту же наклейку боком.</summary>
public enum LabelRotation
{
    None,

    /// <summary>По часовой стрелке: верх макета уходит к правому краю наклейки.</summary>
    Clockwise90,

    /// <summary>Против часовой: верх макета уходит к левому краю.</summary>
    CounterClockwise90,
}

/// <summary>Поля этикетки по сторонам, в миллиметрах.
///
/// <b>Почему по сторонам, а не одно на все четыре.</b> Непечатаемая кромка у принтера почти никогда
/// не одинакова: сверху её съедает подача, снизу — отрыв, слева и справа — направляющие. Единственное
/// поле приходилось задавать по самой широкой кромке, и всё остальное содержимое уезжало к
/// противоположному краю; лечили это сдвигом всего макета — то есть одной кривизной чинили другую.
/// Теперь драйвер отдаёт четыре числа, и они кладутся туда же, четырьмя (см. PrinterPageFit), а
/// сдвиг остаётся тем, чем и был: калибровкой протяжки.</summary>
public readonly record struct LabelMargins(double Left, double Top, double Right, double Bottom)
{
    public static LabelMargins All(double mm) => new(mm, mm, mm, mm);

    /// <summary>Самое узкое поле — им описывается набор одним числом там, где четырёх не завезли:
    /// в настройке для старых версий программы и в короткой строке состояния.</summary>
    public double Min => Math.Min(Math.Min(Left, Right), Math.Min(Top, Bottom));

    public bool Uniform =>
        Math.Abs(Left - Top) < 0.01 && Math.Abs(Left - Right) < 0.01 && Math.Abs(Left - Bottom) < 0.01;

    /// <summary>Поля так, как их видит повёрнутый макет. Разворот содержимого меняет местами и
    /// стороны: верх макета при повороте по часовой стрелке смотрит в правый край наклейки, значит
    /// «поле сверху» для него — это правое поле наклейки.</summary>
    public LabelMargins ForRotation(LabelRotation rotation) => rotation switch
    {
        LabelRotation.Clockwise90 => new LabelMargins(Top, Right, Bottom, Left),
        LabelRotation.CounterClockwise90 => new LabelMargins(Bottom, Left, Top, Right),
        _ => this,
    };
}

/// <summary>Вид самого кода. <see cref="Classic"/> — обычная чёрная матрица (запасной вариант на
/// случай упрямого сканера), <see cref="Rounded"/> — фирменный со скруглёнными клетками,
/// <see cref="Dots"/> — клетки точками. Все три читаются одинаково: скругление и форма клетки не
/// меняют ни её центр, ни размер, а угловые маркеры во всех видах рисуются целиком.</summary>
public enum QrStyle
{
    Rounded,
    Classic,
    Dots,
}

/// <summary>Макет этикетки с QR — всё, что задаёт её вид, одной записью. Отдельно от самой отрисовки
/// (LabelPrinter живёт в App: там WPF), потому что настройки читаются и пишутся в Core и должны
/// проверяться тестами без окна.
///
/// <b>Зачем понадобилось.</b> Раньше настраивались только ширина и высота, а всё остальное было
/// зашито в код: поля 0, ссылка 8-м кеглем в самом низу, рамка вплотную к краю. На настоящем
/// принтере этикеток это давало ровно то, о чём была жалоба: «что 97, что 90, что 100 ставлю —
/// верх обрезается», и ссылку «плохо пропечатывает, очень мелкая». Причина у обеих одна: печать
/// начинается от физического угла листа, у которого есть непечатаемая зона, а поля этикетки были
/// нулевыми, и первые миллиметры содержимого просто не попадали на бумагу.
///
/// Поэтому здесь есть и <see cref="Margins"/> (отступы содержимого от краёв — лечат обрезку сами по
/// себе), и <see cref="OffsetXMm"/>/<see cref="OffsetYMm"/> (сдвиг всего макета — на случай, когда
/// принтер смещает ленту, и лечить надо не полями, а именно сдвигом). Разделены они намеренно:
/// поля влияют на то, сколько места остаётся содержимому, а сдвиг — нет.</summary>
public sealed record LabelLayout
{
    public double WidthMm { get; init; } = 97.5;
    public double HeightMm { get; init; } = 72;

    /// <summary>Отступы содержимого от краёв этикетки, по сторонам. По умолчанию 3 мм со всех —
    /// с запасом больше непечатаемой зоны любого известного термопринтера этикеток.</summary>
    public LabelMargins Margins { get; init; } = LabelMargins.All(3);

    /// <summary>Поле одним числом — для короткой строки состояния и для настройки, которую читают
    /// старые версии программы. Ставит одинаковое поле со всех сторон.</summary>
    public double MarginMm
    {
        get => Margins.Min;
        init => Margins = LabelMargins.All(value);
    }

    /// <summary>Поворот содержимого на наклейке — см. <see cref="LabelRotation"/>. Per-machine, как
    /// и размер: зависит от того, каким боком наклейка едет в этом принтере.</summary>
    public LabelRotation Rotation { get; init; } = LabelRotation.None;

    /// <summary>Сдвиг всего содержимого вправо/вниз (можно отрицательный) — калибровка под
    /// конкретный принтер. Per-machine, как и имя принтера.</summary>
    public double OffsetXMm { get; init; }
    public double OffsetYMm { get; init; }

    /// <summary>Сторона QR-кода. 0 — «сам»: столько, сколько остаётся по высоте, но не больше
    /// половины ширины этикетки.</summary>
    public double QrMm { get; init; }

    public double TitlePt { get; init; } = 16;

    /// <summary>Кегль строки со ссылкой. 9 вместо прежних зашитых 8 — на 203 dpi термопринтере
    /// восьмёрка кириллицей уже разваливается.</summary>
    public double CaptionPt { get; init; } = 9;

    /// <summary>Печатать ли ссылку текстом под QR. Она нужна, когда телефон код не берёт, но на
    /// узкой этикетке иногда важнее заголовок — поэтому выключаемо.
    ///
    /// По умолчанию ВЫКЛЮЧЕНА (см. <see cref="Default"/>): длинный адрес хостинга кеглем в 9 пт
    /// занимает у наклейки три строки, читать его с шкафа всё равно никто не будет, а место он
    /// отнимает у кода — то есть у единственного, чем этой наклейкой пользуются.</summary>
    public bool ShowLink { get; init; }

    /// <summary>Рамка по краю этикетки. На листах с высечкой она помогает попасть в границы, на
    /// рулоне — только тратит тонер. По умолчанию ВЫКЛЮЧЕНА: наклейки печатаются рулоном, и там
    /// граница уже задана высечкой, а нарисованная рамка при малейшем уходе ленты печатается
    /// кривой относительно края — то есть аккуратнее без неё.</summary>
    public bool ShowFrame { get; init; }

    /// <summary>Вид кода: скруглённый (по умолчанию), классическая матрица или точки. Читается во
    /// всех трёх видах: форма клетки не меняет ни её центр, ни размер, а уровень коррекции Q
    /// допускает потерю до четверти кода — вырез под подпись занимает заметно меньше.</summary>
    public QrStyle Style { get; init; } = QrStyle.Rounded;

    /// <summary>Рисованный QR — то есть любой вид, кроме классической растровой матрицы. Осталось
    /// отдельным свойством, потому что от него зависит не только отрисовка: у классического кода нет
    /// окошка под подпись в центре.</summary>
    public bool FancyQr => Style != QrStyle.Classic;

    /// <summary>Где стоит код относительно текста. По умолчанию — СПРАВА: наклейку на шкафу читают
    /// слева направо, сначала название установки, потом наводят телефон, и код под правой рукой
    /// удобнее. Прежнее умолчание («сам», код слева) осталось отдельным пунктом списка.</summary>
    public QrPlacement QrPlace { get; init; } = QrPlacement.Right;

    /// <summary>Строка над кодом, объясняющая, ЗАЧЕМ этот QR.
    ///
    /// <b>Зачем настройка.</b> На шкафу наклеек несколько (паспорт, ОТК, инструкция), и без подписи
    /// заказчик видит «просто QR с названием установки и непонятными цифрами» — дословная жалоба. Одна
    /// строка снимает вопрос ещё до того, как человек достанет телефон. Текст правится, а не зашит:
    /// у одних шкафов это «Руководство по эксплуатации» (умолчание), у других — «Паспорт шкафа» или
    /// имя заказчика, и переписывать программу ради этого никто не должен.</summary>
    public string HeadlineText { get; init; } = DefaultHeadline;

    /// <summary>Печатать ли строку <see cref="HeadlineText"/>. Отдельно от пустого текста: выключить
    /// подпись на мелкой наклейке и стереть заготовленный текст — разные намерения.</summary>
    public bool ShowHeadline { get; init; } = true;

    /// <summary>Где печатать подпись назначения. По умолчанию — ПОЛОСОЙ СВЕРХУ: «Руководство по
    /// эксплуатации» должно читаться первым, до названия установки и до кода, иначе заказчик видит
    /// «просто QR и непонятные цифры» — ровно ту жалобу, ради которой подпись и появилась. Прежнее
    /// умолчание («сам», первой строкой в колонке текста) осталось отдельным пунктом списка.</summary>
    public HeadlinePlacement HeadlinePlace { get; init; } = HeadlinePlacement.Top;

    /// <summary>Выравнивание подписи назначения в её строке.</summary>
    public HeadlineAlignment HeadlineAlign { get; init; } = HeadlineAlignment.Center;

    /// <summary>Свободная строка под названием установки — то, что на этой наклейке надо дописать
    /// «от себя»: номер договора, имя объекта, «после наладки не снимать». Печатается тем же кеглем,
    /// что и подзаголовок с версией, и рядом с ним.
    ///
    /// Отдельно от подписи назначения: та объясняет, ЗАЧЕМ наклейка (одна на всё предприятие), а эта
    /// — про конкретную партию шкафов, и меняют её чаще, чем весь остальной макет.</summary>
    public string NoteText { get; init; } = "";

    /// <summary>Подпись в окошке по центру фирменного кода. Пусто — окна нет вовсе, код рисуется
    /// сплошным. Длиннее <see cref="HoleWrapAfter"/> знаков — верстается в две-три строки (см.
    /// <see cref="QrHoleText"/>): одной строкой «ИНСТРУКЦИЯ» вырождалась в нечитаемый кегль, потому
    /// что плашке расти некуда, а в три строки те же буквы получаются заметно крупнее.</summary>
    public string HoleText { get; init; } = DefaultHoleText;

    /// <summary>Что печатается на наклейке по умолчанию. Поправка была такая: «кст текст не
    /// инструкция, а руководство по эксплуатации» — наклейку читает заказчик, и документ, который он
    /// по ней откроет, называется именно так. Внутренние имена (папка «Инструкция» на диске,
    /// instructions_path, InstructionNaming) при этом не трогаются: их читает не заказчик, а диск и
    /// синхронизация, и переименование сломало бы и то и другое.</summary>
    public const string DefaultHeadline = "Руководство по эксплуатации";

    /// <summary>Подпись в окошке кода — имя предприятия. Наклейка уходит заказчику, и в центре
    /// кода читается ровно то, чьё это оборудование; «что это за документ» уже сказано подписью
    /// назначения полосой сверху. Семь знаков ложатся в две строки (AMPE / RUS, см.
    /// <see cref="QrHoleText"/>) — то есть квадратом, а не полоской, и кегль от этого только
    /// выигрывает.</summary>
    public const string DefaultHoleText = "AMPERUS";

    /// <summary>Прежние умолчания. Нужны разовым миграциям (Database.RenameLabelInstructionTextsOnce,
    /// Database.ApplyLabelDesignDefaultsOnce): переписать текст можно только там, где его не правили
    /// руками. «РЭ» — умолчание, стоявшее между «ИНСТ» и нынешним.</summary>
    public const string LegacyHeadline = "Инструкция для заказчика";
    public const string LegacyHoleText = "ИНСТ";
    public const string LegacyHoleTextRe = "РЭ";

    /// <summary>Столько знаков в подписи центра помещается в плашку в 20 % стороны кода, если верстать
    /// её в три строки. Больше — кегль падает ниже различимого на 203 dpi, поэтому лишнее отсекается.</summary>
    public const int MaxHoleTextLength = 15;

    /// <summary>До скольких знаков подпись центра печатается одной строкой. Дословная просьба: «подпись
    /// в центре чтобы могла в 2-3 строки по длине, если больше 4 букв».</summary>
    public const int HoleWrapAfter = 4;

    /// <summary>Текст подписи над кодом с учётом галочки — то, что реально пойдёт на этикетку.</summary>
    public string EffectiveHeadline() => ShowHeadline ? (HeadlineText ?? "").Trim() : "";

    /// <summary>Подпись центра с учётом фирменного вида: у обычной растровой матрицы окна нет.</summary>
    public string EffectiveHoleText() => FancyQr ? (HoleText ?? "").Trim() : "";

    // ── Границы разумного ────────────────────────────────────────────────────

    /// <summary>Приводит значения к рабочему диапазону. Нужна и при чтении настроек (там могло
    /// оказаться что угодно, в т.ч. чужое синхронизированное), и при живой правке в окне: этикетка
    /// с полями в половину своей ширины не должна складываться в отрицательный размер.</summary>
    public LabelLayout Clamped()
    {
        var w = Clamp(WidthMm, 20, 300);
        var h = Clamp(HeightMm, 15, 300);
        return this with
        {
            WidthMm = w,
            HeightMm = h,
            // Каждое поле — не больше четверти своей стороны: два противоположных вместе не должны
            // съесть половину наклейки, иначе содержимому остаётся полоска.
            Margins = new LabelMargins(
                Clamp(Margins.Left, 0, w / 4), Clamp(Margins.Top, 0, h / 4),
                Clamp(Margins.Right, 0, w / 4), Clamp(Margins.Bottom, 0, h / 4)),
            OffsetXMm = Clamp(OffsetXMm, -20, 20),
            OffsetYMm = Clamp(OffsetYMm, -20, 20),
            QrMm = QrMm <= 0 ? 0 : Clamp(QrMm, 10, Math.Min(w, h)),
            TitlePt = Clamp(TitlePt, 6, 48),
            CaptionPt = Clamp(CaptionPt, 5, 24),
            HeadlineText = Trim(HeadlineText, 60),
            NoteText = Trim(NoteText, 80),
            // Длинная подпись в центре не «ужимается», а вырождается: плашка не растёт (её площадь
            // ограничена тем, что вытягивает коррекция ошибок), поэтому лишнее отсекается здесь, а не
            // превращается в нечитаемую строку на печати.
            HoleText = Trim(HoleText, MaxHoleTextLength),
        };
    }

    /// <summary>Строка настройки: без краевых пробелов, без переводов строки (этикетка верстает
    /// перенос сама) и не длиннее разумного. Значение могло приехать синхронизацией с чужой машины,
    /// поэтому чистится там же, где и числа.</summary>
    private static string Trim(string? value, int max)
    {
        var v = (value ?? "").Replace('\r', ' ').Replace('\n', ' ').Trim();
        while (v.Contains("  ", StringComparison.Ordinal)) v = v.Replace("  ", " ", StringComparison.Ordinal);
        return v.Length <= max ? v : v[..max].TrimEnd();
    }

    /// <summary>Сторона QR в миллиметрах с учётом «0 = сам».
    ///
    /// Раньше здесь была своя формула, и именно её расхождение с настоящей вёрсткой давало «увеличил
    /// QR — обрезался текст»: место под ссылку она оценивала в 2.6 строки кегля, а сам блок ссылки
    /// занимал до 3.6 строки плюс отступ, и про рамку с промежутком между блоками формула не знала
    /// вовсе. Теперь величина берётся из общей компоновки (<see cref="LabelPlanner"/>) — той самой,
    /// по которой этикетка и рисуется, так что разойтись им больше негде.</summary>
    public double EffectiveQrMm() => LabelPlanner.Plan(this, "Заголовок", "", ShowLink ? "ссылка" : "").Qr.W;

    /// <summary>Макет так, как его считает раскладка. При повороте содержимого (рулонные принтеры,
    /// см. <see cref="LabelRotation"/>) компоновка ведётся на ПЕРЕВЁРНУТОЙ стороне — ширина с высотой
    /// меняются местами, вместе с ними меняются местами поля и сдвиг, — а обратно на наклейку готовую
    /// раскладку кладёт уже отрисовка, одним поворотом.
    ///
    /// Так решено, чтобы поворот не размазывался по всей компоновке: <see cref="LabelPlanner"/> о нём
    /// не знает вовсе и остаётся тем же расчётом, который проверяется тестами.</summary>
    public LabelLayout ForDesign() => Rotation switch
    {
        LabelRotation.Clockwise90 => Swapped(OffsetYMm, -OffsetXMm),
        LabelRotation.CounterClockwise90 => Swapped(-OffsetYMm, OffsetXMm),
        _ => this,
    };

    private LabelLayout Swapped(double offX, double offY) => this with
    {
        WidthMm = HeightMm,
        HeightMm = WidthMm,
        Margins = Margins.ForRotation(Rotation),
        OffsetXMm = offX,
        OffsetYMm = offY,
        Rotation = LabelRotation.None,
    };

    private static double Clamp(double value, double min, double max) =>
        double.IsNaN(value) ? min : Math.Min(max, Math.Max(min, value));

    // ── Настройки ────────────────────────────────────────────────────────────

    /// <summary>Макет «из коробки» — та самая запись, которую даёт <c>new LabelLayout()</c>.
    ///
    /// Существует отдельным полем, чтобы <see cref="FromConfig"/> брал запасные значения ИЗ НЕЁ, а
    /// не повторял их числами и словами у себя. Повторял он их ровно до этой правки — и разошёлся:
    /// у записи «положение кода» умолчание одно, а в чтении настроек рядом стояло своё, зашитое.
    /// Пока умолчания совпадали, разницы не было видно; в тот день, когда умолчание поменяли в
    /// одном месте из двух, кнопка «Вернуть исходный макет» и первый запуск на чистой машине стали
    /// давать РАЗНЫЕ этикетки. Теперь источник один.</summary>
    public static readonly LabelLayout Default = new();

    public static LabelLayout FromConfig(ConfigService cfg) => new LabelLayout
    {
        WidthMm = cfg.LabelWidthMm(),
        HeightMm = cfg.LabelHeightMm(),
        // Поля по сторонам появились позже единого поля. Пока их не сохраняли, читается прежний
        // ключ — иначе обновление программы обнулило бы подобранный отступ и «верх обрезается»
        // вернулось бы в первый же день.
        Margins = ReadMargins(cfg),
        Rotation = ReadEnum(cfg.LabelText("label_rotation", ""), Default.Rotation),
        OffsetXMm = cfg.LabelNumber("label_offset_x_mm", Default.OffsetXMm),
        OffsetYMm = cfg.LabelNumber("label_offset_y_mm", Default.OffsetYMm),
        QrMm = cfg.LabelNumber("label_qr_mm", Default.QrMm),
        TitlePt = cfg.LabelNumber("label_title_pt", Default.TitlePt),
        CaptionPt = cfg.LabelNumber("label_caption_pt", Default.CaptionPt),
        ShowLink = cfg.LabelFlag("label_show_link", Default.ShowLink),
        ShowFrame = cfg.LabelFlag("label_show_frame", Default.ShowFrame),
        // Вид кода раньше был галочкой «фирменный QR» (label_fancy_qr). На машинах, где её снимали,
        // ключ так и лежит — новый ключ читается с оглядкой на него, иначе снятая когда-то галочка
        // молча вернулась бы после обновления.
        Style = ReadStyle(cfg.LabelText("label_qr_style", ""), cfg.LabelFlag("label_fancy_qr", Default.FancyQr)),
        QrPlace = ReadEnum(cfg.LabelText("label_qr_place", ""), Default.QrPlace),
        HeadlineText = cfg.LabelText("label_headline", Default.HeadlineText),
        ShowHeadline = cfg.LabelFlag("label_show_headline", Default.ShowHeadline),
        HeadlinePlace = ReadEnum(cfg.LabelText("label_headline_place", ""), Default.HeadlinePlace),
        HeadlineAlign = ReadEnum(cfg.LabelText("label_headline_align", ""), Default.HeadlineAlign),
        NoteText = cfg.LabelText("label_note", Default.NoteText),
        HoleText = cfg.LabelText("label_hole_text", Default.HoleText),
    }.Clamped();

    /// <summary>Поля по сторонам, а если их ни разу не сохраняли — прежнее единое поле со всех
    /// четырёх сторон.</summary>
    private static LabelMargins ReadMargins(ConfigService cfg)
    {
        var all = cfg.LabelNumber("label_margin_mm", Default.Margins.Min);
        return new LabelMargins(
            cfg.LabelNumber("label_margin_left_mm", all),
            cfg.LabelNumber("label_margin_top_mm", all),
            cfg.LabelNumber("label_margin_right_mm", all),
            cfg.LabelNumber("label_margin_bottom_mm", all));
    }

    public void SaveTo(ConfigService cfg)
    {
        var v = Clamped();
        cfg.SetLabelWidthMm(v.WidthMm);
        cfg.SetLabelHeightMm(v.HeightMm);
        cfg.SetLabelNumber("label_margin_left_mm", v.Margins.Left);
        cfg.SetLabelNumber("label_margin_top_mm", v.Margins.Top);
        cfg.SetLabelNumber("label_margin_right_mm", v.Margins.Right);
        cfg.SetLabelNumber("label_margin_bottom_mm", v.Margins.Bottom);
        // Прежний ключ единого поля пишется и дальше — тем же приёмом, что label_fancy_qr ниже: на
        // соседней машине может стоять версия, которая про стороны ещё не знает, и она обязана
        // получить хоть какое-то поле. Самое узкое: оно точно не выведет содержимое за наклейку.
        cfg.SetLabelNumber("label_margin_mm", v.Margins.Min);
        cfg.SetLabelText("label_rotation", v.Rotation.ToString());
        cfg.SetLabelNumber("label_offset_x_mm", v.OffsetXMm);
        cfg.SetLabelNumber("label_offset_y_mm", v.OffsetYMm);
        cfg.SetLabelNumber("label_qr_mm", v.QrMm);
        cfg.SetLabelNumber("label_title_pt", v.TitlePt);
        cfg.SetLabelNumber("label_caption_pt", v.CaptionPt);
        cfg.SetLabelFlag("label_show_link", v.ShowLink);
        cfg.SetLabelFlag("label_show_frame", v.ShowFrame);
        cfg.SetLabelText("label_qr_style", v.Style.ToString());
        // Старый ключ пишется и дальше: на соседней машине может стоять прежняя версия программы, а
        // конфиг у нас общий — она обязана увидеть хотя бы «фирменный или классический».
        cfg.SetLabelFlag("label_fancy_qr", v.FancyQr);
        cfg.SetLabelText("label_qr_place", v.QrPlace.ToString());
        cfg.SetLabelText("label_headline", v.HeadlineText);
        cfg.SetLabelFlag("label_show_headline", v.ShowHeadline);
        cfg.SetLabelText("label_headline_place", v.HeadlinePlace.ToString());
        cfg.SetLabelText("label_headline_align", v.HeadlineAlign.ToString());
        cfg.SetLabelText("label_note", v.NoteText);
        cfg.SetLabelText("label_hole_text", v.HoleText);

        // Дословная жалоба: «не сохраняются настройки дизайна QR-инструкции». Сохранялись они
        // исправно — их затирал ПРИЁМ общего конфига: оформление синхронизируется (и должно), а
        // применялось оно вслепую, чужое поверх своего, без единого сравнения. Подтяжка идёт сама и
        // у всех ролей, раз в sync_interval_min (по умолчанию 5 минут), а ОТПРАВЛЯЕТ конфиг только
        // администратор и по умолчанию не отправляет вовсе — то есть подобранное оформление жило
        // максимум до следующей подтяжки и уехать к коллегам не могло в принципе.
        //
        // Отметка «это правил человек здесь и вот когда» ставится ровно на те ключи, что уезжают в
        // общий конфиг: приём теперь не трогает их, пока не приедет снимок НОВЕЕ этой правки.
        // Ключи железа (размер, поля, поворот, сдвиг, принтер) отмечать не нужно — они и так
        // per-machine и приёмом никогда не читаются.
        cfg.MarkEditedHere(SyncedKeys);
    }

    /// <summary>Ключи оформления — то из <see cref="SaveTo"/>, что УЕЗЖАЕТ в общий конфиг: вид и
    /// положение кода, кегли, подпись назначения с положением и выравниванием, своя строка, подпись
    /// в центре, рамка, печать ссылки. Ровно дополнение к списку железа в
    /// ConfigSyncService.SkipSettingsKeys, и это сверяется тестом
    /// (LabelSettingsSyncTests) — забытый здесь ключ означал бы настройку, которая снова
    /// возвращается к чужой через пять минут после сохранения.</summary>
    public static readonly IReadOnlyList<string> SyncedKeys = new[]
    {
        "label_qr_mm",
        "label_title_pt",
        "label_caption_pt",
        "label_show_link",
        "label_show_frame",
        "label_qr_style",
        "label_fancy_qr",
        "label_qr_place",
        "label_headline",
        "label_show_headline",
        "label_headline_place",
        "label_headline_align",
        "label_note",
        "label_hole_text",
    };

    /// <summary>Значение перечисления из настройки. Нечитаемое значение (пусто, чужая версия
    /// программы записала неизвестное слово) — это не повод падать: берётся значение по умолчанию.</summary>
    private static T ReadEnum<T>(string value, T fallback) where T : struct, Enum =>
        Enum.TryParse<T>(value, ignoreCase: true, out var parsed) && Enum.IsDefined(parsed) ? parsed : fallback;

    private static QrStyle ReadStyle(string value, bool fancy) =>
        Enum.TryParse<QrStyle>(value, ignoreCase: true, out var parsed) && Enum.IsDefined(parsed)
            ? parsed
            : fancy ? QrStyle.Rounded : QrStyle.Classic;

    /// <summary>Поля одной строкой: одинаковые — одним числом, разные — всеми четырьмя. Читать
    /// «3 / 3 / 3 / 3» в строке состояния каждый раз незачем.</summary>
    public string MarginsCaption() => Margins.Uniform
        ? Mm(Margins.Left)
        : $"{Mm(Margins.Left)} / {Mm(Margins.Top)} / {Mm(Margins.Right)} / {Mm(Margins.Bottom)}";

    private static string Mm(double value) => value.ToString("0.##", CultureInfo.CurrentCulture);

    public string SizeCaption() =>
        $"{WidthMm.ToString("0.##", CultureInfo.CurrentCulture)} × {HeightMm.ToString("0.##", CultureInfo.CurrentCulture)}";
}
