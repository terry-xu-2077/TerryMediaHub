// Package douban 拉取豆瓣「热门」榜单，为推荐排序提供评分与热度（位次）。
package douban

import (
	"context"
	"encoding/json"
	"fmt"
	"io"
	"net/http"
	"net/url"
	"time"

	"terryhub/internal/application/ranking"
)

// 各榜单：type / tag。与首页四个分类对应。
var lists = []struct{ Typ, Tag string }{
	{"movie", "热门"},
	{"tv", "热门"},
	{"tv", "综艺"},
	{"tv", "日本动画"},
	{"tv", "国产剧"},
}

// Fetch 拉取全部榜单，返回 片名 → {评分, 榜内位次}。某个榜单失败不影响其它；全部失败才返回错误。
func Fetch(ctx context.Context) (map[string]ranking.Hot, error) {
	out := map[string]ranking.Hot{}
	var lastErr error
	ok := 0
	for _, l := range lists {
		items, err := fetchOne(ctx, l.Typ, l.Tag)
		if err != nil {
			lastErr = err
			continue
		}
		ok++
		for i, it := range items {
			rate := 0.0
			fmt.Sscan(it.Rate, &rate)
			rank := i + 1
			if old, exists := out[it.Title]; !exists || rank < old.Rank {
				out[it.Title] = ranking.Hot{Rate: rate, Rank: rank}
			} else if old.Rate == 0 && rate > 0 {
				old.Rate = rate
				out[it.Title] = old
			}
		}
	}
	if ok == 0 {
		return nil, lastErr
	}
	return out, nil
}

type subject struct {
	Title string `json:"title"`
	Rate  string `json:"rate"`
}

func fetchOne(ctx context.Context, typ, tag string) ([]subject, error) {
	u := "https://movie.douban.com/j/search_subjects?type=" + typ + "&tag=" + url.QueryEscape(tag) +
		"&sort=recommend&page_limit=100&page_start=0"
	ctx, cancel := context.WithTimeout(ctx, 15*time.Second)
	defer cancel()
	req, err := http.NewRequestWithContext(ctx, http.MethodGet, u, nil)
	if err != nil {
		return nil, err
	}
	req.Header.Set("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/122.0.0.0 Safari/537.36")
	req.Header.Set("Referer", "https://movie.douban.com/")
	resp, err := http.DefaultClient.Do(req)
	if err != nil {
		return nil, err
	}
	defer resp.Body.Close()
	if resp.StatusCode != http.StatusOK {
		return nil, fmt.Errorf("豆瓣 %s/%s HTTP %d", typ, tag, resp.StatusCode)
	}
	body, err := io.ReadAll(io.LimitReader(resp.Body, 4<<20))
	if err != nil {
		return nil, err
	}
	var r struct {
		Subjects []subject `json:"subjects"`
	}
	if err := json.Unmarshal(body, &r); err != nil {
		return nil, fmt.Errorf("豆瓣返回非 JSON（可能被限流）: %w", err)
	}
	return r.Subjects, nil
}
