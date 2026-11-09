// Package ranking 给推荐列表打分排序：评分 + 热度 + 近期更新加权，年代越久越减权。
//
// 规则沿用旧版 C# MediaSort（评分 − min(年龄,40)×0.15，同分新片优先），并补上热度与更新加成。
package ranking

import (
	"math"
	"regexp"
	"strconv"
	"strings"
	"sync"
	"time"
	"unicode"
)

// Hot 是外部榜单（豆瓣热门）里的一条：评分与榜内位次。
type Hot struct {
	Rate float64 // 0~10，0 表示无
	Rank int     // 从 1 开始，0 表示不在榜
}

const (
	defaultRating  = 6.0  // 无评分时的基准分
	unknownAge     = 10   // 年份未知时按多少岁算
	agePenaltyYear = 0.15 // 每年扣分（同 C#），最多扣 40 年
	maxAge         = 40
	hotMaxBonus    = 2.5 // 榜首热度加成
	hotSpan        = 120.0
	ongoingBonus   = 0.6 // 正在更新（更新至N集/最新一期…）
	flagHotBonus   = 0.4 // 源站自己标了「最热」
	flagNewBonus   = 0.2 // 源站自己标了「最新」
)

var (
	yearRe   = regexp.MustCompile(`(?:^|[^\d])(19\d{2}|20\d{2})(?:[^\d]|$)`)
	scoreRe  = regexp.MustCompile(`⭐\s*(\d{1,2}(?:\.\d)?)\s*分`)
	ongoing  = []string{"更新至", "更新到", "连载", "期", "最新", "第", "集全", "HD", "抢先", "TC", "枪版"}
	suffixRe = regexp.MustCompile(`(第[一二三四五六七八九十百零\d]+[季部]|[\s·:：\-—]*(国语|粤语|日语|英语|中字|普通话)版?|全集|\(.*?\)|（.*?）|\d{4}$)`)
)

// Ranker 持有外部榜单并给条目打分。线程安全。
type Ranker struct {
	mu  sync.RWMutex
	hot map[string]Hot // 归一化片名 → 榜单信息
	now func() time.Time
}

// New 创建排序器。
func New() *Ranker { return &Ranker{hot: map[string]Hot{}, now: time.Now} }

// SetHot 整体替换外部榜单（key 为原始片名，内部归一化）。
func (r *Ranker) SetHot(list map[string]Hot) {
	m := make(map[string]Hot, len(list))
	for name, h := range list {
		k := Normalize(name)
		if k == "" {
			continue
		}
		if old, ok := m[k]; !ok || (h.Rank > 0 && (old.Rank == 0 || h.Rank < old.Rank)) {
			m[k] = h
		}
	}
	r.mu.Lock()
	r.hot = m
	r.mu.Unlock()
}

// HotSize 返回榜单条数（诊断用）。
func (r *Ranker) HotSize() int {
	r.mu.RLock()
	defer r.mu.RUnlock()
	return len(r.hot)
}

var dedupSuffixRe = regexp.MustCompile(`([\s·:：\-—]*(国语|粤语|日语|英语|中字|普通话)版?|全集|\(.*?\)|（.*?）|\d{4}$)`)

// DedupKey 用于同一作品的去重：只去掉年份、语言版本等后缀，保留「第N季」，
// 避免把不同季合并，又能合并「突如其来」与「突如其来2026」。
func DedupKey(s string) string {
	s = dedupSuffixRe.ReplaceAllString(s, "")
	var b strings.Builder
	for _, r := range s {
		if unicode.IsLetter(r) || unicode.IsDigit(r) {
			b.WriteRune(unicode.ToLower(r))
		}
	}
	return b.String()
}

// Normalize 归一化片名，用于跨来源匹配：去标点空白、季/版本后缀、结尾年份，转小写。
func Normalize(s string) string {
	s = suffixRe.ReplaceAllString(s, "")
	var b strings.Builder
	for _, r := range s {
		if unicode.IsLetter(r) || unicode.IsDigit(r) {
			b.WriteRune(unicode.ToLower(r))
		}
	}
	return b.String()
}

// Score 计算一条的综合分（越大越靠前）。
func (r *Ranker) Score(name, year, remarks string) float64 {
	now := r.now()
	r.mu.RLock()
	h, inHot := r.hot[Normalize(name)]
	r.mu.RUnlock()

	rating := defaultRating
	if inHot && h.Rate > 0 {
		rating = h.Rate
	} else if m := scoreRe.FindStringSubmatch(remarks); m != nil {
		if v, err := strconv.ParseFloat(m[1], 64); err == nil && v > 0 && v <= 10 {
			rating = v
		}
	}

	hot := 0.0
	if inHot && h.Rank > 0 {
		hot = hotMaxBonus * math.Max(0, 1-float64(h.Rank-1)/hotSpan)
	}

	fresh := 0.0
	for _, k := range ongoing {
		if strings.Contains(remarks, k) {
			fresh = ongoingBonus
			break
		}
	}
	if strings.Contains(remarks, "最热") {
		fresh += flagHotBonus
	}
	if strings.Contains(remarks, "最新") {
		fresh += flagNewBonus
	}

	age := unknownAge
	if y := ParseYear(year, name, now.Year()); y > 0 {
		age = max(0, now.Year()-y)
	}
	penalty := float64(min(age, maxAge)) * agePenaltyYear

	// 正在更新的作品只有在较新时才有「近期更新」意义：老剧的「第N集」不加分。
	if age > 3 {
		fresh = 0
	}
	return rating + hot + fresh - penalty
}

// ParseYear 从年份字段或片名里取出 4 位年份；取不到返回 0。
func ParseYear(year, name string, maxYear int) int {
	for _, s := range []string{year, name} {
		if m := yearRe.FindStringSubmatch(s); m != nil {
			if y, err := strconv.Atoi(m[1]); err == nil && y <= maxYear+1 {
				return y
			}
		}
	}
	return 0
}

// Item 是排序所需的最小字段集合。
type Item struct{ Name, Year, Remarks string }

// Order 返回按综合分降序的下标序列（同分：年份新者优先，其次保持原顺序）。
func (r *Ranker) Order(items []Item) []int {
	type scored struct {
		i    int
		s    float64
		year int
	}
	now := r.now().Year()
	list := make([]scored, len(items))
	for i, it := range items {
		list[i] = scored{i, r.Score(it.Name, it.Year, it.Remarks), ParseYear(it.Year, it.Name, now)}
	}
	// 稳定插入排序依赖 sort.SliceStable；这里手写以避免引入额外依赖面
	stableSort(list, func(a, b scored) bool {
		if math.Abs(a.s-b.s) > 1e-9 {
			return a.s > b.s
		}
		return a.year > b.year
	})
	out := make([]int, len(list))
	for k, s := range list {
		out[k] = s.i
	}
	return out
}

func stableSort[T any](s []T, less func(a, b T) bool) {
	for i := 1; i < len(s); i++ {
		for j := i; j > 0 && less(s[j], s[j-1]); j-- {
			s[j], s[j-1] = s[j-1], s[j]
		}
	}
}
