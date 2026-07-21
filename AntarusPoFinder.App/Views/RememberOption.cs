using System.Collections.Generic;

namespace AntarusPoFinder.App.Views;

public record RememberOption(string Key, string Label);

/// <summary>Items for the "Запомнить вход на этом компьютере" combo shared by RoleSwitchDialog and
/// AdStartupLoginDialog — every option is a personal choice made by whoever is logging in right
/// now, not an administrator setting (that's just the "default" option's number of days). Key
/// "default"/"always" are sentinels; every other key is itself a parseable day count.</summary>
public static class RememberOptions
{
    public const string DefaultKey = "default";
    public const string AlwaysKey = "always";

    public static List<RememberOption> All(int defaultDays) => new()
    {
        new(DefaultKey, $"Как задано администратором ({defaultDays} дн.)"),
        new("1", "1 день"),
        new("7", "7 дней"),
        new("30", "30 дней"),
        new(AlwaysKey, "Всегда — не спрашивать, пока не выйду вручную"),
    };
}
