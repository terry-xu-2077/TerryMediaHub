# TerryMediaHub — 自建点播聚合媒体服务

一个独立运行的 **Go 媒体服务 + React 网页前端**（Infuse 风格界面）：订阅影视仓 / TVBox 兼容的配置，
把其中的 CSP（Java jar）、苹果CMS、JS、Python 点播源聚合成统一的「首页推荐 / 分类 / 搜索 / 详情 / 播放」体验，
并提供收藏、播放历史、多订阅源管理。浏览器打开即用，无需 Jellyfin。

> 早期的 Jellyfin 插件方案（`src/`）已搁置，原 README 存档在 [docs/jellyfin-plugin.md](docs/jellyfin-plugin.md)。

## 功能

- 聚合多种源：苹果CMS 接口、`csp_` Java 爬虫（内置 JVM 宿主 + Android 兼容层）、JS（Node worker）、Python worker
- 多订阅源管理（设置页）：支持加密配置 / 图片伪装订阅的解码、站点探索、启用/停用、测试与重载
- 首页推荐、分类浏览、跨站流式搜索、详情选集、自动选线播放（HLS，hls.js）
- 收藏、最近播放历史，海报图片缓存与代理（解决防盗链）
- 后台每 30 分钟自动刷新首页推荐、最近 5 条播放记录和收藏的片源
- 前端内嵌进可执行文件，单文件部署

## 启动

### 运行环境

- **JDK 8+**（运行 `csp_` 爬虫必需；自动探测 `JAVA_HOME` / PATH，也可用 `-java` 指定）
- Node.js / Python 3（仅 JS / Python 类站点需要）
- 构建时：Node.js（前端）；Go 工具链已随仓库放在 `tools/go`

### 直接运行

```bash
bin/terryhub.exe
```

然后访问 <http://localhost:8090>。首次启动会自动下载订阅里的 spider jar（耗时较长，之后有缓存）；
数据（jar、收藏、历史、图片缓存、订阅配置）都在可执行文件旁的 `data/` 目录。

命令行参数：

| 参数 | 默认 | 说明 |
|---|---|---|
| `-port` | `8090` | 监听端口 |
| `-data` | `<exe 目录>/data` | 数据目录 |
| `-sub` | 内置默认订阅 | 订阅地址（TVBox 配置 URL），也可在网页设置页管理 |
| `-java` | 自动探测 | `java.exe` 路径 |
| `-refresh` | `30m` | 后台刷新间隔 |
| `-web` | 空（用内嵌前端） | 前端目录，开发调试用 |

### 从源码构建

```bash
# 1. 构建前端（输出到 apps/server/internal/webui/dist，会被内嵌）
cd apps/web && npm install && npm run build

# 2. 构建服务端
cd ../server && ../../tools/go/bin/go build -o ../../bin/terryhub.exe ./cmd/terryhub
```

前端开发：`cd apps/web && npm run dev`（Vite 热更新），后端用 `-web` 指向前端目录或单独跑。

## 目录结构

```
apps/server/      Go 服务端（cmd/terryhub 入口；internal/{adapters,application,domain,store,webui}）
apps/web/         React + Vite 前端
workers/          java / js / python 三类解析 worker
contracts/        worker 通信协议
data/             运行时数据（jar、订阅、收藏、历史、图片缓存）
docs/             需求、架构、兼容性、验收、电视端规划（见 docs/README.md）
src/              旧 Jellyfin 插件与 APK 逆向资料（搁置，仅供参考）
```

## HTTP 接口（摘要）

`/api/home` `/api/categories` `/api/category` `/api/search` `/api/search/stream` `/api/detail`
`/api/play` `/api/play/auto` `/api/proxy` `/api/img` `/api/favorites` `/api/history`
`/api/status` `/api/refresh` `/api/sites` `/api/sources`（含 `test` / `toggle` / `reload`）

## 文档

- [docs/README.md](docs/README.md)：需求、架构、兼容性、验收文档入口
- [docs/tv-app-plan.md](docs/tv-app-plan.md)：电视端规划
- [docs/jellyfin-plugin.md](docs/jellyfin-plugin.md)：旧 Jellyfin 插件说明，其中 JAR 逆向与 csp 运行时原理（第 4 节）、TV6.2.9 APK 分析（第 8 节）对现服务仍有参考价值
