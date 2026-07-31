# Интеграция с Segnetics Loader

## Назначение

Searcher выполняет интерактивную загрузку проектов в ПЛК через локальный
process API `SegneticsLoader.Automation.exe --stdio`. Окно Segnetics Loader не
открывается: Searcher показывает параметры, прогресс и журнал операции, а
production-пайплайн Loader определяет тип файла и выполняет требуемые действия.

Текущий сценарий запускается только по кнопке `Загрузить в ПЛК` на карточке
версии. Загрузка нового PSL программистом в базу Searcher сохраняет файл без
фонового запуска сборки.

## Выбор файла

Карточка представляет версию ПО, поэтому Searcher проверяет артефакты этой
версии в порядке `LFS -> PSL`:

| Файлы версии | Действие |
|---|---|
| Только LFS | Открыть диалог с готовым LFS |
| Только PSL | Открыть диалог с PSL; Loader соберёт LFS и загрузит его |
| LFS и PSL | Открыть диалог с готовым LFS |

В основном диалоге путь можно заменить вручную до запуска операции.

## Подготовка ПЛК

Единственная опция Searcher называется `Форматировать проект и обновить ядро`.
Она передаётся в запрос Automation атомарно:

| Состояние | `preparation` |
|---|---|
| Выключено | `none` |
| Включено | `formatAndUpdateFirmware` |

Адрес ПЛК, режим подключения, SSH-параметры и путь к прошивке Searcher не
передаёт. Automation читает их из настроек Segnetics Loader:

```text
%LOCALAPPDATA%\SegneticsLoader\settings.json
```

## Поиск Automation

Настройка Searcher может содержать:

1. пустое значение: используется
   `<папка Searcher>\Loader\SegneticsLoader.Automation.exe`;
2. папку Segnetics Loader: Automation ищется внутри неё;
3. путь к `SegneticsLoader.exe`: Automation ищется рядом с GUI;
4. путь к `SegneticsLoader.Automation.exe`: используется этот файл.

Если Automation отсутствует, Searcher показывает точный ожидаемый путь и не
подменяет операцию заглушкой или запуском GUI.

## Выполнение операции

1. Searcher создаёт рабочую область в
   `%LOCALAPPDATA%\AntarusPOFinder\loader\<operation>`.
2. Выбранный файл копируется в подпапку `src`.
3. Searcher запускает `SegneticsLoader.Automation.exe --stdio`.
4. В stdin передаётся запрос `deploy` с локальным путём. Для PSL также задаётся
   `outputPath` в подпапке `out` рабочей области.
5. Loader собирает и загружает PSL, затем сохраняет готовый LFS в `outputPath`
   до отправки события `completed`.
6. Searcher после `completed` публикует готовый LFS в папку выбранного исходного
   PSL. При следующей загрузке он выбирается раньше PSL.
7. События `started`, `plan`, `progress` и `log` обновляют прогресс и журнал.
8. `completed`, `failed` или `cancelled` завершает операцию в диалоге.
9. Кнопка `Остановить` передаёт JSONL-команду `cancel` в тот же процесс.

Лог Searcher сохраняется в рабочей области. Технические логи Loader находятся
в каталоге, указанном полем `error.logDirectory` события `failed`.

## Границы ответственности

Searcher отвечает за выбор версии, выбор LFS с резервным переходом к PSL,
локальную копию файла, публикацию успешно собранного LFS в папку проекта и
отображение хода операции. Segnetics Loader отвечает за распознавание артефакта,
сборку PSL, сохранение результата в локальную рабочую область Searcher,
подключение к ПЛК, форматирование, обновление ядра и загрузку.

Исходный код Loader в репозиторий Searcher не копируется. Инсталлятор Searcher
содержит опубликованный framework-dependent runtime Loader без локального
каталога `SMLogix isolated payload`. Перед изолированной сборкой Loader сам
проверяет и при необходимости синхронизирует этот каталог из установленного
SMLogix. Для Automation требуется установленный Microsoft .NET 8 Runtime x64.

## Реализация

| Компонент | Путь |
|---|---|
| Контракт UI/backend | `AntarusPoFinder.Core/Loader/LoaderContracts.cs` |
| JSONL-клиент и resolver | `AntarusPoFinder.Core/Loader/SegneticsLoaderBackend.cs` |
| Выбор backend | `AntarusPoFinder.Core/Loader/FirmwareLoaderFactory.cs` |
| Поиск LFS/PSL | `AntarusPoFinder.Core/Loader/LoaderFiles.cs` |
| Локальная рабочая область | `AntarusPoFinder.Core/Loader/LoaderWorkspace.cs` |
| Окно операции | `AntarusPoFinder.App/Views/LoaderDialog.xaml(.cs)` |
| Кнопка карточки | `AntarusPoFinder.App/Views/SearchView.xaml.cs` |

Полный внешний контракт и архитектура Automation приложены рядом:

- `docs/loader/LOADER_AUTOMATION_API.md`;
- `docs/loader/LOADER_AUTOMATION_ARCHITECTURE.md`.
