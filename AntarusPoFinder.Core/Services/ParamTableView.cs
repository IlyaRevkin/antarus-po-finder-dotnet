using AntarusPoFinder.Core.Domain;

namespace AntarusPoFinder.Core.Services;

/// <summary>Как таблицу параметров ПОКАЗАТЬ человеку — правила отсюда, а не из разметки окна.
///
/// Заведено по третьему подряд замечанию владельца: «таблица всё ещё визуально сложная для
/// восприятия параметров». До этого правились мелочи (отбивка разделов, перенос текста), и не
/// помогало, потому что беда не в оформлении, а в том, ЧТО показано. Замер по живому корпусу
/// (93 файла, 1602 строки) объяснил всё:
///
/// <list type="bullet">
/// <item>«Заводское» заполнено в 0 строках из 1602 — ни разу за всю историю;</item>
/// <item>«Только для» — в 4 из 1602, «Ед.» и «Когда нужно» — в 3 % каждая;</item>
/// <item>вчетвером они держат 1,6 % содержимого и 37 % ширины таблицы.</item>
/// </list>
///
/// Ширины хватало ровно настолько, чтобы окно 1180×720 не помещало таблицу без горизонтальной
/// прокрутки: колонкам нужно 1324 px, отведено 1147. Прокрутка убивает чтение — блокнот в окне
/// того же размера показывает 18 параметров, программа показывала 5.
///
/// Отсюда два правила, оба здесь:
/// <list type="number">
/// <item><b>Колонка появляется по факту содержимого</b> (<see cref="NeedsFactory"/>,
/// <see cref="NeedsChange"/>). Приём в программе не новый — так же ведут себя СВОИ столбцы
/// документа (ParamTableColumnEditing.Visible). Ничего не выброшено: заполнят «Заводское» —
/// колонка вернётся сама.</item>
/// <item><b>Применимость и условие — не поля строки, а ПОДЗАГОЛОВКИ блока</b>
/// (<see cref="Blocks"/>). В исходном txt они так и записаны: «&lt;&lt;&lt;[Только для ПЧ №1]»
/// и «----[Для 55 ГЦ]» — заголовок, прочитанный один раз на группу строк. Полями строки они
/// стали при разборе (и правильно: по ним идёт отбор), но ПОКАЗЫВАТЬ их надо так, как они были
/// написаны, иначе структура, бывшая в источнике, теряется.</item>
/// </list></summary>
public static class ParamTableView
{
    /// <summary>Подзаголовок внутри раздела: применимость и условие, вынесенные из строк наверх.
    ///
    /// <b><see cref="Index"/> входит в равенство не для красоты.</b> Группировка представления
    /// собирает вместе ВСЕ строки с одинаковым ключом, где бы они ни лежали. В том самом файле
    /// ESQ-230 строки без пометки идут двумя кусками — P0-02…P0-10 и P5-00…PD-02, а между ними
    /// блоки «Только для ПЧ №1» и «Для ПЧ №1 и ПЧ №2». Ключ из одного заголовка склеил бы оба
    /// куска в один и поднял P5-00 наверх, к P0-10, — то есть переписал бы порядок документа.
    /// Номер куска делает соседние куски разными группами и порядок сохраняет.</summary>
    public sealed record Block(int Index, string Title)
    {
        /// <summary>Пустой заголовок — «строки без пометки»: такой блок показывается без полосы,
        /// просто строками. Заводить над ними подпись «годится всем» незачем — это и есть
        /// большинство таблицы, и подпись превратилась бы в шум на каждом экране.</summary>
        public bool IsPlain => Title.Length == 0;

        /// <summary>Наружу — средствам доступности и автоматизации — уходит именно ToString(),
        /// и без этого они читают «Block { Index = 1, Title = … }».</summary>
        public override string ToString() => Title;
    }

    /// <summary>Заголовок блока для одной строки: применимость («Только для ПЧ №1») и условие
    /// («Для 55 Гц») вместе. Обе пометки на одной строке бывают — блок «&lt;&lt;&lt;…&gt;&gt;&gt;»
    /// внутри подгруппы «-----», — и терять одну из них нельзя.</summary>
    public static string TitleOf(ParamTableRow? row)
    {
        if (row is null) return "";
        var parts = new List<string>();
        var applicability = (row.Applicability ?? "").Trim();
        var when = (row.AppliesWhen ?? "").Trim();
        if (applicability.Length > 0) parts.Add(applicability);
        if (when.Length > 0) parts.Add(when);
        return string.Join(" · ", parts);
    }

    /// <summary>Разложить строки по блокам — по одному <see cref="Block"/> на каждую строку входа,
    /// в том же порядке. Новый блок начинается там, где сменилась пометка ИЛИ раздел: блок,
    /// перетёкший из раздела в раздел, показал бы одну полосу поверх двух заголовков разделов.
    ///
    /// Строки на вход подаются уже в порядке показа (ParamTableEditing.Ordered) — своего порядка
    /// здесь не наводится намеренно, иначе разделы переставились бы по алфавиту.</summary>
    public static List<Block> Blocks(IEnumerable<ParamTableRow>? rows)
    {
        var result = new List<Block>();
        var index = 0;
        string? previousTitle = null;
        string? previousGroup = null;

        foreach (var row in rows ?? Enumerable.Empty<ParamTableRow>())
        {
            var title = TitleOf(row);
            var group = (row.GroupName ?? "").Trim();
            if (previousTitle is null
                || !string.Equals(title, previousTitle, StringComparison.Ordinal)
                || !string.Equals(group, previousGroup, StringComparison.OrdinalIgnoreCase))
            {
                index++;
                previousTitle = title;
                previousGroup = group;
            }
            result.Add(new Block(index, title));
        }

        return result;
    }

    /// <summary>Нужна ли колонка «Заводское». По живому корпусу — не нужна никогда, но поле
    /// остаётся и в модели, и в окне правки: начнут заполнять — колонка появится сама.</summary>
    public static bool NeedsFactory(IEnumerable<ParamTableRow>? rows) =>
        (rows ?? Enumerable.Empty<ParamTableRow>()).Any(r => (r.Factory ?? "").Trim().Length > 0);

    /// <summary>Нужна ли колонка «Изменение».
    ///
    /// У ПЕРВОЙ ревизии сравнивать не с чем — колонки нет вовсе, и это не мелочь: пустая первая
    /// колонка заставляла глаз каждой строки стартовать с пустого прямоугольника, а код — самое
    /// нужное на строке — начинался только после него.</summary>
    public static bool NeedsChange(ParamTableDiff.Result? diff, IEnumerable<ParamTableRow>? shown)
    {
        if (diff is null) return false;
        return (shown ?? Enumerable.Empty<ParamTableRow>()).Any(r => diff.KindOf(r) is not null);
    }

    /// <summary>Название и описание одной строкой — как в исходнике, где они разделены тире:
    /// «Функция DI1 — Нет функции (для сигнала протечки на ПЛК)». Описание вторично и по смыслу
    /// подчинено названию; отдельной колонкой оно спорило с ним за внимание, да ещё и было
    /// оторвано от него пустыми «Заводское» и «Ед.».
    ///
    /// Возвращает обе части раздельно — цветом их различает разметка окна, а склеить их в одну
    /// строку значило бы отдать эту разницу.</summary>
    public static string Tail(ParamTableRow? row)
    {
        var description = (row?.Description ?? "").Trim();
        return description.Length == 0 ? "" : " — " + description;
    }
}
