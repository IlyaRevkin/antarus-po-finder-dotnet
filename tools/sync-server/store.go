package main

import (
	"encoding/json"
	"errors"
	"os"
	"path/filepath"
	"sync"
)

// Store — хранилище двух объектов обмена: маркера ревизии и снимка конфига.
//
// Никакой базы: два файла на диске, ровно как на сетевой шаре, которую этот сервис заменяет.
// Сервис НЕ разбирает содержимое — он возвращает байт в байт то, что клиент прислал. Формат
// маркера и шифрование снимка живут на стороне приложения, и знать про них тут незачем: чем
// меньше сервис понимает в данных, тем меньше поводов их испортить при обновлении.
type Store struct {
	dir string
	// mu сериализует запись. Два администратора, нажавшие «отправить» одновременно, иначе
	// перемешали бы куски двух снимков в одном файле.
	mu sync.RWMutex
}

const (
	revisionFile = "revision.json"
	configFile   = "config.bin"
)

func NewStore(dir string) (*Store, error) {
	if err := os.MkdirAll(dir, 0o755); err != nil {
		return nil, err
	}
	return &Store{dir: dir}, nil
}

// Read возвращает содержимое объекта. nil без ошибки — объекта ещё нет: это штатное состояние
// чистого сервера, и клиент обязан трактовать его так же, как «на шаре файла нет».
func (s *Store) Read(name string) ([]byte, error) {
	s.mu.RLock()
	defer s.mu.RUnlock()
	raw, err := os.ReadFile(filepath.Join(s.dir, name))
	if errors.Is(err, os.ErrNotExist) {
		return nil, nil
	}
	if err != nil {
		return nil, err
	}
	return raw, nil
}

func (s *Store) Write(name string, data []byte) error {
	s.mu.Lock()
	defer s.mu.Unlock()
	return writeFileAtomic(filepath.Join(s.dir, name), data)
}

// CurrentRevision — номер ревизии из лежащего маркера, 0 если маркера нет или он не читается.
// Единственное место, где сервис заглядывает внутрь данных, и только ради проверки «не старее ли
// присланное» (см. Config.RejectStaleRevision).
func (s *Store) CurrentRevision() int {
	raw, err := s.Read(revisionFile)
	if err != nil || len(raw) == 0 {
		return 0
	}
	var marker struct {
		Revision int `json:"Revision"`
	}
	if err := json.Unmarshal(raw, &marker); err != nil {
		return 0
	}
	return marker.Revision
}

// writeFileAtomic пишет во временный файл и переименовывает поверх.
//
// Это не перестраховка: в снимке лежит ВЕСЬ каталог прошивок. Обрыв связи или падение процесса
// посреди прямой записи оставили бы обрезанный файл, который выглядит как настоящий, — и все
// машины приняли бы его как новый конфиг. Переименование внутри одного тома атомарно, поэтому
// клиент видит либо старый снимок целиком, либо новый целиком.
func writeFileAtomic(path string, data []byte) error {
	dir := filepath.Dir(path)
	if err := os.MkdirAll(dir, 0o755); err != nil {
		return err
	}
	tmp, err := os.CreateTemp(dir, filepath.Base(path)+".tmp-*")
	if err != nil {
		return err
	}
	tmpName := tmp.Name()
	defer os.Remove(tmpName) // если до переименования не дошли — мусор не остаётся

	if _, err := tmp.Write(data); err != nil {
		tmp.Close()
		return err
	}
	// Без Sync переименование может опередить фактическую запись на диск: при отключении
	// питания сервера получили бы пустой файл под правильным именем.
	if err := tmp.Sync(); err != nil {
		tmp.Close()
		return err
	}
	if err := tmp.Close(); err != nil {
		return err
	}
	// В Windows os.Rename поверх существующего файла работает (MoveFileEx с REPLACE_EXISTING).
	return os.Rename(tmpName, path)
}
