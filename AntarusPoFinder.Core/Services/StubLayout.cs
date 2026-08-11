using System;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AntarusPoFinder.Core.Services;

/// <summary>Как выглядит страница-заглушка «Инструкция в разработке» (см. <see cref="InstructionStub"/>).
///
/// Заглушка — не техническая затычка, а то, что реально увидит заказчик, наведя телефон на наклейку
/// на шкафу: наклейку клеят задолго до того, как инструкцию допишут, и до этого момента по
/// постоянной ссылке открывается ровно эта страница. Поэтому её текст и вид — вопрос не разработки,
/// а оформления, и решаться он должен в программе, а не правкой кода.
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

    public string Title { get; init; } = InstructionStub.Text;

    public string Hint { get; init; } =
        "Документ ещё не готов. Файл заменится настоящей инструкцией, как только её приложат к этой версии.";

    /// <summary>Нижняя строка — обычно кто выпустил и к чему это относится. Пусто — не печатается.</summary>
    public string Footer { get; init; } = VersionPlaceholder;

    public double TitleSize { get; init; } = 0.06;
    public double HintSize { get; init; } = 0.022;
    public double FooterSize { get; init; } = 0.018;

    /// <summary>Рамка по краю страницы: на печати помогает увидеть, что лист не обрезан.</summary>
    public bool ShowFrame { get; init; }

    /// <summary>Серый тон подсказки и подписи, 0 — чёрный, 255 — белый. Заголовок всегда чёрный:
    /// он обязан читаться и на плохой печати, и с экрана телефона под углом.</summary>
    public int MutedTone { get; init; } = 0x66;

    public static StubLayout Default { get; } = new();

    /// <summary>Текст с подставленным номером версии. Версия неизвестна (общая папка контроллера) —
    /// подстановка убирается вместе с лишними пробелами, а не оставляет «Версия {версия}».</summary>
    public string Fill(string? text, string? versionRaw)
    {
        if (string.IsNullOrEmpty(text)) return "";
        var version = (versionRaw ?? "").Trim();
        return text.Replace(VersionPlaceholder, version, StringComparison.Ordinal).Trim();
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
        Title = string.IsNullOrWhiteSpace(Title) ? InstructionStub.Text : Title.Trim(),
        TitleSize = Clamp(TitleSize, 0.02, 0.15),
        HintSize = Clamp(HintSize, 0.01, 0.08),
        FooterSize = Clamp(FooterSize, 0.008, 0.06),
        MutedTone = Math.Clamp(MutedTone, 0, 200),
    };

    private static double Clamp(double value, double min, double max) =>
        double.IsFinite(value) ? Math.Clamp(value, min, max) : min;
}
