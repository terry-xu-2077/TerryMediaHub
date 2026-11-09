// Package subscriptions 管理多个订阅源（TVBox/影视仓配置）：增删、启停、测试、合并加载、热更新。
package subscriptions

import (
	"context"
	"crypto/sha1"
	"encoding/hex"
	"encoding/json"
	"fmt"
	"net/url"
	"os"
	"path/filepath"
	"sort"
	"strings"
	"sync"
	"time"

	"terryhub/internal/adapters/sources"
	"terryhub/internal/domain/tvbox"
)

// DefaultID 是内置默认源的 ID；它的站点 key 不加前缀，保持与旧版收藏/历史兼容。
const DefaultID = "default"

// Source 是一个订阅源。
type Source struct {
	ID      string `json:"id"`
	Name    string `json:"name"`
	URL     string `json:"url"`
	Enabled bool   `json:"enabled"`
	AddedAt int64  `json:"addedAt"`
}

// Status 是订阅源及其最近一次加载结果。
type Status struct {
	Source
	Loading   bool           `json:"loading"`
	Sites     int            `json:"sites"`  // 配置里的站点总数
	Usable    int            `json:"usable"` // 有对应解析器的站点数
	ByKind    map[string]int `json:"byKind"` // 按类型统计（csp/cms/js/py/other）
	Error     string         `json:"error,omitempty"`
	LoadedAt  int64          `json:"loadedAt,omitempty"`
	FromCache bool           `json:"fromCache,omitempty"` // 网络失败，使用了上次缓存的配置
}

// Preview 是「测试订阅」的结果（不落盘）。
type Preview struct {
	Sites  int            `json:"sites"`
	Usable int            `json:"usable"`
	ByKind map[string]int `json:"byKind"`
	Spider string         `json:"spider"`
}

// Manager 管理订阅源并把合并后的配置交给 apply。
type Manager struct {
	dir      string
	apply    func(*tvbox.Config)
	prepare  func(context.Context)
	supports func(tvbox.Site) bool
	logf     func(string, ...any)

	mu      sync.Mutex
	sources []Source
	status  map[string]*Status

	reloadMu  sync.Mutex // 串行化 Reload
	reloading bool
	pending   bool
}

// NewManager 创建管理器。dataDir 下使用 store/sources.json、subscriptions/、jars/。
// 若还没有任何源且 seedURL 非空，则把它作为默认源。
func NewManager(dataDir, seedURL string, supports func(tvbox.Site) bool,
	apply func(*tvbox.Config), prepare func(context.Context), logf func(string, ...any)) *Manager {
	m := &Manager{
		dir: dataDir, apply: apply, prepare: prepare, supports: supports, logf: logf,
		status: map[string]*Status{},
	}
	if b, err := os.ReadFile(m.sourcesFile()); err == nil {
		_ = json.Unmarshal(b, &m.sources)
	}
	for i := range m.sources { // 兼容旧数据：名称里的 punycode 还原成原文
		if strings.Contains(m.sources[i].Name, "xn--") {
			m.sources[i].Name = decodePunycodeHost(m.sources[i].Name)
		}
	}
	if len(m.sources) == 0 && seedURL != "" {
		m.sources = []Source{{ID: DefaultID, Name: displayName(seedURL, "默认源"), URL: seedURL, Enabled: true, AddedAt: time.Now().Unix()}}
		m.saveLocked()
	}
	return m
}

func (m *Manager) sourcesFile() string { return filepath.Join(m.dir, "store", "sources.json") }
func (m *Manager) cacheFile(id string) string {
	return filepath.Join(m.dir, "subscriptions", id+".json")
}
func (m *Manager) jarDir() string { return filepath.Join(m.dir, "jars") }

func (m *Manager) saveLocked() {
	_ = os.MkdirAll(filepath.Dir(m.sourcesFile()), 0o755)
	if b, err := json.MarshalIndent(m.sources, "", "  "); err == nil {
		_ = os.WriteFile(m.sourcesFile(), b, 0o644)
	}
}

// displayName 用域名（解码 punycode 显示）作为默认名称。
func displayName(rawURL, fallback string) string {
	if u, err := url.Parse(rawURL); err == nil && u.Hostname() != "" {
		return decodePunycodeHost(u.Hostname())
	}
	return fallback
}

// decodePunycodeHost 把 xn-- 开头的域名标签还原成中文等原文（RFC 3492），失败则原样返回。
func decodePunycodeHost(host string) string {
	parts := strings.Split(host, ".")
	for i, p := range parts {
		if strings.HasPrefix(p, "xn--") {
			if d, ok := punyDecode(p[4:]); ok {
				parts[i] = d
			}
		}
	}
	return strings.Join(parts, ".")
}

func punyDecode(in string) (string, bool) {
	const base, tmin, tmax, skew, damp = 36, 1, 26, 38, 700
	n, i, bias := 128, 0, 72
	var out []rune
	if b := strings.LastIndex(in, "-"); b >= 0 {
		for _, c := range in[:b] {
			out = append(out, c)
		}
		in = in[b+1:]
	}
	digit := func(c byte) int {
		switch {
		case c >= 'a' && c <= 'z':
			return int(c - 'a')
		case c >= 'A' && c <= 'Z':
			return int(c - 'A')
		case c >= '0' && c <= '9':
			return int(c-'0') + 26
		}
		return -1
	}
	for pos := 0; pos < len(in); {
		oldi, w := i, 1
		for k := base; ; k += base {
			if pos >= len(in) {
				return "", false
			}
			d := digit(in[pos])
			pos++
			if d < 0 {
				return "", false
			}
			i += d * w
			t := k - bias
			if t < tmin {
				t = tmin
			} else if t > tmax {
				t = tmax
			}
			if d < t {
				break
			}
			w *= base - t
		}
		x := len(out) + 1
		delta := i - oldi
		if oldi == 0 {
			delta /= damp
		} else {
			delta /= 2
		}
		delta += delta / x
		k := 0
		for delta > ((base-tmin)*tmax)/2 {
			delta /= base - tmin
			k += base
		}
		bias = k + (base-tmin+1)*delta/(delta+skew)
		n += i / x
		i %= x
		out = append(out[:i], append([]rune{rune(n)}, out[i:]...)...)
		i++
	}
	return string(out), true
}

func normalizeURL(raw string) (string, error) {
	raw = strings.TrimSpace(raw)
	if raw == "" {
		return "", fmt.Errorf("地址不能为空")
	}
	if !strings.Contains(raw, "://") {
		raw = "http://" + raw
	}
	u, err := url.Parse(raw)
	if err != nil || u.Host == "" || (u.Scheme != "http" && u.Scheme != "https") {
		return "", fmt.Errorf("地址格式不正确（需要 http:// 或 https://）")
	}
	return raw, nil
}

func newID(rawURL string) string {
	sum := sha1.Sum([]byte(rawURL))
	return hex.EncodeToString(sum[:])[:6]
}

// List 返回所有订阅源及状态。
func (m *Manager) List() []Status {
	m.mu.Lock()
	defer m.mu.Unlock()
	out := make([]Status, 0, len(m.sources))
	for _, s := range m.sources {
		st := Status{Source: s, ByKind: map[string]int{}}
		if cur := m.status[s.ID]; cur != nil {
			st = *cur
			st.Source = s
		}
		out = append(out, st)
	}
	return out
}

// Add 添加订阅源（不触发加载，调用方随后 ReloadAsync）。
func (m *Manager) Add(name, rawURL string) (Source, error) {
	u, err := normalizeURL(rawURL)
	if err != nil {
		return Source{}, err
	}
	m.mu.Lock()
	defer m.mu.Unlock()
	for _, s := range m.sources {
		if s.URL == u {
			return Source{}, fmt.Errorf("该订阅已存在：%s", s.Name)
		}
	}
	if strings.TrimSpace(name) == "" {
		name = displayName(u, "订阅")
	}
	src := Source{ID: newID(u), Name: strings.TrimSpace(name), URL: u, Enabled: true, AddedAt: time.Now().Unix()}
	m.sources = append(m.sources, src)
	m.saveLocked()
	return src, nil
}

// Remove 删除订阅源。
func (m *Manager) Remove(id string) {
	m.mu.Lock()
	defer m.mu.Unlock()
	out := m.sources[:0]
	for _, s := range m.sources {
		if s.ID != id {
			out = append(out, s)
		}
	}
	m.sources = out
	delete(m.status, id)
	m.saveLocked()
	_ = os.Remove(m.cacheFile(id))
}

// SetEnabled 启用/停用订阅源。
func (m *Manager) SetEnabled(id string, on bool) {
	m.mu.Lock()
	defer m.mu.Unlock()
	for i := range m.sources {
		if m.sources[i].ID == id {
			m.sources[i].Enabled = on
		}
	}
	m.saveLocked()
}

func (m *Manager) classify(cfg *tvbox.Config) (total, usable int, byKind map[string]int) {
	byKind = map[string]int{}
	for _, s := range cfg.Sites {
		total++
		byKind[string(s.Kind())]++
		if m.supports(s) {
			usable++
		}
	}
	return
}

// Test 拉取并解码订阅，返回站点统计，不保存、不下载 spider。
func (m *Manager) Test(ctx context.Context, rawURL string) (*Preview, error) {
	u, err := normalizeURL(rawURL)
	if err != nil {
		return nil, err
	}
	cfg, err := sources.LoadSubscription(ctx, u, "")
	if err != nil {
		return nil, err
	}
	total, usable, byKind := m.classify(cfg)
	return &Preview{Sites: total, Usable: usable, ByKind: byKind, Spider: cfg.Spider}, nil
}

// ReloadAsync 在后台重新加载全部启用的订阅。短时间内多次调用会合并：
// 正在加载时只记一个「稍后再来一次」标记，避免连续添加多个源时重复加载。
func (m *Manager) ReloadAsync() {
	m.mu.Lock()
	if m.reloading {
		m.pending = true
		m.mu.Unlock()
		return
	}
	m.reloading = true
	m.mu.Unlock()
	go func() {
		for {
			m.Reload(context.Background())
			m.mu.Lock()
			if !m.pending {
				m.reloading = false
				m.mu.Unlock()
				return
			}
			m.pending = false
			m.mu.Unlock()
		}
	}()
}

// Reload 加载所有启用的订阅：拉配置（失败回退缓存）→ 下载 spider jar → 合并 → apply。
// 同一时刻只会有一个 Reload 在跑；返回合并后的配置。
func (m *Manager) Reload(ctx context.Context) *tvbox.Config {
	m.reloadMu.Lock()
	defer m.reloadMu.Unlock()

	m.mu.Lock()
	srcs := append([]Source(nil), m.sources...)
	for _, s := range srcs {
		if st := m.status[s.ID]; st != nil {
			st.Loading = s.Enabled
		} else {
			m.status[s.ID] = &Status{Source: s, Loading: s.Enabled, ByKind: map[string]int{}}
		}
	}
	m.mu.Unlock()

	type loaded struct {
		src Source
		cfg *tvbox.Config
	}
	results := make([]*loaded, len(srcs))
	var wg sync.WaitGroup
	sem := make(chan struct{}, 3)
	for i, s := range srcs {
		if !s.Enabled {
			continue
		}
		wg.Add(1)
		sem <- struct{}{}
		go func(i int, s Source) {
			defer wg.Done()
			defer func() { <-sem }()
			cfg, st := m.loadOne(ctx, s)
			m.mu.Lock()
			m.status[s.ID] = st
			m.mu.Unlock()
			if cfg != nil {
				results[i] = &loaded{s, cfg}
			}
		}(i, s)
	}
	wg.Wait()

	merged := &tvbox.Config{}
	seenParse := map[string]bool{}
	var jarsChanged bool
	for _, r := range results {
		if r == nil {
			continue
		}
		jarsChanged = true
		if merged.Spider == "" {
			merged.Spider = r.cfg.Spider
		}
		merged.Sites = append(merged.Sites, r.cfg.Sites...)
		for _, p := range r.cfg.Parses {
			if !seenParse[p.Url] {
				seenParse[p.Url] = true
				merged.Parses = append(merged.Parses, p)
			}
		}
	}
	if jarsChanged && m.prepare != nil {
		m.prepare(ctx) // 把新下载的 spider jar 预先转成 class，避免首次调用超时
	}
	if m.apply != nil {
		m.apply(merged)
	}
	return merged
}

// loadOne 加载单个订阅：拉取失败回退到本地缓存；然后下载 spider jar 并改写站点的 key/jar。
func (m *Manager) loadOne(ctx context.Context, s Source) (*tvbox.Config, *Status) {
	st := &Status{Source: s, ByKind: map[string]int{}, LoadedAt: time.Now().Unix()}
	cfg, err := sources.LoadSubscription(ctx, s.URL, "")
	if err != nil {
		st.Error = err.Error()
		if b, e := os.ReadFile(m.cacheFile(s.ID)); e == nil {
			if c, e2 := sources.ParseConfig(b, s.URL); e2 == nil {
				cfg, st.FromCache = c, true
			}
		}
		if cfg == nil {
			return nil, st
		}
	} else if b, e := json.Marshal(cfg); e == nil {
		_ = os.MkdirAll(filepath.Dir(m.cacheFile(s.ID)), 0o755)
		_ = os.WriteFile(m.cacheFile(s.ID), b, 0o644)
	}
	st.Sites, st.Usable, st.ByKind = m.classify(cfg)

	// 下载全局与站点专用 spider jar；个别失败只影响对应站点。
	if _, err := sources.EnsureSpiderJars(ctx, cfg, m.jarDir(), func(f string, a ...any) { m.logf("["+s.Name+"] "+f, a...) }); err != nil {
		if st.Error == "" {
			st.Error = err.Error()
		}
	}
	prefix := ""
	if s.ID != DefaultID {
		prefix = s.ID + "/"
	}
	for i := range cfg.Sites {
		site := &cfg.Sites[i]
		site.Key = prefix + site.Key
		if site.IsCsp() {
			spec := site.Jar
			if spec == "" {
				spec = cfg.Spider
			}
			if _, name := sources.SpiderJarName(spec); name != "" {
				site.Jar = name // Host 按文件名优先加载该站点所属订阅的 jar
			}
		}
	}
	return cfg, st
}

// Names 返回按添加顺序排列的源名称（诊断用）。
func (m *Manager) Names() []string {
	m.mu.Lock()
	defer m.mu.Unlock()
	var out []string
	for _, s := range m.sources {
		out = append(out, s.Name)
	}
	sort.Strings(out)
	return out
}
