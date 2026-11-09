// Package httpapi 提供 HTTP 接口与静态前端服务。
package httpapi

import (
	"encoding/json"
	"fmt"
	"io/fs"
	"net/http"
	"path"
	"strconv"
	"strings"
	"sync"

	"terryhub/internal/application/catalog"
	"terryhub/internal/application/refresher"
	"terryhub/internal/store"
)

// Server 是 HTTP 服务。
type Server struct {
	catalog   *catalog.Service
	store     *store.Store
	refresher *refresher.Refresher
	web       fs.FS // 前端静态资源（嵌入或磁盘）
	imgDir    string
	mux       *http.ServeMux
}

// New 构造 HTTP 服务。web 为前端静态文件系统，imgDir 为海报缓存目录。
func New(c *catalog.Service, st *store.Store, rf *refresher.Refresher, web fs.FS, imgDir string) *Server {
	s := &Server{catalog: c, store: st, refresher: rf, web: web, imgDir: imgDir, mux: http.NewServeMux()}
	s.routes()
	return s
}

func (s *Server) routes() {
	s.mux.HandleFunc("/api/sites", s.handleSites)
	s.mux.HandleFunc("/api/sites/health", func(w http.ResponseWriter, r *http.Request) {
		writeJSON(w, map[string]any{"sites": s.catalog.SiteHealth()})
	})
	s.mux.HandleFunc("/api/home", s.handleHome)
	s.mux.HandleFunc("/api/categories", s.handleCategories)
	s.mux.HandleFunc("/api/category", s.handleCategory)
	s.mux.HandleFunc("/api/search", s.handleSearch)
	s.mux.HandleFunc("/api/search/stream", s.handleSearchStream)
	s.mux.HandleFunc("/api/detail", s.handleDetail)
	s.mux.HandleFunc("/api/play", s.handlePlay)
	s.mux.HandleFunc("/api/play/auto", s.handlePlayAuto)
	s.mux.HandleFunc("/api/proxy", s.handleProxy)
	s.mux.HandleFunc("/api/img", s.handleImg)
	s.mux.HandleFunc("/api/favorites", s.handleFavorites)
	s.mux.HandleFunc("/api/history", s.handleHistory)
	s.mux.HandleFunc("/api/status", s.handleStatus)
	s.mux.HandleFunc("/api/refresh", s.handleRefresh)
	s.mux.Handle("/", s.spa())
}

// spa 提供前端静态文件；非文件路径回退 index.html（React Router）。
func (s *Server) spa() http.Handler {
	if s.web == nil {
		return http.NotFoundHandler()
	}
	files := http.FileServer(http.FS(s.web))
	return http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		p := strings.TrimPrefix(path.Clean(r.URL.Path), "/")
		if p == "" {
			p = "index.html"
		}
		if f, err := s.web.Open(p); err != nil {
			r.URL.Path = "/"
		} else {
			f.Close()
		}
		if p == "index.html" || r.URL.Path == "/" {
			w.Header().Set("Cache-Control", "no-cache")
		} else {
			w.Header().Set("Cache-Control", "public, max-age=31536000, immutable")
		}
		files.ServeHTTP(w, r)
	})
}

// Handler 返回根 handler。
func (s *Server) Handler() http.Handler { return corsMiddleware(s.mux) }

func (s *Server) handleSites(w http.ResponseWriter, r *http.Request) {
	writeJSON(w, map[string]any{"sites": s.catalog.Sites()})
}

// handleCategories 返回导航分类定义。
func (s *Server) handleCategories(w http.ResponseWriter, r *http.Request) {
	writeJSON(w, map[string]any{"categories": catalog.Categories})
}

// handleHome 返回首页推荐：各分类的快照行（来自后台刷新，秒开）。
// 快照为空时（首次启动）同步拉一次，保证首次打开也有内容。
func (s *Server) handleHome(w http.ResponseWriter, r *http.Request) {
	type row struct {
		Key   string            `json:"key"`
		Title string            `json:"title"`
		Items []json.RawMessage `json:"items"`
		At    int64             `json:"at"`
	}
	titles := map[string]string{"series": "热播剧集", "movie": "热门电影", "variety": "热门综艺", "anime": "热门动漫"}
	rows := []row{}
	for _, c := range catalog.Categories {
		sec, ok := s.store.Section(c.Key)
		if !ok || len(sec.Items) == 0 {
			continue
		}
		rows = append(rows, row{c.Key, titles[c.Key], sec.Items, sec.At})
	}
	writeJSON(w, map[string]any{
		"rows":      rows,
		"history":   s.store.RecentHistory(10),
		"favorites": s.store.Favorites(),
		"status":    s.refresher.Status(),
	})
}

// handleCategory 返回某分类第 page 页的聚合列表。第 1 页优先用后台快照。
func (s *Server) handleCategory(w http.ResponseWriter, r *http.Request) {
	cat := r.URL.Query().Get("cat")
	if cat == "" {
		writeError(w, http.StatusBadRequest, "缺少分类 cat")
		return
	}
	page, _ := strconv.Atoi(r.URL.Query().Get("page"))
	if page < 1 {
		page = 1
	}
	if page == 1 {
		if sec, ok := s.store.Section(cat); ok && len(sec.Items) > 0 {
			writeJSON(w, map[string]any{"items": sec.Items, "page": 1, "more": true})
			return
		}
	}
	items := s.catalog.Category(r.Context(), cat, page)
	writeJSON(w, map[string]any{"items": items, "page": page, "more": len(items) > 0})
}

func (s *Server) handleSearch(w http.ResponseWriter, r *http.Request) {
	key := strings.TrimSpace(r.URL.Query().Get("q"))
	if key == "" {
		writeError(w, http.StatusBadRequest, "缺少搜索关键词 q")
		return
	}
	ctx := r.Context()
	results := s.catalog.Search(ctx, key, 0)
	writeJSON(w, map[string]any{"results": results})
}

// handleSearchStream 以 SSE 流式返回搜索结果：快站结果秒出、慢站结果后到。
// 每站完成推送一条 `data: {"site":..,"items":[...]}`，全部结束推 `data: {"done":true}`。
func (s *Server) handleSearchStream(w http.ResponseWriter, r *http.Request) {
	key := strings.TrimSpace(r.URL.Query().Get("q"))
	if key == "" {
		writeError(w, http.StatusBadRequest, "缺少搜索关键词 q")
		return
	}

	fl, ok := w.(http.Flusher)
	if !ok {
		writeError(w, http.StatusInternalServerError, "当前环境不支持流式响应")
		return
	}

	w.Header().Set("Content-Type", "text/event-stream; charset=utf-8")
	w.Header().Set("Cache-Control", "no-cache")
	w.Header().Set("Connection", "keep-alive")
	w.Header().Set("X-Accel-Buffering", "no")

	ctx := r.Context()
	send := func(v any) bool {
		b, err := json.Marshal(v)
		if err != nil {
			return false
		}
		if _, err := fmt.Fprintf(w, "data: %s\n\n", b); err != nil {
			return false
		}
		fl.Flush()
		return true
	}

	var mu sync.Mutex
	closed := false
	closeOnce := func() {
		mu.Lock()
		if !closed {
			closed = true
			mu.Unlock()
			_ = send(map[string]any{"done": true})
			return
		}
		mu.Unlock()
	}

	s.catalog.SearchStream(ctx, key, 0, func(siteName string, items []catalog.SearchItem) {
		mu.Lock()
		if closed {
			mu.Unlock()
			return
		}
		mu.Unlock()
		_ = send(map[string]any{"site": siteName, "items": items})
	}, closeOnce)
}

func (s *Server) handleDetail(w http.ResponseWriter, r *http.Request) {
	site := r.URL.Query().Get("site")
	id := r.URL.Query().Get("id")
	if site == "" || id == "" {
		writeError(w, http.StatusBadRequest, "缺少 site 或 id")
		return
	}
	d, err := s.catalog.Detail(r.Context(), site, id)
	if err != nil {
		writeError(w, http.StatusInternalServerError, err.Error())
		return
	}
	writeJSON(w, d)
}

func (s *Server) handlePlay(w http.ResponseWriter, r *http.Request) {
	site := r.URL.Query().Get("site")
	id := r.URL.Query().Get("id")
	ep := r.URL.Query().Get("ep")
	flag := r.URL.Query().Get("flag")
	if site == "" || ep == "" {
		writeError(w, http.StatusBadRequest, "缺少 site 或 ep")
		return
	}
	p, err := s.catalog.Play(r.Context(), site, id, ep, flag)
	if err != nil {
		writeError(w, http.StatusInternalServerError, err.Error())
		return
	}
	writeJSON(w, p)
}

// handlePlayAuto 自动换线路：接收同一集的多条线路候选，逐个解析+探测，返回第一个可播的。
// 请求体：{"site":..,"id":..,"candidates":[{"flag":..,"url":..},...]}
func (s *Server) handlePlayAuto(w http.ResponseWriter, r *http.Request) {
	var req struct {
		Site       string                  `json:"site"`
		ID         string                  `json:"id"`
		Candidates []catalog.PlayCandidate `json:"candidates"`
	}
	if err := json.NewDecoder(r.Body).Decode(&req); err != nil {
		writeError(w, http.StatusBadRequest, "请求体解析失败: "+err.Error())
		return
	}
	if req.Site == "" || len(req.Candidates) == 0 {
		writeError(w, http.StatusBadRequest, "缺少 site 或 candidates")
		return
	}
	p, err := s.catalog.PlayAuto(r.Context(), req.Site, req.ID, req.Candidates)
	if err != nil {
		writeError(w, http.StatusInternalServerError, err.Error())
		return
	}
	writeJSON(w, p)
}

// handleProxy 转发流（解决 Referer/UA/CORS，直通播放）。
// 前端播放器直接用视频直链也可能因跨域失败，这里提供一个透传代理。
func (s *Server) handleProxy(w http.ResponseWriter, r *http.Request) {
	target := r.URL.Query().Get("url")
	if target == "" {
		writeError(w, http.StatusBadRequest, "缺少 url")
		return
	}
	proxyStream(w, r, target)
}

func writeJSON(w http.ResponseWriter, v any) {
	w.Header().Set("Content-Type", "application/json; charset=utf-8")
	_ = json.NewEncoder(w).Encode(v)
}

func writeError(w http.ResponseWriter, code int, msg string) {
	w.Header().Set("Content-Type", "application/json; charset=utf-8")
	w.WriteHeader(code)
	_ = json.NewEncoder(w).Encode(map[string]string{"error": msg})
}

func corsMiddleware(next http.Handler) http.Handler {
	return http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		w.Header().Set("Access-Control-Allow-Origin", "*")
		w.Header().Set("Access-Control-Allow-Methods", "GET, POST, DELETE, OPTIONS")
		w.Header().Set("Access-Control-Allow-Headers", "Content-Type")
		if r.Method == http.MethodOptions {
			w.WriteHeader(http.StatusOK)
			return
		}
		next.ServeHTTP(w, r)
	})
}
