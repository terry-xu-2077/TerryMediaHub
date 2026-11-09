// terryhub 是 TerryMediaHub 服务端入口。
//
// 默认启动 HTTP 媒体服务（浏览/搜索/播放闭环）。
// 用法：
//
//	terryhub [-sub <订阅地址>] [-port 8090] [-data <数据目录>]
package main

import (
	"context"
	"flag"
	"fmt"
	"io/fs"
	"log"
	"net/http"
	"os"
	"os/exec"
	"os/signal"
	"path/filepath"
	"sync/atomic"
	"syscall"
	"time"

	"terryhub/internal/adapters/httpapi"
	"terryhub/internal/adapters/sources"
	"terryhub/internal/adapters/worker"
	"terryhub/internal/application/catalog"
	"terryhub/internal/application/ranking"
	"terryhub/internal/application/refresher"
	"terryhub/internal/application/subscriptions"
	"terryhub/internal/assets"
	"terryhub/internal/domain/tvbox"
	"terryhub/internal/store"
	"terryhub/internal/webui"
)

func main() {
	if err := run(); err != nil {
		fmt.Fprintln(os.Stderr, "错误:", err)
		os.Exit(1)
	}
}

func run() error {
	exe, _ := os.Executable()
	defaultData := filepath.Join(filepath.Dir(exe), "data")
	sub := flag.String("sub", defaultSub, "订阅地址（TVBox 配置 URL）")
	dataDir := flag.String("data", defaultData, "数据目录（jar、收藏、历史、海报缓存）")
	java := flag.String("java", "", "java.exe 路径（缺省自动探测）")
	port := flag.Int("port", 8090, "监听端口")
	webDir := flag.String("web", "", "前端目录（开发用；缺省使用内嵌前端）")
	every := flag.Duration("refresh", 30*time.Minute, "后台刷新间隔")
	flag.Parse()

	ctx, stop := signal.NotifyContext(context.Background(), os.Interrupt, syscall.SIGTERM)
	defer stop()

	// 1. Java + 宿主 jar（内嵌，启动时释放）
	javaPath := *java
	if javaPath == "" {
		javaPath = findJava()
	}
	if javaPath == "" {
		return fmt.Errorf("未找到 java，请安装 JDK 8+ 或用 -java 指定")
	}
	hostDir := filepath.Join(*dataDir, "host")
	jarDir := filepath.Join(*dataDir, "jars")
	if err := os.MkdirAll(hostDir, 0o755); err != nil {
		return err
	}
	cleanTemp(jarDir)
	hostPath := filepath.Join(hostDir, "host-runtime.jar")
	if err := os.WriteFile(hostPath, assets.HostRuntimeJar, 0o644); err != nil {
		return err
	}
	log.Printf("[1/5] Java: %s", javaPath)

	// 2. 解析器：JAR(JVM) / 苹果CMS / Python / JS
	const workerPoolSize = 6
	pool := worker.NewPool(workerPoolSize, javaPath, hostPath, jarDir, "")
	defer pool.Close()
	health := sources.NewHealth(filepath.Join(*dataDir, "store", "health.json"))
	defer health.Flush()
	jar := sources.NewPoolResolver(pool)
	jar.SetHealth(health)
	resolver := sources.NewRouter(health)
	resolver.Register(tvbox.KindCsp, jar)
	resolver.Register(tvbox.KindCMS, sources.NewCMSResolver(health))
	for _, c := range startScriptRuntimes(ctx, *dataDir, health, resolver) {
		defer c()
	}
	log.Printf("[2/5] 解析器已注册")

	// 3. 订阅源：加载全部启用的源 → 下载 spider jar → 合并站点
	st, err := store.Open(filepath.Join(*dataDir, "store"))
	if err != nil {
		return err
	}
	catalogSvc := catalog.New(&tvbox.Config{}, resolver)
	ranker := ranking.New()
	catalogSvc.SetRanker(ranker)
	var rf *refresher.Refresher
	var poolReady atomic.Bool
	mgr := subscriptions.NewManager(*dataDir, *sub, resolver.Supports,
		func(cfg *tvbox.Config) { // 站点集合变化：热更新并触发一轮后台刷新
			catalogSvc.SetConfig(cfg)
			log.Printf("      订阅已加载：站点 %d 个（可用 %d 个）", len(cfg.Sites), catalogSvc.SiteCount())
			if rf != nil {
				rf.Trigger()
			}
		},
		func(c context.Context) { // 新 jar 下载后预转换 dex
			if poolReady.Load() {
				if err := pool.Prepare(c); err != nil {
					log.Printf("      jar 预转换失败: %v", err)
				}
			}
		}, log.Printf)
	log.Printf("[3/5] 加载订阅源（%d 个）", len(mgr.List()))
	mgr.Reload(ctx)

	// 4. JVM worker 池（先转换全部 dex，再起其余进程）
	log.Printf("[4/5] 启动 JVM worker 池（size=%d）", workerPoolSize)
	if err := pool.Start(ctx); err != nil {
		return err
	}
	poolReady.Store(true)
	rf = refresher.New(catalogSvc, st, ranker, *every)
	go rf.Run(ctx)

	// 5. HTTP
	var web fs.FS = webui.FS()
	if *webDir != "" {
		web = os.DirFS(*webDir)
	}
	srv := httpapi.New(catalogSvc, st, rf, mgr, web, filepath.Join(*dataDir, "imgcache"))
	httpServer := &http.Server{Addr: fmt.Sprintf(":%d", *port), Handler: srv.Handler()}
	go func() {
		<-ctx.Done()
		c, cancel := context.WithTimeout(context.Background(), 5*time.Second)
		defer cancel()
		_ = httpServer.Shutdown(c)
	}()
	log.Printf("[5/5] 服务已启动: http://localhost:%d （后台每 %s 刷新一次）", *port, *every)
	if err := httpServer.ListenAndServe(); err != nil && err != http.ErrServerClosed {
		return err
	}
	return nil
}

const defaultSub = "https://xn--i8sz35d1yr.top/%E7%A6%81%E6%AD%A2%E8%B4%A9%E5%8D%96"

// findJava 探测 java.exe。
func findJava() string {
	candidates := []string{}
	if home := os.Getenv("JAVA_HOME"); home != "" {
		candidates = append(candidates, filepath.Join(home, "bin", "java.exe"))
	}
	if p, err := exec.LookPath("java"); err == nil {
		candidates = append(candidates, p)
	}
	for _, root := range []string{
		`C:\Program Files\Microsoft`,
		`C:\Program Files\Java`,
		`C:\Program Files\Eclipse Adoptium`,
		`C:\Program Files\Zulu`,
		`C:\Program Files\Amazon Corretto`,
	} {
		entries, _ := os.ReadDir(root)
		for i := len(entries) - 1; i >= 0; i-- {
			candidates = append(candidates, filepath.Join(root, entries[i].Name(), "bin", "java.exe"))
		}
	}
	for _, c := range candidates {
		if _, err := os.Stat(c); err == nil {
			return c
		}
	}
	return ""
}

// cleanTemp 清理上次异常退出遗留的转换临时文件。
func cleanTemp(dir string) {
	for _, pat := range []string{"*.tmp", "*.part", "*.tmp.dex", "zipfstmp*"} {
		matches, _ := filepath.Glob(filepath.Join(dir, pat))
		for _, m := range matches {
			_ = os.Remove(m)
		}
	}
}

// startScriptRuntimes 释放内嵌的桥接脚本并启动 python / node worker 池，返回清理函数。
// 缺少对应运行时（或依赖未安装）只会跳过该类站点，不影响其它站点。
func startScriptRuntimes(ctx context.Context, dataDir string, health *sources.Health, router *sources.Router) []func() {
	var closers []func()
	root := filepath.Join(dataDir, "runtime")
	if err := extractFS(assets.Runtime, "runtime", root); err != nil {
		log.Printf("      释放脚本运行时失败: %v", err)
		return nil
	}

	// Node：运行 JS 爬虫
	if node, err := exec.LookPath("node"); err == nil {
		pool := worker.NewCommandPool(2, []string{node, filepath.Join(root, "js", "bridge.mjs")},
			[]string{"NODE_NO_WARNINGS=1", "TERRY_JS_CACHE=" + filepath.Join(dataDir, "scripts", "js")}, 45*time.Second)
		if err := pool.Start(ctx); err != nil {
			log.Printf("      JS worker 启动失败，跳过 JS 站点: %v", err)
		} else {
			router.Register(tvbox.KindJS, sources.NewScriptResolver(pool, health))
			closers = append(closers, pool.Close)
			log.Printf("      JS worker 就绪（node）")
		}
	} else {
		log.Printf("      未找到 node，跳过 JS 站点")
	}

	// Python：运行 Python 爬虫（依赖 bs4/lxml/pycryptodome 装在 runtime/py/lib）
	py := ""
	for _, name := range []string{"python", "python3"} {
		if p, err := exec.LookPath(name); err == nil {
			py = p
			break
		}
	}
	if py == "" {
		log.Printf("      未找到 python，跳过 Python 站点")
		return closers
	}
	lib := filepath.Join(root, "py", "lib")
	chk := exec.Command(py, "-c", "import sys; sys.path.insert(0, sys.argv[1]); import bs4, lxml, Crypto, requests", lib)
	if err := chk.Run(); err != nil {
		log.Printf("      Python 依赖缺失，跳过 Python 站点。安装: %s -m pip install --target \"%s\" beautifulsoup4 lxml pycryptodome requests", py, lib)
		return closers
	}
	pool := worker.NewCommandPool(2, []string{py, "-X", "utf8", filepath.Join(root, "py", "bridge.py")},
		[]string{"PYTHONIOENCODING=utf-8", "TERRY_PY_CACHE=" + filepath.Join(dataDir, "scripts", "py")}, 45*time.Second)
	if err := pool.Start(ctx); err != nil {
		log.Printf("      Python worker 启动失败，跳过 Python 站点: %v", err)
		return closers
	}
	router.Register(tvbox.KindPy, sources.NewScriptResolver(pool, health))
	log.Printf("      Python worker 就绪")
	return append(closers, pool.Close)
}

// extractFS 把嵌入的目录树写到磁盘（覆盖同名文件，保留目录里的其它内容，如 py/lib）。
func extractFS(fsys fs.FS, from, to string) error {
	return fs.WalkDir(fsys, from, func(p string, d fs.DirEntry, err error) error {
		if err != nil {
			return err
		}
		rel, _ := filepath.Rel(from, p)
		dst := filepath.Join(to, rel)
		if d.IsDir() {
			return os.MkdirAll(dst, 0o755)
		}
		b, err := fs.ReadFile(fsys, p)
		if err != nil {
			return err
		}
		return os.WriteFile(dst, b, 0o644)
	})
}
