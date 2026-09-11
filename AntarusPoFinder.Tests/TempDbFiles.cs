using Microsoft.Data.Sqlite;

namespace AntarusPoFinder.Tests;

/// <summary>Удаление временных файлов SQLite после теста.
///
/// Каждый тест на базе заводит свой файл во временной папке и в конце его сносит. Само удаление
/// изредка падало с «файл занят другим процессом» — и это НЕ утечка соединения в продукте:
/// Database уже закрыт (using), пул сброшен, но Windows отпускает дескриптор не мгновенно, а
/// финализатор соединения к этому моменту мог ещё не отработать. Прогон из-за этого краснел на
/// случайном тесте, не имеющем отношения к правке, — худший вид шума: ему перестают верить.
///
/// Распараллеливание тестов уже отключено (см. AssemblyInfo.cs) — не помогло, гонка не между
/// классами, а между Dispose и файловой системой. Поэтому здесь короткие повторы: дескриптор
/// освобождается за миллисекунды.
///
/// Если и после повторов не вышло — молча оставляем файл. Временная папка не наша территория, её
/// чистит система, а ронять из-за неубранного мусора тест, который проверял слияние конфига,
/// значит врать о причине падения.</summary>
internal static class TempDbFiles
{
    public static void Delete(params string[] dbPaths)
    {
        SqliteConnection.ClearAllPools();
        foreach (var db in dbPaths)
            foreach (var f in new[] { db, db + "-wal", db + "-shm" })
                TryDelete(f);
    }

    private static void TryDelete(string path)
    {
        for (var attempt = 0; attempt < 20; attempt++)
        {
            try
            {
                if (File.Exists(path)) File.Delete(path);
                return;
            }
            catch (IOException)
            {
                // Второй заход делаем уже после финализаторов: именно недобежавший финализатор
                // соединения и держит файл, ждать его «просто так» можно долго.
                if (attempt == 0)
                {
                    GC.Collect();
                    GC.WaitForPendingFinalizers();
                    SqliteConnection.ClearAllPools();
                    continue;
                }
                Thread.Sleep(25);
            }
            catch (UnauthorizedAccessException)
            {
                Thread.Sleep(25);
            }
        }
    }
}
