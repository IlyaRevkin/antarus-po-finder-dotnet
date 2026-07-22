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
}
