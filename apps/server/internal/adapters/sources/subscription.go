// Package sources 提供订阅配置的加载与解码。
package sources

import (
	"bytes"
	"context"
	"crypto/aes"
	"crypto/cipher"
	"encoding/base64"
	"encoding/hex"
	"encoding/json"
	"fmt"
	"io"
	"net/http"
	neturl "net/url"
	"regexp"
	"strings"
	"time"

	"terryhub/internal/domain/tvbox"
)

const subscriptionUA = "okhttp/3.15"

// LoadSubscription 拉取订阅地址，解码并解析为 TVBox 配置（相对路径已按订阅地址补全）。
func LoadSubscription(ctx context.Context, url, userAgent string) (*tvbox.Config, error) {
	if userAgent == "" {
		userAgent = subscriptionUA
	}
	var lastErr error
	for attempt := 0; attempt < 2; attempt++ {
		cfg, err := loadOnce(ctx, url, userAgent)
		if err == nil {
			return cfg, nil
		}
		lastErr = err
		if ctx.Err() != nil {
			break
		}
		time.Sleep(time.Second)
	}
	return nil, lastErr
}

func loadOnce(ctx context.Context, url, ua string) (*tvbox.Config, error) {
	req, err := http.NewRequestWithContext(ctx, http.MethodGet, url, nil)
	if err != nil {
		return nil, err
	}
	req.Header.Set("User-Agent", ua)
	resp, err := (&http.Client{Timeout: 30 * time.Second}).Do(req)
	if err != nil {
		return nil, fmt.Errorf("拉取订阅失败: %w", err)
	}
	defer resp.Body.Close()
	body, err := io.ReadAll(io.LimitReader(resp.Body, 20<<20))
	if err != nil {
		return nil, fmt.Errorf("读取订阅失败: %w", err)
	}
	if resp.StatusCode != http.StatusOK {
		return nil, fmt.Errorf("订阅返回 HTTP %d", resp.StatusCode)
	}
	return ParseConfig(body, url)
}

// ParseConfig 解码并解析配置内容；baseURL 用于补全相对路径（可为空）。
func ParseConfig(body []byte, baseURL string) (*tvbox.Config, error) {
	text, err := DecodeConfig(body)
	if err != nil {
		return nil, err
	}
	var cfg tvbox.Config
	if err := json.Unmarshal(cleanJSON(text), &cfg); err != nil {
		return nil, fmt.Errorf("解析订阅 JSON 失败: %w", err)
	}
	if len(cfg.Sites) == 0 {
		return nil, fmt.Errorf("订阅里没有站点")
	}
	resolveRelative(&cfg, baseURL)
	return &cfg, nil
}

// DecodeConfig 把订阅内容还原成 JSON 文本。支持 TVBox 生态常见的三种封装（可嵌套）：
//  1. 纯 JSON（含 // 与 /* */ 注释）
//  2. 图片里藏配置：内容中第一个「**」之后是 base64
//  3. AES-CBC 加密：以十六进制 "2423"(即 "$#") 开头，形如 $#密钥#$ + 密文 + 13 字节 IV(26 个十六进制字符)
func DecodeConfig(body []byte) ([]byte, error) {
	for i := 0; i < 4; i++ {
		trimmed := bytes.TrimSpace(bytes.TrimPrefix(body, []byte("\xef\xbb\xbf")))
		if len(trimmed) > 0 && (trimmed[0] == '{' || trimmed[0] == '[') {
			return trimmed, nil
		}
		if idx := bytes.Index(body, []byte("**")); idx >= 0 {
			b64 := strings.Map(func(r rune) rune {
				if r == '\r' || r == '\n' || r == ' ' || r == '\t' {
					return -1
				}
				return r
			}, string(body[idx+2:]))
			dec, err := base64.StdEncoding.DecodeString(b64 + strings.Repeat("=", (4-len(b64)%4)%4))
			if err != nil {
				return nil, fmt.Errorf("图片内 base64 解码失败: %w", err)
			}
			body = dec
			continue
		}
		if s := strings.ToLower(string(trimmed)); strings.HasPrefix(s, "2423") {
			dec, err := decryptCBC(s)
			if err != nil {
				return nil, err
			}
			body = dec
			continue
		}
		head := string(trimmed)
		if len(head) > 60 {
			head = head[:60]
		}
		return nil, fmt.Errorf("无法识别的订阅格式（开头: %q）", head)
	}
	return nil, fmt.Errorf("订阅封装层数过多")
}

func padKey(s string) []byte {
	b := []byte(s + "0000000000000000")
	return b[:16]
}

func decryptCBC(h string) ([]byte, error) {
	sep := strings.Index(h, "2324") // "#$"
	if sep < 0 || len(h) < sep+4+26+32 {
		return nil, fmt.Errorf("加密订阅格式不正确")
	}
	keyRaw, err := hex.DecodeString(h[:sep])
	if err != nil {
		return nil, fmt.Errorf("加密订阅密钥解码失败: %w", err)
	}
	key := strings.NewReplacer("$#", "", "#$", "").Replace(string(keyRaw))
	ivRaw, err := hex.DecodeString(h[len(h)-26:])
	if err != nil {
		return nil, fmt.Errorf("加密订阅 IV 解码失败: %w", err)
	}
	ct, err := hex.DecodeString(h[sep+4 : len(h)-26])
	if err != nil || len(ct) == 0 || len(ct)%16 != 0 {
		return nil, fmt.Errorf("加密订阅密文不正确")
	}
	block, err := aes.NewCipher(padKey(key))
	if err != nil {
		return nil, err
	}
	out := make([]byte, len(ct))
	cipher.NewCBCDecrypter(block, padKey(string(ivRaw))).CryptBlocks(out, ct)
	pad := int(out[len(out)-1])
	if pad < 1 || pad > 16 || pad > len(out) {
		return nil, fmt.Errorf("加密订阅解密失败（密钥或格式不对）")
	}
	return out[:len(out)-pad], nil
}

var trailingComma = regexp.MustCompile(`,(\s*[}\]])`)

// cleanJSON 去掉 // 与 /* */ 注释（识别字符串，不会误伤 URL 里的 //）和多余的尾逗号。
func cleanJSON(in []byte) []byte {
	var out bytes.Buffer
	out.Grow(len(in))
	inStr, esc := false, false
	for i := 0; i < len(in); i++ {
		c := in[i]
		if inStr {
			out.WriteByte(c)
			switch {
			case esc:
				esc = false
			case c == '\\':
				esc = true
			case c == '"':
				inStr = false
			}
			continue
		}
		switch {
		case c == '"':
			inStr = true
			out.WriteByte(c)
		case c == '/' && i+1 < len(in) && in[i+1] == '/':
			for i < len(in) && in[i] != '\n' {
				i++
			}
			out.WriteByte('\n')
		case c == '/' && i+1 < len(in) && in[i+1] == '*':
			i += 2
			for i+1 < len(in) && !(in[i] == '*' && in[i+1] == '/') {
				i++
			}
			i++
		default:
			out.WriteByte(c)
		}
	}
	return trailingComma.ReplaceAll(out.Bytes(), []byte("$1"))
}

// absURL 把 ./x、../x 补全成绝对地址；其它形式（http、assets://、csp_ 等）原样返回。
func absURL(base *neturl.URL, ref string) string {
	if base == nil || !(strings.HasPrefix(ref, "./") || strings.HasPrefix(ref, "../")) {
		return ref
	}
	r, err := neturl.Parse(ref)
	if err != nil {
		return ref
	}
	return base.ResolveReference(r).String()
}

// resolveRelative 按订阅地址补全站点与 spider 里的相对路径。
func resolveRelative(cfg *tvbox.Config, baseURL string) {
	base, err := neturl.Parse(baseURL)
	if err != nil || baseURL == "" {
		return
	}
	// 以 ';' 分隔的 "url;md5;hash" 只处理 url 段
	spiderURL := func(spec string) string {
		parts := strings.SplitN(spec, ";", 2)
		parts[0] = absURL(base, parts[0])
		return strings.Join(parts, ";")
	}
	cfg.Spider = spiderURL(cfg.Spider)
	for i := range cfg.Sites {
		s := &cfg.Sites[i]
		if !s.IsCsp() {
			s.Api = absURL(base, s.Api)
		}
		if s.Jar != "" {
			s.Jar = spiderURL(s.Jar)
		}
		var str string
		if json.Unmarshal(s.Ext, &str) == nil && str != "" {
			if abs := absURL(base, str); abs != str {
				s.Ext, _ = json.Marshal(abs)
			}
		}
	}
}
