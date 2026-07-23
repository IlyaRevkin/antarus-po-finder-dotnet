# Подключение реального лоадера

Документ для того, кто будет прикручивать настоящую загрузку прошивки в контроллер (форк
репозитория). В приложении сейчас лежит **заготовка**: весь обвес написан и работает, реального
лоадера нет.

## Что уже сделано и трогать не нужно

| Готово | Где |
|--------|-----|
| Диалог с параметрами, прогресс-баром, логом, кнопкой «Остановить», сохранением лога | `AntarusPoFinder.App/Views/LoaderDialog.xaml(.cs)` |
| Кнопка «Загрузить в ПЛК» в результатах поиска (подставляет найденный `.lfs`) | `AntarusPoFinder.App/Views/SearchView.xaml.cs` → `OpenLoader` |
| Локальная рабочая область: копирование исходника на машину оператора, публикация результата на диск | `AntarusPoFinder.Core/Loader/LoaderWorkspace.cs` |
| Поиск `.lfs`/`.psl` рядом с версией | `AntarusPoFinder.Core/Loader/LoaderFiles.cs` |
| Путь к лоадеру в настройках (Настройки → Общие → «Лоадер») | `ConfigService.LoaderExePath()` |
| Галочки «Форматировать»/«Обновить ядро», порт/адрес — с запоминанием последних значений | `ConfigService.LoaderFormatDefault/LoaderUpdateKernelDefault/LoaderLastTarget` |

## Что нужно сделать

1. Реализовать `IFirmwareLoaderBackend` (`AntarusPoFinder.Core/Loader/LoaderContracts.cs`).
2. Вернуть свою реализацию из `FirmwareLoaderFactory.Create`
   (`AntarusPoFinder.Core/Loader/StubFirmwareLoaderBackend.cs`) — это **единственное** место, где
   приложение решает, чем грузить.

Больше ничего менять не требуется: диалог, прогресс, лог и публикация уже завязаны на контракт.

```csharp
public interface IFirmwareLoaderBackend
{
    string Name { get; }                 // показывается оператору в диалоге
    bool IsAvailable { get; }            // false = заготовка, диалог покажет предупреждение
    string? UnavailableReason { get; }   // почему недоступен — текст для оператора
    Task<LoaderResult> RunAsync(LoaderRequest request, IProgress<LoaderProgress> progress, CancellationToken ct);
}
```

### Что приходит в `RunAsync`

| Поле `LoaderRequest` | Что в нём |
|---|---|
| `Operation` | `Flash` — залить `.lfs` в контроллер; `Build` — собрать `.psl` → `.lfs` |
| `SourcePath` | **локальный** путь к уже скопированному исходнику (никогда не сетевой) |
| `WorkspaceDir` | локальная папка запуска; результат класть в её подпапку `out\` |
| `PublishDir` | куда приложение опубликует содержимое `out\` после успеха (для `Build`); пусто — не публиковать |
| `VersionName` | имя версии, только для логов/заголовков |
| `Options.Format` | галочка «Форматировать память контроллера» |
| `Options.UpdateKernel` | галочка «Обновить ядро контроллера» |
| `Options.Target` | порт/адрес из поля диалога («COM3», IP и т.п.) — формат задаёте вы |
| `Options.Extra` | словарь на будущее, чтобы новые флаги не ломали контракт |

### Что нужно возвращать

- `progress.Report(new LoaderProgress(percent, stage, message, level))` — на каждый заметный шаг.
  `percent = -1`, если прогресс неизвестен (диалог покажет «бегущую» полосу). `stage` попадает в
  подпись под прогресс-баром, `message` — в лог. `level` (`Info`/`Warning`/`Error`/`Success`)
  задаёт цвет строки лога.
- `LoaderResult.Ok(message, artifacts)` / `LoaderResult.Fail(message)` — итог. `artifacts` — пути к
  собранным файлам (информационно; на диск публикуется содержимое `out\`).
- `ct.ThrowIfCancellationRequested()` в местах, где загрузку можно оборвать — кнопка «Остановить»
  отменяет именно этот токен. Прервали процесс — доведите до безопасного состояния сами: приложение
  про контроллер ничего не знает.
- Исключения не глотать: диалог покажет `ex.Message` в логе красным. Молча «успешно ничего не
  сделать» — худший из вариантов.

### Пример каркаса

```csharp
public sealed class RealLoaderBackend : IFirmwareLoaderBackend
{
    private readonly string _exePath;
    public RealLoaderBackend(string exePath) => _exePath = exePath;

    public string Name => "Segnetics Loader";
    public bool IsAvailable => File.Exists(_exePath);
    public string? UnavailableReason => IsAvailable ? null : $"Лоадер не найден: {_exePath}";

    public async Task<LoaderResult> RunAsync(LoaderRequest r, IProgress<LoaderProgress> progress, CancellationToken ct)
    {
        progress.Report(new LoaderProgress(5, "Подготовка", $"Файл: {r.SourcePath}"));
        // запустить _exePath с нужными аргументами, читать stdout, транслировать в progress.Report(...)
        // результат положить в Path.Combine(r.WorkspaceDir, "out")
        return LoaderResult.Ok("Загрузка завершена");
    }
}
```

## Обязательное правило: всё локально

Приложение **не клиент-серверное**. Сетевой диск компании регулярно отваливается (см. историю в
`README.md` и `NetworkPathHelper`), поэтому:

- лоадер получает только локальные пути — исходник копирует `LoaderWorkspace.Import` **до** запуска;
- всё промежуточное пишется в локальную рабочую область (`%LocalAppData%\AntarusPOFinder\loader\…`);
- на сетевой диск уезжает **только** результат успешной сборки, через `LoaderWorkspace.Publish`;
- публикация докладывает файлы в папку версии и **не** удаляет то, что там уже лежит.

Запускать лоадер напрямую по сетевому пути нельзя — обрыв сессии SMB посреди работы превращается в
наполовину записанный контроллер.

## Открытые вопросы к разработчику лоадера

Публичного headless/CLI-режима у Segnetics Loader на момент анализа (v2.6.0) не нашли —
`SegneticsLoader.exe` это GUI. Что нужно от него, чтобы интеграция стала возможной:

1. Режим запуска без интерфейса: аргументы командной строки или папка-очередь (`queue/in` →
   `queue/out`) для конвертации PSL → LFS.
2. Машиночитаемый прогресс/итог (коды возврата, строки в stdout) — иначе прогресс-бар останется
   «бегущим», а ошибки придётся вылавливать по тексту.
3. Подтверждение, требует ли сборка интерактивной сессии Windows (процесс дёргает реальный
   SMLogix.exe) — от этого зависит, можно ли вообще запускать её из-под службы/фоново.
