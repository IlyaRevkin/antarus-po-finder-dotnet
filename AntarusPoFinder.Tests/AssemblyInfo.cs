using Xunit;

// Every Database-backed test opens its own temp SQLite file and cleans up with
// SqliteConnection.ClearAllPools() (a PROCESS-WIDE pool flush, not scoped to that test's own
// connection) before deleting the file. With enough Database-test classes running concurrently
// (xUnit parallelizes across test classes/collections by default), one test's ClearAllPools() can
// race a still-closing connection from a DIFFERENT test class, occasionally hitting "file in use"
// on the delete — flaky, not a real product bug. Adding two more Database-heavy test classes for
// the app-users roster (Часть 2/3) made this latent race noticeably more likely to actually fire.
// Serializing test execution avoids it; these are fast in-process SQLite tests, so this costs
// single-digit seconds overall.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
