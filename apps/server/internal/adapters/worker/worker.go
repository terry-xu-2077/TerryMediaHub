// Package worker 实现与 JVM 解析宿主的逐行 JSON RPC 客户端。
//
// 协议见 contracts/worker/protocol.md。
package worker

import (
	"bufio"
	"context"
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"os"
	"os/exec"
	"strings"
	"sync"
	"time"
)

// Request 是发往 worker 的请求帧。
type Request struct {
	Method string          `json:"method"`
	Site   string          `json:"site"`
	Ext    string          `json:"ext"`
	Jar    string          `json:"jar,omitempty"`
	Api    string          `json:"api,omitempty"` // 脚本站点的脚本地址（py/js worker 用）
	Args   json.RawMessage `json:"args,omitempty"`
}

// Response 是 worker 返回的响应帧。
type Response struct {
	OK    bool            `json:"ok"`
	Data  json.RawMessage `json:"data"`
	Error string          `json:"error"`
	Trace string          `json:"trace"`
	MS    int64           `json:"ms"`
	// 握手帧
	Ready  bool   `json:"ready"`
	Dir    string `json:"dir"`
	Global string `json:"global"`
}

// Host 管理一个常驻的 JVM 子进程，串行复用。
type Host struct {
	Java      string // java.exe 路径
	HostJar   string // host-runtime.jar 路径
	SpiderDir string // spider-*.jar 所在目录
	GlobalJar string // 全局 spider jar 文件名

	// Command 非空时直接运行该命令（python/node 桥接 worker），忽略上面的 Java 相关字段。
	Command []string
	Env     []string

	mu     sync.Mutex
	cmd    *exec.Cmd
	stdin  io.WriteCloser
	stdout *bufio.Reader
	stderr *bufio.Reader

	Timeout time.Duration
}

// New 构造宿主（不启动）。
func New(java, hostJar, spiderDir, globalJar string) *Host {
	return &Host{
		Java:      java,
		HostJar:   hostJar,
		SpiderDir: spiderDir,
		GlobalJar: globalJar,
		Timeout:   60 * time.Second,
	}
}

// Start 启动子进程并等待握手。
func (h *Host) Start(ctx context.Context) error {
	h.mu.Lock()
	defer h.mu.Unlock()
	if h.cmd != nil {
		return nil
	}
	return h.startLocked(ctx)
}

// Call 发送一次 RPC，返回 data 字段的原始 JSON。
// 若子进程已退出（例如某次调用超时被终止），会自动重启一次。
func (h *Host) Call(ctx context.Context, req Request) (json.RawMessage, error) {
	h.mu.Lock()
	defer h.mu.Unlock()
	if h.cmd == nil {
		// 自动重启（超时被 kill 后自愈）
		if err := h.startLocked(ctx); err != nil {
			return nil, err
		}
	}

	body, err := json.Marshal(req)
	if err != nil {
		return nil, err
	}
	if _, err := h.stdin.Write(append(body, '\n')); err != nil {
		h.kill()
		return nil, fmt.Errorf("写入请求失败: %w", err)
	}

	line, err := h.readLine(ctx, h.Timeout)
	if err != nil {
		h.kill()
		return nil, err
	}

	var resp Response
	if err := json.Unmarshal([]byte(line), &resp); err != nil {
		return nil, fmt.Errorf("解析响应失败: %s", line)
	}
	if !resp.OK {
		msg := resp.Error
		if resp.Trace != "" {
			msg += "\n" + firstLines(resp.Trace, 4)
		}
		return nil, fmt.Errorf("spider[%s.%s] 失败: %s", req.Site, req.Method, msg)
	}
	return resp.Data, nil
}

// startLocked 在已持有 mu 的前提下启动子进程（供 Call 自愈使用）。
func (h *Host) startLocked(ctx context.Context) error {
	var cmd *exec.Cmd
	if len(h.Command) > 0 {
		cmd = exec.Command(h.Command[0], h.Command[1:]...)
		cmd.Env = append(os.Environ(), h.Env...)
	} else {
		args := []string{"-Dfile.encoding=UTF-8", "-Djava.awt.headless=true", "-cp", h.HostJar,
			"com.terry.spiderhost.Host", h.SpiderDir}
		if h.GlobalJar != "" {
			args = append(args, h.GlobalJar) // 为空则由 Host 自选最新的 spider jar
		}
		cmd = exec.Command(h.Java, args...)
	}
	stdin, err := cmd.StdinPipe()
	if err != nil {
		return err
	}
	stdout, err := cmd.StdoutPipe()
	if err != nil {
		return err
	}
	stderr, err := cmd.StderrPipe()
	if err != nil {
		return err
	}
	if err := cmd.Start(); err != nil {
		return err
	}

	h.cmd = cmd
	h.stdin = stdin
	h.stdout = bufio.NewReader(stdout)
	h.stderr = bufio.NewReader(stderr)

	line, err := h.readLine(ctx, 30*time.Second)
	if err != nil {
		h.kill()
		return fmt.Errorf("spider 宿主握手失败: %w", err)
	}
	var hello Response
	if err := json.Unmarshal([]byte(line), &hello); err != nil || !hello.Ready {
		h.kill()
		return fmt.Errorf("spider 宿主握手异常: %s", line)
	}
	return nil
}

// Ping 探测宿主存活。
func (h *Host) Ping(ctx context.Context) error {
	data, err := h.Call(ctx, Request{Method: "ping", Site: "_ping_"})
	if err != nil {
		return err
	}
	if string(data) == `"pong"` {
		return nil
	}
	return nil
}

// Close 终止子进程。
func (h *Host) Close() {
	h.mu.Lock()
	defer h.mu.Unlock()
	h.kill()
}

func (h *Host) kill() {
	if h.cmd != nil && h.cmd.Process != nil {
		_ = h.cmd.Process.Kill()
		_ = h.cmd.Wait()
	}
	h.cmd = nil
	h.stdin = nil
	h.stdout = nil
	h.stderr = nil
}

// readLine 读一行，跳过非 JSON 噪声。
//
// 关键：捕获当前 stdout 到局部变量，供读 goroutine 固定使用。绝不能在循环里
// 动态访问 h.stdout —— 若本次调用超时、进程被 kill 后重启（h.stdout 被重新
// 赋值为新进程的 reader），泄漏的读 goroutine 会因 continue 而读到新进程的
// stdout，与新请求的响应串包。
func (h *Host) readLine(ctx context.Context, timeout time.Duration) (string, error) {
	type result struct {
		line string
		err  error
	}
	stdout := h.stdout
	ch := make(chan result, 1)
	go func() {
		for i := 0; i < 64; i++ {
			line, err := stdout.ReadString('\n')
			if err != nil {
				ch <- result{"", err}
				return
			}
			line = strings.TrimSpace(line)
			if line == "" {
				continue
			}
			if !strings.HasPrefix(line, "{") {
				continue // 噪声行
			}
			ch <- result{line, nil}
			return
		}
		ch <- result{"", errors.New("响应噪声过多")}
	}()

	select {
	case r := <-ch:
		return r.line, r.err
	case <-ctx.Done():
		return "", ctx.Err()
	case <-time.After(timeout):
		return "", fmt.Errorf("spider 调用超时（>%s）", timeout)
	}
}

func firstLines(s string, n int) string {
	lines := strings.Split(s, "\n")
	if len(lines) > n {
		lines = lines[:n]
	}
	return strings.Join(lines, "\n")
}
