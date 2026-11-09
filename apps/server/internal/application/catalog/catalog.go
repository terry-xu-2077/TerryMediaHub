// Package catalog 提供媒体目录的聚合能力：跨站点搜索、详情、播放。
package catalog

import (
	"context"
	"encoding/json"
	"fmt"
	"strings"
	"sync"
	"sync/atomic"
	"time"

	"terryhub/internal/adapters/sources"
	"terryhub/internal/application/ranking"
	"terryhub/internal/domain/source"
	"terryhub/internal/domain/tvbox"
)

// Service 是媒体目录服务。
type Service struct {
	resolver *sources.Router
	state    atomic.Pointer[siteState] // 当前订阅配置与站点（可热更新）

	// detailCache 缓存搜索阶段探测出的详情，点详情时直接复用，避免二次请求。
	// key = site.Key + "|" + vodID，带 TTL（盗版源播放地址易过期，缓存不宜长）。
	mu          sync.Mutex
	detailCache map[string]cachedDetail

	// searchCache 缓存搜索结果（跨站点去重后的完整列表），解决「同一关键词
	// 时搜有、时搜空」的源站不稳定问题：盗版源搜索接口约 30% 概率超时/失败，
	// 缓存让用户重搜时直接命中、秒出稳定结果。key = 搜索关键词，带 TTL。
	searchCache map[string]cachedSearch

	// classCache 缓存各站点的分类列表（Categories 结果）。分类结构基本不变，
	// 缓存后分类页无需每次重新拉，避免 worker 池被 Categories+Category 两级
	// RPC 打爆。key = site.Key。
	classCache map[string]cachedClass

	// homeCache / categoryCache 缓存首页热播与分类列表的聚合结果（跨站去重后）。
	// 与 searchCache 同理：源站 home/category 接口不稳定，缓存让重复访问秒出、
	// 稳定，避免「时有时无」。
	lists  *ttlCache[[]MediaItem]
	ranker *ranking.Ranker // 推荐排序（评分/热度/更新/年代）；nil 则保持来源顺序

	// playableCache 缓存站点「可播性」探测结果：某站点 detail 是否真能返回
	// 播放集数（episodes 非空）。盗版源里大量站点是网盘站（detail 无播放地址）
	// 或 detail 接口已失效，首页/分类/搜索应只展示可播站点，避免用户点详情
	// 「打不开」。key = site.Key，TTL 较长（源站能力变化慢）。
	playableCache map[string]cachedPlayable
}

// cachedPlayable 是站点可播性探测结果。
type cachedPlayable struct {
	ok bool
	at time.Time
}

// playableTTL 是站点可播性探测缓存有效期。
const playableTTL = 30 * time.Minute

// cachedClass 是缓存的分类列表。
type cachedClass struct {
	classes []source.Class
	at      time.Time
}

// cachedSearch 是缓存的搜索结果。
type cachedSearch struct {
	items []SearchItem
	at    time.Time
}

// searchTTL 是搜索结果缓存有效期。
const searchTTL = 10 * time.Minute

// cachedDetail 是缓存的详情及其探测时间。
type cachedDetail struct {
	detail *DetailResult
	at     time.Time
}

// detailTTL 是详情缓存有效期。
const detailTTL = 5 * time.Minute

// classTTL 是分类列表缓存有效期（分类结构基本不变，缓存可长）。
const classTTL = 30 * time.Minute

// mediaListTTL 是首页热播/分类列表聚合结果的缓存有效期。
const mediaListTTL = 10 * time.Minute

// New 构造服务。
func New(cfg *tvbox.Config, resolver *sources.Router) *Service {
	s := &Service{resolver: resolver, detailCache: make(map[string]cachedDetail), searchCache: make(map[string]cachedSearch), classCache: make(map[string]cachedClass), lists: newTTL[[]MediaItem](mediaListTTL), playableCache: make(map[string]cachedPlayable)}
	s.SetConfig(cfg)
	return s
}

// siteState 是一份不可变的站点快照。
type siteState struct {
	cfg   *tvbox.Config
	sites []tvbox.Site
}

func (s *Service) sitesNow() []tvbox.Site {
	if st := s.state.Load(); st != nil {
		return st.sites
	}
	return nil
}

// SetConfig 用（合并后的）订阅配置替换站点集合，并清空依赖站点的缓存。可在运行中调用。
func (s *Service) SetConfig(cfg *tvbox.Config) {
	if cfg == nil {
		cfg = &tvbox.Config{}
	}
	var sites []tvbox.Site
	for _, st := range cfg.Sites {
		// 保留有对应解析器、可搜索的影视站点：
		//  - Supports：csp / 苹果CMS / py / js（按 Router 注册情况）
		//  - Searchable != 0：排除明确标注不可搜索的站点（配置/弹幕/直播/教育类）
		if !s.resolver.Supports(st) || st.Key == "" || st.Name == "" {
			continue
		}
		if st.Searchable != nil && *st.Searchable == 0 {
			continue
		}
		if st.IsCsp() && st.CspClass() == "" {
			continue
		}
		// 站点名黑名单：排除非影视站（弹幕配置/养生知识/学习课堂/网盘/磁力/合集/P2P 等）。
		if isNonVideoSite(st.Name) {
			continue
		}
		sites = append(sites, st)
	}
	// 订阅里 key 可能重复（如两个「柠檬」站共用同一 spider 类，但参数不同）。
	// key 是站点唯一标识，重复会导致 findSite 定位错站点，这里给重复 key 追加序号后缀。
	seen := make(map[string]int)
	for i := range sites {
		k := sites[i].Key
		if n := seen[k]; n > 0 {
			sites[i].Key = fmt.Sprintf("%s#%d", k, n+1)
		}
		seen[k]++
	}
	s.state.Store(&siteState{cfg: cfg, sites: sites})

	s.mu.Lock()
	s.detailCache = make(map[string]cachedDetail)
	s.searchCache = make(map[string]cachedSearch)
	s.classCache = make(map[string]cachedClass)
	s.playableCache = make(map[string]cachedPlayable)
	s.mu.Unlock()
	s.lists.Clear()
}

// isNonVideoSite 判断站点名是否为非影视站（弹幕配置/养生/学习/网盘/磁力/合集/P2P 等）。
// 这些站没有正常的影视分类与首页热播，会污染 home/category 聚合，也影响搜索质量。
func isNonVideoSite(name string) bool {
	keywords := []string{
		"弹幕", "配置", "养生", "知识", "学习", "课堂", "宝宝", "趣学",
		"网盘", "盘社", "夸克", "磁力", "P2P", "荐片", "合集",
		// 云盘分享/下载站：detail 无在线播放地址，只有网盘下载链接，无法「搜到即播」。
		"云盘", "盘社", "云集", "盘搜",
	}
	for _, kw := range keywords {
		if strings.Contains(name, kw) {
			return true
		}
	}
	return false
}

// SiteCount 返回可用 csp 站点数。
func (s *Service) SiteCount() int { return len(s.sitesNow()) }

// Sites 返回站点列表（key/name）。
func (s *Service) Sites() []SiteInfo {
	sites := s.sitesNow()
	out := make([]SiteInfo, 0, len(sites))
	for _, st := range sites {
		out = append(out, SiteInfo{Key: st.Key, Name: st.Name})
	}
	return out
}

// SiteInfo 是站点摘要。
type SiteInfo struct {
	Key  string `json:"key"`
	Name string `json:"name"`
}

// SearchItem 是聚合搜索的一条结果。
type SearchItem struct {
	Site     string `json:"site"`
	SiteName string `json:"siteName"`
	VodID    string `json:"vodId"`
	Name     string `json:"name"`
	Pic      string `json:"pic"`
	Year     string `json:"year"`
	Area     string `json:"area"`
	TypeName string `json:"typeName"`
	Remarks  string `json:"remarks"`
}

// Search 跨站点搜索，并行请求（慢站不阻塞整体）。
//
// 并发能力来自 worker 进程池：每个站点绑定一个独立 worker 进程，天然隔离、
// 不会串包。整体延迟从「各站串行之和」降到「最慢单站」。
// 如需边搜边出结果，请用 SearchStream（SSE 场景）。
func (s *Service) Search(ctx context.Context, key string, maxSites int) []SearchItem {
	var (
		mu  sync.Mutex
		out []SearchItem
	)
	s.SearchStream(ctx, key, maxSites, func(_ string, items []SearchItem) {
		mu.Lock()
		out = append(out, items...)
		mu.Unlock()
	}, nil)
	return out
}

// SearchStream 跨站点流式搜索：每个站点搜完立即通过 onSite 回调推出一批结果，
// 快站结果秒出、慢站结果后到，前端可边搜边渲染。
//
// onSite 会按站点逐个（并发完成顺序）回调，返回该站搜到的结果；回调在 worker
// 池并发上下文里执行，需自行加锁/聚合。onDone 在所有站点完成（或 ctx 取消）后调用一次。
func (s *Service) SearchStream(ctx context.Context, key string, maxSites int, onSite func(siteName string, items []SearchItem), onDone func()) {
	const searchTimeout = 6 * time.Second
	const maxSearchSites = 12
	limit := maxSearchSites
	if maxSites > 0 && maxSites < limit {
		limit = maxSites
	}
	sites := s.ranked(limit)

	// 命中搜索结果缓存：直接秒出上次结果，解决源站不稳定导致的「时有时无」。
	if onSite != nil {
		s.mu.Lock()
		if cs, ok := s.searchCache[key]; ok && time.Since(cs.at) < searchTTL {
			cached := cs.items
			s.mu.Unlock()
			if len(cached) > 0 {
				onSite("缓存", cached)
			}
			if onDone != nil {
				onDone()
			}
			return
		}
		s.mu.Unlock()
	}

	// 跨站点去重：多个站点是同一后端的马甲（订阅里大量重复源），返回相同内容。
	// 按「名称 + 海报指纹」去重，只保留首个返回的，避免同一部剧刷屏；
	// 同时多个马甲互为冗余，任何一个返回就能出结果，显著提升稳定性。
	var (
		dedupMu  sync.Mutex
		seenKey  = make(map[string]bool)
		allItems []SearchItem // 聚合全部结果，用于写缓存
	)

	var wg sync.WaitGroup
	for _, st := range sites {
		select {
		case <-ctx.Done():
			goto wait
		default:
		}
		wg.Add(1)
		go func(site tvbox.Site) {
			defer wg.Done()
			c, cancel := context.WithTimeout(ctx, searchTimeout)
			defer cancel()
			res, err := s.resolver.Search(c, site, key, false)
			if err != nil {
				return
			}
			// 收集本店命中条目：基础字段过滤 + 关键词相关性过滤。
			// 相关性：名称必须与关键词相关（包含关系或高相似），过滤掉源站返回的
			// 完全无关脏数据（如搜「后会有期」返回「十二封信」）。
			var items []SearchItem
			for _, v := range res.List {
				if v.VodID.String() == "" || v.VodName == "" {
					continue
				}
				if !nameRelated(v.VodName, key) {
					continue
				}
				item := SearchItem{
					Site:     site.Key,
					SiteName: site.Name,
					VodID:    v.VodID.String(),
					Name:     v.VodName,
					Pic:      v.VodPic,
					Year:     v.VodYear,
					Area:     v.VodArea,
					TypeName: v.TypeName,
					Remarks:  v.VodRemarks,
				}
				// 去重键：名称 + 海报（海报是内容指纹，同后端马甲海报一致）。
				dk := item.Name + "|" + item.Pic
				dedupMu.Lock()
				dup := seenKey[dk]
				if !dup {
					seenKey[dk] = true
				}
				dedupMu.Unlock()
				if dup {
					continue
				}
				items = append(items, item)
			}
			dedupMu.Lock()
			allItems = append(allItems, items...)
			dedupMu.Unlock()
			if onSite != nil {
				onSite(site.Name, items)
			}
		}(st)
	}

wait:
	wg.Wait()
	// 写缓存：即使本次有站点超时失败，成功返回的结果也缓存下来，下次直接命中。
	if len(allItems) > 0 {
		s.mu.Lock()
		s.searchCache[key] = cachedSearch{items: allItems, at: time.Now()}
		s.mu.Unlock()
	}
	if onDone != nil {
		onDone()
	}
}

// nameRelated 判断搜索结果名称是否与搜索关键词相关。
//
// 源站搜索接口常返回与关键词无关的脏数据（模糊匹配到错误条目）。这里用
// 「去空格后包含关系」判定：名称含关键词、或关键词含名称（去掉空白与常见
// 分隔符）。对单字差异（错别字）容忍，避免误杀正常结果。
func nameRelated(name, key string) bool {
	na := normalizeName(name)
	ka := normalizeName(key)
	if na == "" || ka == "" {
		return true // 无法判定时不过滤，宁可多不可漏
	}
	if strings.Contains(na, ka) || strings.Contains(ka, na) {
		return true
	}
	// 容忍单字差异：公共字符占比高也视为相关（如「后会友期」vs「后会有期」）。
	return overlapRatio(na, ka) >= 0.6
}

// normalizeName 去掉空白与常见符号，便于相关性比较。
func normalizeName(s string) string {
	s = strings.TrimSpace(s)
	s = strings.ReplaceAll(s, " ", "")
	s = strings.ReplaceAll(s, "　", "") // 全角空格
	s = strings.ReplaceAll(s, "·", "")
	s = strings.ReplaceAll(s, "《", "")
	s = strings.ReplaceAll(s, "》", "")
	return s
}

// overlapRatio 计算两个字符串公共字符占比（去重后的字符集合重合度）。
// 用于容忍单字差异的模糊匹配。
func overlapRatio(a, b string) float64 {
	if a == "" || b == "" {
		return 0
	}
	set := make(map[rune]bool)
	for _, r := range a {
		set[r] = true
	}
	common := 0
	for _, r := range b {
		if set[r] {
			common++
		}
	}
	// 以较短者长度为基准
	short := len([]rune(a))
	if len([]rune(b)) < short {
		short = len([]rune(b))
	}
	if short == 0 {
		return 0
	}
	return float64(common) / float64(short)
}

// DetailResult 是详情页数据。
type DetailResult struct {
	Site     string    `json:"site"`
	SiteName string    `json:"siteName"`
	VodID    string    `json:"vodId"`
	Name     string    `json:"name"`
	Pic      string    `json:"pic"`
	Year     string    `json:"year"`
	Area     string    `json:"area"`
	Actor    string    `json:"actor"`
	Director string    `json:"director"`
	Content  string    `json:"content"`
	Remarks  string    `json:"remarks"`
	Lines    []Line    `json:"lines"` // 播放线路（多线路）
	Episodes []Episode `json:"episodes"`
}

// Line 是一条播放线路。
type Line struct {
	Name string `json:"name"`
}

// Episode 是一集（含该集在所有线路下的候选地址）。
type Episode struct {
	Name string          `json:"name"`
	URL  string          `json:"url"`  // 首选候选（第一条线路的原始地址）
	URLs []PlayCandidate `json:"urls"` // 该集所有线路的候选地址（后台自动换线用）
}

// Detail 获取某站点的详情（优先走缓存：搜索阶段探测已拉过，点详情秒开）。
func (s *Service) Detail(ctx context.Context, siteKey, vodID string) (*DetailResult, error) {
	site := s.findSite(siteKey)
	if site == nil {
		return nil, fmt.Errorf("站点 %s 不存在", siteKey)
	}

	// 命中缓存（搜索阶段探测过）直接返回，避免二次请求。
	cacheKey := site.Key + "|" + vodID
	s.mu.Lock()
	if cd, ok := s.detailCache[cacheKey]; ok && time.Since(cd.at) < detailTTL {
		s.mu.Unlock()
		return cd.detail, nil
	}
	s.mu.Unlock()

	c, cancel := context.WithTimeout(ctx, 30*time.Second)
	defer cancel()
	d, err := s.fetchDetail(c, *site, vodID)
	if err != nil {
		return nil, err
	}
	s.mu.Lock()
	s.detailCache[cacheKey] = cachedDetail{detail: d, at: time.Now()}
	s.mu.Unlock()
	return d, nil
}

// fetchDetail 拉取并解析单条详情（不含缓存逻辑，供探测与 Detail 复用）。
func (s *Service) fetchDetail(ctx context.Context, site tvbox.Site, vodID string) (*DetailResult, error) {
	res, err := s.resolver.Detail(ctx, site, []string{vodID})
	if err != nil {
		return nil, err
	}
	if len(res.List) == 0 {
		return nil, fmt.Errorf("详情为空")
	}
	v := res.List[0]
	lines, eps := parsePlay(v.VodPlayFrom, v.VodPlayURL)
	if len(eps) == 0 {
		// 详情有返回但没有任何播放集：对该站点是一次软失败。
		s.resolver.Health().Record(site.Key, 0, "详情无播放集")
	}
	return &DetailResult{
		Site:     site.Key,
		SiteName: site.Name,
		VodID:    v.VodID.String(),
		Name:     v.VodName,
		Pic:      v.VodPic,
		Year:     v.VodYear,
		Area:     v.VodArea,
		Actor:    v.VodActor,
		Director: v.VodDirector,
		Content:  v.VodContent,
		Remarks:  v.VodRemarks,
		Lines:    lines,
		Episodes: eps,
	}, nil
}

// Playable 是解析后的可播地址。
type Playable struct {
	Name string `json:"name"`
	URL  string `json:"url"`
}

// probePlayable 探测站点是否可播：抽测一条详情，看能否返回播放集数。
//
// 盗版源里大量站点是网盘站（detail 无播放地址）或 detail 接口已失效，点详情
// 「打不开」。首页/分类聚合时应只纳入可播站点。探测结果缓存 30 分钟，避免
// 每次聚合都做额外 RPC。
//
// 探测方式：用给定的一条 (vodID) 拉详情，若 episodes 非空则判可播。
func (s *Service) probePlayable(ctx context.Context, site tvbox.Site, vodID string) bool {
	if vodID == "" {
		return false
	}
	s.mu.Lock()
	if pc, ok := s.playableCache[site.Key]; ok && time.Since(pc.at) < playableTTL {
		s.mu.Unlock()
		return pc.ok
	}
	s.mu.Unlock()

	c, cancel := context.WithTimeout(ctx, 8*time.Second)
	defer cancel()
	d, err := s.fetchDetail(c, site, vodID)
	ok := err == nil && d != nil && len(d.Episodes) > 0
	s.mu.Lock()
	s.playableCache[site.Key] = cachedPlayable{ok: ok, at: time.Now()}
	s.mu.Unlock()
	return ok
}

// Play 解析某一集的真实播放地址。
//
// epURL 是 detail 里的原始地址（可能是直链、也可能是加密地址）。
// 正确路径是调用 spider 的 playerContent（flag=线路名, id=原始地址），
// spider 内部会处理加密格式（如 qiaoji 内嵌解析器）返回真实 m3u8。
// 仅当 spider 解析失败时，才退回通用 ResolvePlayURL（直链/订阅 parses）。
func (s *Service) Play(ctx context.Context, siteKey, vodID, epURL, flag string) (*Playable, error) {
	site := s.findSite(siteKey)
	if site == nil {
		return nil, fmt.Errorf("站点 %s 不存在", siteKey)
	}
	return s.resolvePlay(ctx, *site, epURL, flag)
}

// PlayAuto 自动换线路：从多个线路的原始地址中，逐个解析+探测，返回第一个可播的。
//
// candidates 是同一集在不同线路下的原始地址列表（含线路名）。
// 盗版源极不稳定，常某条线路失效，这里自动跳过失效线路。
func (s *Service) PlayAuto(ctx context.Context, siteKey, vodID string, candidates []PlayCandidate) (*Playable, error) {
	site := s.findSite(siteKey)
	if site == nil {
		return nil, fmt.Errorf("站点 %s 不存在", siteKey)
	}
	if len(candidates) == 0 {
		return nil, fmt.Errorf("没有可尝试的线路")
	}

	// 直链优先：直链解析+探测快，加密地址走 spider 慢（可能 10s+）。
	// 把直链候选排前面，可大幅缩短「失效线路换线」的总耗时。
	ordered := make([]PlayCandidate, 0, len(candidates))
	for _, c := range candidates {
		if sources.StripLineSuffixIfDirect(c.URL) != "" {
			ordered = append(ordered, c)
		}
	}
	for _, c := range candidates {
		if sources.StripLineSuffixIfDirect(c.URL) == "" {
			ordered = append(ordered, c)
		}
	}

	var lastErr error
	for _, c := range ordered {
		select {
		case <-ctx.Done():
			return nil, ctx.Err()
		default:
		}
		p, err := s.resolvePlay(ctx, *site, c.URL, c.Flag)
		if err != nil {
			lastErr = err
			continue
		}
		// 探测可用性：失效源（返回"链接失效"提示页）直接跳过
		if !sources.ProbePlayable(ctx, p.URL) {
			lastErr = fmt.Errorf("线路「%s」源失效", c.Flag)
			continue
		}
		return p, nil
	}
	if lastErr != nil {
		return nil, lastErr
	}
	return nil, fmt.Errorf("所有线路均无法播放")
}

// PlayCandidate 是一集在某条线路下的候选地址。
type PlayCandidate struct {
	Flag string `json:"flag"` // 线路名
	URL  string `json:"url"`  // 原始地址（待解析）
}

// resolvePlay 解析单条原始地址为可播地址。
//
// 优化顺序：直链直接剥离后缀返回（快）；只有加密地址（非 http 或需解析）才走
// spider 的 playerContent，最后退回通用解析。
func (s *Service) resolvePlay(ctx context.Context, site tvbox.Site, epURL, flag string) (*Playable, error) {
	// 0. 直链（http/https + 已知扩展名）：剥离线路后缀直接返回，不必走 spider
	if u := sources.StripLineSuffixIfDirect(epURL); u != "" {
		return &Playable{Name: "", URL: u}, nil
	}
	// 0.5 内嵌解析器 JSON 地址：解包出真直链直接返回
	if u := sources.UnwrapPlayURL(epURL); u != "" {
		return &Playable{Name: "", URL: u}, nil
	}

	// 0.6 解析器 + 加密串格式（秒播站，如热播 TianTian）：
	// {"p":["https://api.xxx/?key=K&url="],"url":"NBY-XMYAES...|key"}
	// 把加密串拼到解析器地址后，GET 一次即可拿到真实 m3u8。
	if parser, cipher := sources.UnwrapParserURL(epURL); parser != "" {
		if u := sources.ResolveViaParser(ctx, parser, cipher); u != "" {
			return &Playable{Name: "", URL: u}, nil
		}
	}

	// 1. 加密地址：走 spider 的 playerContent（正确处理内嵌解析器格式）
	if k := site.Kind(); k == tvbox.KindCsp || k == tvbox.KindPy || k == tvbox.KindJS {
		c, cancel := context.WithTimeout(ctx, 10*time.Second)
		res, err := s.resolver.Play(c, site, flag, epURL, nil)
		cancel()
		if err == nil && res != nil && res.URL != "" {
			u := strings.TrimSpace(res.URL)
			if sources.IsDirectURL(u) {
				return &Playable{Name: "", URL: u}, nil
			}
		}
	}

	// 2. 退回通用解析（直链 + 订阅 parses）
	url, err := sources.ResolvePlayURL(ctx, s.state.Load().cfg, epURL)
	if err != nil {
		return nil, err
	}
	return &Playable{Name: "", URL: url}, nil
}

func (s *Service) findSite(key string) *tvbox.Site {
	sites := s.sitesNow()
	for i := range sites {
		if sites[i].Key == key {
			return &sites[i]
		}
	}
	return nil
}

// parsePlay 解析播放线路与集数。
//
// TVBox 多线路格式：
//
//	vod_play_from = "线路二 (点击换线)$$$线路三 (勿信广告)$$$线路四"
//	vod_play_url  = "1$url#2$url###2$url#3$url$$$..."   （$$$ 分隔线路，# 分隔集，$ 分隔集名与地址）
//
// 线路名与地址段按 $$$ 对齐：from 有 N 段、url 有 N 段。二者不一致时以 url 为准，
// 缺失的线路名自动补「线路 N」。
func parsePlay(playFrom, playURL string) ([]Line, []Episode) {
	var lines []Line

	fromParts := splitLines(playFrom)
	urlParts := splitLines(playURL)

	// 以地址段数为准（真正决定有多少条线路）
	lineCount := len(urlParts)
	for i := 0; i < lineCount; i++ {
		name := ""
		if i < len(fromParts) {
			name = strings.TrimSpace(fromParts[i])
		}
		if name == "" {
			name = fmt.Sprintf("线路%d", i+1)
		}
		lines = append(lines, Line{Name: name})
	}

	// 按集名去重合并：线路只是同一内容的冗余源，不应把多线路的集数摊开展示。
	// 同一集名（如 "1"）在不同线路下的地址，合并进该集的 URLs 候选列表。
	order := make([]string, 0) // 集名出现顺序
	byName := make(map[string][]PlayCandidate)

	for li, seg := range urlParts {
		flag := ""
		if li < len(lines) {
			flag = lines[li].Name
		}
		// 单条线路内按 # 拆集
		for _, part := range strings.Split(seg, "#") {
			part = strings.TrimSpace(part)
			if part == "" || !strings.Contains(part, "$") {
				continue
			}
			kv := strings.SplitN(part, "$", 2)
			name := strings.TrimSpace(kv[0])
			u := strings.TrimSpace(kv[1])
			if u == "" {
				continue
			}
			// 解包「内嵌解析器」格式（如 {"flag":"lz","url":"https://...m3u8"}），
			// 直接解出真直链，避免前端把整段 JSON 当地址发给播放接口。
			if unwrapped := sources.UnwrapPlayURL(u); unwrapped != "" {
				u = unwrapped
			}
			if name == "" {
				name = fmt.Sprintf("第%d集", len(order)+1)
			}
			if _, ok := byName[name]; !ok {
				order = append(order, name)
			}
			byName[name] = append(byName[name], PlayCandidate{Flag: flag, URL: u})
		}
	}

	// 组装去重后的集列表（保持首次出现顺序）
	eps := make([]Episode, 0, len(order))
	for _, name := range order {
		cands := byName[name]
		eps = append(eps, Episode{
			Name: name,
			URL:  cands[0].URL,
			URLs: cands,
		})
	}
	return lines, eps
}

// splitLines 按 $$$ 拆线路。
func splitLines(s string) []string {
	s = strings.TrimSpace(s)
	if s == "" {
		return nil
	}
	parts := strings.Split(s, "$$$")
	out := make([]string, 0, len(parts))
	for _, p := range parts {
		p = strings.TrimSpace(p)
		if p == "" {
			continue
		}
		out = append(out, p)
	}
	return out
}

var _ = source.Vod{} // 保留类型引用
var _ = json.Marshal
