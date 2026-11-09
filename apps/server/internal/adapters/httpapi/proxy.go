// Package httpapi 提供流代理：转发 m3u8/mp4 直链，透传 Referer/UA。
package httpapi

import (
	"io"
	"net/http"
	"net/url"
	"strings"
	"time"
)

// proxyStream 把目标 URL 的内容转发给客户端。
// 对 m3u8 会重写其中的相对地址，保证分片也能通过本代理走通（解决跨域/防盗链）。
func proxyStream(w http.ResponseWriter, r *http.Request, target string) {
	if !strings.HasPrefix(target, "http://") && !strings.HasPrefix(target, "https://") {
		writeError(w, http.StatusBadRequest, "url 必须是 http(s)")
		return
	}

	req, err := http.NewRequestWithContext(r.Context(), http.MethodGet, target, nil)
	if err != nil {
		writeError(w, http.StatusInternalServerError, err.Error())
		return
	}
	// 透传常用请求头
	if ua := r.Header.Get("User-Agent"); ua != "" {
		req.Header.Set("User-Agent", ua)
	} else {
		req.Header.Set("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/122.0.0.0 Safari/537.36")
	}
	// Referer：默认设为目标站点自身域名（规避防盗链），不透传本地 localhost referer
	req.Header.Set("Referer", originOf(target))

	client := &http.Client{Timeout: 60 * time.Second}
	resp, err := client.Do(req)
	if err != nil {
		writeError(w, http.StatusBadGateway, err.Error())
		return
	}
	defer resp.Body.Close()

	contentType := resp.Header.Get("Content-Type")
	isM3u8 := strings.Contains(contentType, "mpegurl") || strings.HasSuffix(strings.ToLower(target), ".m3u8")

	if isM3u8 {
		// 读全文重写相对地址，再统一写响应头（重写后长度会变）
		body, err := io.ReadAll(io.LimitReader(resp.Body, 8<<20))
		if err != nil {
			writeError(w, http.StatusBadGateway, err.Error())
			return
		}
		rewritten := rewriteM3u8(string(body), target, r)
		w.Header().Set("Content-Type", "application/vnd.apple.mpegurl")
		w.Header().Set("Access-Control-Allow-Origin", "*")
		w.Header().Set("Cache-Control", "no-cache")
		w.WriteHeader(http.StatusOK)
		_, _ = w.Write([]byte(rewritten))
		return
	}

	// 复制响应头
	for k, vs := range resp.Header {
		for _, v := range vs {
			w.Header().Add(k, v)
		}
	}
	w.Header().Set("Access-Control-Allow-Origin", "*")
	w.WriteHeader(resp.StatusCode)

	// 其他（mp4/ts/分片）直接流式拷贝
	_, _ = io.Copy(w, resp.Body)
}

// rewriteM3u8 把 m3u8 里的相对地址改写成指向本代理的绝对地址。
func rewriteM3u8(content, baseURL string, r *http.Request) string {
	// 简化处理：把相对 URI 拼接成完整 URL，并包一层代理
	base := baseURL
	if idx := strings.LastIndex(base, "/"); idx >= 0 {
		base = base[:idx+1]
	}

	proxySelf := schemeHost(r)

	var b strings.Builder
	for _, line := range strings.Split(content, "\n") {
		line = strings.TrimRight(line, "\r")
		trimmed := strings.TrimSpace(line)
		if trimmed == "" || strings.HasPrefix(trimmed, "#") {
			b.WriteString(line + "\n")
			continue
		}
		var abs string
		if strings.HasPrefix(trimmed, "http://") || strings.HasPrefix(trimmed, "https://") {
			abs = trimmed
		} else {
			abs = base + trimmed
		}
		b.WriteString(proxySelf + "/api/proxy?url=" + url.QueryEscape(abs) + "\n")
	}
	return b.String()
}

func schemeHost(r *http.Request) string {
	scheme := "http"
	if r.TLS != nil {
		scheme = "https"
	}
	if h := r.Header.Get("X-Forwarded-Proto"); h != "" {
		scheme = h
	}
	return scheme + "://" + r.Host
}

// originOf 提取 URL 的 origin（scheme://host）。
func originOf(rawURL string) string {
	u, err := url.Parse(rawURL)
	if err != nil {
		return ""
	}
	return u.Scheme + "://" + u.Host
}
