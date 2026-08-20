package main

import (
	"bytes"
	"encoding/json"
	"io"
	"log"
	"net/http"
	"net/http/httptest"
	"path/filepath"
	"testing"
)

// Контракт задан НЕ здесь, а клиентом: AntarusPoFinder.Core/Services/HttpSyncTransport.cs.
// Эти тесты existуют ровно затем, чтобы правка сервера не разошлась с ним молча — расхождение
// проявилось бы как «у машин снаружи перестал обновляться каталог», без единой ошибки на экране.

func newTestServer(t *testing.T, clients []Client, rejectStale bool) *httptest.Server {
	t.Helper()
	dir := t.TempDir()
	cfg := defaultConfig()
	cfg.DataDir = filepath.Join(dir, "data")
	cfg.LogFile = ""
	cfg.Clients = clients
	cfg.RejectStaleRevision = rejectStale
	cfg.normalize()
	if err := cfg.validate(); err != nil {
		t.Fatalf("настройки не прошли проверку: %v", err)
	}
	store, err := NewStore(cfg.DataDir)
	if err != nil {
		t.Fatalf("хранилище: %v", err)
	}
	logger := log.New(io.Discard, "", 0)
	return httptest.NewServer(NewServer(cfg, store, logger).Handler())
}

func do(t *testing.T, srv *httptest.Server, method, path, key string, body []byte) *http.Response {
	t.Helper()
	var r io.Reader
	if body != nil {
		r = bytes.NewReader(body)
	}
	req, err := http.NewRequest(method, srv.URL+path, r)
	if err != nil {
		t.Fatalf("запрос: %v", err)
	}
	if key != "" {
		req.Header.Set(KeyHeader, key)
	}
	resp, err := srv.Client().Do(req)
	if err != nil {
		t.Fatalf("выполнение: %v", err)
	}
	return resp
}

var admin = Client{Name: "admin", Key: "KEY-ADMIN", CanWrite: true}
var reader = Client{Name: "naladchik", Key: "KEY-READ", CanWrite: false}

func TestБезКлючаНеПускает(t *testing.T) {
	srv := newTestServer(t, []Client{admin}, false)
	defer srv.Close()

	for _, path := range []string{"/ping", "/revision", "/config"} {
		resp := do(t, srv, http.MethodGet, path, "", nil)
		resp.Body.Close()
		if resp.StatusCode != http.StatusUnauthorized {
			t.Fatalf("%s без ключа: получили %d, ждали 401", path, resp.StatusCode)
		}
	}
}

func TestЧужойКлючНеПускает(t *testing.T) {
	srv := newTestServer(t, []Client{admin}, false)
	defer srv.Close()
	resp := do(t, srv, http.MethodGet, "/ping", "НЕ-ТОТ-КЛЮЧ", nil)
	resp.Body.Close()
	if resp.StatusCode != http.StatusUnauthorized {
		t.Fatalf("чужой ключ: получили %d, ждали 401", resp.StatusCode)
	}
}

// Отключённая машина отличается от неизвестной: ключ узнан, но доступ снят — 403, не 401.
// Разница нужна при разборе жалоб: «ключ не тот» и «доступ отобрали» лечатся по-разному.
func TestОтключённаяМашинаПолучаетЗапрет(t *testing.T) {
	off := Client{Name: "уволенный", Key: "KEY-OFF", Disabled: true}
	srv := newTestServer(t, []Client{admin, off}, false)
	defer srv.Close()
	resp := do(t, srv, http.MethodGet, "/ping", "KEY-OFF", nil)
	resp.Body.Close()
	if resp.StatusCode != http.StatusForbidden {
		t.Fatalf("отключённая машина: получили %d, ждали 403", resp.StatusCode)
	}
}

func TestPingОпознаётИПоказываетКудаСлать(t *testing.T) {
	srv := newTestServer(t, []Client{admin, reader}, false)
	defer srv.Close()

	resp := do(t, srv, http.MethodGet, "/ping", reader.Key, nil)
	defer resp.Body.Close()
	if resp.StatusCode != http.StatusOK {
		t.Fatalf("ping: получили %d, ждали 200", resp.StatusCode)
	}
	var got pingResponse
	if err := json.NewDecoder(resp.Body).Decode(&got); err != nil {
		t.Fatalf("ответ ping не разобрался: %v", err)
	}
	if got.Client != reader.Name {
		t.Fatalf("ping назвал клиента %q, а пришёл %q", got.Client, reader.Name)
	}
	if got.CanWrite {
		t.Fatal("наладчику показано право записи, которого у него нет")
	}
	for _, want := range []string{"ping", "revision", "config"} {
		if got.Endpoints[want] == "" {
			t.Fatalf("в ping нет адреса для %q — клиент не узнает, куда слать", want)
		}
	}
}

// Пустое хранилище обязано отвечать 404, а не 200 с пустым телом: клиент трактует 404 как
// «сведений нет» и работает дальше, а пустой успешный ответ принял бы за пустой каталог.
func TestПустоеХранилищеОтдаёт404(t *testing.T) {
	srv := newTestServer(t, []Client{admin}, false)
	defer srv.Close()
	for _, path := range []string{"/revision", "/config"} {
		resp := do(t, srv, http.MethodGet, path, admin.Key, nil)
		resp.Body.Close()
		if resp.StatusCode != http.StatusNotFound {
			t.Fatalf("%s на пустом сервере: получили %d, ждали 404", path, resp.StatusCode)
		}
	}
}

func TestЗаписьИЧтениеБайтВБайт(t *testing.T) {
	srv := newTestServer(t, []Client{admin}, false)
	defer srv.Close()

	// Двоичные данные с нулями и кириллицей в UTF-8: конфиг приезжает зашифрованным,
	// и любое «умное» преобразование текста его испортит.
	payload := []byte{0x00, 0x01, 0xFF, 0xFE}
	payload = append(payload, []byte("тег: шкаф управления")...)
	payload = append(payload, 0x00)

	resp := do(t, srv, http.MethodPost, "/config", admin.Key, payload)
	resp.Body.Close()
	if resp.StatusCode != http.StatusOK {
		t.Fatalf("запись конфига: получили %d, ждали 200", resp.StatusCode)
	}

	resp = do(t, srv, http.MethodGet, "/config", admin.Key, nil)
	defer resp.Body.Close()
	got, _ := io.ReadAll(resp.Body)
	if !bytes.Equal(got, payload) {
		t.Fatalf("конфиг вернулся изменённым:\nотправили %v\nполучили  %v", payload, got)
	}
}

func TestБезПраваЗаписиНеПишет(t *testing.T) {
	srv := newTestServer(t, []Client{admin, reader}, false)
	defer srv.Close()

	resp := do(t, srv, http.MethodPost, "/config", reader.Key, []byte("что-то"))
	resp.Body.Close()
	if resp.StatusCode != http.StatusForbidden {
		t.Fatalf("запись без права: получили %d, ждали 403", resp.StatusCode)
	}
	// И главное — ничего не записалось.
	resp = do(t, srv, http.MethodGet, "/config", admin.Key, nil)
	resp.Body.Close()
	if resp.StatusCode != http.StatusNotFound {
		t.Fatal("запрещённая запись всё-таки прошла")
	}
}

// Пустое тело затёрло бы каталог у всех машин разом. Это почти всегда обрыв связи, а не намерение.
func TestПустоеТелоНеПринимается(t *testing.T) {
	srv := newTestServer(t, []Client{admin}, false)
	defer srv.Close()
	resp := do(t, srv, http.MethodPost, "/config", admin.Key, []byte{})
	resp.Body.Close()
	if resp.StatusCode != http.StatusBadRequest {
		t.Fatalf("пустое тело: получили %d, ждали 400", resp.StatusCode)
	}
}

// Клиент умеет только GET и POST, и это не случайность: остальные глаголы режут корпоративные
// прокси. Тест закрепляет, что сервер не начнёт молча принимать PUT.
func TestТолькоGETиPOST(t *testing.T) {
	srv := newTestServer(t, []Client{admin}, false)
	defer srv.Close()
	for _, method := range []string{http.MethodPut, http.MethodDelete, http.MethodPatch} {
		resp := do(t, srv, method, "/config", admin.Key, []byte("x"))
		resp.Body.Close()
		if resp.StatusCode != http.StatusMethodNotAllowed {
			t.Fatalf("%s: получили %d, ждали 405", method, resp.StatusCode)
		}
	}
}

func TestОткатРевизииОтклоняетсяКогдаВключено(t *testing.T) {
	srv := newTestServer(t, []Client{admin}, true)
	defer srv.Close()

	post := func(rev int) int {
		body, _ := json.Marshal(map[string]int{"Revision": rev})
		resp := do(t, srv, http.MethodPost, "/revision", admin.Key, body)
		resp.Body.Close()
		return resp.StatusCode
	}

	if code := post(5); code != http.StatusOK {
		t.Fatalf("первая запись: получили %d, ждали 200", code)
	}
	if code := post(4); code != http.StatusConflict {
		t.Fatalf("откат назад: получили %d, ждали 409", code)
	}
	if code := post(5); code != http.StatusConflict {
		t.Fatalf("та же ревизия: получили %d, ждали 409", code)
	}
	if code := post(6); code != http.StatusOK {
		t.Fatalf("следующая ревизия: получили %d, ждали 200", code)
	}
}

// По умолчанию защита выключена — семантика обязана совпадать с сетевой шарой, которая
// принимает что дали. Иначе первый же день работы уйдёт на разбор «почему не отправляется».
func TestПоУмолчаниюОткатРазрешён(t *testing.T) {
	srv := newTestServer(t, []Client{admin}, false)
	defer srv.Close()

	body, _ := json.Marshal(map[string]int{"Revision": 9})
	resp := do(t, srv, http.MethodPost, "/revision", admin.Key, body)
	resp.Body.Close()

	body, _ = json.Marshal(map[string]int{"Revision": 2})
	resp = do(t, srv, http.MethodPost, "/revision", admin.Key, body)
	resp.Body.Close()
	if resp.StatusCode != http.StatusOK {
		t.Fatalf("при выключенной защите откат должен приниматься, получили %d", resp.StatusCode)
	}
}

func TestОдинКлючНаДвухКлиентовНеПринимается(t *testing.T) {
	cfg := defaultConfig()
	cfg.Clients = []Client{{Name: "a", Key: "ОДИН"}, {Name: "б", Key: "ОДИН"}}
	if err := cfg.validate(); err == nil {
		t.Fatal("одинаковые ключи у разных клиентов приняты — тогда в журнале не различить, кто пришёл")
	}
}

func TestБелыйСписокАдресов(t *testing.T) {
	dir := t.TempDir()
	cfg := defaultConfig()
	cfg.DataDir = filepath.Join(dir, "data")
	cfg.LogFile = ""
	cfg.Clients = []Client{admin}
	cfg.AllowedIPs = []string{"10.0.0.0/8"} // локальный тест придёт с 127.0.0.1
	cfg.normalize()
	store, _ := NewStore(cfg.DataDir)
	srv := httptest.NewServer(NewServer(cfg, store, log.New(io.Discard, "", 0)).Handler())
	defer srv.Close()

	resp := do(t, srv, http.MethodGet, "/ping", admin.Key, nil)
	resp.Body.Close()
	if resp.StatusCode != http.StatusForbidden {
		t.Fatalf("адрес вне списка: получили %d, ждали 403", resp.StatusCode)
	}
}
