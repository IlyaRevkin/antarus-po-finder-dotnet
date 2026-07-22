using System;
using AntarusPoFinder.App;
using AntarusPoFinder.Core.Data;
using AntarusPoFinder.Core.Services;

namespace AntarusPoFinder.Tests.TestHelpers;

/// <summary>Two independent simulated machines/profiles sharing one on-disk root — the standard shape
/// every EndToEndSyncTests scenario needs (own Database/ConfigService/HierarchyService/AppServices per
/// machine, matching how two real PCs each have their own local AppData, plus one shared folder
/// standing in for the network drive's "Конфиг" channel). Extracted from what used to be ~15 lines of
/// setup plus a bespoke Cleanup() repeated at the top of every [Fact] in EndToEndSyncTests.
///
/// Uses AppServices' test-only (Database, ConfigService, HierarchyService) constructor — plain
/// `new AppServices()` can't represent two machines in one process because ConfigService.AppData/
/// DbPath are `static readonly`, resolved once per process (see that constructor's own doc comment in
/// AntarusPoFinder.App.AppServices).</summary>
public sealed class TwoMachines : IDisposable
{
    private readonly TempDb _dbFileA = new();
    private readonly TempDb _dbFileB = new();

    public TempRoot Root { get; } = new();

    public Database DbA { get; }
    public Database DbB { get; }
    public ConfigService CfgA { get; }
    public ConfigService CfgB { get; }
    public HierarchyService HierA { get; }
    public HierarchyService HierB { get; }
    public AppServices SvcA { get; }
    public AppServices SvcB { get; }

    public TwoMachines(string loginA = "profileA", string loginB = "profileB")
    {
        DbA = new Database(_dbFileA.Path);
        DbB = new Database(_dbFileB.Path);
        CfgA = new ConfigService(DbA);
        CfgB = new ConfigService(DbB);
        HierA = new HierarchyService(DbA);
        HierB = new HierarchyService(DbB);
        SvcA = new AppServices(DbA, CfgA, HierA) { CurrentAdLogin = loginA };
        SvcB = new AppServices(DbB, CfgB, HierB) { CurrentAdLogin = loginB };
    }

    /// <summary>Most scenarios point both machines at the identical shared root right away. A test
    /// that needs to prove path-remapping across two DIFFERENT local root notations for the same
    /// physical drive (see EndToEndSyncTests.Apply_DifferentMachineRoot_RemapsFwVersionDiskPath) sets
    /// CfgA/CfgB individually instead of calling this.</summary>
    public void SetSharedRoot()
    {
        CfgA.SetRootPath(Root.Path);
        CfgB.SetRootPath(Root.Path);
    }

    public void Dispose()
    {
        DbA.Dispose();
        DbB.Dispose();
        _dbFileA.Dispose();
        _dbFileB.Dispose();
        Root.Dispose();
    }
}
