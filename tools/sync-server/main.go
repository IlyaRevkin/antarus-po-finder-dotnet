package main

import (
	"context"
	"crypto/tls"
	"errors"
	"flag"
	"fmt"
	"io"
	"log"
	"net/http"
	"os"
	"path/filepath"
	"strings"
	"time"

	"golang.org/x/sys/windows/svc"
	"golang.org/x/sys/windows/svc/mgr"
)

const serviceName = "AntarusSync"
const serviceDisplay = "Antarus Sync — обмен каталогом прошивок"
const serviceDesc = "Отдаёт и принимает общий конфиг Antarus ПО Finder по HTTP. " +
	"Заменяет обмен через сетевую папку для машин вне корпоративной сети."

func main() {
	// Когда службу запускает диспетчер, аргументов нет и консоли нет. Определяем это ДО разбора
	// флагов: иначе служба падала бы на попытке что-нибудь напечатать.
	isService, err := svc.IsWindowsService()
	if err != nil {
		fmt.Fprintln(os.Stderr, "не удалось определить режим запуска:", err)
		os.Exit(1)
	}
	if isService {
		runService()
		return
	}

	if len(os.Args) < 2 {
		usage()
		os.Exit(2)
	}

	switch os.Args[1] {
	case "run":
		cmdRun(os.Args[2:])
	case "setup":
		cmdSetup(os.Args[2:])
	case "install":
		cmdInstall(os.Args[2:])
	case "uninstall":
		cmdUninstall()
	case "start":
		cmdControl("start")
	case "stop":
		cmdControl("stop")
	case "addkey":
		cmdAddKey(os.Args[2:])
	case "keys":
		cmdKeys(os.Args[2:])
	case "help", "-h", "--help":
		usage()
	default:
		fmt.Fprintf(os.Stderr, "неизвестная команда %q\n\n", os.Args[1])
		usage()
		os.Exit(2)
	}
}

func usage() {
	fmt.Print(`antarus-sync — служба обмена каталогом прошивок Antarus ПО Finder.

Команды:
  setup [-config ФАЙЛ]     задать параметры вопрос-ответ (порт, папки, первый ключ)
  install [-config ФАЙЛ]   зарегистрировать службу Windows (автозапуск)
  uninstall                удалить службу
  start | stop             управление службой
  run [-config ФАЙЛ]       запустить в консоли (для проверки, служба не нужна)
  addkey ИМЯ [-write]      выдать ключ доступа новой машине
  keys                     показать выданные ключи

Файл настроек по умолчанию — antarus-sync.json рядом с exe.
Если его нет, install и run сами спросят параметры; на каждый вопрос Enter
подставляет значение по умолчанию (порт 8443 и прочее). Когда спросить некого
(запуск скриптом, служба), молча берутся умолчания.
`)
}

// configPath возвращает путь к настройкам. Относительный путь разрешается от папки exe, а не от
// текущего каталога: у службы текущий каталог — System32, и «файл рядом с программой» иначе
// превращается в «файл в системной папке».
func configPath(explicit string) string {
	if explicit != "" {
		if filepath.IsAbs(explicit) {
			return explicit
		}
		return filepath.Join(exeDir(), explicit)
	}
	return filepath.Join(exeDir(), "antarus-sync.json")
}

func exeDir() string {
	exe, err := os.Executable()
	if err != nil {
		return "."
	}
	return filepath.Dir(exe)
}

// setupLogger — журнал в файл и (в консольном режиме) на экран.
//
// Файл нужен именно службе: у неё нет консоли, и без журнала разбор жалобы «не синхронизируется»
// упирается в гадание. Ротация по размеру самая простая — один предыдущий файл: журнал тут
// строчечный, а бесконечно растущий файл на системном диске сервера никому не нужен.
func setupLogger(path string, alsoStdout bool) (*log.Logger, io.Closer, error) {
	if strings.TrimSpace(path) == "" {
		return log.New(os.Stdout, "", log.LstdFlags), io.NopCloser(nil), nil
	}
	if err := os.MkdirAll(filepath.Dir(path), 0o755); err != nil {
		return nil, nil, err
	}
	if st, err := os.Stat(path); err == nil && st.Size() > 8<<20 {
		_ = os.Remove(path + ".old")
		_ = os.Rename(path, path+".old")
	}
	f, err := os.OpenFile(path, os.O_CREATE|os.O_WRONLY|os.O_APPEND, 0o644)
	if err != nil {
		return nil, nil, err
	}
	var w io.Writer = f
	if alsoStdout {
		w = io.MultiWriter(f, os.Stdout)
	}
	return log.New(w, "", log.LstdFlags), f, nil
}

// buildServer поднимает всё, что нужно для работы, и возвращает готовый http.Server.
//
// interactive — можно ли спрашивать человека, если настроек ещё нет. У службы нельзя: она
// стартует без консоли, и вопрос в пустоту навсегда подвесил бы её запуск.
func buildServer(cfgFile string, console, interactive bool) (*http.Server, *Config, *log.Logger, io.Closer, error) {
	cfg, err := ensureConfig(cfgFile, interactive)
	if err != nil {
		return nil, nil, nil, nil, err
	}
	logger, closer, err := setupLogger(cfg.LogFile, console)
	if err != nil {
		return nil, nil, nil, nil, err
	}
	store, err := NewStore(cfg.DataDir)
	if err != nil {
		return nil, nil, nil, nil, err
	}
	srv := &http.Server{
		Addr:    cfg.Listen,
		Handler: NewServer(cfg, store, logger).Handler(),
		// Таймауты выставлены явно: у http.Server по умолчанию их нет вовсе, и одно
		// подвисшее соединение держит поток до конца времён.
		ReadHeaderTimeout: 15 * time.Second,
		ReadTimeout:       5 * time.Minute,
		WriteTimeout:      5 * time.Minute,
		IdleTimeout:       2 * time.Minute,
	}
	if cfg.UseTLS() {
		srv.TLSConfig = &tls.Config{MinVersion: tls.VersionTLS12}
	}
	return srv, cfg, logger, closer, nil
}

func listenAndServe(srv *http.Server, cfg *Config, logger *log.Logger) error {
	scheme := "http"
	if cfg.UseTLS() {
		scheme = "https"
	}
	logger.Printf("antarus-sync %s слушает %s (%s), путь %s, данные в %s, клиентов %d",
		ServiceVersion, cfg.Listen, scheme, cfg.BasePath, cfg.DataDir, len(cfg.Clients))
	if cfg.UseTLS() {
		return srv.ListenAndServeTLS(cfg.TLS.CertFile, cfg.TLS.KeyFile)
	}
	return srv.ListenAndServe()
}

func cmdRun(args []string) {
	fs := flag.NewFlagSet("run", flag.ExitOnError)
	cfgFlag := fs.String("config", "", "путь к файлу настроек")
	_ = fs.Parse(args)

	srv, cfg, logger, closer, err := buildServer(configPath(*cfgFlag), true, true)
	if err != nil {
		fmt.Fprintln(os.Stderr, "ошибка запуска:", err)
		os.Exit(1)
	}
	defer closer.Close()

	if err := listenAndServe(srv, cfg, logger); err != nil && !errors.Is(err, http.ErrServerClosed) {
		logger.Printf("сервер остановлен с ошибкой: %v", err)
		os.Exit(1)
	}
}

// --- служба Windows ---------------------------------------------------------

type windowsService struct{}

func (windowsService) Execute(args []string, req <-chan svc.ChangeRequest, status chan<- svc.Status) (bool, uint32) {
	const accepted = svc.AcceptStop | svc.AcceptShutdown
	status <- svc.Status{State: svc.StartPending}

	// Файл настроек службе передаётся аргументом при регистрации; если его нет — берём соседний.
	cfgFile := ""
	for i := 0; i < len(args)-1; i++ {
		if args[i] == "-config" || args[i] == "--config" {
			cfgFile = args[i+1]
		}
	}

	srv, cfg, logger, closer, err := buildServer(configPath(cfgFile), false, false)
	if err != nil {
		// Печатать некуда, консоли нет: сообщаем код выхода, подробности — в журнале, если он поднялся.
		status <- svc.Status{State: svc.Stopped}
		return true, 1
	}
	defer closer.Close()

	errCh := make(chan error, 1)
	go func() { errCh <- listenAndServe(srv, cfg, logger) }()

	status <- svc.Status{State: svc.Running, Accepts: accepted}

	for {
		select {
		case err := <-errCh:
			if err != nil && !errors.Is(err, http.ErrServerClosed) {
				logger.Printf("сервер упал: %v", err)
				status <- svc.Status{State: svc.Stopped}
				return true, 2
			}
			status <- svc.Status{State: svc.Stopped}
			return false, 0

		case c := <-req:
			switch c.Cmd {
			case svc.Interrogate:
				status <- c.CurrentStatus
			case svc.Stop, svc.Shutdown:
				logger.Printf("получена команда остановки")
				status <- svc.Status{State: svc.StopPending}
				ctx, cancel := context.WithTimeout(context.Background(), 20*time.Second)
				_ = srv.Shutdown(ctx)
				cancel()
				status <- svc.Status{State: svc.Stopped}
				return false, 0
			}
		}
	}
}

func runService() {
	_ = svc.Run(serviceName, windowsService{})
}

// cmdSetup — перенастроить уже поставленную службу, не переустанавливая её.
func cmdSetup(args []string) {
	fs := flag.NewFlagSet("setup", flag.ExitOnError)
	cfgFlag := fs.String("config", "", "путь к файлу настроек")
	_ = fs.Parse(args)

	path := configPath(*cfgFlag)
	if _, err := os.Stat(path); err == nil {
		fmt.Printf("Файл настроек уже есть: %s\n", path)
		fmt.Println("Мастер перезапишет его целиком, включая список выданных ключей.")
		if !newPrompter().askYesNo("Продолжить?", false) {
			fmt.Println("Отменено, файл не тронут.")
			return
		}
	}
	if _, err := RunSetupWizard(path); err != nil {
		fmt.Fprintln(os.Stderr, "настройка:", err)
		os.Exit(1)
	}
	fmt.Println("Если служба уже запущена, перезапустите её: stop, затем start.")
}

func cmdInstall(args []string) {
	fs := flag.NewFlagSet("install", flag.ExitOnError)
	cfgFlag := fs.String("config", "", "путь к файлу настроек")
	_ = fs.Parse(args)

	exe, err := os.Executable()
	if err != nil {
		fmt.Fprintln(os.Stderr, "не нашёл собственный путь:", err)
		os.Exit(1)
	}
	// Настройки создаём до регистрации: пусть человек увидит ключ сразу, а не после первого сбоя.
	cfgFile := configPath(*cfgFlag)
	if _, err := ensureConfig(cfgFile, true); err != nil {
		fmt.Fprintln(os.Stderr, "настройки:", err)
		os.Exit(1)
	}

	m, err := mgr.Connect()
	if err != nil {
		fmt.Fprintln(os.Stderr, "нет доступа к диспетчеру служб (запустите от администратора):", err)
		os.Exit(1)
	}
	defer m.Disconnect()

	if s, err := m.OpenService(serviceName); err == nil {
		s.Close()
		fmt.Printf("служба %s уже зарегистрирована\n", serviceName)
		return
	}

	svcArgs := []string{}
	if *cfgFlag != "" {
		svcArgs = append(svcArgs, "-config", cfgFile)
	}
	s, err := m.CreateService(serviceName, exe, mgr.Config{
		DisplayName:  serviceDisplay,
		Description:  serviceDesc,
		StartType:    mgr.StartAutomatic,
		ErrorControl: mgr.ErrorNormal,
	}, svcArgs...)
	if err != nil {
		fmt.Fprintln(os.Stderr, "не удалось создать службу:", err)
		os.Exit(1)
	}
	defer s.Close()

	fmt.Printf("служба %s зарегистрирована\n", serviceName)
	fmt.Printf("настройки: %s\n", cfgFile)
	fmt.Println("запуск:   antarus-sync.exe start")
}

func cmdUninstall() {
	m, err := mgr.Connect()
	if err != nil {
		fmt.Fprintln(os.Stderr, "нет доступа к диспетчеру служб (запустите от администратора):", err)
		os.Exit(1)
	}
	defer m.Disconnect()

	s, err := m.OpenService(serviceName)
	if err != nil {
		fmt.Fprintf(os.Stderr, "служба %s не зарегистрирована\n", serviceName)
		os.Exit(1)
	}
	defer s.Close()

	_, _ = s.Control(svc.Stop)
	if err := s.Delete(); err != nil {
		fmt.Fprintln(os.Stderr, "не удалось удалить службу:", err)
		os.Exit(1)
	}
	fmt.Printf("служба %s удалена\n", serviceName)
}

func cmdControl(action string) {
	m, err := mgr.Connect()
	if err != nil {
		fmt.Fprintln(os.Stderr, "нет доступа к диспетчеру служб (запустите от администратора):", err)
		os.Exit(1)
	}
	defer m.Disconnect()

	s, err := m.OpenService(serviceName)
	if err != nil {
		fmt.Fprintf(os.Stderr, "служба %s не зарегистрирована — сначала install\n", serviceName)
		os.Exit(1)
	}
	defer s.Close()

	switch action {
	case "start":
		if err := s.Start(); err != nil {
			fmt.Fprintln(os.Stderr, "не удалось запустить:", err)
			os.Exit(1)
		}
		fmt.Println("служба запущена")
	case "stop":
		if _, err := s.Control(svc.Stop); err != nil {
			fmt.Fprintln(os.Stderr, "не удалось остановить:", err)
			os.Exit(1)
		}
		fmt.Println("служба остановлена")
	}
}

func cmdAddKey(args []string) {
	fs := flag.NewFlagSet("addkey", flag.ExitOnError)
	write := fs.Bool("write", false, "разрешить машине запись (отправку конфига)")
	cfgFlag := fs.String("config", "", "путь к файлу настроек")
	_ = fs.Parse(args)

	name := strings.TrimSpace(strings.Join(fs.Args(), " "))
	if name == "" {
		fmt.Fprintln(os.Stderr, "укажите имя машины: antarus-sync.exe addkey naladchik-1 [-write]")
		os.Exit(2)
	}

	path := configPath(*cfgFlag)
	cfg, _, err := LoadConfig(path)
	if err != nil {
		fmt.Fprintln(os.Stderr, "настройки:", err)
		os.Exit(1)
	}
	for _, cl := range cfg.Clients {
		if strings.EqualFold(cl.Name, name) {
			fmt.Fprintf(os.Stderr, "машина %q уже есть в списке\n", name)
			os.Exit(1)
		}
	}
	key := NewKey()
	cfg.Clients = append(cfg.Clients, Client{Name: name, Key: key, CanWrite: *write})
	if err := SaveConfig(path, cfg); err != nil {
		fmt.Fprintln(os.Stderr, "не удалось сохранить настройки:", err)
		os.Exit(1)
	}
	fmt.Printf("машина: %s\nключ:   %s\nзапись: %v\n", name, key, *write)
	fmt.Println("\nКлюч показывается один раз здесь и хранится в файле настроек.")
	fmt.Println("Чтобы служба увидела нового клиента, перезапустите её: stop, затем start.")
}

func cmdKeys(args []string) {
	fs := flag.NewFlagSet("keys", flag.ExitOnError)
	cfgFlag := fs.String("config", "", "путь к файлу настроек")
	_ = fs.Parse(args)

	cfg, _, err := LoadConfig(configPath(*cfgFlag))
	if err != nil {
		fmt.Fprintln(os.Stderr, "настройки:", err)
		os.Exit(1)
	}
	if len(cfg.Clients) == 0 {
		fmt.Println("ключей нет")
		return
	}
	fmt.Printf("%-32s %-8s %-10s %s\n", "МАШИНА", "ЗАПИСЬ", "СОСТОЯНИЕ", "КЛЮЧ")
	for _, cl := range cfg.Clients {
		state := "включён"
		if cl.Disabled {
			state = "отключён"
		}
		fmt.Printf("%-32s %-8v %-10s %s\n", cl.Name, cl.CanWrite, state, cl.Key)
	}
}
