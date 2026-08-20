package main

import (
	"crypto/subtle"
	"encoding/json"
	"io"
	"log"
	"net"
	"net/http"
	"strings"
	"time"
)

// KeyHeader — заголовок с ключом доступа.
//
// Именно заголовок, а не Basic-авторизация, и это требование клиента (см. HttpSyncTransport в
// AntarusPoFinder.Core): Basic по дороге перехватывает Windows-редиректор и часть корпоративных
// прокси, начиная спрашивать учётные данные у человека посреди фоновой синхронизации.
const KeyHeader = "X-Antarus-Key"

const ServiceVersion = "1.0.0"

type Server struct {
	cfg   *Config
	store *Store
	log   *log.Logger
}

func NewServer(cfg *Config, store *Store, logger *log.Logger) *Server {
	return &Server{cfg: cfg, store: store, log: logger}
}

// Handler собирает маршруты.
//
// Только GET и POST. Ни PUT, ни PATCH, ни глаголов WebDAV: корпоративные прокси режут их
// регулярно, а отладить такое с рабочей машины почти невозможно — на клиенте это закреплено
// тестом, и сервер обязан не соблазнять никого добавить PUT «для красоты».
func (s *Server) Handler() http.Handler {
	mux := http.NewServeMux()
	p := s.cfg.BasePath // всегда с ведущим и хвостовым слешем, кроме корня

	route := func(name string) string {
		if p == "/" {
			return "/" + name
		}
		return p + name
	}

	mux.HandleFunc(route("ping"), s.withAuth(s.handlePing))
	mux.HandleFunc(route("revision"), s.withAuth(s.handleObject(revisionFile)))
	mux.HandleFunc(route("config"), s.withAuth(s.handleObject(configFile)))

	// Всё остальное — 404 с коротким текстом: чтобы человек, открывший адрес браузером,
	// понял, что попал в сервис, но адрес не тот.
	mux.HandleFunc("/", func(w http.ResponseWriter, r *http.Request) {
		http.Error(w, "antarus-sync: неизвестный путь. Рабочие: ping, revision, config", http.StatusNotFound)
	})

	return s.withLogging(mux)
}

type pingResponse struct {
	Service   string            `json:"service"`
	Version   string            `json:"version"`
	Server    string            `json:"server"`
	Time      string            `json:"time"`
	Client    string            `json:"client"`
	CanWrite  bool              `json:"can_write"`
	Revision  int               `json:"revision"`
	HasConfig bool              `json:"has_config"`
	Endpoints map[string]string `json:"endpoints"`
}

// handlePing — «постучались и узнали, куда слать дальше».
//
// Клиент старой версии смотрит только на код ответа, поэтому тело можно наполнять свободно.
// Здесь оно наполнено тем, что снимает вопросы при разборе жалоб: с каким сервером
// разговариваем, кем нас признали, есть ли право на запись и какая ревизия лежит сейчас.
// Список endpoints отдаётся явно, чтобы адреса не приходилось держать в голове или в коде.
func (s *Server) handlePing(w http.ResponseWriter, r *http.Request, cl *Client) {
	if r.Method != http.MethodGet {
		methodNotAllowed(w)
		return
	}
	body, _ := s.store.Read(configFile)
	resp := pingResponse{
		Service:   "antarus-sync",
		Version:   ServiceVersion,
		Server:    s.cfg.ServerName,
		Time:      time.Now().UTC().Format(time.RFC3339),
		Client:    cl.Name,
		CanWrite:  cl.CanWrite,
		Revision:  s.store.CurrentRevision(),
		HasConfig: len(body) > 0,
		Endpoints: map[string]string{
			"ping":     strings.TrimSuffix(s.cfg.BasePath, "/") + "/ping",
			"revision": strings.TrimSuffix(s.cfg.BasePath, "/") + "/revision",
			"config":   strings.TrimSuffix(s.cfg.BasePath, "/") + "/config",
		},
	}
	writeJSON(w, http.StatusOK, resp)
}

// handleObject — общий обработчик для маркера и снимка: GET отдаёт, POST записывает.
func (s *Server) handleObject(name string) func(http.ResponseWriter, *http.Request, *Client) {
	return func(w http.ResponseWriter, r *http.Request, cl *Client) {
		switch r.Method {
		case http.MethodGet:
			data, err := s.store.Read(name)
			if err != nil {
				s.log.Printf("ОШИБКА чтения %s: %v", name, err)
				http.Error(w, "не удалось прочитать объект", http.StatusInternalServerError)
				return
			}
			// Объекта ещё нет — 404. Клиент трактует это как «сведений нет», а не как поломку:
			// ровно так же ведёт себя пустая папка на шаре.
			if data == nil {
				http.Error(w, "объект ещё не записан", http.StatusNotFound)
				return
			}
			if name == revisionFile {
				w.Header().Set("Content-Type", "application/json; charset=utf-8")
			} else {
				w.Header().Set("Content-Type", "application/octet-stream")
			}
			w.WriteHeader(http.StatusOK)
			_, _ = w.Write(data)

		case http.MethodPost:
			if !cl.CanWrite {
				s.log.Printf("ОТКАЗ: %q пытался записать %s без права записи", cl.Name, name)
				http.Error(w, "у этого ключа нет права записи", http.StatusForbidden)
				return
			}
			limit := int64(s.cfg.MaxBodyMB) << 20
			data, err := io.ReadAll(http.MaxBytesReader(w, r.Body, limit))
			if err != nil {
				s.log.Printf("ОТКАЗ: %q прислал %s, который не прочитался (предел %d МБ): %v",
					cl.Name, name, s.cfg.MaxBodyMB, err)
				http.Error(w, "тело запроса не прочитано или превышает предел", http.StatusRequestEntityTooLarge)
				return
			}
			if len(data) == 0 {
				// Пустой снимок затёр бы каталог у всех. Это почти наверняка обрыв, а не намерение.
				http.Error(w, "пустое тело не принимается", http.StatusBadRequest)
				return
			}
			if name == revisionFile && s.cfg.RejectStaleRevision {
				if incoming := revisionOf(data); incoming > 0 {
					if current := s.store.CurrentRevision(); incoming <= current {
						s.log.Printf("ОТКАЗ: %q прислал ревизию %d, на сервере уже %d", cl.Name, incoming, current)
						http.Error(w, "на сервере более свежая ревизия", http.StatusConflict)
						return
					}
				}
			}
			if err := s.store.Write(name, data); err != nil {
				s.log.Printf("ОШИБКА записи %s от %q: %v", name, cl.Name, err)
				http.Error(w, "не удалось записать объект", http.StatusInternalServerError)
				return
			}
			s.log.Printf("записано %s от %q, %d байт", name, cl.Name, len(data))
			w.WriteHeader(http.StatusOK)

		default:
			methodNotAllowed(w)
		}
	}
}

func revisionOf(data []byte) int {
	var m struct {
		Revision int `json:"Revision"`
	}
	if err := json.Unmarshal(data, &m); err != nil {
		return 0
	}
	return m.Revision
}

// withAuth опознаёт машину по ключу.
//
// Сравнение постоянного времени: обычное сравнение строк отвечает тем быстрее, чем раньше
// разошлись символы, и по времени ответа ключ подбирается посимвольно.
func (s *Server) withAuth(next func(http.ResponseWriter, *http.Request, *Client)) http.HandlerFunc {
	return func(w http.ResponseWriter, r *http.Request) {
		if !s.ipAllowed(r) {
			s.log.Printf("ОТКАЗ по адресу: %s -> %s", r.RemoteAddr, r.URL.Path)
			http.Error(w, "адрес не в списке разрешённых", http.StatusForbidden)
			return
		}
		key := strings.TrimSpace(r.Header.Get(KeyHeader))
		if key == "" {
			http.Error(w, "нет заголовка "+KeyHeader, http.StatusUnauthorized)
			return
		}
		var found *Client
		for i := range s.cfg.Clients {
			cl := &s.cfg.Clients[i]
			if subtle.ConstantTimeCompare([]byte(cl.Key), []byte(key)) == 1 {
				found = cl
				break
			}
		}
		if found == nil {
			s.log.Printf("ОТКАЗ: неизвестный ключ с %s на %s", r.RemoteAddr, r.URL.Path)
			http.Error(w, "ключ не опознан", http.StatusUnauthorized)
			return
		}
		if found.Disabled {
			s.log.Printf("ОТКАЗ: ключ %q отключён", found.Name)
			http.Error(w, "доступ для этой машины отключён", http.StatusForbidden)
			return
		}
		next(w, r, found)
	}
}

func (s *Server) ipAllowed(r *http.Request) bool {
	if len(s.cfg.AllowedIPs) == 0 {
		return true
	}
	host, _, err := net.SplitHostPort(r.RemoteAddr)
	if err != nil {
		host = r.RemoteAddr
	}
	ip := net.ParseIP(host)
	if ip == nil {
		return false
	}
	for _, entry := range s.cfg.AllowedIPs {
		entry = strings.TrimSpace(entry)
		if entry == "" {
			continue
		}
		if strings.Contains(entry, "/") {
			if _, network, err := net.ParseCIDR(entry); err == nil && network.Contains(ip) {
				return true
			}
			continue
		}
		if allowed := net.ParseIP(entry); allowed != nil && allowed.Equal(ip) {
			return true
		}
	}
	return false
}

type statusRecorder struct {
	http.ResponseWriter
	status int
}

func (w *statusRecorder) WriteHeader(code int) {
	w.status = code
	w.ResponseWriter.WriteHeader(code)
}

func (s *Server) withLogging(next http.Handler) http.Handler {
	return http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		started := time.Now()
		rec := &statusRecorder{ResponseWriter: w, status: http.StatusOK}
		next.ServeHTTP(rec, r)
		s.log.Printf("%s %s %s -> %d за %s", r.RemoteAddr, r.Method, r.URL.Path, rec.status,
			time.Since(started).Round(time.Millisecond))
	})
}

func methodNotAllowed(w http.ResponseWriter) {
	w.Header().Set("Allow", "GET, POST")
	http.Error(w, "поддерживаются только GET и POST", http.StatusMethodNotAllowed)
}

func writeJSON(w http.ResponseWriter, code int, v any) {
	w.Header().Set("Content-Type", "application/json; charset=utf-8")
	w.WriteHeader(code)
	_ = json.NewEncoder(w).Encode(v)
}
