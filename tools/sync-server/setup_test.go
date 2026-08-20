package main

import (
	"bufio"
	"os"
	"path/filepath"
	"strings"
	"testing"
)

func promptFrom(input string) *prompter {
	return &prompter{in: bufio.NewReader(strings.NewReader(input))}
}

// Enter на любом вопросе обязан оставлять умолчание. Это главное обещание мастера: человек,
// который не знает, что отвечать, жмёт Enter и получает рабочую настройку.
func TestПустойОтветОставляетУмолчание(t *testing.T) {
	p := promptFrom("\n")
	if got := p.ask("порт", ":8443"); got != ":8443" {
		t.Fatalf("получили %q, ждали умолчание", got)
	}
}

func TestОтветПерекрываетУмолчание(t *testing.T) {
	p := promptFrom("C:/Данные/обмен\n")
	if got := p.ask("папка", `C:\ProgramData`); got != "C:/Данные/обмен" {
		t.Fatalf("получили %q", got)
	}
}

// Обрыв ввода (конец файла) не должен ронять мастер: скрипт мог передать меньше строк,
// чем вопросов, и остаток обязан достаться умолчаниям.
func TestКонецВводаДаётУмолчание(t *testing.T) {
	p := promptFrom("")
	if got := p.ask("порт", ":8443"); got != ":8443" {
		t.Fatalf("получили %q, ждали умолчание при обрыве ввода", got)
	}
}

func TestДаНетПонимаетОбаЯзыка(t *testing.T) {
	cases := []struct {
		in   string
		def  bool
		want bool
	}{
		{"д\n", false, true},
		{"да\n", false, true},
		{"y\n", false, true},
		{"yes\n", false, true},
		{"н\n", true, false},
		{"нет\n", true, false},
		{"n\n", true, false},
		{"\n", true, true},
		{"\n", false, false},
		{"", true, true}, // обрыв ввода
	}
	for _, c := range cases {
		p := promptFrom(c.in)
		if got := p.askYesNo("вопрос", c.def); got != c.want {
			t.Fatalf("ответ %q при умолчании %v: получили %v, ждали %v", c.in, c.def, got, c.want)
		}
	}
}

// Непонятный ответ переспрашивается, а не трактуется как «нет».
func TestДаНетПереспрашивает(t *testing.T) {
	p := promptFrom("ага\nда\n")
	if !p.askYesNo("вопрос", false) {
		t.Fatal("после переспроса ответ «да» не принят")
	}
}

func TestПортПроверяется(t *testing.T) {
	// Мусор, ноль, слишком большой — и только потом годный.
	p := promptFrom("восемь\n0\n70000\n9000\n")
	if got := p.askPort(8443); got != 9000 {
		t.Fatalf("получили %d, ждали 9000", got)
	}
}

func TestПортПринимаетФормуСДвоеточием(t *testing.T) {
	p := promptFrom(":8443\n")
	if got := p.askPort(8443); got != 8443 {
		t.Fatalf("получили %d", got)
	}
}

// Без консоли мастер запускаться НЕ должен: служба стартует без неё, и вопрос в пустоту
// навсегда подвесил бы запуск. Проверяем, что ensureConfig в этом случае молча создаёт
// настройки по умолчанию.
func TestБезКонсолиБерутсяУмолчания(t *testing.T) {
	dir := t.TempDir()
	path := filepath.Join(dir, "antarus-sync.json")

	cfg, err := ensureConfig(path, false)
	if err != nil {
		t.Fatalf("ensureConfig: %v", err)
	}
	if cfg.Listen != ":8443" {
		t.Fatalf("порт по умолчанию %q, ждали :8443", cfg.Listen)
	}
	if len(cfg.Clients) != 1 {
		t.Fatalf("клиентов %d, ждали одного заготовленного", len(cfg.Clients))
	}
	if cfg.Clients[0].Key == "" {
		t.Fatal("ключ первой машины пустой — зайти к службе будет некому")
	}
	if _, err := os.Stat(path); err != nil {
		t.Fatalf("файл настроек не создан: %v", err)
	}
}

// Существующие настройки мастер не трогает, даже если консоль есть.
func TestГотовыеНастройкиНеПерезаписываются(t *testing.T) {
	dir := t.TempDir()
	path := filepath.Join(dir, "antarus-sync.json")

	original := defaultConfig()
	original.Listen = ":9999"
	original.Clients = []Client{{Name: "уже-был", Key: "КЛЮЧ-1", CanWrite: true}}
	if err := SaveConfig(path, original); err != nil {
		t.Fatalf("подготовка: %v", err)
	}

	cfg, err := ensureConfig(path, true)
	if err != nil {
		t.Fatalf("ensureConfig: %v", err)
	}
	if cfg.Listen != ":9999" {
		t.Fatalf("порт стал %q — существующие настройки перезаписаны", cfg.Listen)
	}
	if len(cfg.Clients) != 1 || cfg.Clients[0].Name != "уже-был" {
		t.Fatal("список ключей подменён")
	}
}
