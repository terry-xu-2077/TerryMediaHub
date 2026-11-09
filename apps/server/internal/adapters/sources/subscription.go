// Package sources 提供订阅配置的加载。
package sources

import (
	"context"
	"encoding/json"
	"fmt"
	"io"
	"net/http"
	"time"

	"terryhub/internal/domain/tvbox"
)

// LoadSubscription 拉取订阅地址并解析为 TVBox 配置。
func LoadSubscription(ctx context.Context, url, userAgent string) (*tvbox.Config, error) {
	req, err := http.NewRequestWithContext(ctx, http.MethodGet, url, nil)
	if err != nil {
		return nil, err
	}
	if userAgent != "" {
		req.Header.Set("User-Agent", userAgent)
	} else {
		req.Header.Set("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/122.0.0.0 Safari/537.36")
	}

	client := &http.Client{Timeout: 30 * time.Second}
	resp, err := client.Do(req)
	if err != nil {
		return nil, fmt.Errorf("拉取订阅失败: %w", err)
	}
	defer resp.Body.Close()

	body, err := io.ReadAll(io.LimitReader(resp.Body, 10<<20)) // 限 10MB
	if err != nil {
		return nil, fmt.Errorf("读取订阅失败: %w", err)
	}
	if resp.StatusCode != http.StatusOK {
		return nil, fmt.Errorf("订阅返回 %d", resp.StatusCode)
	}

	var cfg tvbox.Config
	if err := json.Unmarshal(body, &cfg); err != nil {
		return nil, fmt.Errorf("解析订阅 JSON 失败: %w", err)
	}
	return &cfg, nil
}
