using System;
using AntarusPoFinder.Core.Data;
using AntarusPoFinder.Core.Services;

namespace AntarusPoFinder.App;

/// <summary>Shared service instances passed into every page's ViewModel — the WPF equivalent of
/// the Python app's "pages hold a back-reference to MainWindow" pattern, but as plain constructor
/// injection instead of a God-object reference.</summary>
public class AppServices
{
    public Database Db { get; }
    public ConfigService Cfg { get; }
    public HierarchyService Hierarchy { get; }
    public SchematicService Schematics { get; }

    /// <summary>Set once per session on a successful AD login (see RoleSwitchDialog.AdAuth_Click),
    /// via either the AD-group or the app-roster path — null for the whole session if the operator
    /// only ever picked a role through the plain shared-password dialog, in which case CurrentUserName
    /// keeps falling back to the Windows/machine account exactly as before this existed.</summary>
    public string? CurrentAdLogin { get; set; }

    /// <summary>"Кто сейчас действует" for every CreatedBy/exported_by/"кем зарезервирован" field —
    /// the AD login if this session authenticated via AD, otherwise the shared Windows/machine
    /// account name that was the only "who" available before AD login threaded an identity through.
    /// Root cause this fixes: two colleagues both logging in via AD got the right roles, but every
    /// downstream audit field still said "наладка3" (the shared PC account), not "revkin.i".</summary>
    public string CurrentUserName => CurrentAdLogin ?? Environment.UserName;

    public AppServices()
    {
        Db = new Database(ConfigService.DbPath);
        Cfg = new ConfigService(Db);
        Hierarchy = new HierarchyService(Db);
        Schematics = new SchematicService();
    }
}
