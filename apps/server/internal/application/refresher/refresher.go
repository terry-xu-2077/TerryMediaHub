// Package refresher 是后台定时任务：更新首页推荐、最近 5 条播放记录与收藏列表的片源。
package refresher

import (
	"context"
	"encoding/json"
	"log"
	"sync"
	"time"

	"terryhub/internal/adapters/douban"
	"terryhub/internal/adapters/sources"
	"terryhub/internal/application/catalog"
	"terryhub/internal/application/ranking"
	"terryhub/internal/store"
)

// Status 描述后台刷新状态，供前端展示。
type Status struct {
	Running    bool   `json:"running"`
	LastStart  int64  `json:"lastStart"`
	LastFinish int64  `json:"lastFinish"`
	NextRun    int64  `json:"nextRun"`
	Step       string `json:"step"`
}

// Refresher 周期性刷新。
type Refresher struct {
	cat      *catalog.Service
	ranker   *ranking.Ranker
	st       *store.Store
	Interval time.Duration

	mu     sync.Mutex
	status Status
	kick   chan struct{}
}

// HistoryRefreshCount 是每轮刷新的最近播放记录条数。
const HistoryRefreshCount = 5

// New 构造。
func New(c *catalog.Service, st *store.Store, rk *ranking.Ranker, interval time.Duration) *Refresher {
	r := &Refresher{cat: c, st: st, ranker: rk, Interval: interval, kick: make(chan struct{}, 1)}
	r.loadDouban()
	return r
}

const doubanKey = "_douban"

type doubanRow struct {
	Title string  `json:"t"`
	Rate  float64 `json:"r"`
	Rank  int     `json:"k"`
}

// loadDouban 启动时从快照恢复豆瓣榜单，保证重启后立即有评分/热度可用。
func (r *Refresher) loadDouban() {
	sec, ok := r.st.Section(doubanKey)
	if !ok {
		return
	}
	hot := map[string]ranking.Hot{}
	for _, raw := range sec.Items {
		var d doubanRow
		if json.Unmarshal(raw, &d) == nil {
			hot[d.Title] = ranking.Hot{Rate: d.Rate, Rank: d.Rank}
		}
	}
	r.ranker.SetHot(hot)
}

// refreshDouban 拉取豆瓣榜单并持久化；失败时沿用旧榜单。
func (r *Refresher) refreshDouban(ctx context.Context) {
	if sec, ok := r.st.Section(doubanKey); ok && time.Since(time.Unix(sec.At, 0)) < 6*time.Hour {
		return
	}
	hot, err := douban.Fetch(ctx)
	if err != nil || len(hot) == 0 {
		log.Printf("[refresh] 豆瓣榜单获取失败，沿用旧榜单（%d 条）: %v", r.ranker.HotSize(), err)
		return
	}
	r.ranker.SetHot(hot)
	rows := make([]json.RawMessage, 0, len(hot))
	for t, h := range hot {
		b, _ := json.Marshal(doubanRow{t, h.Rate, h.Rank})
		rows = append(rows, b)
	}
	r.st.SetSection(doubanKey, rows)
	log.Printf("[refresh] 豆瓣榜单 %d 条", len(hot))
}

// Status 返回当前状态。
func (r *Refresher) Status() Status {
	r.mu.Lock()
	defer r.mu.Unlock()
	return r.status
}

// Trigger 请求立即刷新一次（非阻塞）。
func (r *Refresher) Trigger() {
	select {
	case r.kick <- struct{}{}:
	default:
	}
}

func (r *Refresher) set(f func(*Status)) { r.mu.Lock(); f(&r.status); r.mu.Unlock() }

// SectionKeys 返回需要快照的分类 key。
func SectionKeys() []string {
	var ks []string
	for _, c := range catalog.Categories {
		ks = append(ks, c.Key)
	}
	return ks
}

// Run 阻塞运行直到 ctx 结束：快照未过期则等到过期，否则立即刷新，之后按间隔刷新。
func (r *Refresher) Run(ctx context.Context) {
	wait := time.Duration(0)
	if at := r.st.SnapshotAt(SectionKeys()); at > 0 {
		if age := time.Since(time.Unix(at, 0)); age < r.Interval {
			wait = r.Interval - age
		}
	}
	if wait == 0 {
		wait = 15 * time.Second // 等 JVM worker 预热（首次还要转换 dex）
	}
	for {
		next := time.Now().Add(wait).Unix()
		r.set(func(s *Status) { s.NextRun = next })
		select {
		case <-ctx.Done():
			return
		case <-time.After(wait):
		case <-r.kick:
		}
		wait = r.Interval
		if r.RefreshAll(ctx) == 0 {
			wait = time.Minute // 源站/worker 尚未就绪，1 分钟后重试
		}
	}
}

// RefreshAll 执行一轮完整刷新。
func (r *Refresher) RefreshAll(ctx context.Context) (ok int) {
	r.set(func(s *Status) { s.Running = true; s.LastStart = time.Now().Unix() })
	defer r.set(func(s *Status) { s.Running = false; s.LastFinish = time.Now().Unix(); s.Step = "" })
	fresh := catalog.WithFresh(ctx) // 列表拉取是 12 站并发的短突发，不受后台限流
	bg := catalog.WithFresh(sources.WithBackground(ctx))

	r.set(func(s *Status) { s.Step = "豆瓣榜单" })
	r.refreshDouban(ctx)

	// 1. 首页推荐：每个分类取第一页（已按综合分排序），失败则保留旧快照。
	for _, c := range catalog.Categories {
		if ctx.Err() != nil {
			return ok
		}
		label := c.Label
		r.set(func(s *Status) { s.Step = "推荐：" + label })
		items := r.cat.VerifyPlayable(bg, r.cat.Category(fresh, c.Key, 1), 30, 60)
		if len(items) == 0 {
			log.Printf("[refresh] %s 无数据，保留旧快照", c.Label)
			continue
		}
		raws := make([]json.RawMessage, 0, len(items))
		for _, it := range items {
			b, _ := json.Marshal(it)
			raws = append(raws, b)
		}
		r.st.SetSection(c.Key, raws)
		ok++
		log.Printf("[refresh] %s %d 条", c.Label, len(items))
	}

	// 2. 最近 5 条播放记录 + 全部收藏：刷新片源/更新集数。
	r.set(func(s *Status) { s.Step = "播放记录" })
	for _, h := range r.st.RecentHistory(HistoryRefreshCount) {
		if ctx.Err() != nil {
			return ok
		}
		e := h.Entry
		r.refreshEntry(bg, &e)
		r.st.UpdateHistory(h.Key, func(x *store.History) { x.Entry = e })
	}
	r.set(func(s *Status) { s.Step = "收藏" })
	for _, f := range r.st.Favorites() {
		if ctx.Err() != nil {
			return ok
		}
		e := f
		r.refreshEntry(bg, &e)
		r.st.UpdateFavorite(f.Key, func(x *store.Entry) { *x = e })
	}
	return ok
}

// refreshEntry 刷新单个作品：先查原站详情；失败则按片名全网找替代源并换源。
func (r *Refresher) refreshEntry(ctx context.Context, e *store.Entry) {
	apply := func(d *catalog.DetailResult) {
		e.Site, e.VodID = d.Site, d.VodID
		if d.Pic != "" {
			e.Pic = d.Pic
		}
		e.Remarks, e.Eps = d.Remarks, len(d.Episodes)
		e.Stale = false
	}
	e.Checked = time.Now().Unix()
	if d, err := r.cat.RefreshDetail(ctx, e.Site, e.VodID); err == nil && len(d.Episodes) > 0 {
		apply(d)
		return
	}
	if alt := r.cat.FindAlternative(ctx, e.Name); alt != nil {
		if d, err := r.cat.RefreshDetail(ctx, alt.Site, alt.VodID); err == nil && len(d.Episodes) > 0 {
			log.Printf("[refresh] 《%s》换源 %s → %s", e.Name, e.Site, alt.Site)
			apply(d)
			return
		}
	}
	e.Stale = true
}
