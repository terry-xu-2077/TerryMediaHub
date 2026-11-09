package sources

import (
	"context"
	"encoding/json"
	"errors"
	"fmt"
	"strings"
	"time"

	"terryhub/internal/adapters/worker"
	"terryhub/internal/domain/source"
	"terryhub/internal/domain/tvbox"
)

// ScriptResolver 通过 python / node 桥接 worker 运行 .py / .js 爬虫脚本。
type ScriptResolver struct {
	pool   *worker.Pool
	health *Health
}

// NewScriptResolver 创建解析器。
func NewScriptResolver(pool *worker.Pool, h *Health) *ScriptResolver {
	return &ScriptResolver{pool: pool, health: h}
}

// Supports 只接受绝对地址的脚本：相对路径（./py/x.py、./js/drpy2.min.js）是 APP 内置资源，订阅里没有对应文件。
func (r *ScriptResolver) Supports(site tvbox.Site) bool {
	return strings.HasPrefix(strings.ToLower(site.Api), "http")
}

func (r *ScriptResolver) call(ctx context.Context, site tvbox.Site, method string, args any) (json.RawMessage, error) {
	req := worker.Request{Method: method, Site: site.Key, Api: site.Api, Ext: site.ExtString()}
	if args != nil {
		req.Args, _ = json.Marshal(args)
	}
	h, err := r.pool.Acquire(ctx)
	if err != nil {
		return nil, err
	}
	defer r.pool.Release(h)
	start := time.Now()
	data, err := h.Call(ctx, req)
	if r.health != nil && !errors.Is(err, context.Canceled) {
		msg := ""
		if err != nil {
			msg = err.Error()
		}
		r.health.Record(site.Key, time.Since(start), msg)
	}
	return data, err
}

func (r *ScriptResolver) Home(ctx context.Context, site tvbox.Site) (*source.HomeResult, error) {
	raw, err := r.call(ctx, site, "home", nil)
	if err != nil {
		return nil, err
	}
	var res source.HomeResult
	if err := unmarshalData(raw, &res); err != nil {
		return nil, fmt.Errorf("解析 home 返回失败: %w", err)
	}
	return &res, nil
}

func (r *ScriptResolver) Category(ctx context.Context, site tvbox.Site, tid, page string, extend map[string]string) (*source.CategoryResult, error) {
	raw, err := r.call(ctx, site, "category", map[string]any{"tid": tid, "pg": page, "extend": extend})
	if err != nil {
		return nil, err
	}
	var res source.CategoryResult
	if err := unmarshalData(raw, &res); err != nil {
		return nil, fmt.Errorf("解析 category 返回失败: %w", err)
	}
	return &res, nil
}

func (r *ScriptResolver) Detail(ctx context.Context, site tvbox.Site, ids []string) (*source.DetailResult, error) {
	raw, err := r.call(ctx, site, "detail", map[string]any{"ids": ids})
	if err != nil {
		return nil, err
	}
	var res source.DetailResult
	if err := unmarshalData(raw, &res); err != nil {
		return nil, fmt.Errorf("解析 detail 返回失败: %w", err)
	}
	return &res, nil
}

func (r *ScriptResolver) Search(ctx context.Context, site tvbox.Site, key string, quick bool) (*source.CategoryResult, error) {
	raw, err := r.call(ctx, site, "search", map[string]any{"key": key, "quick": quick})
	if err != nil {
		return nil, err
	}
	var res source.CategoryResult
	if err := unmarshalData(raw, &res); err != nil {
		return nil, fmt.Errorf("解析 search 返回失败: %w", err)
	}
	return &res, nil
}

func (r *ScriptResolver) Play(ctx context.Context, site tvbox.Site, flag, id string, vipFlags []string) (*source.PlayResult, error) {
	raw, err := r.call(ctx, site, "play", map[string]any{"flag": flag, "id": id, "vipFlags": vipFlags})
	if err != nil {
		return nil, err
	}
	var res source.PlayResult
	if err := unmarshalData(raw, &res); err != nil {
		return nil, fmt.Errorf("解析 play 返回失败: %w", err)
	}
	return &res, nil
}
