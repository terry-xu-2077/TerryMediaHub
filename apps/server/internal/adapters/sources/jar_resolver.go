// Package sources 实现站点解析器：通过 worker 调用 spider 的 home/category/detail/search。
package sources

import (
	"context"
	"encoding/json"
	"errors"
	"fmt"
	"time"

	"terryhub/internal/adapters/worker"
	"terryhub/internal/domain/source"
	"terryhub/internal/domain/tvbox"
)

// JarResolver 通过 JVM worker 解析 csp_ 站点。
type JarResolver struct {
	pool  *worker.Pool
	host  *worker.Host  // 单 host 兼容（无池时）
	bgSem chan struct{} // 后台任务并发上限，给用户交互请求留出 worker

	health *Health
}

// Health 返回站点健康度统计器。
func (r *JarResolver) Health() *Health { return r.health }

// SetHealth 注入（可持久化的）健康度统计器。
func (r *JarResolver) SetHealth(h *Health) { r.health = h }

// record 记录一次已实际占用 worker 的调用结果；调用方主动取消不算站点失败。
func (r *JarResolver) record(key string, start time.Time, err error) {
	if r.health == nil || errors.Is(err, context.Canceled) {
		return
	}
	msg := ""
	if err != nil {
		msg = err.Error()
	}
	r.health.Record(key, time.Since(start), msg)
}

type bgKey struct{}

// WithBackground 标记本次调用为后台任务（受并发上限约束，不挤占用户请求）。
func WithBackground(ctx context.Context) context.Context {
	return context.WithValue(ctx, bgKey{}, true)
}

// NewJarResolver 用单个 worker 进程构造解析器。
func NewJarResolver(host *worker.Host) *JarResolver {
	return &JarResolver{host: host, health: NewHealth("")}
}

// NewPoolResolver 用 worker 池构造解析器，支持并发 RPC。
func NewPoolResolver(pool *worker.Pool) *JarResolver {
	n := pool.Size() / 3
	if n < 1 {
		n = 1
	}
	return &JarResolver{pool: pool, bgSem: make(chan struct{}, n), health: NewHealth("")}
}

// call 通过 worker 进程执行一次 RPC，池模式下严格独占一个进程（用完归还），
// 避免并发请求争抢同一进程导致响应串包；单 host 模式退回原 host。
func (r *JarResolver) call(ctx context.Context, key string, req worker.Request) (json.RawMessage, error) {
	if r.pool == nil {
		return r.host.Call(ctx, req)
	}
	if bg, _ := ctx.Value(bgKey{}).(bool); bg && r.bgSem != nil {
		select {
		case r.bgSem <- struct{}{}:
			defer func() { <-r.bgSem }()
		case <-ctx.Done():
			return nil, ctx.Err()
		}
	}
	h, err := r.pool.Acquire(ctx)
	if err != nil {
		return nil, err
	}
	defer r.pool.Release(h)
	start := time.Now()
	data, err := h.Call(ctx, req)
	r.record(key, start, err)
	return data, err
}

// unmarshalData 把 worker 返回的 data 解到目标结构。
// worker 的 data 是「spider 返回 JSON 字符串」再被 Java 侧 toString 序列化一次，
// 因此通常是双重编码：先解出字符串，再解第二层。这里统一处理。
func unmarshalData(raw json.RawMessage, v any) error {
	if len(raw) == 0 {
		return fmt.Errorf("空响应")
	}
	// 情况 1：data 直接是对象/数组
	if raw[0] == '{' || raw[0] == '[' {
		return json.Unmarshal(raw, v)
	}
	// 情况 2：data 是字符串（双重编码），先解出字符串
	var s string
	if err := json.Unmarshal(raw, &s); err != nil {
		return err
	}
	if s == "" {
		return fmt.Errorf("空字符串响应")
	}
	return json.Unmarshal([]byte(s), v)
}

// Home 获取站点首页（分类 + 推荐）。
func (r *JarResolver) Home(ctx context.Context, site tvbox.Site) (*source.HomeResult, error) {
	raw, err := r.call(ctx, site.Key, worker.Request{
		Method: "home",
		Site:   site.CspClass(),
		Ext:    site.ExtString(),
		Jar:    site.Jar,
	})
	if err != nil {
		return nil, err
	}
	var result source.HomeResult
	if err := unmarshalData(raw, &result); err != nil {
		return nil, fmt.Errorf("解析 home 返回失败: %w", err)
	}
	return &result, nil
}

// Categories 获取分类列表（等同 Home 的 Class 部分）。
func (r *JarResolver) Categories(ctx context.Context, site tvbox.Site) ([]source.Class, error) {
	home, err := r.Home(ctx, site)
	if err != nil {
		return nil, err
	}
	return home.Class, nil
}

// Category 获取某分类下某页的列表。
func (r *JarResolver) Category(ctx context.Context, site tvbox.Site, tid, page string, extend map[string]string) (*source.CategoryResult, error) {
	args := map[string]any{"tid": tid, "pg": page}
	if len(extend) > 0 {
		args["extend"] = extend
	}
	argRaw, _ := json.Marshal(args)
	raw, err := r.call(ctx, site.Key, worker.Request{
		Method: "category",
		Site:   site.CspClass(),
		Ext:    site.ExtString(),
		Jar:    site.Jar,
		Args:   argRaw,
	})
	if err != nil {
		return nil, err
	}
	var result source.CategoryResult
	if err := unmarshalData(raw, &result); err != nil {
		return nil, fmt.Errorf("解析 category 返回失败: %w", err)
	}
	return &result, nil
}

// Detail 获取详情（含播放地址）。
func (r *JarResolver) Detail(ctx context.Context, site tvbox.Site, ids []string) (*source.DetailResult, error) {
	argRaw, _ := json.Marshal(map[string]any{"ids": ids})
	raw, err := r.call(ctx, site.Key, worker.Request{
		Method: "detail",
		Site:   site.CspClass(),
		Ext:    site.ExtString(),
		Jar:    site.Jar,
		Args:   argRaw,
	})
	if err != nil {
		return nil, err
	}
	var result source.DetailResult
	if err := unmarshalData(raw, &result); err != nil {
		return nil, fmt.Errorf("解析 detail 返回失败: %w", err)
	}
	return &result, nil
}

// Search 搜索站点。
func (r *JarResolver) Search(ctx context.Context, site tvbox.Site, key string, quick bool) (*source.CategoryResult, error) {
	argRaw, _ := json.Marshal(map[string]any{"key": key, "quick": quick})
	raw, err := r.call(ctx, site.Key, worker.Request{
		Method: "search",
		Site:   site.CspClass(),
		Ext:    site.ExtString(),
		Jar:    site.Jar,
		Args:   argRaw,
	})
	if err != nil {
		return nil, err
	}
	var result source.CategoryResult
	if err := unmarshalData(raw, &result); err != nil {
		return nil, fmt.Errorf("解析 search 返回失败: %w", err)
	}
	return &result, nil
}

// Play 解析播放地址。
func (r *JarResolver) Play(ctx context.Context, site tvbox.Site, flag, id string, vipFlags []string) (*source.PlayResult, error) {
	argRaw, _ := json.Marshal(map[string]any{"flag": flag, "id": id, "vipFlags": vipFlags})
	raw, err := r.call(ctx, site.Key, worker.Request{
		Method: "play",
		Site:   site.CspClass(),
		Ext:    site.ExtString(),
		Jar:    site.Jar,
		Args:   argRaw,
	})
	if err != nil {
		return nil, err
	}
	var result source.PlayResult
	if err := unmarshalData(raw, &result); err != nil {
		return nil, fmt.Errorf("解析 play 返回失败: %w", err)
	}
	return &result, nil
}
