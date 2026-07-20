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

    public AppServices()
    {
        Db = new Database(ConfigService.DbPath);
        Cfg = new ConfigService(Db);
        Hierarchy = new HierarchyService(Db);
        Schematics = new SchematicService();
    }
}
