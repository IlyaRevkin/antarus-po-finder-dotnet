using System;
using System.IO;

namespace AntarusPoFinder.Tests.TestHelpers;

/// <summary>Allocates a throwaway SQLite file path under the OS temp folder and guarantees cleanup
/// on Dispose — including the WAL/SHM siblings, and clearing Microsoft.Data.Sqlite's native
/// connection pool first (it pools connections even after Database.Dispose(), so the underlying file
/// handle can outlive a plain `using var db = ...` block and make File.Delete throw without this).
///
/// Deliberately does NOT open the Database itself, so callers keep full control over connection
/// lifetime/order. Declare the TempDb *before* the Database that uses its Path — `using` disposes
/// locals in reverse declaration order, so the connection closes first and the pool/files get cleared
/// after:
/// <code>
/// using var dbFile = new TempDb();
/// using var db = new Database(dbFile.Path);
/// </code>
///
/// Collapses what used to be a private `NewTempDb()`/`Cleanup()` pair, copy-pasted near-verbatim at
/// the top of nearly every test file in this project (DatabaseSmokeTests, ConfigSyncTests,
/// ConflictResolutionTests, TicketTests, AppUsersDbTests, AdSessionServiceTests,
/// AppUserAuthServiceTests, LayoutFallbackDbTests all had byte-for-byte identical copies), into one
/// shared, disposal-safe type.</summary>
public sealed class TempDb : IDisposable
{
    public string Path { get; } =
        System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"antarus_test_{Guid.NewGuid():N}.db");

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (var f in new[] { Path, Path + "-wal", Path + "-shm" })
            if (File.Exists(f)) File.Delete(f);
    }
}
