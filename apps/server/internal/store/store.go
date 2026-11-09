// Package store 用原子写的 JSON 文件持久化用户数据（收藏、播放记录）与首页快照。
package store

import (
	"encoding/json"
	"os"
	"path/filepath"
	"sync"
	"time"
)

// Entry 是收藏/历史共有的作品信息。Key 为归一化片名，换源后记录不丢。
type Entry struct {
	Key      string `json:"key"`
	Site     string `json:"site"`
	VodID    string `json:"vodId"`
	Name     string `json:"name"`
	Pic      string `json:"pic"`
	Year     string `json:"year,omitempty"`
	TypeName string `json:"typeName,omitempty"`
	Remarks  string `json:"remarks,omitempty"` // 如「更新至20集」
	Eps      int    `json:"eps,omitempty"`     // 当前可播集数
	AddedAt  int64  `json:"addedAt"`
	Checked  int64  `json:"checkedAt,omitempty"` // 后台上次刷新时间
	Stale    bool   `json:"stale,omitempty"`     // 刷新失败且找不到替代源
}

// History 是一条播放记录。
type History struct {
	Entry
	Episode   string  `json:"episode"`
	Position  float64 `json:"position"` // 秒
	Duration  float64 `json:"duration"`
	WatchedAt int64   `json:"watchedAt"`
}

// Section 是首页快照的一行。
type Section struct {
	Items []json.RawMessage `json:"items"`
	At    int64             `json:"at"`
}

type userData struct {
	Favorites []Entry   `json:"favorites"`
	History   []History `json:"history"`
}

// Store 是线程安全的存储。
type Store struct {
	dir  string
	mu   sync.Mutex
	user userData
	snap map[string]Section
}

const maxHistory = 100

// Open 打开（或初始化）数据目录。
func Open(dir string) (*Store, error) {
	if err := os.MkdirAll(dir, 0o755); err != nil {
		return nil, err
	}
	s := &Store{dir: dir, snap: map[string]Section{}}
	readJSON(filepath.Join(dir, "user.json"), &s.user)
	readJSON(filepath.Join(dir, "snapshot.json"), &s.snap)
	if s.snap == nil {
		s.snap = map[string]Section{}
	}
	return s, nil
}

func readJSON(path string, v any) {
	if b, err := os.ReadFile(path); err == nil {
		_ = json.Unmarshal(b, v)
	}
}

func writeJSON(path string, v any) error {
	b, err := json.Marshal(v)
	if err != nil {
		return err
	}
	tmp := path + ".tmp"
	if err := os.WriteFile(tmp, b, 0o644); err != nil {
		return err
	}
	return os.Rename(tmp, path)
}

func (s *Store) saveUser() { _ = writeJSON(filepath.Join(s.dir, "user.json"), s.user) }

func now() int64 { return time.Now().Unix() }

// ---- 收藏 ----

func (s *Store) Favorites() []Entry {
	s.mu.Lock()
	defer s.mu.Unlock()
	return append([]Entry{}, s.user.Favorites...)
}

func (s *Store) IsFavorite(key string) bool {
	s.mu.Lock()
	defer s.mu.Unlock()
	for _, f := range s.user.Favorites {
		if f.Key == key {
			return true
		}
	}
	return false
}

func (s *Store) AddFavorite(e Entry) {
	s.mu.Lock()
	defer s.mu.Unlock()
	for _, f := range s.user.Favorites {
		if f.Key == e.Key {
			return
		}
	}
	e.AddedAt = now()
	s.user.Favorites = append([]Entry{e}, s.user.Favorites...)
	s.saveUser()
}

func (s *Store) RemoveFavorite(key string) {
	s.mu.Lock()
	defer s.mu.Unlock()
	out := make([]Entry, 0, len(s.user.Favorites))
	for _, f := range s.user.Favorites {
		if f.Key != key {
			out = append(out, f)
		}
	}
	s.user.Favorites = out
	s.saveUser()
}

// UpdateFavorite 后台刷新后回写（按 Key 定位）。
func (s *Store) UpdateFavorite(key string, fn func(*Entry)) {
	s.mu.Lock()
	defer s.mu.Unlock()
	for i := range s.user.Favorites {
		if s.user.Favorites[i].Key == key {
			fn(&s.user.Favorites[i])
		}
	}
	s.saveUser()
}

// ---- 历史 ----

func (s *Store) History() []History {
	s.mu.Lock()
	defer s.mu.Unlock()
	return append([]History{}, s.user.History...)
}

// RecentHistory 返回最近 n 条（已按观看时间倒序）。
func (s *Store) RecentHistory(n int) []History {
	h := s.History()
	if len(h) > n {
		h = h[:n]
	}
	return h
}

// PutHistory 写入/更新播放记录并置顶。
func (s *Store) PutHistory(h History) {
	s.mu.Lock()
	defer s.mu.Unlock()
	h.WatchedAt = now()
	if h.AddedAt == 0 {
		h.AddedAt = h.WatchedAt
	}
	list := []History{h}
	for _, old := range s.user.History {
		if old.Key != h.Key {
			list = append(list, old)
		}
	}
	if len(list) > maxHistory {
		list = list[:maxHistory]
	}
	s.user.History = list
	s.saveUser()
}

func (s *Store) RemoveHistory(key string) {
	s.mu.Lock()
	defer s.mu.Unlock()
	out := make([]History, 0, len(s.user.History))
	for _, h := range s.user.History {
		if h.Key != key {
			out = append(out, h)
		}
	}
	s.user.History = out
	s.saveUser()
}

func (s *Store) UpdateHistory(key string, fn func(*History)) {
	s.mu.Lock()
	defer s.mu.Unlock()
	for i := range s.user.History {
		if s.user.History[i].Key == key {
			fn(&s.user.History[i])
		}
	}
	s.saveUser()
}

// ---- 快照 ----

func (s *Store) SetSection(key string, items []json.RawMessage) {
	s.mu.Lock()
	defer s.mu.Unlock()
	s.snap[key] = Section{Items: items, At: now()}
	_ = writeJSON(filepath.Join(s.dir, "snapshot.json"), s.snap)
}

func (s *Store) Section(key string) (Section, bool) {
	s.mu.Lock()
	defer s.mu.Unlock()
	sec, ok := s.snap[key]
	return sec, ok
}

// SnapshotAt 返回所有快照中最旧的更新时间（任一缺失返回 0）。
func (s *Store) SnapshotAt(keys []string) int64 {
	s.mu.Lock()
	defer s.mu.Unlock()
	var oldest int64
	for i, k := range keys {
		sec, ok := s.snap[k]
		if !ok {
			return 0
		}
		if i == 0 || sec.At < oldest {
			oldest = sec.At
		}
	}
	return oldest
}
