#!/usr/bin/env bash
# Ящик агента в Nextcloud на cloud.rev-il.ru.
#
# Илья кладёт туда тикеты, реквизиты RustDesk и скриншоты через обычную веб-страницу Nextcloud,
# агент забирает их отсюда по WebDAV. Реквизиты доступа — в tools/inbox/.env, в гит не попадают.
#
#   ./inbox.sh check     — проверить доступ
#   ./inbox.sh init      — создать структуру папок и шаблоны в облаке (один раз)
#   ./inbox.sh pull      — скачать весь ящик в .inbox/
#   ./inbox.sh tickets   — показать очередь тикетов
#   ./inbox.sh put <файл> [имя в облаке] — положить файл в ящик (ответ, сборка, лог)
#
set -euo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO="$(cd "$HERE/../.." && pwd)"
ENV_FILE="$HERE/.env"
LOCAL_MIRROR="$REPO/.inbox"

die() { printf '%s\n' "$*" >&2; exit 1; }

[ -f "$ENV_FILE" ] || die "Нет $ENV_FILE — заполни его по образцу tools/inbox/env.example (см. tools/inbox/README.md)."
# shellcheck disable=SC1090
set -a; . "$ENV_FILE"; set +a

NC_URL="${NC_URL:-https://cloud.rev-il.ru}"
NC_URL="${NC_URL%/}"
NC_ROOT="${NC_ROOT:-Antarus}"

# Два способа доступа. Пароль приложения даёт чтение и запись, публичная ссылка-«шара» —
# только то, что разрешил владелец.
if [ -n "${NC_SHARE:-}" ]; then
  AUTH="${NC_SHARE}:${NC_SHARE_PASS:-}"
  DAV_BASE="$NC_URL/public.php/dav/files/$NC_SHARE"
  DAV_FALLBACK="$NC_URL/public.php/webdav"
elif [ -n "${NC_USER:-}" ] && [ -n "${NC_PASS:-}" ]; then
  AUTH="${NC_USER}:${NC_PASS}"
  DAV_BASE="$NC_URL/remote.php/dav/files/$NC_USER"
  DAV_FALLBACK=""
else
  die "В .env нужен либо NC_USER + NC_PASS (пароль приложения), либо NC_SHARE (токен публичной ссылки)."
fi

# Имена в ящике держим латиницей, так что из экранирования реально нужен только пробел.
enc() { printf '%s' "$1" | sed 's/ /%20/g'; }
dec() { printf '%s' "$1" | sed 's/%20/ /g'; }

url_for() {  # $1 — путь внутри ящика, может быть пустым
  local p="${1:-}"
  if [ -n "$p" ]; then
    printf '%s/%s/%s' "$DAV_BASE" "$(enc "$NC_ROOT")" "$(enc "$p")"
  else
    printf '%s/%s' "$DAV_BASE" "$(enc "$NC_ROOT")"
  fi
}

# req МЕТОД URL [аргументы curl...] — печатает тело, код кладёт в HTTP_CODE.
# Сетевой сбой это тоже переживает: HTTP_CODE=000, дальше решает вызывающий.
req() {
  local method="$1" url="$2"; shift 2
  local out
  if ! out="$(curl -sS -m 60 -u "$AUTH" -X "$method" "$url" -w '\n%{http_code}' "$@" 2>/dev/null)"; then
    HTTP_CODE="000"; return 0
  fi
  HTTP_CODE="${out##*$'\n'}"
  printf '%s' "${out%$'\n'*}"
}

ok() { case "${HTTP_CODE:-000}" in 2*) return 0;; *) return 1;; esac; }

probe_base() {
  req PROPFIND "$DAV_BASE/" -H 'Depth: 0' >/dev/null
  if ! ok && [ -n "$DAV_FALLBACK" ]; then
    DAV_BASE="$DAV_FALLBACK"
    req PROPFIND "$DAV_BASE/" -H 'Depth: 0' >/dev/null
  fi
  ok || die "WebDAV не пустил: HTTP $HTTP_CODE. Проверь NC_USER и NC_PASS в .env — нужен пароль приложения, не пароль от входа."
}

# PROPFIND отдаёт XML; берём href-ы детей и режем их до пути внутри ящика.
# Папки остаются со слешом на конце — по нему pull и отличает их от файлов.
list_dir() {  # $1 — путь внутри ящика
  local body prefix self
  body="$(req PROPFIND "$(url_for "${1:-}")/" -H 'Depth: 1')"
  ok || return 1
  prefix="$(printf '%s' "$DAV_BASE" | sed 's#^https\?://[^/]*##')/$(enc "$NC_ROOT")/"
  # Depth:1 всегда возвращает и саму папку. Не выкинуть её — pull уйдёт в вечную рекурсию.
  self="$(enc "${1:-}")"
  printf '%s' "$body" \
    | sed 's#></#>\n<#g' \
    | grep -oiE '<[a-z0-9]*:?href>[^<]*' \
    | sed 's/^[^>]*>//' \
    | sed "s#^${prefix}##" \
    | grep -v '^/' \
    | grep -vxF "$self" \
    | grep -vxF "$self/" \
    | grep -v '^$' || true
}

mkcol() {  # создать папку; «уже есть» (405) — не ошибка
  local p="${1:-}"
  [ "$p" = "." ] && p=""
  req MKCOL "$(url_for "$p")/" >/dev/null
  case "$HTTP_CODE" in
    2*|405) return 0 ;;
    *) die "Не смог создать папку '$p': HTTP $HTTP_CODE" ;;
  esac
}

# Шаблоны кладём только если их ещё нет: написанное Ильёй важнее наших заготовок.
put_text() {  # $1 — путь в облаке, содержимое — со stdin
  local tmp; tmp="$(mktemp)"; cat > "$tmp"
  req HEAD "$(url_for "$1")" >/dev/null
  if ok; then rm -f "$tmp"; printf '  уже есть, не трогаю: %s\n' "$1"; return 0; fi
  req PUT "$(url_for "$1")" --data-binary "@$tmp" >/dev/null
  rm -f "$tmp"
  ok || die "Не смог записать $1: HTTP $HTTP_CODE"
  printf '  создан: %s\n' "$1"
}

cmd_check() {
  probe_base
  printf 'Доступ есть: %s\n' "$DAV_BASE"
  req PROPFIND "$(url_for)/" -H 'Depth: 0' >/dev/null
  if ok; then
    printf 'Ящик /%s на месте.\n' "$NC_ROOT"
  else
    printf 'Ящика /%s ещё нет — запусти: ./inbox.sh init\n' "$NC_ROOT"
  fi
}

cmd_init() {
  probe_base
  mkcol ""
  mkcol "files"

  put_text "README.md" <<'TPL'
# Ящик агента — Antarus ПО Finder

Всё, что нужно передать агенту, кладётся сюда. Агент читает эту папку сам, в гит она не попадает.

- `TICKETS.md` — очередь тикетов на следующий патч.
- `RUSTDESK.md` — реквизиты удалённого доступа к рабочему ПК.
- `files/` — скриншоты, логи, конфиги: всё вложениями.

Правило одно: пишет сюда Илья, помечает сделанное агент.
TPL

  put_text "TICKETS.md" <<'TPL'
# Тикеты на следующий патч

Формат свободный: строка-заголовок и, если надо, пара строк подробностей. Дословная жалоба
ценнее аккуратной формулировки — по ней видно, что человек делал и чего ждал.

Скриншот кладётся в `files/`, в тикете достаточно упомянуть имя файла.

Шаблон:

```
### <короткий заголовок>
Что делаю:
Что происходит:
Что должно быть:
Где (окно/страница):
Файл: files/<имя скриншота>
```

## Очередь

<!-- Илья пишет сюда. Агент разбирает сверху вниз. -->

_Пусто._

## Разобранные

<!-- Сюда агент переносит закрытые, с номером версии, в которую вошло. -->

_Пусто._
TPL

  put_text "RUSTDESK.md" <<'TPL'
# RustDesk — доступ к рабочему ПК

Заполнить и сказать агенту «попробуй подключиться».

```
ID:
Одноразовый пароль:
Постоянный пароль (если задан):
Адрес ретрансляции (Relay/ID server), если свой:
Ключ (Key), если свой сервер:
```

Что должно быть включено на рабочем ПК, иначе подключение не встанет:

- [ ] RustDesk запущен и в окне виден ID (не «Готово к соединению…» без номера).
- [ ] «Неконтролируемый доступ» (постоянный пароль) задан — иначе на каждое подключение нужно
      нажимать «Принять» руками, а ПК стоит без человека.
- [ ] Если стоит корпоративный сервер — заполнены обе строки выше (адрес и ключ), без них клиент
      уходит на публичные ретрансляторы и не находит машину.
- [ ] Экран не заблокирован, либо в RustDesk разрешён вход на экран блокировки.

Сеанс идёт с домашней машины к рабочему ПК; агент в песочнице своего GUI-канала не имеет,
поэтому подключение поднимает и держит Илья, а агент работает с тем, что видно на экране.

`Z:\Software\Antarus Finder\Конфиг` из песочницы по-прежнему не виден — свежие exe/MSI туда
копируются вручную, удалённый сеанс этого не меняет.

Одноразовый пароль после сеанса лучше сменить: файл лежит в облаке.
TPL

  printf 'Готово. Ящик открывается тут: %s/index.php/apps/files/?dir=/%s\n' "$NC_URL" "$NC_ROOT"
}

pull_dir() {  # $1 — путь внутри ящика
  local entry name target
  while IFS= read -r entry; do
    [ -n "$entry" ] || continue
    name="$(dec "${entry%/}")"
    target="$LOCAL_MIRROR/$name"
    if [ "$entry" != "${entry%/}" ]; then
      mkdir -p "$target"
      pull_dir "$name"
    else
      mkdir -p "$(dirname "$target")"
      curl -sS -m 180 -u "$AUTH" -o "$target" "$(url_for "$name")"
      printf '  %s\n' "$name"
    fi
  done < <(list_dir "${1:-}")
}

cmd_pull() {
  probe_base
  req PROPFIND "$(url_for)/" -H 'Depth: 0' >/dev/null
  ok || die "Ящика /$NC_ROOT в облаке нет (HTTP $HTTP_CODE) — запусти ./inbox.sh init."
  mkdir -p "$LOCAL_MIRROR"
  printf 'Забираю /%s -> .inbox/\n' "$NC_ROOT"
  pull_dir ""
  printf 'Готово.\n'
}

cmd_tickets() {
  cmd_pull >/dev/null
  local f="$LOCAL_MIRROR/TICKETS.md"
  [ -f "$f" ] || die "В ящике нет TICKETS.md — запусти ./inbox.sh init."
  cat "$f"
}

cmd_put() {
  [ $# -ge 1 ] || die "Укажи файл: ./inbox.sh put <файл> [имя в облаке]"
  [ -f "$1" ] || die "Нет файла: $1"
  probe_base
  local remote="${2:-files/$(basename "$1")}"
  local dir; dir="$(dirname "$remote")"
  [ "$dir" != "." ] && mkcol "$dir"
  req PUT "$(url_for "$remote")" --data-binary "@$1" >/dev/null
  ok || die "Не загрузилось: HTTP $HTTP_CODE"
  printf 'Загружено: %s\n' "$remote"
}

case "${1:-check}" in
  check)   cmd_check ;;
  init)    cmd_init ;;
  pull)    cmd_pull ;;
  tickets) cmd_tickets ;;
  put)     shift; cmd_put "$@" ;;
  *)       die "Команды: check | init | pull | tickets | put <файл> [имя]" ;;
esac
