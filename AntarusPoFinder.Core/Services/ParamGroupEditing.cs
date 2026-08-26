using AntarusPoFinder.Core.Data;
using AntarusPoFinder.Core.Domain;

namespace AntarusPoFinder.Core.Services;

/// <summary>Правка СПРАВОЧНИКА ГРУПП параметров: завести, переименовать, переставить, убрать.
///
/// До этого справочник засевался миграцией и правился только косвенно — новая группа заводилась
/// сама, если человек вписал её в предпросмотре импорта. Интерфейса не было вообще, а порядок групп
/// — это ровно то, ради чего справочник и заведён: наладчик идёт по таблице сверху вниз, и «Сброс
/// до заводских», оказавшийся в середине, обнуляет всё, что он уже выставил.
///
/// Логика живёт здесь, а не в код-behind окна настроек, потому что правил тут больше, чем кажется:
/// свёртка регистра по кириллице, перенумерация всего списка при перестановке, отказ увести
/// «Сброс до заводских» с последнего места молча.
///
/// ⚠️ <b>Регистр сворачивает .NET, а не SQLite.</b> У param_groups.name объявлен COLLATE NOCASE, и
/// полагаться на него нельзя: он сворачивает только ASCII, а все группы кириллические — «Двигатель»
/// и «двигатель» для базы РАЗНЫЕ строки (см. CLAUDE.md и Database.ConfigExchange.ImportFlatList).</summary>
public static class ParamGroupEditing
{
    /// <summary>Шаг между соседними группами. Крупный намеренно: между двумя готовыми группами
    /// можно вписать свою, не переписывая весь список.</summary>
    public const int Step = 10;

    public record Result(bool Ok, string Message)
    {
        public static Result Fail(string message) => new(false, message);
        public static Result Done(string message) => new(true, message);
    }

    /// <summary>Правит справочник только тот, кто правит и таблицы: у наладчика порядок групп на
    /// объекте разошёлся бы с тем, что видят остальные, а документ ездит в общем конфиге.</summary>
    public static bool CanEdit(string? role) => ParamTableEditing.CanEdit(role);

    /// <summary>Есть ли уже такая группа — с игнором регистра и с точностью до написания, которое
    /// лежит в базе. Возвращает именно СОХРАНЁННОЕ написание: им дальше пользуются и правка, и
    /// удаление (в SQL сравнение двоичное).</summary>
    public static string? Stored(IEnumerable<string> catalog, string? name)
    {
        var wanted = (name ?? "").Trim();
        if (wanted.Length == 0) return null;
        foreach (var known in catalog)
            if (string.Equals(known, wanted, StringComparison.OrdinalIgnoreCase))
                return known;
        return null;
    }

    public static Result Add(Database db, string? name)
    {
        var wanted = (name ?? "").Trim();
        if (wanted.Length == 0) return Result.Fail("У группы должно быть название.");
        if (wanted.Length > 60) return Result.Fail("Название группы длиннее 60 знаков — в шапку таблицы такое не встанет.");

        var existing = Stored(db.GetParamGroups(), wanted);
        if (existing is not null)
            return Result.Fail($"Группа «{existing}» уже есть в справочнике.");

        db.AddParamGroup(wanted);
        return Result.Done($"Группа параметров добавлена: {wanted}");
    }

    /// <summary>Переименовать группу. Вместе со справочником переписывается подпись в уже
    /// сохранённых строках — иначе они выпали бы из порядка показа (см.
    /// Database.RenameParamGroupInRows, там же про то, почему это правка ЛОКАЛЬНАЯ).</summary>
    public static Result Rename(Database db, string? from, string? to)
    {
        var wanted = (to ?? "").Trim();
        if (wanted.Length == 0) return Result.Fail("У группы должно быть название.");
        if (wanted.Length > 60) return Result.Fail("Название группы длиннее 60 знаков — в шапку таблицы такое не встанет.");

        var source = Stored(db.GetParamGroups(), from);
        if (source is null) return Result.Fail("Такой группы в справочнике уже нет — обновите список.");
        if (string.Equals(source, wanted, StringComparison.Ordinal)) return Result.Fail("Название не изменилось.");

        var clash = Stored(db.GetParamGroups(), wanted);
        // Смена ТОЛЬКО регистра («двигатель» → «Двигатель») — не столкновение, а ровно то, ради чего
        // правка и нужна: две записи с разным регистром в базе живут спокойно и путают всех.
        if (clash is not null && !string.Equals(clash, source, StringComparison.OrdinalIgnoreCase))
            return Result.Fail($"Группа «{clash}» уже есть — двух с одним названием быть не должно.");

        var order = db.GetParamGroupsWithOrder()
            .Where(g => string.Equals(g.Name, source, StringComparison.Ordinal))
            .Select(g => (int?)g.SortOrder).FirstOrDefault();

        db.DeleteParamGroup(source);
        db.AddParamGroup(wanted, order);
        var touched = db.RenameParamGroupInRows(source, wanted);

        return Result.Done(touched == 0
            ? $"Группа переименована: «{source}» → «{wanted}»"
            : $"Группа переименована: «{source}» → «{wanted}»; подпись поправлена в строках: {touched}");
    }

    public static Result Remove(Database db, string? name)
    {
        var source = Stored(db.GetParamGroups(), name);
        if (source is null) return Result.Fail("Такой группы в справочнике уже нет — обновите список.");

        db.DeleteParamGroup(source);
        return Result.Done($"Группа параметров убрана: {source}");
    }

    /// <summary>Сколько ЖИВЫХ строк таблиц помечено этой группой. Спрашивается перед удалением: сама
    /// подпись у строк останется (группа лежит в них текстом), но человек должен знать, что убирает
    /// не пустое место.</summary>
    public static int UsedBy(Database db, string? name)
    {
        var wanted = (name ?? "").Trim();
        if (wanted.Length == 0) return 0;
        return db.CountParamRowsInGroup(wanted);
    }

    /// <summary>Переставить группу на одну позицию вверх (delta = -1) или вниз (+1).
    ///
    /// Перестановка перенумеровывает ВЕСЬ список десятками, а не меняет два числа местами: порядок
    /// правят руками, у двух групп место вполне может совпасть (тогда их разводит название), и
    /// «поменяй местами два sort_order» на совпавших числах не делает ничего — кнопка выглядит
    /// сломанной.</summary>
    public static Result Move(Database db, string? name, int delta)
    {
        var groups = db.GetParamGroupsWithOrder().Select(g => g.Name).ToList();
        var source = Stored(groups, name);
        if (source is null) return Result.Fail("Такой группы в справочнике уже нет — обновите список.");

        var at = groups.FindIndex(g => string.Equals(g, source, StringComparison.Ordinal));
        var to = at + Math.Sign(delta);
        if (to < 0 || to >= groups.Count)
            return Result.Fail(delta < 0 ? "Эта группа и так первая." : "Эта группа и так последняя.");

        (groups[at], groups[to]) = (groups[to], groups[at]);
        Renumber(db, groups);

        return Result.Done($"Группа «{source}» переставлена {(delta < 0 ? "выше" : "ниже")}");
    }

    /// <summary>Разложить весь список по порядку десятками.</summary>
    public static void Renumber(Database db, IReadOnlyList<string> ordered)
    {
        for (var i = 0; i < ordered.Count; i++)
            db.AddParamGroup(ordered[i], (i + 1) * Step);
    }

    /// <summary>Вернуть заводской порядок (ParamGroupCatalog.Defaults) тем группам, которые в нём
    /// есть; свои группы человека остаются на своих местах, но уезжают в конец — перед «Сбросом до
    /// заводских». Кнопка нужна ровно для одного случая: список перетасовали и запутались.</summary>
    public static Result ResetToDefaults(Database db)
    {
        var defaults = ParamGroupCatalog.Defaults.ToDictionary(d => d.Name, d => d.SortOrder, StringComparer.OrdinalIgnoreCase);
        var own = db.GetParamGroups().Where(g => !defaults.ContainsKey(g)).ToList();

        foreach (var (name, order) in ParamGroupCatalog.Defaults)
            if (Stored(db.GetParamGroups(), name) is { } stored)
                db.AddParamGroup(stored, order);

        // Свои группы — между «Прочим» (900) и «Сбросом до заводских» (1000): места на девять штук,
        // и всё равно сброс останется ниже любой из них.
        var slot = 900;
        foreach (var name in own)
            db.AddParamGroup(name, Math.Min(slot += 10, 999));

        return Result.Done("Порядок групп возвращён к заводскому");
    }
}
