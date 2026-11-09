package sources

import (
	"context"
	"encoding/json"
	"encoding/xml"
	"fmt"
	"io"
	"net/http"
	"net/url"
	"strings"
	"sync"
	"time"

	"terryhub/internal/domain/source"
	"terryhub/internal/domain/tvbox"
)

// CMSResolver 解析苹果CMS v10 采集接口（JSON 与 XML 两种格式）。
// 纯 Go 实现，不需要任何外部运行时，是最轻、最稳定的一类站点。
type CMSResolver struct {
	client *http.Client
	health *Health

	mu      sync.Mutex
	classes map[string]cmsClasses // site.Key → 分类缓存（含父子关系）
}

type cmsClass struct{ ID, PID, Name string }

type cmsClasses struct {
	list []cmsClass
	at   time.Time
}

// adultKeywords 命中的分类一律丢弃：家庭媒体库不展示成人内容。
var adultKeywords = []string{"伦理", "三级", "写真", "两性", "福利", "情色", "成人", "擦边", "午夜", "热舞", "里番", "无码", "有码"}

func isAdultName(name string) bool {
	for _, k := range adultKeywords {
		if strings.Contains(name, k) {
			return true
		}
	}
	return false
}

// NewCMSResolver 创建解析器。
func NewCMSResolver(h *Health) *CMSResolver {
	return &CMSResolver{client: &http.Client{Timeout: 15 * time.Second}, health: h, classes: map[string]cmsClasses{}}
}

// buildURL 在 api 基址上拼接查询参数，丢弃 api 自带的 ac/pg/t/ids/wd 以免冲突。
func buildURL(api, query string) string {
	api = strings.TrimSpace(api)
	base, existing, hasQ := strings.Cut(api, "?")
	base = strings.TrimRight(base, "/")
	var kept []string
	if hasQ {
		for _, p := range strings.Split(existing, "&") {
			k, _, _ := strings.Cut(p, "=")
			switch strings.ToLower(k) {
			case "", "ac", "pg", "t", "ids", "wd":
			default:
				kept = append(kept, p)
			}
		}
	}
	kept = append(kept, query)
	return base + "/?" + strings.Join(kept, "&")
}

func (c *CMSResolver) get(ctx context.Context, site tvbox.Site, query string) ([]byte, error) {
	start := time.Now()
	body, err := c.fetch(ctx, buildURL(site.Api, query))
	if c.health != nil && !isCanceled(err) {
		msg := ""
		if err != nil {
			msg = err.Error()
		}
		c.health.Record(site.Key, time.Since(start), msg)
	}
	return body, err
}

func isCanceled(err error) bool {
	return err != nil && strings.Contains(err.Error(), "context canceled")
}

func (c *CMSResolver) fetch(ctx context.Context, u string) ([]byte, error) {
	req, err := http.NewRequestWithContext(ctx, http.MethodGet, u, nil)
	if err != nil {
		return nil, err
	}
	req.Header.Set("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/122.0.0.0 Safari/537.36")
	if pu, e := url.Parse(u); e == nil {
		req.Header.Set("Referer", pu.Scheme+"://"+pu.Host+"/")
	}
	resp, err := c.client.Do(req)
	if err != nil {
		return nil, err
	}
	defer resp.Body.Close()
	if resp.StatusCode != http.StatusOK {
		return nil, fmt.Errorf("HTTP %d", resp.StatusCode)
	}
	return io.ReadAll(io.LimitReader(resp.Body, 16<<20))
}

func looksXML(b []byte) bool {
	s := strings.TrimSpace(string(b[:min(len(b), 64)]))
	return strings.HasPrefix(s, "<?xml") || strings.HasPrefix(s, "<rss")
}

// ---- JSON ----

type cmsJSONVod struct {
	ID       source.FlexString `json:"vod_id"`
	Name     string            `json:"vod_name"`
	Pic      string            `json:"vod_pic"`
	Remarks  string            `json:"vod_remarks"`
	Year     source.FlexString `json:"vod_year"`
	Area     string            `json:"vod_area"`
	Actor    string            `json:"vod_actor"`
	Director string            `json:"vod_director"`
	Content  string            `json:"vod_content"`
	Blurb    string            `json:"vod_blurb"`
	Type     string            `json:"type_name"`
	Class    string            `json:"vod_class"`
	From     string            `json:"vod_play_from"`
	URL      string            `json:"vod_play_url"`
}

type cmsJSON struct {
	Code      source.FlexString `json:"code"`
	Page      source.FlexString `json:"page"`
	PageCount source.FlexString `json:"pagecount"`
	Total     source.FlexString `json:"total"`
	List      []cmsJSONVod      `json:"list"`
	Class     []struct {
		ID   source.FlexString `json:"type_id"`
		PID  source.FlexString `json:"type_pid"`
		Name string            `json:"type_name"`
	} `json:"class"`
}

func (v cmsJSONVod) toVod() source.Vod {
	typ := v.Type
	if typ == "" {
		typ = v.Class
	}
	content := v.Content
	if content == "" {
		content = v.Blurb
	}
	return source.Vod{
		VodID: v.ID, VodName: v.Name, VodPic: v.Pic, VodRemarks: v.Remarks, VodYear: v.Year.String(),
		VodArea: v.Area, VodActor: v.Actor, VodDirector: v.Director, VodContent: content,
		TypeName: typ, VodPlayFrom: v.From, VodPlayURL: v.URL,
	}
}

// ---- XML ----

type cmsXML struct {
	List struct {
		Page      string `xml:"page,attr"`
		PageCount string `xml:"pagecount,attr"`
		Videos    []struct {
			ID       string `xml:"id"`
			Name     string `xml:"name"`
			Type     string `xml:"type"`
			Pic      string `xml:"pic"`
			Area     string `xml:"area"`
			Year     string `xml:"year"`
			Note     string `xml:"note"`
			Actor    string `xml:"actor"`
			Director string `xml:"director"`
			Des      string `xml:"des"`
			DL       struct {
				DD []struct {
					Flag string `xml:"flag,attr"`
					Text string `xml:",chardata"`
				} `xml:"dd"`
			} `xml:"dl"`
		} `xml:"video"`
	} `xml:"list"`
	Class struct {
		Ty []struct {
			ID   string `xml:"id,attr"`
			Name string `xml:",chardata"`
		} `xml:"ty"`
	} `xml:"class"`
}

func parseXML(b []byte) (*cmsXML, error) {
	var x cmsXML
	dec := xml.NewDecoder(strings.NewReader(string(b)))
	dec.Strict = false
	if err := dec.Decode(&x); err != nil {
		return nil, err
	}
	return &x, nil
}

func (x *cmsXML) vods() []source.Vod {
	var out []source.Vod
	for _, v := range x.List.Videos {
		var from, urls []string
		for _, dd := range v.DL.DD {
			from = append(from, dd.Flag)
			urls = append(urls, strings.TrimSpace(dd.Text))
		}
		out = append(out, source.Vod{
			VodID: source.FlexString(strings.TrimSpace(v.ID)), VodName: strings.TrimSpace(v.Name), VodPic: strings.TrimSpace(v.Pic),
			VodRemarks: strings.TrimSpace(v.Note), VodYear: strings.TrimSpace(v.Year), VodArea: v.Area, VodActor: v.Actor,
			VodDirector: v.Director, VodContent: v.Des, TypeName: v.Type,
			VodPlayFrom: strings.Join(from, "$$$"), VodPlayURL: strings.Join(urls, "$$$"),
		})
	}
	return out
}

// listQuery 同时兼容 ac=detail（v10 标准）与 ac=videolist（旧别名）。
func (c *CMSResolver) listQuery(ctx context.Context, site tvbox.Site, params string) ([]source.Vod, int, []source.Class, error) {
	var lastErr error
	for _, ac := range []string{"videolist", "detail"} {
		body, err := c.get(ctx, site, "ac="+ac+"&"+params)
		if err != nil {
			lastErr = err
			continue
		}
		if looksXML(body) {
			x, err := parseXML(body)
			if err != nil {
				lastErr = err
				continue
			}
			var cls []source.Class
			for _, t := range x.Class.Ty {
				cls = append(cls, source.Class{TypeID: t.ID, TypeName: strings.TrimSpace(t.Name)})
			}
			vods := x.vods()
			if len(vods) == 0 && ac == "videolist" {
				continue
			}
			pc := 0
			fmt.Sscan(x.List.PageCount, &pc)
			return vods, pc, cls, nil
		}
		var j cmsJSON
		if err := json.Unmarshal(body, &j); err != nil {
			lastErr = fmt.Errorf("响应不是有效 JSON: %w", err)
			continue
		}
		if len(j.List) == 0 && ac == "videolist" {
			continue // 有的站只支持 ac=detail
		}
		vods := make([]source.Vod, 0, len(j.List))
		for _, v := range j.List {
			vods = append(vods, v.toVod())
		}
		pc := 0
		fmt.Sscan(j.PageCount.String(), &pc)
		return vods, pc, nil, nil
	}
	return nil, 0, nil, lastErr
}

// loadClasses 拉取并缓存分类（含父子关系），已剔除成人分类。
func (c *CMSResolver) loadClasses(ctx context.Context, site tvbox.Site) ([]cmsClass, error) {
	c.mu.Lock()
	if e, ok := c.classes[site.Key]; ok && time.Since(e.at) < 30*time.Minute {
		c.mu.Unlock()
		return e.list, nil
	}
	c.mu.Unlock()
	body, err := c.get(ctx, site, "ac=list")
	if err != nil {
		return nil, err
	}
	var all []cmsClass
	if looksXML(body) {
		x, err := parseXML(body)
		if err != nil {
			return nil, err
		}
		for _, t := range x.Class.Ty {
			all = append(all, cmsClass{ID: t.ID, Name: strings.TrimSpace(t.Name)})
		}
	} else {
		var j cmsJSON
		if err := json.Unmarshal(body, &j); err != nil {
			return nil, fmt.Errorf("响应不是有效 JSON: %w", err)
		}
		for _, cl := range j.Class {
			if cl.ID.String() != "" && cl.Name != "" {
				all = append(all, cmsClass{ID: cl.ID.String(), PID: cl.PID.String(), Name: cl.Name})
			}
		}
	}
	adult := map[string]bool{}
	for _, cl := range all {
		if isAdultName(cl.Name) {
			adult[cl.ID] = true
		}
	}
	var list []cmsClass
	for _, cl := range all {
		if !adult[cl.ID] && !adult[cl.PID] {
			list = append(list, cl)
		}
	}
	if len(list) == 0 {
		return nil, fmt.Errorf("没有分类")
	}
	c.mu.Lock()
	c.classes[site.Key] = cmsClasses{list: list, at: time.Now()}
	c.mu.Unlock()
	return list, nil
}

// Home 返回分类列表（ac=list）。
func (c *CMSResolver) Home(ctx context.Context, site tvbox.Site) (*source.HomeResult, error) {
	list, err := c.loadClasses(ctx, site)
	if err != nil {
		return nil, err
	}
	out := make([]source.Class, 0, len(list))
	for _, cl := range list {
		out = append(out, source.Class{TypeID: cl.ID, TypeName: cl.Name})
	}
	return &source.HomeResult{Class: out}, nil
}

// kidsOf 返回父分类的子分类 id。接口给了 type_pid 就直接用；
// 没给（有的站全部 pid 为空）则按父分类名与常见子类名猜测。
func kidsOf(classes []cmsClass, tid string) []string {
	var kids []string
	hasPID := false
	var parent cmsClass
	for _, cl := range classes {
		if cl.PID != "" && cl.PID != "0" {
			hasPID = true
		}
		if cl.ID == tid {
			parent = cl
		}
	}
	for _, cl := range classes {
		if hasPID {
			if cl.PID == tid {
				kids = append(kids, cl.ID)
			}
			continue
		}
		if cl.ID == tid || parent.Name == "" {
			continue
		}
		n := cl.Name
		var ok bool
		switch {
		case strings.Contains(parent.Name, "电影"):
			ok = strings.HasSuffix(n, "片") && !strings.Contains(n, "预告") && !strings.Contains(parent.Name, n)
		case strings.Contains(parent.Name, "连续剧") || strings.Contains(parent.Name, "电视剧"):
			ok = strings.HasSuffix(n, "剧") && !strings.Contains(n, "短剧") && !strings.Contains(parent.Name, n)
		case strings.Contains(parent.Name, "综艺"):
			ok = strings.Contains(n, "综艺") && n != parent.Name
		case strings.Contains(parent.Name, "动漫"):
			ok = strings.Contains(n, "动漫") && n != parent.Name
		}
		if ok {
			kids = append(kids, cl.ID)
		}
	}
	if len(kids) > 6 {
		kids = kids[:6]
	}
	return kids
}

// Category 返回某分类第 page 页（含完整播放地址）。
// 多数采集站按精确 type_id 过滤：传父分类（如「电影」）会得到空列表或寥寥几条，
// 此时改为聚合它的若干子分类（动作片、喜剧片…）并交错合并。
func (c *CMSResolver) Category(ctx context.Context, site tvbox.Site, tid, page string, _ map[string]string) (*source.CategoryResult, error) {
	q := "pg=" + url.QueryEscape(page)
	if tid != "" {
		q += "&t=" + url.QueryEscape(tid)
	}
	direct, pc, _, err := c.listQuery(ctx, site, q)
	if err == nil && len(direct) >= 10 {
		return &source.CategoryResult{PageCount: pc, List: direct}, nil
	}
	classes, cerr := c.loadClasses(ctx, site)
	if cerr != nil {
		if err != nil {
			return nil, err
		}
		return nil, cerr
	}
	kids := kidsOf(classes, tid)
	if len(kids) == 0 {
		if err != nil {
			return nil, err
		}
		return &source.CategoryResult{PageCount: pc, List: direct}, nil
	}
	parts := make([][]source.Vod, len(kids))
	var wg sync.WaitGroup
	for i, kid := range kids {
		wg.Add(1)
		go func(i int, kid string) {
			defer wg.Done()
			v, _, _, e := c.listQuery(ctx, site, "pg="+url.QueryEscape(page)+"&t="+url.QueryEscape(kid))
			if e == nil {
				parts[i] = v
			}
		}(i, kid)
	}
	wg.Wait()
	merged := append([]source.Vod(nil), direct...)
	for round := 0; ; round++ {
		added := false
		for _, p := range parts {
			if round < len(p) {
				merged = append(merged, p[round])
				added = true
			}
		}
		if !added {
			break
		}
	}
	return &source.CategoryResult{List: merged}, nil
}

// Detail 按 id 取详情。
func (c *CMSResolver) Detail(ctx context.Context, site tvbox.Site, ids []string) (*source.DetailResult, error) {
	vods, _, _, err := c.listQuery(ctx, site, "ids="+url.QueryEscape(strings.Join(ids, ",")))
	if err != nil {
		return nil, err
	}
	return &source.DetailResult{List: vods}, nil
}

// Search 按关键词搜索。
func (c *CMSResolver) Search(ctx context.Context, site tvbox.Site, key string, _ bool) (*source.CategoryResult, error) {
	vods, pc, _, err := c.listQuery(ctx, site, "wd="+url.QueryEscape(key)+"&pg=1")
	if err != nil {
		return nil, err
	}
	return &source.CategoryResult{PageCount: pc, List: vods}, nil
}

// Play：CMS 的播放地址就是直链，原样返回。
func (c *CMSResolver) Play(_ context.Context, _ tvbox.Site, _, id string, _ []string) (*source.PlayResult, error) {
	return &source.PlayResult{URL: id}, nil
}
