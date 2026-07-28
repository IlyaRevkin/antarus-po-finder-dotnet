# Комбинированная загрузка PSL/LFS + ELF/ZOP

## Назначение

Штатный vendor/template запуск ELF подменяет текущий проект ПЛК:
останавливает сервисы PSL/LFS-проекта, переключает служебные файлы `/projects`
на ELF как самостоятельный проект и из-за этого может останавливать веб-интерфейс
и runtime PSL/LFS.

Комбинированный режим нужен для другого сценария: PSL/LFS остаётся основным
проектом ПЛК, а ELF запускается рядом с ним как дополнительный companion-процесс.
Это позволяет отлаживать связку PSL + ELF: переустанавливать PSL/LFS-проект,
не теряя companion-бинарник и не переводя ПЛК в standalone ELF-режим.

## Термины

Комбинированный режим нужен для проекта, где штатный PSL/LFS остаётся основным
проектом ПЛК, а ELF запускается поверх него как companion-процесс.

- PSL/LFS - основной проект ПЛК.
- ELF - companion-бинарник поверх основного проекта.
- ZOP - контейнер Loader, содержащий LFS и один ELF payload.
- standalone ELF - ELF, загруженный старой vendor/template механикой как
  самостоятельный активный проект.
- managed-блок - участок `/projects/start.after` между маркерами Loader.

В комбинированном режиме запрещено:

- менять `/projects/.template_active_project`;
- вызывать template-скрипт `./start` из ELF-проекта;
- останавливать `logix`, `sqld`, `mbs`, `smse`, `smserv`, `lighttpd`,
  `php-cgi` и другие runtime/web-сервисы PSL-проекта;
- останавливать процессы по одному только имени.

## Файлы

Loader хранит companion-состояние в своей служебной области:

```text
/projects/sys/segnetics-loader/
  companion.env
  compatibility.contract
  start-companion.sh
  bin/
    <processName>
```

`companion.env`:

```sh
ENABLED=1
PROCESS_NAME=binaryProjectName
BINARY=/projects/sys/segnetics-loader/bin/binaryProjectName
LOG=/tmp/binaryProjectName.log
COMPATIBILITY_FINGERPRINT=<sha256>
COMPATIBILITY_CONTRACT=/projects/sys/segnetics-loader/compatibility.contract
```

Логи:

```text
/tmp/<processName>.log
/tmp/segnetics-loader-companion.log
/tmp/segnetics-loader-companion-start.after.log
```

Managed-блок в `/projects/start.after`:

```sh
# BEGIN SEGNETICS_LOADER_COMPANION
/projects/sys/segnetics-loader/start-companion.sh --autostart >>/tmp/segnetics-loader-companion-start.after.log 2>&1 || true
# END SEGNETICS_LOADER_COMPANION
```

Loader добавляет или обновляет только managed-блок. Остальное содержимое
`/projects/start.after` не затирается. В managed-блоке не должно быть имени
конкретного ELF.

## Контракт Совместимости

При первой загрузке companion ELF/ZOP текущий `/projects/load_files.srv`
принимается как эталонный контракт совместимости. Это соответствует рабочему
порядку: сначала загружается PSL/LFS-проект, затем бинарник, совместимый с этим
проектом.

`compatibility.contract` содержит отсортированные строки:

```text
<name>|<type>|<section>
```

В контракт включаются только пользовательские runtime-переменные. Секция
`System` исключается, потому что содержит системные I/O и служебные поля ПЛК,
которые не являются контрактом совместимости companion-бинарника с PSL/LFS.

Пример:

```text
State|BOOL|Coil
VALUE|SHORT|Holdreg
```

`COMPATIBILITY_FINGERPRINT` - это hash сохранённого
`compatibility.contract`:

```text
sha256(compatibility.contract)
```

При последующих запусках launcher:

1. Проверяет целостность `compatibility.contract` через
   `COMPATIBILITY_FINGERPRINT`.
2. Строит текущую карту из `/projects/load_files.srv` в формате
   `<name>|<type>|<section>`.
3. Проверяет, что каждая строка из `compatibility.contract` присутствует в
   текущей карте.

Несовместимость:

- эталонная переменная исчезла;
- эталонная переменная поменяла тип;
- эталонная переменная перешла в другую секцию.

Новые переменные в текущем `/projects/load_files.srv` допускаются и не
блокируют запуск companion.

## Загрузка ELF/ZOP

Загрузка PSL/LFS через Loader не изменяет companion-настройки. Если companion
уже был установлен, его восстановление после загрузки PSL/LFS выполняется
только через существующий `/projects/start.after`.

ELF и ELF payload из ZOP загружаются через companion deploy path.

Алгоритм загрузки ELF:

1. Проверить SSH-подключение к ПЛК.
2. Создать `/projects/sys/segnetics-loader/bin`.
3. Остановить старые companion-процессы, у которых `/proc/<pid>/exe` указывает
   внутрь `/projects/sys/segnetics-loader/bin/`.
4. Очистить `/projects/sys/segnetics-loader/bin/*`.
5. Загрузить новый ELF во временный файл `<processName>.tmp`.
6. Атомарно переименовать его в
   `/projects/sys/segnetics-loader/bin/<processName>` и сделать исполняемым.
7. Построить `compatibility.contract` по текущему `/projects/load_files.srv`.
   Если переменных нет, создаётся пустой `compatibility.contract`. Пустой
   контракт валиден и означает отсутствие требований к PSL-переменным.
8. Записать `companion.env`.
9. Записать или обновить `start-companion.sh`.
10. Добавить или обновить managed-блок в `/projects/start.after`.
11. Запустить `/projects/sys/segnetics-loader/start-companion.sh --direct`.
12. Если companion не стартовал, вывести хвост `/tmp/<processName>.log` и
    завершить операцию ошибкой.

Алгоритм загрузки ZOP:

1. Проверить контейнер ZOP: magic, manifest, размеры payload-секций и hash
   LFS/ELF. Если проверка не прошла, остановить операцию до любых изменений на
   ПЛК.
2. Распаковать LFS и ELF во временную папку.
3. Загрузить вложенный LFS как основной PSL/LFS-проект.
4. Дождаться готовности LFS-проекта на ПЛК.
5. Загрузить вложенный ELF по алгоритму companion ELF.

Для ELF из ZOP:

- `PROCESS_NAME` берётся из `manifest.processName`;
- `BINARY` на ПЛК записывается как
  `/projects/sys/segnetics-loader/bin/<manifest.processName>`;
- искать ELF внутри ZOP не нужно: формат содержит ровно один ELF payload.

Лог `/tmp/<processName>.log` при прямом запуске нового companion
перезаписывается. Лог `/tmp/segnetics-loader-companion.log` ведётся в
append-режиме.

## Launcher

`start-companion.sh` поддерживает два режима:

- `--autostart` - запуск из `/projects/start.after`;
- `--direct` - прямой запуск из Loader во время загрузки ELF/ZOP.

В `--autostart` любые ошибки проверки или запуска пишутся в
`/tmp/segnetics-loader-companion.log`, после чего launcher завершает работу с
кодом `0`. Автозапуск companion не должен мешать старту основного PSL/LFS.

В `--direct` ошибки проверки или запуска возвращаются Loader-у ненулевым кодом,
потому что пользователь явно выполняет загрузку companion ELF/ZOP и ожидает его
запуск.

Алгоритм launcher-а:

1. Прочитать `/projects/sys/segnetics-loader/companion.env`.
2. Если `ENABLED` не равен `1`, завершить работу с кодом `0`.
3. Проверить, что `BINARY` существует и исполняемый.
4. Проверить, что `COMPATIBILITY_CONTRACT` существует.
5. Проверить hash `COMPATIBILITY_CONTRACT` по `COMPATIBILITY_FINGERPRINT`.
6. Проверить, что все строки из `compatibility.contract` присутствуют в текущей
   карте `/projects/load_files.srv`.
7. Если проверка не прошла, записать причину в
   `/tmp/segnetics-loader-companion.log` и завершиться по правилу текущего
   режима.
8. Остановить старые companion-процессы из
   `/projects/sys/segnetics-loader/bin/`.
9. Запустить:

```sh
cd /projects
nohup "$BINARY" >"$LOG" 2>&1 </dev/null &
```

10. Через короткую паузу проверить наличие процесса по полному пути к `BINARY`.
11. Если процесс не найден, записать диагностику и завершиться по правилу
    текущего режима.

## Vendor/Template И Отключение

Если до включения комбинированного режима был загружен standalone ELF через
vendor/template механику, он мог оставить:

```text
/projects/.template_active_project = <oldElfName>
```

Если указанный standalone ELF реально запущен как процесс из `/projects/<oldElfName>`,
а `/projects/load_files.srv` отсутствует, PSL/LFS не считается подготовленным
основным проектом ПЛК. При попытке загрузить companion ELF/ZOP нужно блокировать
операцию сообщением:

```text
Комбинированный режим невозможен: активным проектом ПЛК сейчас является standalone ELF.
Сначала загрузите PSL/LFS-проект, затем повторите загрузку ELF/ZOP в комбинированном режиме.
```

Если на ПЛК уже есть `/projects/load_files.srv`, но при этом продолжает работать
процесс `/projects/<oldElfName>`, указанный в `.template_active_project`, это
считается конфликтом старого vendor/template standalone ELF с установленным
PSL/LFS-проектом. Перед переходом в companion-модель Loader останавливает этот
standalone-процесс, удаляет `.template_active_project` и продолжает загрузку
companion ELF/ZOP.

Если после комбинированного режима пользователь загружает ELF как standalone
через vendor/template механику, vendor-модель имеет приоритет. Она может
вызвать `./start`, изменить `.template_active_project` и сделать standalone ELF
активным проектом ПЛК. Managed-блок в `start.after` не должен ломать этот
режим: `start-companion.sh --autostart` при несовместимости или отключенном
режиме ничего не запускает и завершается с кодом `0`.

Отключение комбинированного режима:

1. Записать в `companion.env`:

```sh
ENABLED=0
```

2. Остановить процессы из `/projects/sys/segnetics-loader/bin/`.

`ENABLED=0` является единственным признаком отключения режима. При отключении
не нужно удалять `companion.env`, `compatibility.contract`,
`start-companion.sh`, `bin/*` и логи. Эти файлы можно оставить как
диагностическое и восстановительное состояние.

Очистка `/projects/sys/segnetics-loader/bin/*` выполняется при установке нового
companion ELF/ZOP. При отключении режима эта очистка не требуется.
