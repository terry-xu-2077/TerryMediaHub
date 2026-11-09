// Package worker 实现与 JVM 解析宿主的逐行 JSON RPC 客户端。
package worker

import (
	"context"
	"log"
	"sync"
	"time"
)

// Pool 管理一组常驻 JVM worker 进程，提供并发 RPC 能力。
//
// 每个 worker 进程拥有独立的 stdin/stdout，天然隔离、不会串包。
// Acquire/Release 基于 channel 信号量实现「严格独占」：同一时刻一个 worker
// 进程只被一个调用占用，用完必须 Release 归还，避免并发请求争抢同一进程
// 导致响应串包。
type Pool struct {
	hosts  []*Host    // 所有 worker 进程
	idle   chan *Host // 空闲进程信号量
	closed bool
	mu     sync.Mutex
}

// NewPool 创建指定大小的 worker 池（不启动）。
func NewPool(size int, java, hostJar, spiderDir, globalJar string) *Pool {
	hosts := make([]*Host, size)
	for i := range hosts {
		hosts[i] = New(java, hostJar, spiderDir, globalJar)
	}
	return &Pool{hosts: hosts, idle: make(chan *Host, size)}
}

// NewCommandPool 创建运行任意桥接命令（python/node）的 worker 池。
func NewCommandPool(size int, command, env []string, timeout time.Duration) *Pool {
	hosts := make([]*Host, size)
	for i := range hosts {
		hosts[i] = &Host{Command: command, Env: env, Timeout: timeout}
	}
	return &Pool{hosts: hosts, idle: make(chan *Host, size)}
}

// Start 启动所有 worker 进程并等待握手。惰性失败：只要求至少一个可用。
//
// 先只启动第 0 个进程并让它「prepare」：把所有 spider jar 的 dex 一次性转成 class 并缓存。
// 若 6 个进程同时冷启动，会重复转换同一批 jar、互相抢临时文件，且前几十秒的请求大量超时。
// 转换完成后其余进程直接复用缓存，几秒即可就绪。
func (p *Pool) Start(ctx context.Context) error {
	first := p.hosts[0]
	if err := first.Start(ctx); err != nil {
		return err
	}
	if len(first.Command) == 0 { // 仅 JVM worker 需要预转换 dex
		old := first.Timeout
		first.Timeout = 10 * time.Minute
		_, perr := first.Call(ctx, Request{Method: "prepare"})
		first.Timeout = old
		if perr != nil {
			log.Printf("[worker] prepare 失败（继续启动）: %v", perr)
		}
	}
	p.idle <- first

	var (
		mu       sync.Mutex
		wg       sync.WaitGroup
		firstErr error
	)
	for _, h := range p.hosts[1:] {
		wg.Add(1)
		go func(h *Host) {
			defer wg.Done()
			if err := h.Start(ctx); err != nil {
				mu.Lock()
				if firstErr == nil {
					firstErr = err
				}
				mu.Unlock()
				return
			}
			p.idle <- h
		}(h)
	}
	wg.Wait()
	if firstErr != nil {
		log.Printf("[worker] 部分进程启动失败: %v", firstErr)
	}
	return nil
}

// Size 返回池大小。
func (p *Pool) Size() int { return len(p.hosts) }

// Acquire 阻塞获取一个空闲 worker 进程（严格独占，用完必须 Release）。
// 若所有进程都忙，则等待直到有进程释放或 ctx 取消。
func (p *Pool) Acquire(ctx context.Context) (*Host, error) {
	select {
	case h := <-p.idle:
		return h, nil
	case <-ctx.Done():
		return nil, ctx.Err()
	}
}

// Release 归还 worker 进程到空闲队列，供其他调用使用。
func (p *Pool) Release(h *Host) {
	if h == nil {
		return
	}
	p.mu.Lock()
	if p.closed {
		p.mu.Unlock()
		return
	}
	p.mu.Unlock()
	p.idle <- h
}

// Close 终止所有 worker 进程。
func (p *Pool) Close() {
	p.mu.Lock()
	if p.closed {
		p.mu.Unlock()
		return
	}
	p.closed = true
	close(p.idle)
	p.mu.Unlock()
	for _, h := range p.hosts {
		h.Close()
	}
}

// Prepare 让一个 JVM worker 预先转换全部 spider jar（新增订阅/新下载 jar 后调用）。
func (p *Pool) Prepare(ctx context.Context) error {
	h, err := p.Acquire(ctx)
	if err != nil {
		return err
	}
	defer p.Release(h)
	if len(h.Command) > 0 {
		return nil
	}
	old := h.Timeout
	h.Timeout = 10 * time.Minute
	defer func() { h.Timeout = old }()
	_, err = h.Call(ctx, Request{Method: "prepare"})
	return err
}
