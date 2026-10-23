using System;

namespace AntarusPoFinder.Core.Services;

/// <summary>Коды поля <c>fw_versions.status</c> — то, что лежит в БАЗЕ, в отличие от подписей для
/// человека (их считает <see cref="FwHistoryStatus"/> по всей истории шкафа).
///
/// Состояний три, и они отвечают на три РАЗНЫХ вопроса — именно на этом разделении настаивал
/// заказчик: «помимо откатной логики сделать выключение… чтобы включить новую версию с каким-то
/// исполнением и оставить только её».
///
/// <list type="bullet">
/// <item><c>active</c> — обычная версия, участвует в поиске и в вычислении «текущей».</item>
/// <item><c>rolled_back</c> — ОТКАТ: в версии нашли критическую ошибку, её признают несостоявшейся.
///   Номер SW освобождается под следующую загрузку, папка на диске получает пометку «_ОТКАТАНО»
///   (<see cref="Core.Infrastructure.FileSystemHelpers.MarkRolledBackOnDisk"/>), откат едет к
///   коллегам как решение модерации. Это действие «версии не должно было быть».</item>
/// <item><c>disabled</c> — ВЫКЛЮЧЕНИЕ: версия нормальная, но сейчас выдавать её не нужно — залили
///   лишнее, раздвоили прошивку, либо из нескольких копий под разные исполнения в работе должна
///   остаться одна. Номер не освобождается, файлы не переименовываются, включается обратно одной
///   кнопкой. Выключать можно ЛЮБУЮ версию по отдельности — в этом всё отличие от отката, который
///   на практике требовал откатывать всю цепочку от последней к первой.</item>
/// </list>
///
/// Почему выключение — ещё одно значение status, а не своя колонка: все выборки выдачи (поиск,
/// история, заливка в ПЛК, очередь модерации) уже отбирают строки условием
/// <c>(status IS NULL OR status='active')</c>, и новое значение автоматически исчезает из них всех.
/// Отдельный флаг пришлось бы дописывать в полтора десятка запросов, и забытый означал бы «выключил,
/// а оно всё равно выдаётся». Синхронизация статуса тоже уже сделана «по времени правки»
/// (<c>status_changed_at</c>, см. Database.ConfigExchange), а не монотонно, поэтому включение
/// обратно доезжает до коллег само и не отменяется их устаревшим снимком.</summary>
public static class FwStatus
{
    public const string Active = "active";
    public const string RolledBack = "rolled_back";
    public const string Disabled = "disabled";

    /// <summary>Пустое значение в старых строках означает «active» — так же, как в SQL-условиях.</summary>
    public static string Normalize(string? status) =>
        string.IsNullOrWhiteSpace(status) ? Active : status.Trim();

    /// <summary>Версия не выдаётся: откатана или выключена. Одно место вместо россыпи сравнений
    /// <c>!= "rolled_back"</c>, каждое из которых пришлось бы вспомнить при добавлении состояния.</summary>
    public static bool IsHidden(string? status) => Normalize(status) is RolledBack or Disabled;

    public static bool IsDisabled(string? status) => Normalize(status) == Disabled;

    public static bool IsRolledBack(string? status) => Normalize(status) == RolledBack;
}
