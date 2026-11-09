package sources

import (
	"context"
	"fmt"
	"io"
	"net/http"
	"os"
	"path/filepath"
	"strings"
	"time"

	"terryhub/internal/domain/tvbox"
)

// SpiderJarName 从 "url;md5;HASH" 形式的 spider 描述得到本地文件名 spider-<hash>.jar。
func SpiderJarName(spec string) (url, name string) {
	parts := strings.Split(spec, ";")
	url = strings.TrimSpace(parts[0])
	if url == "" {
		return "", ""
	}
	if len(parts) >= 3 && strings.EqualFold(parts[1], "md5") {
		return url, "spider-" + strings.ToLower(strings.TrimSpace(parts[2])) + ".jar"
	}
	return url, ""
}

// EnsureSpiderJars 把订阅里的全局 spider 与各站点专用 jar 下载到 dir（已存在则跳过）。
// 返回全局 jar 的文件名。单个站点 jar 下载失败只记录、不中断（对应站点会走全局 jar）。
func EnsureSpiderJars(ctx context.Context, cfg *tvbox.Config, dir string, logf func(string, ...any)) (global string, err error) {
	if err := os.MkdirAll(dir, 0o755); err != nil {
		return "", err
	}
	specs := []string{cfg.Spider}
	seen := map[string]bool{}
	for _, s := range cfg.Sites {
		if s.Jar != "" && !seen[s.Jar] {
			seen[s.Jar] = true
			specs = append(specs, s.Jar)
		}
	}
	for i, spec := range specs {
		url, name := SpiderJarName(spec)
		if url == "" || name == "" {
			continue
		}
		if i == 0 {
			global = name
		}
		dst := filepath.Join(dir, name)
		if st, e := os.Stat(dst); e == nil && st.Size() > 1024 {
			continue
		}
		if e := download(ctx, url, dst); e != nil {
			logf("下载 spider jar 失败 %s: %v", name, e)
			if i == 0 {
				return "", fmt.Errorf("全局 spider 下载失败: %w", e)
			}
			continue
		}
		logf("已下载 %s", name)
	}
	return global, nil
}

func download(ctx context.Context, url, dst string) error {
	ctx, cancel := context.WithTimeout(ctx, 90*time.Second)
	defer cancel()
	req, err := http.NewRequestWithContext(ctx, http.MethodGet, url, nil)
	if err != nil {
		return err
	}
	req.Header.Set("User-Agent", "Mozilla/5.0")
	resp, err := http.DefaultClient.Do(req)
	if err != nil {
		return err
	}
	defer resp.Body.Close()
	if resp.StatusCode != http.StatusOK {
		return fmt.Errorf("HTTP %d", resp.StatusCode)
	}
	tmp := dst + ".part"
	f, err := os.Create(tmp)
	if err != nil {
		return err
	}
	if _, err = io.Copy(f, io.LimitReader(resp.Body, 64<<20)); err != nil {
		f.Close()
		return err
	}
	f.Close()
	return os.Rename(tmp, dst)
}
