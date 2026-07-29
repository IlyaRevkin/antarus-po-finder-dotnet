using System;
using AntarusPoFinder.Core.Domain;

namespace AntarusPoFinder.App.ViewModels;

/// <summary>One entry in the notification history — every ShowStatus() call and every banner
/// (update/firmware-update/config-sync) appearance gets recorded here so a message that only
/// flashed on screen for a few seconds is still findable afterwards. Reopen lets a banner-backed
/// entry re-show its interactive banner (e.g. "Обновить сейчас") instead of just restating text.
/// Category is carried per-entry (Round 43 — previously entries had no category of their own, so
/// the history list couldn't show what kind of notification each row was, only the Настройки →
/// Уведомления panel elsewhere knew about categories at all).</summary>
public record NotificationEntry(string Text, DateTime When, NotificationCategory Category, Action? Reopen = null)
{
    public string WhenLabel => When.ToString("HH:mm:ss");
    public bool CanReopen => Reopen is not null;
    public string CategoryLabel => NotificationCategoryInfo.Label(Category);

    /// <summary>Reopen открывает МОДАЛЬНОЕ окно подробностей поверх (напр. «Что нового»), а не
    /// баннер-призыв на главном окне. Тогда окно истории уведомлений закрывать НЕ надо: подробности
    /// показываются сверху, и после их закрытия оператор остаётся в списке уведомлений и может
    /// открыть следующее (жалоба «после «Закрыть» в подробностях схлопывается всё окно уведомлений —
    /// приходится открывать заново, чтобы прочитать остальные»). Для баннер-reopen (UpdateBannerVisible
    /// и т.п.) флаг остаётся false: баннер рисуется на главном окне, за модальным окном истории его не
    /// видно, поэтому его как раз надо закрыть — прежнее поведение.</summary>
    public bool ReopenIsModal { get; init; }
}
