using System;
using System.IO;

namespace AntarusPoFinder.Tests.TestHelpers;

/// <summary>A throwaway directory tree under the OS temp folder — created up front, recursively
/// deleted on Dispose. Clears every file's attributes first (best-effort): ConfigSyncService.Export
/// deliberately marks the shared config file read-only (see its own class doc), so a plain
/// `Directory.Delete(recursive: true)` against a root that went through a real Export()/Apply() round
/// trip throws without this step — the same fixup EndToEndSyncTests' bespoke Cleanup() used to do by
/// hand.
///
/// Replaces the `NewTempRoot()` + manual `try { ... } finally { Directory.Delete(root, true); }`
/// boilerplate repeated across SchematicServiceTests, FileSystemHelpersRollbackTests,
/// InspectionCleanupServiceTests, HierarchyServiceUnknownScanTests and others:
/// <code>
/// using var root = new TempRoot();
/// ... use root.Path ...
/// </code></summary>
public sealed class TempRoot : IDisposable
{
    public string Path { get; } =
        System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"antarus_test_root_{Guid.NewGuid():N}");

    public TempRoot() => Directory.CreateDirectory(Path);

    public void Dispose()
    {
        if (!Directory.Exists(Path)) return;
        try
        {
            foreach (var f in Directory.EnumerateFiles(Path, "*", SearchOption.AllDirectories))
                File.SetAttributes(f, FileAttributes.Normal);
            Directory.Delete(Path, recursive: true);
        }
        catch
        {
            // Best-effort — a stray locked handle in a leftover temp folder must never fail a test run.
        }
    }
}
