# TerryMediaHub 本机解析服务

本进程只监听 `127.0.0.1:18766`。插件按需启动它；Jellyfin 重启不会中断已经启动的服务进程。抓源、CSP 解析、测速、去重和 `.strm` / `.nfo` 落地在本进程执行，插件负责 Jellyfin 页面、媒体库扫描和流代理。

协议保持精简：

| 接口 | 用途 |
| --- | --- |
| `GET /health` | 检查进程是否就绪 |
| `POST /sync` | 传入插件配置和 Jellyfin 代理地址，启动后台同步 |
| `GET /status` | 查询进度、结果和同步后配置字段 |
| `POST /sources` | 查询站点清单与解析器状态 |
| `POST /resolve` | 将播放参数解析为实际地址 |
| `POST /inspect` | 排查单个站点的分类、列表、详情或播放地址 |
| `GET /site-proxy/{token}` | 调用当前 JS/Python 站点的 `proxy()` / `localProxy()`，返回站点生成的媒体或文本 |

除 `/health` 和 `/site-proxy/{token}` 外，请求均带 `X-Terry-Key`，值为插件已有的 `StreamSecret`。站点代理用该密钥签名的 token 校验，便于媒体客户端直接取流。密钥通过进程环境变量传给服务，不新增配置文件。服务不对局域网开放端口。

解析、测速和媒体文件生成代码位于 `src/TerryMediaHub.SourceCore`，插件与服务分别引用该项目。服务文件部署在 `%ProgramData%\TerryMediaHub\service`。更新解析逻辑时，编译本项目，停止 `TerryMediaHub.LocalService.dll` 进程，复制本项目的 `bin\Release\net8.0` 输出到部署目录；下次插件请求会自动启动新版服务。只更新服务不需要重启 Jellyfin。同步断点仍保存在媒体根目录的 `.sync-progress.json`。

TV6.2.9 的 `.py` 和 `.js` 分别由服务目录下 `Python/bridge.py`、`JavaScript/bridge.mjs` 在独立 Python 3.8、Node.js 进程执行。播放地址用带站点参数的描述符保存，播放和探测时再调用 `playerContent()` 或 `play()`。`JavaScript/drpy2` 包含从 APK 提取的规则引擎及同步请求适配；基础规则的分类、列表已接通，真实站点仍要逐个验证。APK 提取及反编译记录见根目录 `README.md` 第 8 节。

JS worker 将 APK 的 `assets://js/lib/` 模块映射到打包的 `JavaScript/assets`，并提供 `_http`、`getProxy()` 和 `js2Proxy()`。Python worker 的 `getProxyUrl()` 返回签名代理地址，`localProxy()` 可返回二进制内容。`src/TerryMediaHub.AndroidCompat` 是 JVM 宿主的 Android `Context` 兼容覆盖层；构建出的 jar 嵌入 SourceCore，在宿主启动时置于原运行时 jar 前面。
Python 基类还实现 `proxyM3u8/proxyMedia/proxyTs`，供盒子迷 Acfun.py 等站点的
`localProxy()` 调用；HLS 片段和密钥 URI 会改写为签名代理地址。真实 Acfun HLS 与首片
已通过本机服务测试，结果见 `src/reverse/tv629/reports/real-proxy-audit.json`。
