using System;
using System.Globalization;

namespace AntarusPoFinder.Core.Domain;

/// <summary>Код номера ПО: 0001, 0002, … 99999, A0000, A0001, … — счётчик, который продолжает
/// считать, когда цифры кончились. Буква здесь не пометка варианта, а СЛЕДУЮЩИЙ РАЗРЯД счёта
/// (правило Ильи: «идёт 00001 00002 и тд, после 99999 идёт A0000 A0001»).
///
/// <b>Почему это кодек, а не новый тип колонки.</b> В базе <c>sw_version</c> остаётся INTEGER —
/// порядковым номером в последовательности, — а буквенно-цифровой вид считается из него и обратно.
/// На целом держатся выдача следующего номера (<c>Database.GetNextSwVersion</c> — MAX+1), сортировка
/// истории и выдачи, освобождение номера при откате; «A0000» — это просто число 100000, поэтому
/// MAX+1 сам переходит через границу и ни одно из перечисленных мест переписывать не нужно. Сделай
/// колонку строкой — и MAX+1 пришлось бы заменять на разбор каждой строки, а сортировка стала бы
/// лексической («A0000» &lt; «0002»).
///
/// <b>Раскладка разрядов.</b> Первый знак — разряд из [0-9A-Z] (A = 10, Z = 35), остальные четыре —
/// десятичные цифры: <c>ordinal = head * 10000 + tail</c>. Отсюда ПОТОЛОК последовательности —
/// <c>Z9999</c> = 359 999 (см. <see cref="Max"/>), а не «ZZZZZ»: буква занимает только старший знак.
/// Это буквальное следствие примера «после 99999 идёт A0000» — чтобы буквы шли во всех пяти знаках,
/// счёт после 99999 должен был бы продолжиться «A0000 … A9999, B0000 …» и дальше «AA000», что уже
/// другая система (смешанное основание) и другой вид номера.
///
/// <b>Ширина.</b> Цифровая часть дополняется нулями до ЧЕТЫРЁХ знаков, как было всегда
/// («2.1.0042.<b>0001</b>»), а не до пяти. Иначе каждая уже существующая версия при пересборке строки
/// превратилась бы из «0001» в «00001» — а строка версии это имя папки на сетевом диске
/// (VersionLayout), и такие папки перестали бы находиться. Последовательность от этого не меняется:
/// 1, 2, … 9999, 10000, … 99999, A0000 — ровно порядок, который задал Илья, просто первые четыре
/// разряда печатаются с ведущими нулями.</summary>
public static class SwNumberCode
{
    /// <summary>Алфавит старшего разряда: 0-9, затем A-Z. Только латиница — код становится именем
    /// папки и файла, и кириллическая «А» рядом с латинской «A» дала бы две неразличимые на глаз
    /// папки. Ввод в кириллице переводится в латиницу при разборе (<see cref="TryParse"/>).</summary>
    private const string HeadDigits = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ";

    /// <summary>Первый номер, которому нужна буква: 100000 → «A0000».</summary>
    public const int FirstLettered = 100_000;

    /// <summary>Последний номер, который эта нумерация способна изобразить: «Z9999».</summary>
    public const int Max = 359_999;

    /// <summary>Кириллические двойники латинских букв — наладчик набирает код в той раскладке, какая
    /// стоит, и «А0000» в русской раскладке обязано читаться как A0000. Буквы без латинского
    /// двойника (Б, Г, Д…) в коде невозможны и не разбираются.</summary>
    private const string CyrillicLookalikes = "АВЕКМНОРСТУХ";
    private const string LatinLookalikes    = "ABEKMHOPCTYX";

    /// <summary>Число → код. Числа ниже 100000 печатаются цифрами с дополнением до четырёх знаков
    /// (как раньше), выше — буквой старшего разряда и четырьмя цифрами.</summary>
    public static string Format(int ordinal)
    {
        if (ordinal < 0) return "0000";
        if (ordinal < FirstLettered) return ordinal.ToString("D4", CultureInfo.InvariantCulture);
        // Выход за Z9999 невозможен в жизни (номер считается внутри одной связки подтип/контроллер/HW),
        // но молча портить строку версии нельзя: печатаем числом — такой код по-прежнему разбирается
        // обратно и остаётся годным именем папки.
        if (ordinal > Max) return ordinal.ToString(CultureInfo.InvariantCulture);
        return HeadDigits[ordinal / 10_000] + (ordinal % 10_000).ToString("D4", CultureInfo.InvariantCulture);
    }

    /// <summary>Короткий вид для подписей вида «hw42.sw1»: у чисто цифровых номеров — просто число
    /// без ведущих нулей (как было до появления букв), у номеров с буквой — код целиком.</summary>
    public static string FormatShort(int ordinal) =>
        ordinal >= 0 && ordinal < FirstLettered
            ? ordinal.ToString(CultureInfo.InvariantCulture)
            : Format(ordinal);

    /// <summary>Код → число. Принимает и новый вид с буквой («A0000»), и старые чисто цифровые
    /// номера любой ширины («1», «0001», «10000») — папки с такими именами уже лежат на диске и
    /// обязаны читаться как раньше.</summary>
    public static bool TryParse(string? code, out int ordinal)
    {
        ordinal = 0;
        var s = (code ?? "").Trim();
        if (s.Length == 0) return false;

        s = s.ToUpperInvariant();
        int cyr = CyrillicLookalikes.IndexOf(s[0]);
        if (cyr >= 0) s = LatinLookalikes[cyr] + s.Substring(1);

        if (char.IsAsciiDigit(s[0]))
            return int.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out ordinal);

        int head = HeadDigits.IndexOf(s[0]);
        if (head < 10) return false; // не буква латинского алфавита
        if (s.Length != 5) return false; // буква старшего разряда — и ровно четыре цифры за ней
        if (!int.TryParse(s.AsSpan(1), NumberStyles.None, CultureInfo.InvariantCulture, out int tail)) return false;

        ordinal = head * 10_000 + tail;
        return true;
    }

    /// <summary>Разбор для полей ввода: вернуть число или null. Разбирать умеют ОБЕ сборки — номер,
    /// заведённый в бете, уезжает в общий конфиг, и у коллеги с рабочей версией он обязан остаться
    /// читаемым.</summary>
    public static int? Parse(string? code) => TryParse(code, out int n) ? n : null;
}
