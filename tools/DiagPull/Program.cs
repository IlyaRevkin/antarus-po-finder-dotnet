using AntarusPoFinder.Core.Data;
using AntarusPoFinder.Core.Services;

// ОЧЕРЕДЬ ЖУРНАЛОВ ЗАЛИВОК: посмотреть и отправить руками.
//
//   dotnet run --project tools/DiagPull            — что лежит в очереди и куда она поедет
//   dotnet run --project tools/DiagPull -- --send  — отправить прямо сейчас
//
// В обычной работе этот инструмент не нужен: программа сама кладёт журнал каждой заливки в очередь
// и сама отправляет её ближайшим проходом синхронизации (см. LoaderDiagnostics). Он нужен для
// проверки канала и для случая «надо прямо сейчас, не дожидаясь прохода».
//
// Забирать журналы на той стороне — по SSH с домашнего сервера: /mnt/data/ticket-drop/.
// Страница приёма умеет только принимать, скачивания у неё нет вовсе.

var send = args.Contains("--send");

// Адрес и код берём из настроек установленной программы — тех же, какими пользуется она сама.
// ⚠️ База КОПИРУЕТСЯ: программа может быть запущена прямо сейчас, а SQLite — один файл с
// блокировками. Копия стоит миллисекунды и исключает любое влияние на работающее приложение.
var dbCopy = Path.Combine(Path.GetTempPath(), $"antarus-diag-{Guid.NewGuid():N}.db");
string url, code;
try
{
    File.Copy(ConfigService.DbPath, dbCopy, overwrite: true);
    using var db = new Database(dbCopy);
    var cfg = new ConfigService(db);
    url = cfg.DiagnosticsDropUrl();
    code = cfg.DiagnosticsDropCode();
}
catch (Exception ex)
{
    Console.WriteLine($"Не удалось прочитать настройки программы ({ConfigService.DbPath}): {ex.Message}");
    return 2;
}
finally
{
    try { File.Delete(dbCopy); } catch (IOException) { /* временный файл, переживём */ }
}

var queue = LoaderDiagnostics.QueueFolder(ConfigService.AppData);
var pending = LoaderDiagnostics.Pending(queue);

Console.WriteLine($"Очередь: {queue}");
Console.WriteLine($"Записей: {pending.Count}");
foreach (var file in pending.TakeLast(5))
    Console.WriteLine($"  {Path.GetFileName(file)}  ({new FileInfo(file).Length} байт)");
Console.WriteLine($"Адрес отправки: {url}");
Console.WriteLine($"Код: {(string.IsNullOrWhiteSpace(code) ? "не задан" : "задан")}");

if (!send)
{
    Console.WriteLine();
    Console.WriteLine("Отправить прямо сейчас: --send");
    return 0;
}

var result = await LoaderDiagnostics.FlushAsync(ConfigService.AppData, url, code);
if (!result.Ok)
{
    Console.WriteLine($"Отправлено: {result.Sent}, осталось: {result.Failed}. Ошибка: {result.Error}");
    return 4;
}

Console.WriteLine($"Отправлено: {result.Sent}. Файлы на сервере: /mnt/data/ticket-drop/");
return 0;
