package sources

import (
	"encoding/json"
	"os"
	"sort"
	"sync"
	"time"
)

// SiteHealth 是单个站点的健康度统计（指数衰减，近期结果权重更大）。
type SiteHealth struct {
	Key      string  `json:"key"`
	OK       float64 `json:"ok"`
	Fail     float64 `json:"fail"`
	Samples  int     `json:"samples"`
	AvgMS    float64 `json:"avgMs"`
	LastOK   int64   `json:"lastOk"`
	LastFail int64   `json:"lastFail"`
	LastErr  string  `json:"lastErr,omitempty"`
	Score    float64 `json:"score"` // 0~1，拉普拉斯平滑后的成功率
	Benched  bool    `json:"benched"`
}

const (
	healthDecay      = 0.97             // 每次记录对历史的衰减
	benchScore       = 0.25             // 低于此分且样本足够 → 暂时停用
	benchMinSamples  = 6                // 至少这么多样本才判定停用
	benchRetryPeriod = 20 * time.Minute // 停用站点每隔多久放行一次探测
)

// Health 记录并持久化各站点的成败与延迟。线程安全。
type Health struct {
	mu    sync.Mutex
	m     map[string]*SiteHealth
	path  string
	dirty bool
	saved time.Time
}

// NewHealth 创建统计器；path 非空时从该文件加载并在变更后（最多每 30 秒）落盘。
func NewHealth(path string) *Health {
	h := &Health{m: map[string]*SiteHealth{}, path: path}
	if path != "" {
		if b, err := os.ReadFile(path); err == nil {
			var list []*SiteHealth
			if json.Unmarshal(b, &list) == nil {
				for _, s := range list {
					h.m[s.Key] = s
				}
			}
		}
	}
	return h
}

func (s *SiteHealth) recompute() {
	s.Score = (s.OK + 1) / (s.OK + s.Fail + 2)
	s.Benched = s.Samples >= benchMinSamples && s.Score < benchScore
}

// Record 记录一次调用结果。errMsg 为空表示成功。
func (h *Health) Record(key string, d time.Duration, errMsg string) {
	h.mu.Lock()
	defer h.mu.Unlock()
	s := h.m[key]
	if s == nil {
		s = &SiteHealth{Key: key}
		h.m[key] = s
	}
	s.OK *= healthDecay
	s.Fail *= healthDecay
	now := time.Now().Unix()
	if errMsg == "" {
		s.OK++
		s.LastOK = now
		if s.AvgMS == 0 {
			s.AvgMS = float64(d.Milliseconds())
		} else {
			s.AvgMS = s.AvgMS*0.8 + float64(d.Milliseconds())*0.2
		}
	} else {
		s.Fail++
		s.LastFail = now
		if len(errMsg) > 120 {
			errMsg = errMsg[:120]
		}
		s.LastErr = errMsg
	}
	s.Samples++
	if s.Samples > 1000 {
		s.Samples = 1000
	}
	s.recompute()
	h.dirty = true
	h.saveLocked(false)
}

// Score 返回站点评分；未知站点给中性偏上的分，让新站点有机会被试到。
func (h *Health) Score(key string) float64 {
	h.mu.Lock()
	defer h.mu.Unlock()
	if s := h.m[key]; s != nil {
		return s.Score
	}
	return 0.6
}

// Usable 判断站点当前是否应被使用：被停用的站点每隔 benchRetryPeriod 放行一次探测。
func (h *Health) Usable(key string) bool {
	h.mu.Lock()
	defer h.mu.Unlock()
	s := h.m[key]
	if s == nil || !s.Benched {
		return true
	}
	return time.Since(time.Unix(s.LastFail, 0)) > benchRetryPeriod
}

// Rank 按评分从高到低排序（同分按延迟低优先），并剔除当前停用的站点。
func (h *Health) Rank(keys []string) []string {
	type kv struct {
		k  string
		sc float64
		ms float64
	}
	var list []kv
	h.mu.Lock()
	for _, k := range keys {
		s := h.m[k]
		item := kv{k: k, sc: 0.6}
		if s != nil {
			if s.Benched && time.Since(time.Unix(s.LastFail, 0)) <= benchRetryPeriod {
				continue
			}
			item.sc, item.ms = s.Score, s.AvgMS
		}
		list = append(list, item)
	}
	h.mu.Unlock()
	sort.SliceStable(list, func(i, j int) bool {
		if d := list[i].sc - list[j].sc; d > 0.05 || d < -0.05 {
			return list[i].sc > list[j].sc
		}
		return list[i].ms < list[j].ms
	})
	out := make([]string, len(list))
	for i, it := range list {
		out[i] = it.k
	}
	return out
}

// Snapshot 返回全部站点统计（供 /api/sites/health 展示）。
func (h *Health) Snapshot() []SiteHealth {
	h.mu.Lock()
	defer h.mu.Unlock()
	out := make([]SiteHealth, 0, len(h.m))
	for _, s := range h.m {
		out = append(out, *s)
	}
	sort.Slice(out, func(i, j int) bool { return out[i].Score > out[j].Score })
	return out
}

// Flush 立即落盘。
func (h *Health) Flush() {
	h.mu.Lock()
	defer h.mu.Unlock()
	h.saveLocked(true)
}

func (h *Health) saveLocked(force bool) {
	if h.path == "" || !h.dirty || (!force && time.Since(h.saved) < 30*time.Second) {
		return
	}
	list := make([]*SiteHealth, 0, len(h.m))
	for _, s := range h.m {
		list = append(list, s)
	}
	b, err := json.Marshal(list)
	if err != nil {
		return
	}
	tmp := h.path + ".tmp"
	if os.WriteFile(tmp, b, 0o644) == nil && os.Rename(tmp, h.path) == nil {
		h.dirty = false
		h.saved = time.Now()
	}
}
