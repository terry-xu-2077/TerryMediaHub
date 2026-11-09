package httpapi

import (
	"context"
	"encoding/json"
	"net/http"
	"time"
)

// handleSources: GET 列表 / POST 添加 / DELETE ?id= 删除。
func (s *Server) handleSources(w http.ResponseWriter, r *http.Request) {
	switch r.Method {
	case http.MethodGet:
		writeJSON(w, map[string]any{"sources": s.subs.List()})
	case http.MethodPost:
		var req struct{ Name, URL string }
		if err := json.NewDecoder(r.Body).Decode(&req); err != nil {
			writeError(w, http.StatusBadRequest, "请求体解析失败")
			return
		}
		src, err := s.subs.Add(req.Name, req.URL)
		if err != nil {
			writeError(w, http.StatusBadRequest, err.Error())
			return
		}
		s.subs.ReloadAsync()
		writeJSON(w, map[string]any{"ok": true, "source": src})
	case http.MethodDelete:
		s.subs.Remove(r.URL.Query().Get("id"))
		s.subs.ReloadAsync()
		writeJSON(w, map[string]any{"ok": true})
	default:
		writeError(w, http.StatusMethodNotAllowed, "method not allowed")
	}
}

// handleSourceTest 拉取并解码订阅，返回站点统计（不保存）。
func (s *Server) handleSourceTest(w http.ResponseWriter, r *http.Request) {
	var req struct{ URL string }
	if err := json.NewDecoder(r.Body).Decode(&req); err != nil {
		writeError(w, http.StatusBadRequest, "请求体解析失败")
		return
	}
	ctx, cancel := context.WithTimeout(r.Context(), 45*time.Second)
	defer cancel()
	p, err := s.subs.Test(ctx, req.URL)
	if err != nil {
		writeError(w, http.StatusBadGateway, err.Error())
		return
	}
	writeJSON(w, p)
}

func (s *Server) handleSourceToggle(w http.ResponseWriter, r *http.Request) {
	var req struct {
		ID      string `json:"id"`
		Enabled bool   `json:"enabled"`
	}
	if err := json.NewDecoder(r.Body).Decode(&req); err != nil || req.ID == "" {
		writeError(w, http.StatusBadRequest, "需要 id")
		return
	}
	s.subs.SetEnabled(req.ID, req.Enabled)
	s.subs.ReloadAsync()
	writeJSON(w, map[string]any{"ok": true})
}

func (s *Server) handleSourceReload(w http.ResponseWriter, r *http.Request) {
	s.subs.ReloadAsync()
	writeJSON(w, map[string]any{"ok": true})
}
