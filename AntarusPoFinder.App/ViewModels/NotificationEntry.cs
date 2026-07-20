using System;

namespace AntarusPoFinder.App.ViewModels;

/// <summary>One entry in the notification history — every ShowStatus() call and every banner
/// (update/firmware-update/config-sync) appearance gets recorded here so a message that only
/// flashed on screen for a few seconds is still findable afterwards. Reopen lets a banner-backed
/// entry re-show its interactive banner (e.g. "Обновить сейчас") instead of just restating text.</summary>
public record NotificationEntry(string Text, DateTime When, Action? Reopen = null)
{
    public string WhenLabel => When.ToString("HH:mm:ss");
    public bool CanReopen => Reopen is not null;
}
