package sources

import (
	"context"
	"fmt"

	"terryhub/internal/domain/source"
	"terryhub/internal/domain/tvbox"
)

// Resolver 是所有站点类型（JAR / 苹果CMS / Python / JS）统一的解析接口。
type Resolver interface {
	Home(ctx context.Context, site tvbox.Site) (*source.HomeResult, error)
	Category(ctx context.Context, site tvbox.Site, tid, page string, extend map[string]string) (*source.CategoryResult, error)
	Detail(ctx context.Context, site tvbox.Site, ids []string) (*source.DetailResult, error)
	Search(ctx context.Context, site tvbox.Site, key string, quick bool) (*source.CategoryResult, error)
	Play(ctx context.Context, site tvbox.Site, flag, id string, vipFlags []string) (*source.PlayResult, error)
}

// Router 按站点类型把调用分发给对应的 Resolver，并持有全局的站点健康度统计。
type Router struct {
	byKind map[tvbox.Kind]Resolver
	health *Health
}

// NewRouter 创建路由器。
func NewRouter(health *Health) *Router {
	if health == nil {
		health = NewHealth("")
	}
	return &Router{byKind: map[tvbox.Kind]Resolver{}, health: health}
}

// Register 注册某类型的解析器。
func (r *Router) Register(k tvbox.Kind, res Resolver) { r.byKind[k] = res }

// Health 返回健康度统计器。
func (r *Router) Health() *Health { return r.health }

// Supports 判断该站点类型是否有可用的解析器。
func (r *Router) Supports(site tvbox.Site) bool {
	res, ok := r.byKind[site.Kind()]
	if !ok {
		return false
	}
	if sp, ok := res.(interface{ Supports(tvbox.Site) bool }); ok {
		return sp.Supports(site)
	}
	return true
}

func (r *Router) pick(site tvbox.Site) (Resolver, error) {
	if res, ok := r.byKind[site.Kind()]; ok {
		return res, nil
	}
	return nil, fmt.Errorf("不支持的站点类型 %s", site.Kind())
}

func (r *Router) Home(ctx context.Context, site tvbox.Site) (*source.HomeResult, error) {
	res, err := r.pick(site)
	if err != nil {
		return nil, err
	}
	return res.Home(ctx, site)
}

func (r *Router) Categories(ctx context.Context, site tvbox.Site) ([]source.Class, error) {
	h, err := r.Home(ctx, site)
	if err != nil {
		return nil, err
	}
	return h.Class, nil
}

func (r *Router) Category(ctx context.Context, site tvbox.Site, tid, page string, extend map[string]string) (*source.CategoryResult, error) {
	res, err := r.pick(site)
	if err != nil {
		return nil, err
	}
	return res.Category(ctx, site, tid, page, extend)
}

func (r *Router) Detail(ctx context.Context, site tvbox.Site, ids []string) (*source.DetailResult, error) {
	res, err := r.pick(site)
	if err != nil {
		return nil, err
	}
	return res.Detail(ctx, site, ids)
}

func (r *Router) Search(ctx context.Context, site tvbox.Site, key string, quick bool) (*source.CategoryResult, error) {
	res, err := r.pick(site)
	if err != nil {
		return nil, err
	}
	return res.Search(ctx, site, key, quick)
}

func (r *Router) Play(ctx context.Context, site tvbox.Site, flag, id string, vipFlags []string) (*source.PlayResult, error) {
	res, err := r.pick(site)
	if err != nil {
		return nil, err
	}
	return res.Play(ctx, site, flag, id, vipFlags)
}
