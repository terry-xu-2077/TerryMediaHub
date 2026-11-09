package catalog

import (
	"sync"
	"time"
)

// ttlCache 是带过期时间的泛型内存缓存。
type ttlCache[V any] struct {
	mu  sync.Mutex
	m   map[string]ttlEntry[V]
	ttl time.Duration
}

type ttlEntry[V any] struct {
	v  V
	at time.Time
}

func newTTL[V any](ttl time.Duration) *ttlCache[V] {
	return &ttlCache[V]{m: make(map[string]ttlEntry[V]), ttl: ttl}
}

func (c *ttlCache[V]) Get(k string) (V, bool) {
	c.mu.Lock()
	defer c.mu.Unlock()
	e, ok := c.m[k]
	if !ok || time.Since(e.at) > c.ttl {
		var zero V
		return zero, false
	}
	return e.v, true
}

func (c *ttlCache[V]) Set(k string, v V) {
	c.mu.Lock()
	c.m[k] = ttlEntry[V]{v: v, at: time.Now()}
	c.mu.Unlock()
}

// Clear 清空缓存。
func (c *ttlCache[V]) Clear() {
	c.mu.Lock()
	c.m = make(map[string]ttlEntry[V])
	c.mu.Unlock()
}
