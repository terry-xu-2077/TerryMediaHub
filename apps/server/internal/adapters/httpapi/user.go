package httpapi

import (
	"crypto/sha1"
	"encoding/hex"
	"encoding/json"
	"io"
	"net/http"
	"os"
	"path/filepath"
	"strings"
	"time"

	"terryhub/internal/application/catalog"
	"terryhub/internal/store"
)

// entryReq 是前端提交收藏/播放进度时携带的作品信息。
type entryReq struct {
	Site     string  `json:"site"`
	VodID    string  `json:"vodId"`
	Name     string  `json:"name"`
	Pic      string  `json:"pic"`
	Year     string  `json:"year"`
	TypeName string  `json:"typeName"`
	Remarks  string  `json:"remarks"`
	Eps      int     `json:"eps"`
	Episode  string  `json:"episode"`
	Position float64 `json:"position"`
	Duration float64 `json:"duration"`
}

func (e entryReq) entry() store.Entry {
	return store.Entry{
		Key: catalog.NormalizeName(e.Name), Site: e.Site, VodID: e.VodID, Name: e.Name,
		Pic: e.Pic, Year: e.Year, TypeName: e.TypeName, Remarks: e.Remarks, Eps: e.Eps,
	}
}

func decodeEntry(w http.ResponseWriter, r *http.Request) (entryReq, bool) {
	var e entryReq
	if err := json.NewDecoder(r.Body).Decode(&e); err != nil || e.Name == "" || e.Site == "" || e.VodID == "" {
		writeError(w, http.StatusBadRequest, "需要 site / vodId / name")
		return e, false
	}
	return e, true
}

// handleFavorites: GET 列表 / POST 添加 / DELETE ?name= 移除。
func (s *Server) handleFavorites(w http.ResponseWriter, r *http.Request) {
	switch r.Method {
	case http.MethodGet:
		writeJSON(w, map[string]any{"items": s.store.Favorites()})
	case http.MethodPost:
		if e, ok := decodeEntry(w, r); ok {
			s.store.AddFavorite(e.entry())
			writeJSON(w, map[string]any{"ok": true})
		}
	case http.MethodDelete:
		s.store.RemoveFavorite(catalog.NormalizeName(r.URL.Query().Get("name")))
		writeJSON(w, map[string]any{"ok": true})
	default:
		writeError(w, http.StatusMethodNotAllowed, "method not allowed")
	}
}

// handleHistory: GET 列表 / POST 上报进度 / DELETE ?name= 移除。
func (s *Server) handleHistory(w http.ResponseWriter, r *http.Request) {
	switch r.Method {
	case http.MethodGet:
		writeJSON(w, map[string]any{"items": s.store.History()})
	case http.MethodPost:
		if e, ok := decodeEntry(w, r); ok {
			s.store.PutHistory(store.History{Entry: e.entry(), Episode: e.Episode, Position: e.Position, Duration: e.Duration})
			writeJSON(w, map[string]any{"ok": true})
		}
	case http.MethodDelete:
		s.store.RemoveHistory(catalog.NormalizeName(r.URL.Query().Get("name")))
		writeJSON(w, map[string]any{"ok": true})
	default:
		writeError(w, http.StatusMethodNotAllowed, "method not allowed")
	}
}

func (s *Server) handleStatus(w http.ResponseWriter, r *http.Request) {
	writeJSON(w, s.refresher.Status())
}

// handleRefresh 手动触发一轮后台刷新。
func (s *Server) handleRefresh(w http.ResponseWriter, r *http.Request) {
	s.refresher.Trigger()
	writeJSON(w, map[string]any{"ok": true})
}

// handleImg 代理并磁盘缓存海报：绕过源站防盗链/混合内容，电视端也走同一入口。
func (s *Server) handleImg(w http.ResponseWriter, r *http.Request) {
	target := r.URL.Query().Get("url")
	if strings.HasPrefix(target, "//") {
		target = "https:" + target
	}
	if !strings.HasPrefix(target, "http://") && !strings.HasPrefix(target, "https://") {
		writeError(w, http.StatusBadRequest, "url 必须是 http(s)")
		return
	}
	sum := sha1.Sum([]byte(target))
	file := filepath.Join(s.imgDir, hex.EncodeToString(sum[:]))
	data, err := os.ReadFile(file)
	if err != nil {
		req, _ := http.NewRequestWithContext(r.Context(), http.MethodGet, target, nil)
		req.Header.Set("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/122.0.0.0 Safari/537.36")
		req.Header.Set("Referer", originOf(target))
		resp, err := (&http.Client{Timeout: 15 * time.Second}).Do(req)
		if err != nil {
			writeError(w, http.StatusBadGateway, err.Error())
			return
		}
		defer resp.Body.Close()
		data, err = io.ReadAll(io.LimitReader(resp.Body, 8<<20))
		if resp.StatusCode != http.StatusOK || err != nil || len(data) == 0 {
			writeError(w, http.StatusBadGateway, "图片获取失败")
			return
		}
		if !strings.HasPrefix(http.DetectContentType(data), "image/") {
			writeError(w, http.StatusBadGateway, "不是图片")
			return
		}
		_ = os.MkdirAll(s.imgDir, 0o755)
		_ = os.WriteFile(file, data, 0o644)
	}
	w.Header().Set("Content-Type", http.DetectContentType(data))
	w.Header().Set("Cache-Control", "public, max-age=604800")
	_, _ = w.Write(data)
}
