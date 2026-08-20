package main

import (
	"bufio"
	"fmt"
	"os"
	"strconv"
	"strings"

	"golang.org/x/sys/windows"
)

// Мастер первого запуска.
//
// Задача: тот, кто ставит службу, не должен сначала читать документацию, чтобы узнать имена полей
// в JSON. Спрашиваем только то, что действительно бывает разным, и на каждый вопрос показываем
// готовый ответ в скобках — Enter соглашается с ним.
//
// Ключевое правило: мастер запускается ТОЛЬКО когда есть живая консоль и человек за ней. Служба
// стартует без консоли, и вопрос в пустоту навсегда подвесил бы её запуск — поэтому там молча
// берутся умолчания.

// hasConsole — есть ли интерактивный ввод.
//
// Проверяем именно тип дескриптора, а не «запущены ли мы службой»: exe могут запустить из
// планировщика, из скрипта установки, с перенаправленным вводом — во всех этих случаях спрашивать
// некого, и ожидание ответа выглядело бы как зависание.
func hasConsole() bool {
	fi, err := os.Stdin.Stat()
	if err != nil {
		return false
	}
	if fi.Mode()&os.ModeCharDevice == 0 {
		return false // ввод перенаправлен из файла или канала
	}
	var mode uint32
	h := windows.Handle(os.Stdin.Fd())
	return windows.GetConsoleMode(h, &mode) == nil
}

type prompter struct{ in *bufio.Reader }

func newPrompter() *prompter { return &prompter{in: bufio.NewReader(os.Stdin)} }

// ask задаёт вопрос и возвращает ответ либо умолчание, если человек нажал Enter.
func (p *prompter) ask(question, def string) string {
	if def != "" {
		fmt.Printf("%s [%s]: ", question, def)
	} else {
		fmt.Printf("%s (можно пусто): ", question)
	}
	line, err := p.in.ReadString('\n')
	if err != nil && strings.TrimSpace(line) == "" {
		return def
	}
	line = strings.TrimSpace(line)
	if line == "" {
		return def
	}
	return line
}

func (p *prompter) askYesNo(question string, def bool) bool {
	hint := "д/Н"
	if def {
		hint = "Д/н"
	}
	for {
		fmt.Printf("%s [%s]: ", question, hint)
		line, _ := p.in.ReadString('\n')
		switch strings.ToLower(strings.TrimSpace(line)) {
		case "":
			return def
		case "д", "да", "y", "yes":
			return true
		case "н", "нет", "n", "no":
			return false
		default:
			fmt.Println("  Не понял. Ответьте «д» или «н», либо нажмите Enter.")
		}
	}
}

// askPort спрашивает порт и не выпускает, пока не получит осмысленный.
// Порт — единственное, что спрашивают почти всегда, поэтому проверяем его по-настоящему.
func (p *prompter) askPort(def int) int {
	for {
		raw := p.ask("Порт, который будет слушать служба", strconv.Itoa(def))
		raw = strings.TrimPrefix(strings.TrimSpace(raw), ":")
		port, err := strconv.Atoi(raw)
		if err != nil || port < 1 || port > 65535 {
			fmt.Println("  Порт — число от 1 до 65535.")
			continue
		}
		if port < 1024 {
			fmt.Println("  Внимание: порты ниже 1024 обычно заняты системными службами.")
		}
		return port
	}
}

// askExistingFile просит путь к файлу и проверяет, что он есть. Пустой ответ — «не задавать».
func (p *prompter) askExistingFile(question string) string {
	for {
		path := strings.Trim(p.ask(question, ""), `"`)
		if path == "" {
			return ""
		}
		if _, err := os.Stat(path); err != nil {
			fmt.Printf("  Файл не найден: %s\n", path)
			if !p.askYesNo("  Указать другой путь?", true) {
				return ""
			}
			continue
		}
		return path
	}
}

// RunSetupWizard спрашивает параметры и пишет файл настроек. Возвращает готовый конфиг.
func RunSetupWizard(path string) (*Config, error) {
	cfg := defaultConfig()
	p := newPrompter()

	fmt.Println()
	fmt.Println("═══ Настройка службы antarus-sync ═══")
	fmt.Println()
	fmt.Println("Отвечайте на вопросы или жмите Enter — в скобках значение по умолчанию.")
	fmt.Printf("Настройки лягут в %s, потом их можно править этим же файлом.\n", path)
	fmt.Println()

	port := p.askPort(8443)
	cfg.Listen = ":" + strconv.Itoa(port)

	if p.askYesNo("Слушать только этот компьютер (localhost)? Отвечайте «да», если наружу службу отдаёт IIS или nginx", false) {
		cfg.Listen = "127.0.0.1:" + strconv.Itoa(port)
	}

	// TLS спрашиваем только если служба смотрит наружу: за прокси он ей не нужен, и лишний
	// вопрос про сертификаты только путает.
	if !strings.HasPrefix(cfg.Listen, "127.0.0.1") {
		if p.askYesNo("Служба будет сама работать по HTTPS (нужны файлы сертификата)?", false) {
			cfg.TLS.CertFile = p.askExistingFile("  Путь к файлу сертификата (.pem или .crt)")
			cfg.TLS.KeyFile = p.askExistingFile("  Путь к файлу ключа (.pem или .key)")
			if cfg.TLS.CertFile == "" || cfg.TLS.KeyFile == "" {
				fmt.Println("  Заданы не оба файла — HTTPS выключен, служба будет слушать HTTP.")
				cfg.TLS = TLSConfig{}
			}
		}
	}

	cfg.DataDir = p.ask("Папка для данных обмена", cfg.DataDir)
	cfg.LogFile = p.ask("Файл журнала", cfg.LogFile)

	host, _ := os.Hostname()
	cfg.ServerName = p.ask("Как служба представляется клиентам", host)

	// Первый ключ выдаём сразу: без него служба поднимется, но зайти к ней будет некому,
	// и первым делом человек всё равно полез бы читать про addkey.
	fmt.Println()
	name := p.ask("Имя первой машины, которой даём доступ", "admin-1")
	canWrite := p.askYesNo("Разрешить ей отправлять конфиг (право записи)?", true)
	key := NewKey()
	cfg.Clients = []Client{{Name: name, Key: key, CanWrite: canWrite}}

	cfg.normalize()
	if err := cfg.validate(); err != nil {
		return nil, err
	}
	if err := SaveConfig(path, cfg); err != nil {
		return nil, err
	}

	scheme := "http"
	if cfg.UseTLS() {
		scheme = "https"
	}
	addr := cfg.Listen
	if strings.HasPrefix(addr, ":") {
		addr = "<адрес сервера>" + addr
	}

	fmt.Println()
	fmt.Println("═══ Готово ═══")
	fmt.Printf("Настройки:  %s\n", path)
	fmt.Printf("Адрес:      %s://%s%s\n", scheme, addr, strings.TrimSuffix(cfg.BasePath, "/"))
	fmt.Printf("Данные:     %s\n", cfg.DataDir)
	fmt.Printf("Журнал:     %s\n", cfg.LogFile)
	fmt.Println()
	fmt.Printf("Машина:     %s\n", name)
	fmt.Printf("Ключ:       %s\n", key)
	fmt.Println()
	fmt.Println("Ключ передайте на ту машину — он же лежит в файле настроек.")
	fmt.Printf("Ещё ключи:  antarus-sync.exe addkey ИМЯ [-write]\n")
	fmt.Println()
	if !cfg.UseTLS() && !strings.HasPrefix(cfg.Listen, "127.0.0.1") {
		fmt.Println("ВНИМАНИЕ: служба слушает обычный HTTP и смотрит наружу.")
		fmt.Println("Ключ доступа пойдёт по сети открытым текстом — поставьте перед ней HTTPS-прокси")
		fmt.Println("или задайте сертификат в разделе \"tls\" файла настроек.")
		fmt.Println()
	}
	if err := openFirewallHint(port); err != nil {
		_ = err
	}
	return cfg, nil
}

func openFirewallHint(port int) error {
	fmt.Println("Не забудьте открыть порт в брандмауэре:")
	fmt.Printf(`  netsh advfirewall firewall add rule name="Antarus Sync %d" dir=in action=allow protocol=TCP localport=%d`+"\n", port, port)
	fmt.Println()
	return nil
}

// ensureConfig — общая точка входа для команд, которым нужен готовый конфиг.
//
// Файл есть — читаем. Файла нет и человек за консолью — спрашиваем. Файла нет и спросить некого —
// молча берём умолчания: служба обязана подняться сама, даже если её поставили скриптом.
func ensureConfig(path string, interactive bool) (*Config, error) {
	if _, err := os.Stat(path); err == nil {
		cfg, _, err := LoadConfig(path)
		return cfg, err
	} else if !os.IsNotExist(err) {
		return nil, err
	}

	if interactive && hasConsole() {
		return RunSetupWizard(path)
	}

	cfg, created, err := LoadConfig(path) // создаст с умолчаниями и одним ключом
	if err != nil {
		return nil, err
	}
	if created {
		fmt.Printf("Файл настроек создан с умолчаниями: %s\n", path)
		fmt.Printf("Порт %s, ключ первой машины — внутри файла.\n", cfg.Listen)
	}
	return cfg, nil
}
