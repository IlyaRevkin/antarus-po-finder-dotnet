package main

import (
	"crypto/rand"
	"encoding/hex"
	"encoding/json"
	"fmt"
	"os"
	"path/filepath"
	"strings"
)

// Client — одна машина, которой разрешено ходить к сервису.
//
// Ключ на каждую машину свой, а не один общий на всех, ровно из-за двух вещей: в журнале видно,
// КТО пришёл, и отобрать доступ у одной машины можно, не меняя ключ всем остальным. Это и есть
// «идентификация»: сервис знает, с кем разговаривает, ещё до того, как отдаст хоть байт.
type Client struct {
	Name string `json:"name"`
	Key  string `json:"key"`
	// CanWrite — право на POST. Наладчику снаружи каталог нужен на чтение, и давать ему
	// перезапись общего снимка незачем: снимок перезаписывается ЦЕЛИКОМ, и одна ошибочная
	// отправка стирает чужую работу. По умолчанию false — право выдаётся осознанно.
	CanWrite bool `json:"can_write"`
	// Disabled — временно отключить машину, не удаляя запись (чтобы помнить, что ключ выдавался).
	Disabled bool `json:"disabled"`
}

type TLSConfig struct {
	CertFile string `json:"cert_file"`
	KeyFile  string `json:"key_file"`
}

type Config struct {
	// Listen — адрес и порт. По умолчанию 8443 на всех интерфейсах.
	Listen string `json:"listen"`
	// BasePath — префикс, если сервис публикуют не в корне (например, за обратным прокси
	// на /antarus/). Пустой или "/" — обычный случай.
	BasePath string `json:"base_path"`
	// TLS — если заданы оба файла, сервис слушает HTTPS сам. Если пусто, слушает HTTP:
	// это штатный вариант, когда TLS снимает обратный прокси перед ним.
	TLS TLSConfig `json:"tls"`

	DataDir string `json:"data_dir"`
	LogFile string `json:"log_file"`

	// MaxBodyMB — предел размера принимаемого объекта. Конфиг каталога измеряется мегабайтами,
	// но без предела любой желающий забьёт системный диск сервера одним запросом.
	MaxBodyMB int `json:"max_body_mb"`

	// ServerName — как сервис представляется в /ping. Пусто — берётся имя машины.
	ServerName string `json:"server_name"`

	Clients []Client `json:"clients"`

	// RejectStaleRevision — отклонять маркер ревизии, который не больше уже лежащего.
	//
	// ПО УМОЛЧАНИЮ ВЫКЛЮЧЕНО, и это осознанно: семантика сервиса обязана в первый день совпадать
	// с сетевой шарой один в один, иначе разбирать придётся не «работает ли обмен», а «что
	// изменилось». Включать стоит потом, когда станет видно, что четыре администратора
	// действительно затирают друг друга: тогда проигравший получит 409 вместо тихой потери
	// чужой записи.
	RejectStaleRevision bool `json:"reject_stale_revision"`

	// AllowedIPs — необязательный белый список (адреса или CIDR). Пусто — проверки нет.
	AllowedIPs []string `json:"allowed_ips"`
}

func defaultConfig() *Config {
	base := `C:\ProgramData\AntarusSync`
	return &Config{
		Listen:    ":8443",
		BasePath:  "/",
		DataDir:   filepath.Join(base, "data"),
		LogFile:   filepath.Join(base, "antarus-sync.log"),
		MaxBodyMB: 64,
		Clients:   []Client{},
	}
}

// NewKey — ключ доступа для одной машины. 32 байта из системного источника случайности:
// ключ живёт годами и переживает утечку журнала, поэтому «придумать словами» тут не годится.
func NewKey() string {
	buf := make([]byte, 32)
	if _, err := rand.Read(buf); err != nil {
		panic("нет источника случайных чисел: " + err.Error())
	}
	return hex.EncodeToString(buf)
}

// LoadConfig читает файл настроек. Если файла нет — создаёт с умолчаниями и одним ключом для
// первой машины, чтобы после установки не пришлось ничего сочинять руками.
func LoadConfig(path string) (*Config, bool, error) {
	created := false
	raw, err := os.ReadFile(path)
	if os.IsNotExist(err) {
		cfg := defaultConfig()
		cfg.Clients = append(cfg.Clients, Client{
			Name:     "первая-машина-переименуйте",
			Key:      NewKey(),
			CanWrite: true,
		})
		if err := SaveConfig(path, cfg); err != nil {
			return nil, false, err
		}
		created = true
		raw, err = os.ReadFile(path)
	}
	if err != nil {
		return nil, false, err
	}

	cfg := defaultConfig()
	if err := json.Unmarshal(raw, cfg); err != nil {
		return nil, false, fmt.Errorf("файл настроек %s повреждён: %w", path, err)
	}
	cfg.normalize()
	if err := cfg.validate(); err != nil {
		return nil, false, err
	}
	return cfg, created, nil
}

func SaveConfig(path string, cfg *Config) error {
	if err := os.MkdirAll(filepath.Dir(path), 0o755); err != nil {
		return err
	}
	raw, err := json.MarshalIndent(cfg, "", "  ")
	if err != nil {
		return err
	}
	return writeFileAtomic(path, raw)
}

func (c *Config) normalize() {
	c.Listen = strings.TrimSpace(c.Listen)
	if c.Listen == "" {
		c.Listen = ":8443"
	}
	// «8443» и «:8443» — одно и то же намерение; не заставляем помнить синтаксис Go.
	if !strings.Contains(c.Listen, ":") {
		c.Listen = ":" + c.Listen
	}
	c.BasePath = "/" + strings.Trim(strings.TrimSpace(c.BasePath), "/")
	if c.BasePath != "/" {
		c.BasePath += "/"
	}
	if c.MaxBodyMB <= 0 {
		c.MaxBodyMB = 64
	}
	if strings.TrimSpace(c.DataDir) == "" {
		c.DataDir = defaultConfig().DataDir
	}
	if strings.TrimSpace(c.ServerName) == "" {
		if host, err := os.Hostname(); err == nil {
			c.ServerName = host
		} else {
			c.ServerName = "unknown"
		}
	}
}

func (c *Config) validate() error {
	if (c.TLS.CertFile == "") != (c.TLS.KeyFile == "") {
		return fmt.Errorf("в настройках TLS задан только один файл из двух: нужны и cert_file, и key_file")
	}
	seen := map[string]string{}
	for i, cl := range c.Clients {
		if strings.TrimSpace(cl.Key) == "" {
			return fmt.Errorf("у клиента №%d (%q) пустой ключ", i+1, cl.Name)
		}
		if prev, dup := seen[cl.Key]; dup {
			return fmt.Errorf("один и тот же ключ выдан двум клиентам: %q и %q — тогда в журнале не различить, кто пришёл", prev, cl.Name)
		}
		seen[cl.Key] = cl.Name
	}
	return nil
}

// UseTLS — слушать ли HTTPS самим.
func (c *Config) UseTLS() bool { return c.TLS.CertFile != "" && c.TLS.KeyFile != "" }
