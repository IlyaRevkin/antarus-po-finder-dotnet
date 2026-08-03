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
- profile manager - общий router активного профиля в
  `/projects/sys/segnetics-runtime/profile-manager.sh`.

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

/projects/sys/segnetics-runtime/
  active-profile.env
  pending-profile.env
  rollback/
  profile-manager.sh
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
/tmp/segnetics-profile-manager.log
/tmp/segnetics-active-profile.log
```

Profile manager устанавливает общий router в `/projects/start.after`:

```sh
# BEGIN SEGNETICS_ACTIVE_PROFILE
/projects/sys/segnetics-runtime/profile-manager.sh autostart >>/tmp/segnetics-active-profile.log 2>&1 || true
# END SEGNETICS_ACTIVE_PROFILE
```

В `/projects/start.before` аналогично устанавливается guard vendor takeover.
Менеджер заменяет только собственные и прежние известные managed-блоки,
сохраняя остальное содержимое hook-файлов. Имя конкретного ELF хранится в
`companion.env`, а не в router-е.

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

При загрузке нового PSL/LFS Loader останавливает companion до замены проекта.
После установки profile manager повторно проверяет контракт. Совместимый
companion запускается снова, несовместимый переводится в `ENABLED=0`, а
LFS-загрузка завершается успешно с предупреждением.

ELF и ELF payload из ZOP загружаются через companion deploy path.

Алгоритм загрузки ELF:

1. Проверить SSH-подключение и прочитать текущий `/projects/load_files.srv`.
2. Построить локально `compatibility.contract`, `companion.env` и launcher.
3. Загрузить ELF и служебные файлы с суффиксом `.tmp`.
4. Выполнить `profile-manager prepare vendor-psl segnetics-loader`: сохранить
   rollback-состояние и остановить несовместимые процессы по точным путям.
5. Атомарно переименовать ELF в
   `/projects/sys/segnetics-loader/bin/<processName>` и сделать исполняемым.
6. Атомарно установить `compatibility.contract`, `companion.env` и
   `start-companion.sh`.
7. Выполнить `profile-manager commit vendor-psl segnetics-loader`.
8. Profile manager обновляет hook-блоки, запускает companion и подтверждает
   процесс по точному пути.
9. Если установка или запуск не подтверждены, восстановить предыдущий профиль
   owner-bound rollback-ом и вывести хвост `/tmp/<processName>.log`.

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
- `--stop` - точная остановка своего ELF или `gdbserver`, который его запустил.

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

Companion требует существующий `/projects/load_files.srv`. Если до него был
активен vendor standalone, profile manager транзакционно останавливает
`/projects/<oldElfName>` по точному пути и сохраняет его для rollback. Если
PSL/LFS-проект отсутствует, commit companion не принимается и прежний
standalone восстанавливается.

Обратный переход также управляется единым профилем. Загрузка standalone
останавливает companion, очищает пользовательский PSL/LFS-state и принимает
`PROFILE=vendor-standalone`. Прямой vendor/template запуск имеет приоритет:
guard обнаруживает новый `.template_active_project`, отключает
несовместимый managed runtime и не возвращает ошибку в vendor pipeline.

Отключение companion выполняется так:

1. Записать в `companion.env`:

```sh
ENABLED=0
AUTOSTART=0
```

2. Вызвать `start-companion.sh --stop` и проверить отсутствие точного
   ELF/gdbserver-процесса.

Служебные файлы могут оставаться для диагностики и возможного rollback.
Активное состояние определяется одновременно manifest-ом профиля,
`ENABLED` и фактическим процессом.
