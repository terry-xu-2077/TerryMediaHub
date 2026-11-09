# 电视端 APK 规划（仅规划，尚未实现）

目标设备：小米电视（Android TV / HyperOS），遥控器操作。复用 Go 服务的同一套公共 API，不在电视上跑任何解析引擎。

## 1. 技术选型

| 项 | 选择 | 理由 |
|---|---|---|
| 语言/UI | Kotlin + Jetpack Compose for TV（`androidx.tv:tv-material`） | 原生焦点系统，天然支持 D-pad；与 Infuse 式卡片/横向 Rail 对应 |
| 播放器 | Media3 ExoPlayer（HLS/MP4），必要时 FFmpeg 扩展 | 硬解、倍速、音轨字幕；服务端已提供 `/api/proxy` 绕过防盗链 |
| 网络 | Retrofit/OkHttp + kotlinx.serialization；Coil 加载 `/api/img` | 海报走服务端缓存代理，电视不直连源站 |
| 服务发现 | NSD/mDNS（`_terryhub._tcp`）+ 手动输入 IP:端口 | 免配置；服务端需新增 mDNS 广播 |
| minSdk | 24（Android 7），targetSdk 34 | 覆盖老款电视 |

## 2. 服务端需要先补的接口（电视端前置）

1. `GET /api/ping`：返回版本与服务名，用于发现/连通性检查。
2. mDNS 广播（Go 侧使用 `hashicorp/mdns` 或自实现，注意这会引入第一个第三方依赖）。
3. 多档案（可选）：目前收藏/历史是单用户全局；多人共用电视时加 `X-Profile` 头，store 按档案分文件。
4. 播放进度双端互通：电视与网页共用 `/api/history`，已满足。
5. 访问保护：局域网配对码（首次电视输入 6 位码换取 token），避免暴露在非可信网络。

## 3. 界面与遥控器交互

- **首页**：顶部 Tab（首页/剧集/综艺/电影/动漫/收藏/记录/搜索）；Hero 轮播（OK 进入详情）；其下「继续观看 / 收藏 / 各分类」横向 Rail，焦点放大 + 描边高亮，与网页同构。
- **分类页**：`TvLazyVerticalGrid` 6 列，触底（焦点接近最后一行）自动请求下一页。
- **详情页**：左海报右信息；「播放/继续播放」默认获得焦点；选集为横向网格，已看集标记。
- **搜索**：屏幕键盘 + 语音输入（`SpeechRecognizer`）；历史搜索词；SSE 结果逐步出现。
- **播放器**：
  - 左/右：快退/快进 10 秒（长按加速）；上/下：呼出控制栏/选集面板；OK：暂停/播放；菜单键：线路/倍速/字幕；返回键：退出并上报进度。
  - 每 10 秒 `POST /api/history`；结束自动下一集。
  - 解码失败 → 请求 `/api/play/auto` 换线重试。
- **焦点规范**：所有可交互元素使用 `Modifier.focusable` + 统一焦点样式；返回键行为一致（先关面板，再退出页面）；记住上次焦点位置。

## 4. 工程结构

```
apps/tv/
  app/src/main/java/.../
    data/      Api、Repository、ServerDiscovery
    ui/home|category|detail|search|library|player/
    ui/components/  Poster、Rail、FocusableCard、EpisodeGrid
    navigation/ AppNav.kt
  gradle/libs.versions.toml
```

## 5. 里程碑

| 阶段 | 内容 | 验收 |
|---|---|---|
| M0 | 工程脚手架、服务器连接页（mDNS + 手动）、`/api/ping` | 模拟器/真机连上服务 |
| M1 | 首页 + 分类 + 详情（只读），焦点导航打磨 | 全程仅遥控器可浏览 |
| M2 | ExoPlayer 播放、续播、进度上报、下一集、换线 | 一部剧从首页看到下一集 |
| M3 | 搜索（语音）、收藏、播放记录 | 与网页数据互通 |
| M4 | 配对码鉴权、错误页/空态、性能优化（图片尺寸、预取） | 小米电视实机流畅 |
| M5 | 签名、版本号、侧载安装说明 | 可安装 APK |

## 6. 风险

- 电视内存小：Rail 图片需按显示尺寸下采样，列表用 Lazy 组件。
- 部分源是非标准 m3u8/带广告片段：服务端代理层可扩展广告段过滤。
- 开发环境需 Android SDK / JDK 17 / Gradle，当前机器未安装（仅有 JDK 21），需先配置。
