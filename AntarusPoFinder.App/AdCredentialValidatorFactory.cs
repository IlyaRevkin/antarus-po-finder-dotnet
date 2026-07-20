using AntarusPoFinder.Core.Services;

namespace AntarusPoFinder.App;

/// <summary>Turns Настройки → Общие → «Способ проверки» (ConfigService.AdAuthMode/AdHttpUrl) into
/// the concrete IAdCredentialValidator RoleSwitchDialog should call — the one place that decides
/// between способ №1 (LdapAdCredentialValidator), способ №2 (HttpAdCredentialValidator) and "оба"
/// (CombinedAdCredentialValidator: LDAP first, HTTP only as a fallback when LDAP couldn't reach the
/// domain at all). Nothing else needs to know which mode is active.</summary>
public static class AdCredentialValidatorFactory
{
    public static IAdCredentialValidator Create(ConfigService cfg)
    {
        var mode = cfg.AdAuthMode();
        var http = new HttpAdCredentialValidator(cfg.AdHttpUrl());
        return mode switch
        {
            "http" => http,
            "both" => new CombinedAdCredentialValidator(new LdapAdCredentialValidator(), http),
            _ => new LdapAdCredentialValidator(),
        };
    }
}
