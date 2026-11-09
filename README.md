# TerryMediaHub — 把影视仓 / TVBox 点播源变成 Jellyfin 里的独立媒体库

把 **影视仓 / TVBox 兼容的点播源** 解析成 Jellyfin 里一个**独立的**媒体库（默认名 **「在线媒体」**，
不会和你自己的本地库混在一起）：插件抓取源的影视列表，生成 `.strm` + 本地 NFO + 海报，
自动建库、自动扫描，并把失效的播放地址剔掉。

> **2026-11-09 更新：** 现在的主线是独立 Go 服务 + React 前端（Jellyfin 插件 `src/` 已搁置，仅供参考）。
>
> 运行：`bin/terryhub.exe`（默认端口 8090，数据在 `data/`；首次启动自动下载订阅里的 spider jar，并需要本机有 JDK）。
> 构建：`cd apps/web && npm run build`（输出到 `apps/server/internal/webui/dist` 并被内嵌），
> `cd apps/server && ../../tools/go/bin/go build -o ../../bin/terryhub.exe ./cmd/terryhub`。
> 后台每 30 分钟刷新首页推荐、最近 5 条播放记录、收藏的片源。电视端规划见 [docs/tv-app-plan.md](docs/tv-app-plan.md)。

> **2026-11-04 更新：**下文第 1～7 节保留早期 JAR 逆向过程，部分目录、按钮和能力描述已过时。
> 当前结构和影视仓 TV6.2.9 APK 的继续分析见[第 8 节](#8-当前结构与-tv629-apk-继续分析2026-11-04)。

- 插件名：**TerryMediaHub**
- 插件 ID：`3f5c8a41-9d2e-4b76-8c15-6a0e7d94b2f1`
- 目标：Jellyfin **10.10.x**（net8.0），已在 10.10.7 实测
- 额外依赖：**Java 8+**（只有 `csp_` 爬虫站点需要；没装 Java 时这部分站点会自动跳过，其它源照常工作）
- `.py` 源需要本机 Python 3.8；直接导出的 `.js` 源需要 Node.js。
- 媒体库根目录：`%ProgramData%\TerryMediaHub\OnlineLibrary`

---

## 1. 能力边界（重要）

影视仓配置里的站点大致分三类，本插件的处理：

| api 形态 | 含义 | 状态 |
|---|---|---|
| `http(s)://…/api.php/provide/vod` 等 | 苹果CMS v10（JSON/XML）标准接口 | ✅ 原生解析，最稳 |
| `csp_XXX` | jar 里的 Java 爬虫（catvod `Spider` 类） | ✅ 自带 JVM 运行时执行（见第 4 节） |
| 直接导出的 `*.js` | JS spider | ✅ 部分兼容，按站点验证 |
| `drpy2.min.js` | JS 规则脚本 | 🟡 引擎已接通；逐站解析仍在完善 |
| `*.py` | Python 插件 | ✅ 部分兼容，按站点验证 |

`.py` 和直接导出 `__jsEvalReturn()` 的 `.js` 已接入本机服务，见第 8 节。

`csp_` 站点并不是百分之百能跑：它依赖原 APP 的 Android 环境，本插件用一套自建的桌面运行时去模拟，
**大部分站点能通，少数仍然不行**。失败原因记录在同步结果和服务日志中：

- 需要 Android 原生库 / 需要用户登录态 / 需要 APP 内部 UI 的（如部分「配置中心」「弹幕配置」类）；
- 源自己给的站点专用 jar 链接已经 404 的（同步摘要会提示「N 个站点专用 jar 下载失败」）；
- dex 转 class 时个别方法无法还原，导致类校验失败的（`VerifyError`）。

---

## 2. 装了什么

```
<Jellyfin 数据目录>\plugins\TerryMediaHub_1.0.0.0\
    Jellyfin.Plugin.TerryMediaHub.dll     ← 插件本体（JVM 运行时以嵌入资源打包在里面）
    meta.json

C:\ProgramData\TerryMediaHub\
    OnlineLibrary\                       ← 生成的媒体文件（可在设置里改）
        Movies\<片名> (<年份>)\<片名> (<年份>).strm / .nfo / poster.jpg
        Shows\<剧名> (<年份>)\tvshow.nfo / poster.jpg / Season 01\<剧名> - S01E01.strm
    host\                                ← csp 爬虫的 JVM 工作目录
        host-runtime.jar                 ← 内置的 catvod 运行时（+ Android 桩）
        spider-<md5>.jar                 ← 从源里下载的 spider 包（可能是全局的，也可能是某站点专用的）
        spider-<md5>.classes.jar         ← 由上面那个转出来的普通 jar（缓存，首次会花几十秒）
```

Jellyfin 里会出现媒体库 **「在线媒体库」**（混合类型）。单集的当电影，多集的当剧集。
它和你自己手动建的库互不影响：插件只维护这个库，并且只删「本库 + 本次同步没生成」的条目。

---

## 3. 用起来

1. 重启 Jellyfin（插件只在启动时加载）。
2. 控制台 → **插件** → **TerryMediaHub**（左侧菜单也会出现入口）。
3. 确认「源配置地址」（默认已填盒子迷），点 **测试全部源** 看能解析几个站点。
4. 调参数：
   - **每站分类数上限 / 每分类页数 / 每站影片上限**：决定库有多大。
   - **同步时校验播放地址**：开着才会过滤失效源，代价是每个条目多 1~2 次探测请求。
   - **写进 .strm 的服务地址**：留空自动探测。**局域网/手机端观看请填内网地址**，
     例如 `http://192.168.1.10:8096`。
   - **jar 爬虫**区块：可以关掉 `csp_` 站点、手动指定 `java.exe`、强制指定全局 spider jar。
5. 点 **立即同步**，进度条会走；完成后媒体库会自动扫描。
6. 之后按「自动刷新间隔」定时同步（走 Jellyfin 计划任务「TerryMediaHub 在线媒体库同步」）。

---

## 4. 怎么把「脱离 APP 跑不起来」的 jar 爬虫逆向出来的

影视仓 6.x 的 `csp_XXX` 站点，代码不在 APK 里，而在配置的 `spider` 字段指向的一个包里。
这条链上原来有四道坎，逐条说结论：

### 4.1 包被伪装成图片

配置里是这样的：

```json
"spider": "https://ncstatic.clewm.net/.../56d0b667....png;md5;B63A0EB8852BB7AB06500C424CCC3DAE"
```

下载下来后缀是 `.png`，其实是 zip。里面只有一个 `classes.dex` —— 它本来就不是给普通 JVM 用的，
而是给 Android 的 `DexClassLoader` 用的。所以「文件头不是 zip」这个判断只对 APK 内那份加固的
`assets/classes0.jar` 成立，配置里这一份是好的。

另外 **每个站点还可以有自己的 `jar` 字段**（`"jar": "https://.../xxxx.png;md5;..."`），
这种站点专用的包里常常有全局包里没有的 spider 类 —— 只加载全局包会大面积 `ClassNotFoundException`。
插件现在会把所有站点专用 jar 一并下载、按站点优先级加载。

### 4.2 dex → class

用 dex2jar 现场转换（`Dex2jar.from(dex).withExceptionHandler(...)`），结果缓存成
`spider-<md5>.classes.jar`。有的 dex 里个别方法翻译不了，不带 handler 会整包失败，
带上之后能保住其余 99% 的类。

### 4.3 Android 依赖

jar 里的 spider 继承 `com.github.catvod.crawler.Spider`，还大量用 `okhttp3`、`org.json`、
`android.text.TextUtils`、`android.app.Application`、`SharedPreferences`……这些在运行时里都没有。

做法是**重新实现一份最小运行时**（打进 DLL 的 `Resources/SpiderHost.jar`）：

- `com.github.catvod.crawler.Spider` / `SpiderDebug` / `SpiderApi`：按 jar 实际引用的成员重建；
- 282 个 `android.*` / `androidx.*` 桩，其中 TextUtils、Base64、Uri、Handler、Application、
  SharedPreferences 是**功能性实现**，不是空壳；
- 桌面版 `org.json`（补了 Android 才有语义的 `keySet()`）、okhttp 3.14.9 / okio / gson / ASM / dex2jar。

### 4.4 类加载：多 jar + 容错

`Host` 是个常驻 JVM 子进程，stdin/stdout 一行一个 JSON 做 RPC。类加载模仿 Android 的 `DexClassLoader`：

```
站点专用 jar（子优先）
   └─▶ 全局 jar
          └─▶ 内置运行时（Spider 基类 / android 桩 / okhttp）
```

细节：

- **子优先**只作用于 `com.github.catvod.spider.` 前缀，这样站点 jar 里的 spider 能盖住全局包里的同名类，
  但 `crawler` 基类仍然是我们的；
- 站点 jar 里那份类**转坏了**（`ClassFormatError`、`VerifyError`、缺依赖类）时，自动回落到父加载器
  换一份好的，而不是让整个站点挂掉；
- 一个类在多个 jar 里都有时，按「站点 jar → 全局 jar → 其它」的顺序逐个试，谁先能实例化成功用谁；
- 少量 spider 会声明 `SpiderApi` 字段等 APP 反射注入，Host 在实例化后会自动把自实现的
  `SpiderApi`（`log` / `getAddress` / `getPort` / `getScreenOrientation` / `lrcToSrt`）注入进去。

### 4.5 播放参数

`csp_` 站点给出的往往不是 http 直链，而是一段播放参数（形如 `%7B%22p%22...`，解出来是
`{"p":[解析接口],"r":referer,"u":UA,"url":...}`）。插件在探测和播放时都会先把它交给
`ParseResolver` 换成真链，再做 m3u8 分片改写。

---

## 5. 工作原理

```
源配置(JSON)                         站点解析                        有效性过滤
┌──────────────┐   ┌────────────────────────────┐   ┌─────────────────────────┐
│ sites[]      │──▶│ 按 api 形态选 Resolver       │──▶│ 1. 站点级：分类接口探活    │
│ spider(s)    │   │ 苹果CMS → 原生 HTTP          │   │ 2. 条目级：探测播放地址    │
│ parses/lives │   │ csp_    → JVM 宿主（多 jar）  │   │ 3. 连续失败→自动拉黑站点   │
└──────────────┘   └────────────────────────────┘   └─────────────────────────┘
                                                              │
                      ┌───────────────────────────────────────┘
                      ▼
          写 .strm(指向插件代理) + .nfo + poster.jpg
                      │
                      ▼
      ILibraryManager.AddVirtualFolder("在线媒体库") → 触发扫描
                      │
                      ▼
   播放：/TerryMediaHub/Stream/<token>
           ├─ 带 UA/Referer 回源（解决防盗链）
           ├─ 播放参数 → 解析接口换真链
           ├─ 分享页 → 正则抠出 m3u8 再取
           ├─ m3u8 主/子列表 → 所有分片地址重写成代理 URL
           └─ 支持 Range，直传不落盘
```

几个关键设计：

- **`.strm` 里不是裸地址，而是带 HMAC 签名的代理地址**。签名（`StreamSecret`）防止别人拿你的
  服务当开放代理刷流量；同时支持 Referer 防盗链和 m3u8 分片改写。
- **过滤不是只看 HTTP 200**：校验 Content-Type、m3u8 是否含 `#EXTM3U`、首个分片是否真能下载；
  403 / 返回 HTML / 空响应 / **返回图片**（某些源用一张 PNG 冒充分片）都算失效。
- **站点自动拉黑**：连续 2 次同步失败就进黑名单，界面里「清除自动拉黑」可恢复。
- **媒体库只认本地 NFO**：默认关掉 TMDb 等联网元数据（`DisableInternetMetadata`），
  国内网络下扫描不会被超时拖死。
- **同名影片跨站去重**，避免 4 个站各存一份。

---

## 6. 接口

`/TerryMediaHub/...`：

| 方法 | 路径 | 说明 |
|---|---|---|
| GET | `/TerryMediaHub/Stream/{token}` | 流代理（匿名，靠签名校验） |
| GET | `/TerryMediaHub/Status` | 运行状态 + 站点清单 + jar 运行时状态 |
| POST | `/TerryMediaHub/Sync` | 触发一次同步（后台跑，轮询 Status） |
| POST | `/TerryMediaHub/ResetAutoBlocked` | 清除自动拉黑 |
| GET | `/TerryMediaHub/TestSource?url=` | 测试源配置地址 |
| GET | `/TerryMediaHub/Probe?url=` | 探测单个播放地址 |

除 Stream 外都要求管理员权限。

---

## 7. 常见问题

**同步完媒体库是空的？**
看设置页的「同步状态」摘要与站点清单。若可用站点很多但写入 0，多半是「同步时校验播放地址」把条目都判死了，
先把校验关掉试一次。写完仍看不到，手动跑一次 Jellyfin 的「扫描媒体库」。

**提示「N 个站点专用 jar 下载失败」？**
那是源配置里站点自己的 jar 链接已经失效（返回 404 / HTML）。这些站点没法离线补，跳过即可。

**jar 爬虫站点全是失败？**
先看 `Status` 里的 `jarSpider`：`ready=false` 通常是没装 Java 或 Java 版本太低。
设置页可以把 `java.exe` 路径手动填上。首次同步会花几十秒把 dex 转成 class，之后就快了。

**能播放但卡/花屏？**
把「写进 .strm 的服务地址」改成本机内网地址，别让流量绕公网。速度本身取决于源站 CDN。

**重启后播放地址失效了？**
不会。token 无状态，播放时重新回源。

**会不会动我自己的媒体库？**
不会。插件只创建/维护「在线媒体库」，只在它的 `Movies/` `Shows/` 下维护自己生成的
`.strm/.nfo/poster.jpg`，并且只删除「本次同步没有生成」的条目。

---

## 8. 当前结构与 TV6.2.9 APK 继续分析（2026-11-04）

当前代码拆为 `src/Jellyfin.Plugin.TerryMediaHub`（Jellyfin UI、流代理与任务）、
`src/TerryMediaHub.SourceCore`（源解析、筛选、媒体库生成）、
`src/TerryMediaHub.LocalService`（仅监听 `127.0.0.1:18766` 的解析服务）。
插件与服务用带密钥的精简 HTTP 接口通信。媒体库名为**在线媒体**，同步源数据与重建媒体库分别操作；
分类目录为近期热播、内地、剧集、综艺、动漫、电影。旧章节中的「在线媒体库」、旧按钮和所有逻辑都在插件 DLL 中的说法不再准确。

对根目录 `影视仓-TV6.2.9-64位.apk` 的补充分析保存在 `src/reverse/tv629`：

- JADX 解出的 `sources/com/undcover/freedom/pyramid/{Loader,Spider}.java` 表明 `.py` 站点由
  Chaquopy Python 3.8 加载，`homeContent/categoryContent/detailContent/playerContent` 是实际调用接口。
  APK 的 `assets/chaquopy/app.imy` 内含 `app.pyc` 与 `base/spider.pyc`；解编译结果在
  `src/reverse/tv629/python`。本机服务用独立 Python 进程实现同一调用方式。
- `sources/p1/a.java` 的运行时分派是 `.js` → QuickJS、`.py` → Python、`csp_` → Java jar。
  APK 的 `assets/js/lib/spider.js` 通过 `__jsEvalReturn()` 或默认导出获取 JS Spider，并注入
  `req` HTTP 函数。提取出的 JS 支持库在 `src/reverse/tv629/assets/js/lib`。
- 本机服务已增加 Node.js JS worker，支持直接导出 `init/home/category/detail/play` 的脚本，
  对 `assets://js/lib/cat.js` 做模块映射，播放时再调用 `play()` 获取新鲜地址。
  APK 的 23 个 `assets://js/lib/` 模块已映射到本地，站点 `proxy()` 已通过本机签名代理接通；
  部分配置源脚本链接或站点接口本身也可能失效。
- `drpy2` 已使用 APK 自带的规则引擎接入同一个 JS worker，补了同步 HTTP `req()`、规则初始化、
  基础 HTML 选择器及本地键值存储。内置测试规则已走通分类与列表，原有直接 JS 源仍能工作。
  当前配置中的 3 个 `drpy2` 站点的远端规则地址均返回 HTTP 404，因此尚未验证真实站点的详情和播放。
  引擎接通不代表这些失效的规则地址已恢复。
- JS/Python 站点代理已接入服务：JS `getProxy()`、`js2Proxy()` 和 Python `getProxyUrl()`
  生成签名的 `site-proxy` URL，分别分派到 `proxy()` / `localProxy()`。已用本地脚本验证文本响应、
  Python 二进制响应及错误签名拒绝；具体远端站点仍需逐个验收。
- 从已转换的 11 个 spider jar 扫描不到缺失的 `android.*` 类引用，但原宿主的 `Context`
  多个方法返回 null。`src/TerryMediaHub.AndroidCompat` 覆盖这些方法，提供非空资源、包管理、
  内容解析器、主题与持久化目录；已通过 Java 冒烟及宿主启动测试。这是部分 Android 兼容，
  尚未覆盖依赖原生库或更复杂系统行为的站点。

已在当前机器验证：Python 的 A 站源返回 63 个分类、29 条列表和 12 集，播放 HLS 探测成功；
JS 的极影源返回 3 个分类、20 条列表，示例剧集详情有 4 集，`play()` 返回 MP4，资源探测为 `video/mp4`。
夜猫脚本能加载，但它当前请求的数据返回非有效 JSON，尚不能入库。仅识别为“支持”的站点不等于每个远端源都可用，
实际同步仍会按站点响应和播放探测过滤。
2026-11-05 对当前源中的 15 个 JS/Python 站点与 70 个 CSP 站点做了逐站检查，
包括可解析样本的播放探测；详细结果见 `src/reverse/tv629/reports/README.md`。

## 9. 后续计划

- [ ] **电视直播（lives）**：Jellyfin LiveTV 需要 `ITunerHost` + `IListingsProvider` + `IService`，
      工作量比点播大，单独做。盒子迷配置里有 10 条 live 源、15 个解析器，届时可复用。
- [ ] drpy2 真实站点规则的详情、播放适配；逐站验证 JS/Python 代理和其他模块行为。
- [ ] 剩余 `csp_` 站点：补齐 `SpiderApi` 之外的宿主能力（原生库、登录态）。
- [ ] EPG（`https://epg.112114.xyz/pp.xml`）。
- [ ] 多源合并：同一部片保留多条线路，主线路失效时自动切换。

---

## 10. 开发

```bash
# 编译 + 部署到本机 Jellyfin（默认 %LOCALAPPDATA%\jellyfin\plugins）
scripts/deploy.sh

# 部署到别的目录（比如测试实例）
scripts/deploy.sh "D:/other-jellyfin/plugins"

# 端到端冒烟（需要先起一个测试实例在 8097）
python scripts/e2e_test.py
```

依赖：.NET 8 SDK。项目直接引用 Jellyfin 安装目录的 DLL（`-p:JellyfinDir=...` 可覆盖），
不依赖 NuGet 上的 Jellyfin 包，离线也能编译。

原 JVM 宿主运行时的源码和构建脚本不在本仓库里（工具链较长，dex2jar / CFR / 桩生成），
其 jar 作为嵌入资源随 DLL 发布。`src/TerryMediaHub.AndroidCompat` 包含新增的 Android
兼容覆盖层源码与构建脚本，产物 `AndroidCompat.jar` 嵌入 SourceCore。

源码结构：

```
src/Jellyfin.Plugin.TerryMediaHub/
├── Plugin.cs                 插件入口 + 配置页注册
├── ServiceRegistrator.cs     DI 注册
├── Configuration/            配置模型 + 配置页 HTML（内嵌资源）
├── Model/                    TVBox 配置模型、Vod 模型
├── Sources/                  源拉取、ResolverFactory、苹果CMS / csp 解析器
├── Runtime/SpiderHost.cs     JVM 宿主管理（java 探测、jar 下载、RPC、进程树回收）
├── Health/LinkProbe.cs       播放地址有效性探测（含播放参数解析、分享页解析）
├── Library/                  .strm/.nfo 生成、媒体库注册
├── Api/                      REST 控制器（流代理 + 管理）
├── Tasks/RefreshTask.cs      计划任务
├── Util/                     Http / Token(HMAC) / MediaResolver / ParseResolver
└── Resources/SpiderHost.jar  内置 JVM 运行时（catvod + android 桩 + okhttp + dex2jar）
```
