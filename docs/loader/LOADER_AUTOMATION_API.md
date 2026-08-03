# Automation API Segnetics Loader

## Статус

Документ описывает текущую реализацию локального process API Segnetics Loader.
Версия протокола: `1`.

Исполняемый файл API входит в framework-dependent дистрибутив:

```text
SegneticsLoader.Automation.exe
```

API запускает production-пайплайн Loader без открытия GUI. Один процесс
обслуживает одну операцию и завершается после терминального события.

## Команды запуска

Получение возможностей:

```powershell
& .\SegneticsLoader.Automation.exe --capabilities
```

Выполнение операции через стандартные потоки:

```powershell
& .\SegneticsLoader.Automation.exe --stdio
```

`--capabilities` возвращает одну JSON-строку:

```json
{"protocolVersion":1,"actions":["deploy","build","cancel"],"artifactTypes":["psl","lfs","zop","elf"],"buildArtifactTypes":["psl"],"preparations":["none","formatAndUpdateFirmware"],"events":["started","plan","progress","log","completed","failed","cancelled"]}
```

Коды завершения процесса:

| Код | Значение |
|---:|---|
| `0` | Операция завершена успешно или выведены capabilities |
| `1` | Операция завершена ошибкой |
| `2` | Операция отменена |
| `64` | Некорректные аргументы командной строки |

## Транспорт

Режим `--stdio` использует UTF-8 JSON Lines:

- первая строка `stdin` содержит запрос `deploy` или `build`;
- каждая строка `stdout` содержит одно событие протокола;
- `stdout` используется только для JSONL-событий;
- после запуска операции `stdin` остаётся доступным для команды `cancel`;
- каждое событие немедленно записывается и сбрасывается в `stdout`;
- операция выдаёт ровно одно терминальное событие: `completed`, `failed` или
  `cancelled`.

Необязательные поля со значением `null` в JSON не записываются.

## Запрос операции

```json
{"protocolVersion":1,"operationId":"deploy-20260729-001","action":"deploy","artifactPath":"C:\\Projects\\project.lfs","preparation":"none"}
```

| Поле | Тип | Назначение |
|---|---|---|
| `protocolVersion` | number | Обязательное значение `1` |
| `operationId` | string | Идентификатор операции и имя каталога её логов |
| `action` | string | Первая команда процесса: `deploy` или `build` |
| `artifactPath` | string | Путь к существующему локальному файлу проекта |
| `preparation` | string | Для `deploy`: `none` или `formatAndUpdateFirmware`; пустое поле эквивалентно `none` |
| `outputPath` | string | Для `build` и `deploy` PSL: необязательный путь выходного `.lfs` |
| `overwriteOutput` | boolean | Разрешение перезаписи указанного выходного файла; по умолчанию `true` |

`operationId` после удаления внешних пробелов должен содержать от 1 до 128
символов, быть допустимым именем каталога Windows и отличаться от `.` и `..`.

Параметры подключения, учётные данные и путь к прошивке не входят в запрос.
Automation читает их из настроек Loader:

```text
%LOCALAPPDATA%\SegneticsLoader\settings.json
```

Используются сохранённые режим подключения, IP-адрес, SSH-учётные данные,
сетевой адаптер, путь к `firmware.frw` и режим загрузки ELF.

## Сборка PSL в LFS

Действие `build` запускает только изолированную сборку SMLogix и не подключается
к ПЛК. Поддерживается входной файл `.psl`.

```json
{"protocolVersion":1,"operationId":"build-20260729-001","action":"build","artifactPath":"C:\\Projects\\project.psl"}
```

Если `outputPath` не задан, результат записывается рядом с исходным PSL с тем же
именем и расширением `.lfs`. Если `overwriteOutput` не задан, существующий LFS
перезаписывается.

```json
{"protocolVersion":1,"operationId":"build-20260729-002","action":"build","artifactPath":"C:\\Projects\\project.psl","outputPath":"D:\\Builds\\project.lfs","overwriteOutput":false}
```

Для `build` поле `preparation` должно отсутствовать или иметь значение `none`.
Параметры подключения к ПЛК и прошивки в этой операции не используются.
Событие `plan` содержит один шаг `buildPslToLfs`, режим
`pslBuildMode: "isolated"` и итоговый `outputPath`. Успешное событие
`completed` возвращает путь созданного LFS в `outputPath`.

## Сохранение LFS при загрузке PSL

Для действия `deploy` с входным PSL можно передать `outputPath`:

```json
{"protocolVersion":1,"operationId":"deploy-psl-20260730-001","action":"deploy","artifactPath":"C:\\Projects\\project.psl","preparation":"none","outputPath":"C:\\Workspace\\out\\project.lfs","overwriteOutput":true}
```

Loader собирает LFS во временном каталоге, загружает его в ПЛК и только после
успешной загрузки атомарно сохраняет файл в `outputPath`. Событие `completed`
отправляется после сохранения, поэтому вызывающее приложение может сразу читать
указанный файл. `plan` и `completed` содержат итоговый `outputPath`.

Если `outputPath` отсутствует, временный LFS используется только для загрузки и
удаляется при очистке операции. Это штатный успешный сценарий. Поля сохранения
в `deploy` применяются только к PSL-проектам.

## Поддерживаемые артефакты

Тип определяется самим Loader по расширению, сигнатуре и содержимому файла.

| Тип | План операции |
|---|---|
| `.psl` | Сборка изолированным SMLogix в LFS, затем загрузка LFS |
| `.lfs` | Проверка LFS-сигнатуры и загрузка LFS |
| `.zop` | Извлечение, загрузка LFS, ожидание готовности проекта, загрузка ELF в companion-режиме |
| ELF без расширения | Проверка ELF и загрузка в режиме, определённом runtime metadata и настройками Loader |

Для PSL событие `plan` содержит `pslBuildMode: "isolated"`. Автоматическая
проверка файлов изолированного SMLogix выполняется согласно сохранённой
настройке Loader.

Для прямого ELF событие `plan` содержит итоговый `elfDeployMode`:
`companion`, `vendorStandalone` или `native`. ELF внутри ZOP использует
`companion`.

## Подготовка ПЛК

### `none`

Loader сразу выполняет план выбранного артефакта.

### `formatAndUpdateFirmware`

Перед загрузкой артефакта Loader выполняет единый сценарий:

1. Использует сохранённое USB-подключение и путь к `firmware.frw`.
2. Очищает ELF/ZOP-часть проекта.
3. Обновляет ядро с recovery-форматированием PSL/LFS-части.
4. После маркера завершения recovery до трёх минут ожидает окончательную
   загрузку ПЛК и доступность SSH, сохраняя DHCP активным при дополнительных
   перезагрузках.
5. Выполняет план загрузки выбранного артефакта.

При старте из Rockchip MaskRom или уже активного recovery очистка ELF/ZOP
выполняется после восстановления SSH. Сценарий поддерживает USB-режим;
выбранный Ethernet-режим завершается событием `failed` с кодом
`PREPARATION_FAILED` до изменения ПЛК.

После подтверждённого маркера завершения recovery окончательная загрузка
обрабатывается циклом DHCP/SSH; новая recovery-сессия на этой фазе не
запускается.

Пример запроса:

```json
{"protocolVersion":1,"operationId":"deploy-20260729-002","action":"deploy","artifactPath":"C:\\Projects\\project.lfs","preparation":"formatAndUpdateFirmware"}
```

## События

Обычная последовательность:

```text
started
plan
progress / log
completed | failed | cancelled
```

### `started`

Подтверждает приём первой строки запроса.

```json
{"protocolVersion":1,"operationId":"deploy-20260729-001","event":"started"}
```

### `plan`

Возвращает распознанный артефакт и фактическую последовательность шагов. Для
`build` также возвращает итоговый путь выходного файла.

```json
{"protocolVersion":1,"operationId":"deploy-20260729-001","event":"plan","artifactPath":"C:\\Projects\\project.zop","artifactType":"zop","steps":["extractZop","deployLfs","waitForProjectReady","deployElf"],"elfDeployMode":"companion"}
```

```json
{"protocolVersion":1,"operationId":"build-20260729-001","event":"plan","artifactPath":"C:\\Projects\\project.psl","artifactType":"psl","steps":["buildPslToLfs"],"pslBuildMode":"isolated","outputPath":"C:\\Projects\\project.lfs"}
```

Поддерживаемые имена шагов:

```text
formatAndUpdateFirmware
buildPslToLfs
extractZop
deployLfs
waitForProjectReady
deployElf
```

### `progress`

Передаёт общий процент выполнения от `0` до `100`. Поле `message` может
отсутствовать при обновлении только числового значения.

```json
{"protocolVersion":1,"operationId":"deploy-20260729-001","event":"progress","percent":45,"message":"Загружаю LFS"}
```

### `log`

Передаёт строку журнала. Поддерживаемые уровни: `info`, `warning`, `error`.

```json
{"protocolVersion":1,"operationId":"deploy-20260729-001","event":"log","level":"warning","message":"Настройки Ethernet не применены"}
```

### `completed`

Успешное терминальное событие. `outputPath` и `warnings` записываются при их
наличии.

```json
{"protocolVersion":1,"operationId":"deploy-20260729-001","event":"completed","message":"Проект загружен","outputPath":"C:\\Projects\\project.lfs","warnings":["Настройки Ethernet не применены"]}
```

### `failed`

Терминальное событие ошибки.

```json
{"protocolVersion":1,"operationId":"deploy-20260729-001","event":"failed","error":{"code":"DEPLOY_FAILED","message":"Не удалось загрузить проект","details":"Техническое описание","logDirectory":"C:\\Users\\User\\AppData\\Local\\SegneticsLoader\\logs\\automation\\deploy-20260729-001"}}
```

`error.message` содержит пользовательское сообщение. `error.details` содержит
технические сведения. `error.logDirectory` указывает каталог диагностики
операции.

### `cancelled`

Терминальное событие отменённой операции.

```json
{"protocolVersion":1,"operationId":"deploy-20260729-001","event":"cancelled","message":"Операция отменена"}
```

## Отмена

Команда отмены отправляется второй строкой в `stdin` того же процесса и должна
содержать `operationId` выполняемой операции:

```json
{"protocolVersion":1,"operationId":"deploy-20260729-001","action":"cancel"}
```

Строка с другой версией протокола, другим действием или другим `operationId`
создаёт событие `log` уровня `warning` и не меняет выполняемую операцию.

## Коды ошибок

| Код | Значение |
|---|---|
| `INVALID_REQUEST` | Некорректный запрос, версия протокола, путь или режим подготовки |
| `UNSUPPORTED_ARTIFACT` | Формат, сигнатура или содержимое артефакта не поддерживаются |
| `ARTIFACT_NOT_FOUND` | Файл проекта отсутствует |
| `ISOLATED_SMLOGIX_NOT_READY` | Изолированный SMLogix не готов к сборке PSL |
| `LOADER_BUSY` | GUI или другой Automation-процесс уже выполняет операцию |
| `PREPARATION_FAILED` | Форматирование проекта или обновление ядра завершилось ошибкой |
| `BUILD_FAILED` | Сборка PSL в LFS завершилась ошибкой |
| `OUTPUT_PUBLISH_FAILED` | Проект загружен в ПЛК, но собранный LFS не удалось сохранить в `outputPath` |
| `CONNECTION_FAILED` | Подключение к ПЛК завершилось ошибкой |
| `PLC_MODEL_MISMATCH` | Модель проекта не соответствует подключённому ПЛК |
| `DEPLOY_FAILED` | Загрузка артефакта завершилась ошибкой |
| `CANCELLED` | Операция отменена |
| `INTERNAL_ERROR` | Внутренняя необработанная ошибка Automation |

GUI Loader и Automation используют общую межпроцессную блокировку. Одновременно
выполняется одна операция Loader.

## Диагностические файлы

Для каждой операции создаётся каталог:

```text
%LOCALAPPDATA%\SegneticsLoader\logs\automation\<operationId>\
```

В зависимости от выполненных этапов он содержит:

| Файл | Содержимое |
|---|---|
| `requests.jsonl` | Исходный запрос и команда отмены |
| `events.jsonl` | Все события, отправленные через `stdout` |
| `result.json` | Полный результат операции загрузки или сборки |
| `lfs-deploy.log` | Технический вывод загрузки LFS |
| `elf-deploy.log` | Технический вывод загрузки ELF |
| `process.log` | Вывод дочернего процесса сборки |
| `error.txt` | Технические сведения об ошибке или исключении |

Запись диагностических файлов выполняется независимо от передачи событий через
`stdout` и не изменяет результат операции при недоступности каталога логов.

## Версионирование

Клиент определяет поддерживаемый контракт командой `--capabilities`.
Несовпадение `protocolVersion` в запросе завершается кодом
`INVALID_REQUEST`. Новые необязательные поля могут добавляться в пределах
версии протокола без изменения существующих полей.
