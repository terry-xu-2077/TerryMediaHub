package catalog

import (
	"context"
	"fmt"
	"strings"
	"sync"
	"time"

	"terryhub/internal/application/ranking"
	"terryhub/internal/domain/source"
	"terryhub/internal/domain/tvbox"
)

// MediaItem 是首页/分类页展示的一条媒体，与搜索结果同构。
type MediaItem = SearchItem

// CategoryDef 是顶层分类定义；Keywords 用于匹配源站分类名。
type CategoryDef struct {
	Key      string   `json:"key"`
	Label    string   `json:"label"`
	Keywords []string `json:"-"`
}

// Categories 是对外展示的顶层分类（顺序即导航顺序）。
var Categories = []CategoryDef{
	{"series", "剧集", []string{"连续剧", "电视剧", "剧集", "国产剧", "韩剧", "美剧", "日剧", "港剧", "台剧", "泰剧", "外剧", "短剧"}},
	{"movie", "电影", []string{"电影", "影片"}},
	{"variety", "综艺", []string{"综艺"}},
	{"anime", "动漫", []string{"动漫", "动画", "漫剧"}},
}

// HomeCategoryKeys 是首页推荐行依赖的分类。
func CategoryByKey(key string) *CategoryDef {
	for i := range Categories {
		if Categories[i].Key == key {
			return &Categories[i]
		}
	}
	return nil
}

const (
	listSiteLimit = 24
	listTimeout   = 6 * time.Second
	maxPage       = 20
)

type freshKey struct{}

// WithFresh 让本次调用绕过列表缓存（后台刷新用）。
func WithFresh(ctx context.Context) context.Context { return context.WithValue(ctx, freshKey{}, true) }
func isFresh(ctx context.Context) bool              { v, _ := ctx.Value(freshKey{}).(bool); return v }

func itemFrom(site tvbox.Site, v source.Vod) (MediaItem, bool) {
	// vod_id 含 "$$$" 或超长是个别 spider 返回的脏数据
	if id := v.VodID.String(); id == "" || v.VodName == "" || len(id) > 120 || strings.Contains(id, "$$$") {
		return MediaItem{}, false
	}
	return MediaItem{
		Site: site.Key, SiteName: site.Name, VodID: v.VodID.String(), Name: v.VodName,
		Pic: v.VodPic, Year: v.VodYear, Area: v.VodArea, TypeName: v.TypeName, Remarks: v.VodRemarks,
	}, true
}

// getSiteClasses 获取站点分类列表（缓存 30 分钟）。
func (s *Service) getSiteClasses(ctx context.Context, site tvbox.Site) []source.Class {
	s.mu.Lock()
	if cc, ok := s.classCache[site.Key]; ok && time.Since(cc.at) < classTTL {
		s.mu.Unlock()
		return cc.classes
	}
	s.mu.Unlock()
	classes, err := s.resolver.Categories(ctx, site)
	if err != nil || len(classes) == 0 {
		return nil
	}
	s.mu.Lock()
	s.classCache[site.Key] = cachedClass{classes: classes, at: time.Now()}
	s.mu.Unlock()
	return classes
}

// Category 聚合多个站点某分类的第 page 页，并按「名称+海报」跨站去重。
// 只纳入「可播」站点（抽测详情有播放集）。结果缓存 10 分钟；ctx 带 WithFresh 时跳过缓存读取。
func (s *Service) Category(ctx context.Context, catKey string, page int) []MediaItem {
	def := CategoryByKey(catKey)
	if def == nil {
		return nil
	}
	if page < 1 {
		page = 1
	}
	if page > maxPage {
		return nil
	}
	ck := fmt.Sprintf("%s|%d", catKey, page)
	if !isFresh(ctx) {
		if v, ok := s.lists.Get(ck); ok && len(v) > 0 {
			return v
		}
	}

	sites := s.ranked(listSiteLimit)
	var (
		mu  sync.Mutex
		wg  sync.WaitGroup
		per = make([][]MediaItem, len(sites)) // 按站点顺序保存，保证结果顺序稳定
	)
	for i, st := range sites {
		wg.Add(1)
		go func(i int, site tvbox.Site) {
			defer wg.Done()
			c, cancel := context.WithTimeout(ctx, listTimeout)
			defer cancel()
			var tids []string
			for _, cl := range s.getSiteClasses(c, site) {
				if cl.TypeID != "" && matchAny(cl.TypeName, def.Keywords) {
					tids = append(tids, cl.TypeID)
				}
			}
			var items []MediaItem
			for _, tid := range tids {
				cc, cancel2 := context.WithTimeout(ctx, listTimeout)
				res, err := s.resolver.Category(cc, site, tid, fmt.Sprint(page), nil)
				cancel2()
				if err != nil {
					continue
				}
				for _, v := range res.List {
					if it, ok := itemFrom(site, v); ok {
						items = append(items, it)
					}
				}
				if len(items) > 0 {
					break
				}
			}
			if len(items) > 0 && !s.probePlayable(ctx, site, items[0].VodID) {
				items = nil
			}
			mu.Lock()
			per[i] = items
			mu.Unlock()
		}(i, st)
	}
	wg.Wait()

	seen := make(map[string]bool)
	var out []MediaItem
	// 轮询各站（round-robin），避免第一个站的内容霸占前排。
	for round := 0; ; round++ {
		added := false
		for _, items := range per {
			if round < len(items) {
				added = true
				it := items[round]
				dk := ranking.DedupKey(it.Name)
				if seen[dk] {
					continue
				}
				seen[dk] = true
				out = append(out, it)
			}
		}
		if !added {
			break
		}
	}
	s.sortByRank(out)
	if len(out) > 0 {
		s.lists.Set(ck, out)
	}
	return out
}

// SetRanker 注入排序器。
func (s *Service) SetRanker(r *ranking.Ranker) { s.ranker = r }

// sortByRank 按综合分（评分+热度+近期更新−年代）原地降序排序。
func (s *Service) sortByRank(items []MediaItem) {
	if s.ranker == nil || len(items) < 2 {
		return
	}
	in := make([]ranking.Item, len(items))
	for i, it := range items {
		in[i] = ranking.Item{Name: it.Name, Year: it.Year, Remarks: it.Remarks}
	}
	order := s.ranker.Order(in)
	sorted := make([]MediaItem, len(items))
	for k, i := range order {
		sorted[k] = items[i]
	}
	copy(items, sorted)
}

// RefreshDetail 绕过缓存重新拉取详情（后台刷新收藏/历史用），并写回缓存。
func (s *Service) RefreshDetail(ctx context.Context, siteKey, vodID string) (*DetailResult, error) {
	site := s.findSite(siteKey)
	if site == nil {
		return nil, fmt.Errorf("站点 %s 不存在", siteKey)
	}
	c, cancel := context.WithTimeout(ctx, 20*time.Second)
	defer cancel()
	d, err := s.fetchDetail(c, *site, vodID)
	if err != nil {
		return nil, err
	}
	s.mu.Lock()
	s.detailCache[site.Key+"|"+vodID] = cachedDetail{detail: d, at: time.Now()}
	s.mu.Unlock()
	return d, nil
}

// FindAlternative 按片名在其它站点寻找同名资源（原站失效时换源）。
func (s *Service) FindAlternative(ctx context.Context, name string) *SearchItem {
	want := normalizeName(name)
	for _, it := range s.Search(ctx, name, 0) {
		if normalizeName(it.Name) == want {
			it := it
			return &it
		}
	}
	return nil
}

// NormalizeName 对外暴露名称归一化，供存储层做主键。
func NormalizeName(s string) string { return strings.ToLower(normalizeName(s)) }

func matchAny(s string, keywords []string) bool {
	for _, kw := range keywords {
		if kw != "" && strings.Contains(s, kw) {
			return true
		}
	}
	return false
}

// VerifyPlayable 逐条抽查详情，只保留确有播放集数的条目（最多返回 want 条、最多尝试 maxTry 条）。
// 同一站点连续失败 2 次后跳过该站其余条目，避免被失效站点拖慢。结果顺序与输入一致。
func (s *Service) VerifyPlayable(ctx context.Context, items []MediaItem, want, maxTry int) []MediaItem {
	if len(items) > maxTry {
		items = items[:maxTry]
	}
	ok := make([]bool, len(items))
	var (
		mu    sync.Mutex
		fails = map[string]int{}
		wg    sync.WaitGroup
		sem   = make(chan struct{}, 4)
	)
	for i, it := range items {
		wg.Add(1)
		sem <- struct{}{}
		go func(i int, it MediaItem) {
			defer wg.Done()
			defer func() { <-sem }()
			mu.Lock()
			skip := fails[it.Site] >= 2
			mu.Unlock()
			if skip || ctx.Err() != nil {
				return
			}
			site := s.findSite(it.Site)
			if site == nil {
				return
			}
			c, cancel := context.WithTimeout(ctx, 10*time.Second)
			defer cancel()
			d, err := s.fetchDetail(c, *site, it.VodID)
			good := err == nil && d != nil && len(d.Episodes) > 0
			mu.Lock()
			if good {
				ok[i] = true
				fails[it.Site] = 0
			} else {
				fails[it.Site]++
			}
			mu.Unlock()
			if good {
				s.mu.Lock()
				s.detailCache[site.Key+"|"+it.VodID] = cachedDetail{detail: d, at: time.Now()}
				s.mu.Unlock()
			}
		}(i, it)
	}
	wg.Wait()
	var out []MediaItem
	for i, it := range items {
		if ok[i] && len(out) < want {
			out = append(out, it)
		}
	}
	return out
}

// ranked 按健康度从高到低返回最多 limit 个当前可用的站点（停用站点会定期放行重试）。
// 为了让新加入的订阅/站点有机会积累统计，约 1/4 的名额留给样本不足（<3 次）的站点，
// 否则已验证的老站点会永远排在前面，新站点永远得不到评估。
func (s *Service) ranked(limit int) []tvbox.Site {
	all := s.sitesNow()
	keys := make([]string, len(all))
	byKey := make(map[string]tvbox.Site, len(all))
	for i, st := range all {
		keys[i] = st.Key
		byKey[st.Key] = st
	}
	h := s.resolver.Health()
	order := h.Rank(keys)

	explore := limit / 4
	var fresh, proven []string
	for _, k := range order {
		if h.Samples(k) < 3 {
			fresh = append(fresh, k)
		} else {
			proven = append(proven, k)
		}
	}
	// 探索名额：样本最少的优先；轮转起点随时间变化，使不同站点轮流获得机会。
	if len(fresh) > explore {
		off := int(time.Now().Unix()/60) % len(fresh)
		fresh = append(fresh[off:], fresh[:off]...)
	}
	picked := make([]string, 0, limit)
	seen := map[string]bool{}
	take := func(list []string, max int) {
		for _, k := range list {
			if len(picked) >= limit || max == 0 {
				return
			}
			if !seen[k] {
				seen[k] = true
				picked = append(picked, k)
				max--
			}
		}
	}
	take(fresh, explore)
	take(proven, limit)
	take(fresh, limit) // 老站点不够时用新站点补满
	out := make([]tvbox.Site, 0, len(picked))
	for _, k := range order {
		if seen[k] {
			out = append(out, byKey[k])
		}
	}
	return out
}

// SiteHealth 返回各站点健康度，供诊断页展示。
func (s *Service) SiteHealth() any { return s.resolver.Health().Snapshot() }
