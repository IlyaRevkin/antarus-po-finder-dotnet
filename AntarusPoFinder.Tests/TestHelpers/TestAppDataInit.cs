using System;
using System.IO;
using System.Runtime.CompilerServices;

namespace AntarusPoFinder.Tests.TestHelpers;

/// <summary>Runs once when the test assembly loads — before any test executes, and critically before
/// ConfigService's `static readonly AppData` field is ever first touched (it's evaluated lazily on
/// first access, but only ONCE per process) — and points it at an isolated per-run folder under the OS
/// temp directory, via the same ANTARUS_TEST_APPDATA seam the app itself uses for live two-machine GUI
/// testing (see App.xaml.cs/AppServices doc comments).
///
/// Without this, any test that touches ConfigService.LocalFw (directly, or via LocalFirmwareCache/
/// FirmwareUpdateService — see FirmwareUpdateServiceTests) would silently read/write the REAL
/// %LocalAppData%\AntarusPOFinder\ on whatever machine runs the suite. This project holds "never touch
/// the real %LocalAppData%\AntarusPOFinder\" as a hard rule for live/manual testing; this makes the
/// automated suite honor the same rule automatically, with no per-test opt-in required.</summary>
internal static class TestAppDataInit
{
    [ModuleInitializer]
    internal static void Init()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"antarus_test_appdata_{Guid.NewGuid():N}");
        Environment.SetEnvironmentVariable("ANTARUS_TEST_APPDATA", dir);

        // Best-effort cleanup at process exit — a leftover folder from a killed test run is a stray
        // temp directory, not a correctness problem, so a failure here must never fail the test run.
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
            catch { /* best effort — see above */ }
        };
    }
}
