using System.Security.Cryptography;
using System.Text;

namespace AntarusPoFinder.Core.Services;

/// <summary>Что зашито в QR-код конкретной версии прошивки — и как понять, что этот ответ устарел.
///
/// <b>Зачем это понадобилось.</b> Просьба была такая: «чтобы все qr на все прошивки которые есть
/// создавались в какой-нибудь отдельной таблице и могли генерироваться не в зависимости от
/// интернета, а при обновлении qr или выходе новой прошивки соответственно было бы обновление или
/// добавление или удаление в соответствии с логикой действия».
///
/// <b>Сама картинка интернета никогда и не требовала</b> — код рисуется на месте (QRCoder + QrArt),
/// ни один сервис «сделай мне QR» в проекте не дёргается. Требовала СЕТЬ другая половина: чтобы
/// узнать, ЧТО зашивать, окно наклейки шло на сетевой диск искать файл инструкции
/// (<see cref="InstructionDocResolver"/> обходит папку), и при отвалившемся диске кнопка «QR
/// инструкции» не могла показать ничего. Поэтому хранить надо не картинку, а ответ на вопрос
/// «что зашивать».
///
/// <b>Почему ссылка с отпечатком, а не готовая картинка.</b>
/// <list type="number">
/// <item><description>Картинка — производная от трёх вещей: ссылки, подписи в центре и вида кода
/// (<see cref="LabelLayout"/>: фирменный/классический, форма клетки, текст плашки). Вид правится в
/// окне наклейки на ходу, и сохранённый PNG после первой же такой правки врал бы. Ссылка же —
/// факт: она либо та, либо другая.</description></item>
/// <item><description>Строка в базе весит десятки байт, картинка — десятки килобайт на КАЖДУЮ
/// версию; база лежит в профиле и целиком уезжает в бэкапы.</description></item>
/// <item><description>Отрисовка по готовой ссылке занимает миллисекунды и уже реализована
/// (<see cref="QrHoleText"/> + App.QrArt). Второй реализации QR в проекте быть не должно, а
/// сохранённый растр — это ровно вторая реализация, только протухающая.</description></item>
/// </list>
///
/// <b>Почему ленивая перерисовка по отпечатку, а не «ловить события».</b> Ссылка зависит от
/// instructions_path версии, базового веб-адреса, корня диска и справочника написаний — четыре
/// независимых источника, которые правятся из полутора десятков мест (загрузка, перезаливка
/// инструкции, отвязка документа, откат версии, приём чужого конфига, смена буквы диска). Повесить
/// обновление на каждое из них — значит гарантированно забыть про одно: ровно так протухали бы
/// наклейки, которые уже наклеены на шкаф. Вместо этого записи хранят <see cref="Fingerprint"/>
/// своих входных данных: отпечаток считается НА ЛЕТУ из полей, которые все лежат в базе и
/// настройках (на диск за ним ходить не надо), сравнивается с сохранённым, и расходится ровно тогда,
/// когда ссылка действительно изменилась. Любое событие, про которое мы не подумали, всё равно
/// меняет отпечаток.</summary>
public static class FirmwareQrCodes
{
    /// <summary>Чем оказался адрес в коде. Важно для показа: <see cref="Path"/> — это сетевой путь
    /// вида <c>\\сервер\шара\…</c>, он откроется с компьютера, но не с телефона, и человеку об этом
    /// говорят прямо. <see cref="None"/> — зашивать нечего.</summary>
    public enum PayloadKind { None, Url, Path }

    public static string KindName(PayloadKind kind) => kind switch
    {
        PayloadKind.Url => "url",
        PayloadKind.Path => "path",
        _ => "none",
    };

    public static PayloadKind KindFrom(string? name) => name switch
    {
        "url" => PayloadKind.Url,
        "path" => PayloadKind.Path,
        _ => PayloadKind.None,
    };

    /// <summary>Что зашить в код этой версии. <paramref name="Fingerprint"/> — отпечаток ВХОДНЫХ
    /// данных, по которому видно, что сохранённую запись пора перестроить.</summary>
    public sealed record Payload(string Content, PayloadKind Kind, string Fingerprint)
    {
        public bool HasContent => Content.Length > 0;
        public static readonly Payload Empty = new("", PayloadKind.None, "");
    }

    /// <summary>Исходные данные для одной версии — ровно то, из чего получается ссылка, и ничего
    /// лишнего. Всё это лежит в базе и настройках: ни одно поле не требует обхода диска, поэтому
    /// отпечаток считается и при отвалившемся сетевом диске.
    ///
    /// <paramref name="SourcePath"/> — файл, НА КОТОРЫЙ строится ссылка. <paramref name="InstructionPath"/>
    /// — то, что записано у версии в <c>fw_versions.instructions_path</c>. Чаще всего это одно и то
    /// же, но не всегда: открытое окно наклейки видит живой диск и знает точный документ (из папки
    /// «Инструкция» — конкретный .pdf, а не папку), фоновый проход на диск не ходит и берёт
    /// записанное. Поля два, и ОБА входят в отпечаток:
    /// <list type="bullet">
    /// <item><description>без <paramref name="SourcePath"/> проход затирал бы точную ссылку окна
    /// своей приблизительной на каждом тике;</description></item>
    /// <item><description>без <paramref name="InstructionPath"/> перепривязка документа у версии
    /// («отвязали старый, привязали новый») не меняла бы отпечаток у строки, собранной по старому
    /// файлу, — и наклейка продолжала бы вести на снятый документ.</description></item>
    /// </list>
    ///
    /// Пустой <paramref name="SourcePath"/> — документа у версии нет, и это тоже осмысленное
    /// состояние: отпечаток у такой версии свой, и когда инструкцию привяжут, он изменится сам.</summary>
    public sealed record Inputs(
        string SourcePath,
        string InstructionPath,
        string BaseUrl,
        string DiskRoot,
        string TranslitSignature)
    {
        /// <summary>Данные, собранные без похода на диск: ссылка строится на то, что записано у
        /// версии. Так работает фоновый проход и так заводятся коды «всех прошивок, которые есть».</summary>
        public static Inputs FromRecord(string instructionPath, string baseUrl, string diskRoot, string translit) =>
            new(instructionPath, instructionPath, baseUrl, diskRoot, translit);
    }

    /// <summary>Отпечаток входных данных — короткий хэш, а не склеенная строка: путь к инструкции на
    /// сетевой шаре легко переваливает за две сотни знаков, а хранить и сравнивать надо на каждую
    /// версию. SHA-256 здесь не про безопасность, а про «одинаковые входы — одинаковая строка»;
    /// шестнадцати байт хватает с запасом при тысячах записей.
    ///
    /// Подпись в центре кода (<see cref="LabelLayout.HoleText"/>) в отпечаток НЕ входит намеренно:
    /// она часть внешнего вида наклейки, а не адреса. Сменив её, человек меняет то, как код
    /// нарисован, но не то, куда он ведёт, — перестраивать записи из-за этого незачем.</summary>
    public static string Fingerprint(Inputs inputs)
    {
        var raw = string.Join("\u001f",
            Normalize(inputs.SourcePath),
            Normalize(inputs.InstructionPath),
            Normalize(inputs.BaseUrl),
            Normalize(inputs.DiskRoot),
            inputs.TranslitSignature ?? "");
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(raw));
        return Convert.ToHexString(hash, 0, 16).ToLowerInvariant();
    }

    /// <summary>Пути с диска приходят то с обратными слешами, то с прямыми, то с хвостовым
    /// разделителем, а регистр на Windows значения не имеет. Без приведения к одному виду отпечаток
    /// расходился бы на пустом месте и гонял перестройку каждый запуск.</summary>
    private static string Normalize(string? value) =>
        (value ?? "").Trim().Replace('\\', '/').TrimEnd('/').ToLowerInvariant();

    /// <summary>Собрать содержимое кода. Порядок предпочтений — тот же, что и у окна наклейки, и
    /// другим быть не может: наклейка, напечатанная из сохранённой записи, обязана совпадать с
    /// наклейкой, напечатанной с живого диска.
    /// <list type="number">
    /// <item><description>веб-ссылка на выложенный документ — единственное, что открывается с
    /// телефона (<see cref="LabelLinkBuilder.BuildUrl"/>);</description></item>
    /// <item><description>сетевой путь к самому файлу — если веб-адрес не задан или файл лежит вне
    /// диска прошивок;</description></item>
    /// <item><description>ничего — документа у версии нет.</description></item>
    /// </list>
    ///
    /// <paramref name="toUnc"/> — перевод локального пути в сетевой (<c>Z:\…</c> → <c>\\сервер\…</c>);
    /// он живёт в приложении, поэтому передаётся сюда функцией. null — путь берётся как есть.
    ///
    /// Расширение приводится к ИТОГОВОМУ (<see cref="InstructionPublisher.AsPublishedName"/>):
    /// инструкция в Word уезжает на хостинг собранным PDF, и ссылка на .docx вела бы в пустоту.</summary>
    public static Payload Build(Inputs inputs, TranslitMap? translit, Func<string, string>? toUnc = null)
    {
        var fingerprint = Fingerprint(inputs);
        var file = (inputs.SourcePath ?? "").Trim();
        if (file.Length == 0) return new Payload("", PayloadKind.None, fingerprint);

        var published = InstructionPublisher.AsPublishedName(file);
        if (LabelLinkBuilder.BuildUrl(inputs.BaseUrl, inputs.DiskRoot, published, translit) is { } url)
            return new Payload(url, PayloadKind.Url, fingerprint);

        var unc = toUnc is null ? file : (toUnc(file) ?? file);
        return new Payload(unc, PayloadKind.Path, fingerprint);
    }

    /// <summary>По-русски: что человеку сказать про сохранённый код. Отдельно от построения, потому
    /// что одна и та же запись объясняется по-разному — свежепостроенная и взятая из базы при
    /// недоступном диске.</summary>
    public static string Explain(PayloadKind kind, string content) => kind switch
    {
        PayloadKind.Url => $"В QR: {content}",
        PayloadKind.Path => "Веб-адрес диска инструкций не задан или файл лежит вне его, поэтому в QR — "
                            + $"сетевой путь: с компьютера откроется, с телефона нет.\nВ QR: {content}",
        _ => "Файл инструкции не найден — печатать нечего.",
    };
}
