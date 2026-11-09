// Package sources 提供播放地址解析（parse 接口换真链）。
package sources

import (
	"context"
	"encoding/json"
	"fmt"
	"io"
	"net/http"
	"net/url"
	"strings"
	"time"

	"terryhub/internal/domain/tvbox"
)

// PlayableEpisode 是可直接交给播放器的一集。
type PlayableEpisode struct {
	Name string `json:"name"`
	URL  string `json:"url"`
}

// ParseResult 是解析接口的返回。
type ParseResult struct {
	URL string `json:"url"`
}

// ResolvePlayURL 把一条加密/待解析的播放地址换成真实可播地址。
//
// 逻辑（与影视仓 APK 一致）：
//   - 若地址本身是 http(s) 的 m3u8/mp4 直链，直接返回；
//   - 否则通过订阅里的 parses 解析接口依次尝试，直到拿到真链。
func ResolvePlayURL(ctx context.Context, cfg *tvbox.Config, rawURL string) (string, error) {
	if rawURL == "" {
		return "", fmt.Errorf("空播放地址")
	}

	// 已是直链：http/https 开头，且不是需要解析的占位（不含明显的解析标记）
	if isDirect(rawURL) {
		return stripLineSuffix(rawURL), nil
	}

	// 需要通过 parse 接口解析
	for _, p := range cfg.Parses {
		if strings.TrimSpace(p.Url) == "" {
			continue
		}
		resolved, err := callParse(ctx, p, rawURL)
		if err == nil && isDirect(resolved) {
			return resolved, nil
		}
	}
	// 全部解析失败：退回原始地址（至少能看个响）
	return rawURL, nil
}

// isDirect 判断是否是可直接播放的直链。
func isDirect(s string) bool {
	return IsDirectURL(s)
}

// IsDirectURL 判断是否是可直接播放的直链（http/https + 已知媒体扩展名）。
func IsDirectURL(s string) bool {
	s = strings.TrimSpace(s)
	if !strings.HasPrefix(s, "http://") && !strings.HasPrefix(s, "https://") {
		return false
	}
	lower := strings.ToLower(s)
	if strings.Contains(lower, ".m3u8") || strings.Contains(lower, ".mp4") ||
		strings.Contains(lower, ".flv") || strings.Contains(lower, ".ts") ||
		strings.Contains(lower, ".m3u") {
		return true
	}
	// 无扩展名的 http 地址，可能是加密源，交给 parse 处理
	return false
}

// ProbePlayable 快速探测一个直链是否可播。
//
// 盗版源极不稳定，常返回「链接失效」等提示页（非 #EXTM3U 内容）。
// 这里 GET 一次，校验是否为合法 m3u8（以 #EXTM3U 开头）或 mp4/ts（非 HTML）。
// 返回 true 表示可用；超时或内容非法返回 false。
func ProbePlayable(ctx context.Context, rawURL string) bool {
	s := strings.TrimSpace(rawURL)
	if !strings.HasPrefix(s, "http://") && !strings.HasPrefix(s, "https://") {
		return false
	}
	req, err := http.NewRequestWithContext(ctx, http.MethodGet, s, nil)
	if err != nil {
		return false
	}
	req.Header.Set("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/122.0.0.0 Safari/537.36")
	if origin := originOfURL(s); origin != "" {
		req.Header.Set("Referer", origin)
	}

	client := &http.Client{Timeout: 6 * time.Second}
	resp, err := client.Do(req)
	if err != nil {
		return false
	}
	defer resp.Body.Close()
	if resp.StatusCode != http.StatusOK {
		return false
	}

	body, err := io.ReadAll(io.LimitReader(resp.Body, 64<<10))
	if err != nil {
		return false
	}
	head := strings.TrimSpace(string(body))
	if head == "" {
		return false
	}

	ct := strings.ToLower(resp.Header.Get("Content-Type"))

	// 关键：先看内容本身，而不是只看 URL 扩展名。
	// 盗版源失效时常返回 HTML 提示页（如 "<pre>...链接失效..."），
	// 这类内容无论 URL 长什么样都不可用。
	if strings.HasPrefix(head, "<") || strings.HasPrefix(head, "<html") ||
		strings.Contains(head, "<pre") || strings.Contains(head, "链接失效") {
		return false
	}
	// 合法 m3u8
	if strings.HasPrefix(head, "#EXTM3U") {
		return true
	}
	// 二进制媒体（mp4/ts 分片）不以文本开头，且类型匹配
	if strings.Contains(ct, "mp4") || strings.Contains(ct, "mp2t") ||
		strings.Contains(ct, "mpegurl") || strings.Contains(ct, "octet-stream") {
		return true
	}
	// 其他：有内容且非 HTML 就默认可用
	return !strings.HasPrefix(head, "<")
}

// UnwrapPlayURL 解包「内嵌解析器」格式的播放地址，返回真正的直链。
//
// 部分站点（如 lz 解析器）detail 里的播放地址不是直链，而是一个 URL 编码的
// JSON，形如 {"p":[],"r":"","flag":"lz","u":"","url":"https://.../index.m3u8"}。
// 真正可播地址藏在 url 字段里。若不识别，前端点选集时会把整段 JSON 当地址发
// 给播放接口，导致「解析播放地址」后无下文。
//
// 返回：
//   - 若能解包出直链，返回该直链；
//   - 若本来就是直链，原样返回；
//   - 否则返回空串（表示不是可识别的直链，需走 spider/parse 解析）。
func UnwrapPlayURL(rawURL string) string {
	s := strings.TrimSpace(rawURL)
	if s == "" {
		return ""
	}
	// 已是直链：直接返回
	if IsDirectURL(s) {
		return StripLineSuffixIfDirect(s)
	}
	// 去掉 URL 编码，尝试 JSON 解包
	dec := s
	if !strings.HasPrefix(s, "{") {
		if u, err := url.PathUnescape(s); err == nil {
			dec = u
		}
	}
	dec = strings.TrimSpace(dec)
	if !strings.HasPrefix(dec, "{") {
		return ""
	}
	var m map[string]any
	if err := json.Unmarshal([]byte(dec), &m); err != nil {
		return ""
	}
	// 优先取 url / playurl 字段
	for _, k := range []string{"url", "playUrl", "playurl", "m3u8", "mp4"} {
		if v, ok := m[k].(string); ok {
			v = strings.TrimSpace(v)
			if IsDirectURL(v) {
				return StripLineSuffixIfDirect(v)
			}
		}
	}
	return ""
}

// UnwrapParserURL 解包「解析器 + 加密串」格式的播放地址，返回解析器地址与加密串。
//
// 秒播站（如 TianTian 热播）的播放地址形如 URL 编码的 JSON：
//
//	{"p":["https://api.xxx.top/api/?key=K&url="],"r":"","flag":"nby","u":"UA","url":"NBY-XMYAES...|key"}
//
// 其中 p[0] 是解析器地址（末尾带 url= 参数），url 是加密串。播放时把加密串
// 拼到解析器地址后面，GET 一次即可拿到真实 m3u8。
//
// 返回 (parserURL, cipher)：
//   - parserURL 是 p[0] 解析器地址（已确保以 url= 结尾可拼接）；
//   - cipher 是 url 字段的加密串；
//   - 若非此格式，两者均返回空。
func UnwrapParserURL(rawURL string) (string, string) {
	s := strings.TrimSpace(rawURL)
	if s == "" {
		return "", ""
	}
	dec := s
	if !strings.HasPrefix(s, "{") {
		u, err := url.PathUnescape(s)
		if err != nil {
			return "", ""
		}
		dec = u
	}
	dec = strings.TrimSpace(dec)
	if !strings.HasPrefix(dec, "{") {
		return "", ""
	}
	var m map[string]any
	if err := json.Unmarshal([]byte(dec), &m); err != nil {
		return "", ""
	}
	// 取解析器地址列表 p
	parser := ""
	if ps, ok := m["p"].([]any); ok && len(ps) > 0 {
		if p0, ok := ps[0].(string); ok {
			parser = strings.TrimSpace(p0)
		}
	}
	cipher, _ := m["url"].(string)
	cipher = strings.TrimSpace(cipher)
	if parser == "" || cipher == "" {
		return "", ""
	}
	// cipher 不能本身是直链（直链走 UnwrapPlayURL）
	if IsDirectURL(cipher) {
		return "", ""
	}
	return parser, cipher
}

// ResolveViaParser 通过解析器地址 + 加密串换真实 m3u8 直链。
//
// parser 形如 "https://api.xxx.top/api/?key=K&url="，cipher 是加密串
// （如 "NBY-XMYAES...|key"）。把 cipher 拼到 parser 后 GET，解析器返回
// JSON，其中 url 字段是真实 m3u8 地址。
//
// 返回真实直链；失败或非直链返回空串。
func ResolveViaParser(ctx context.Context, parser, cipher string) string {
	p := strings.TrimSpace(parser)
	c := strings.TrimSpace(cipher)
	if p == "" || c == "" {
		return ""
	}
	// 拼接：若 parser 已以 url= 结尾（或含 = 结尾），直接拼；否则补 &url=
	target := p + c
	if !strings.Contains(p, "url=") {
		// 解析器可能不带 url 参数，兜底用 &url= 拼接
		sep := "&"
		if !strings.Contains(p, "?") {
			sep = "?"
		}
		target = p + sep + "url=" + c
	}

	req, err := http.NewRequestWithContext(ctx, http.MethodGet, target, nil)
	if err != nil {
		return ""
	}
	req.Header.Set("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/122.0.0.0 Safari/537.36")

	client := &http.Client{Timeout: 15 * time.Second}
	resp, err := client.Do(req)
	if err != nil {
		return ""
	}
	defer resp.Body.Close()
	body, err := io.ReadAll(io.LimitReader(resp.Body, 2<<20))
	if err != nil {
		return ""
	}

	text := strings.TrimSpace(string(body))
	// 直接返回直链文本
	if IsDirectURL(text) {
		return StripLineSuffixIfDirect(text)
	}
	// 解析 JSON 里的 url 字段
	var m map[string]any
	if err := json.Unmarshal(body, &m); err == nil {
		if u, ok := m["url"].(string); ok {
			u = strings.TrimSpace(u)
			if IsDirectURL(u) {
				return StripLineSuffixIfDirect(u)
			}
		}
	}
	return ""
}

// originOfURL 提取 URL 的 origin。
func originOfURL(rawURL string) string {
	u, err := url.Parse(rawURL)
	if err != nil {
		return ""
	}
	return u.Scheme + "://" + u.Host
}

// StripLineSuffixIfDirect 若地址是直链（http/https + 媒体扩展名），剥离线路后缀并返回；
// 否则返回空串（表示不是直链，需进一步解析）。
func StripLineSuffixIfDirect(rawURL string) string {
	s := strings.TrimSpace(rawURL)
	if !strings.HasPrefix(s, "http://") && !strings.HasPrefix(s, "https://") {
		return ""
	}
	lower := strings.ToLower(s)
	hasExt := strings.Contains(lower, ".m3u8") || strings.Contains(lower, ".mp4") ||
		strings.Contains(lower, ".flv") || strings.Contains(lower, ".ts") ||
		strings.Contains(lower, ".mkv") || strings.Contains(lower, ".webm")
	if !hasExt {
		return ""
	}
	return stripLineSuffix(s)
}

// stripLineSuffix 剥离播放地址末尾的线路/解析器标记。
// TVBox 的 vod_play_url 常在真链后追加解析器名（如 ".../index.m3u8qiaojino"），
// 真链其实到扩展名为止。这里把扩展名之后的多余标记去掉。
func stripLineSuffix(rawURL string) string {
	s := strings.TrimSpace(rawURL)
	if !strings.HasPrefix(s, "http://") && !strings.HasPrefix(s, "https://") {
		return s
	}
	for _, ext := range []string{".m3u8", ".mp4", ".flv", ".ts", ".mkv", ".webm"} {
		if idx := strings.Index(s, ext); idx >= 0 {
			return s[:idx+len(ext)]
		}
	}
	return s
}

// callParse 调用一个解析接口。
func callParse(ctx context.Context, p tvbox.Parse, rawURL string) (string, error) {
	parseURL := strings.TrimSpace(p.Url)
	if parseURL == "" {
		return "", fmt.Errorf("解析接口为空")
	}

	// 解析接口通常接受 ?url= 参数；若地址里已有 {url} 占位则替换
	encoded := url.QueryEscape(rawURL)
	target := parseURL
	if strings.Contains(target, "{url}") {
		target = strings.ReplaceAll(target, "{url}", encoded)
	} else {
		target = appendQuery(target, "url", rawURL)
	}

	req, err := http.NewRequestWithContext(ctx, http.MethodGet, target, nil)
	if err != nil {
		return "", err
	}
	req.Header.Set("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/122.0.0.0 Safari/537.36")

	client := &http.Client{Timeout: 15 * time.Second}
	resp, err := client.Do(req)
	if err != nil {
		return "", err
	}
	defer resp.Body.Close()
	body, err := io.ReadAll(io.LimitReader(resp.Body, 2<<20))
	if err != nil {
		return "", err
	}

	// 解析接口可能返回纯文本 URL，或 JSON {url:...}
	text := strings.TrimSpace(string(body))
	if isDirect(text) {
		return text, nil
	}
	var pr ParseResult
	if err := json.Unmarshal(body, &pr); err == nil && isDirect(pr.URL) {
		return pr.URL, nil
	}
	// 尝试从 JSON 里找任意 url 字段
	var m map[string]any
	if err := json.Unmarshal(body, &m); err == nil {
		if u, ok := m["url"].(string); ok && isDirect(u) {
			return u, nil
		}
	}
	return "", fmt.Errorf("解析接口未返回直链")
}

func appendQuery(base, key, val string) string {
	sep := "?"
	if strings.Contains(base, "?") {
		sep = "&"
	}
	return base + sep + key + "=" + url.QueryEscape(val)
}
