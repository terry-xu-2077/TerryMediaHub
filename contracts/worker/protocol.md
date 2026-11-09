# Worker 解析协议（JVM / JS / Python 通用）

本文是解析 worker（JVM 宿主、JS 宿主、Python 宿主）与 Go 主服务之间的控制通道契约，单一权威来源。
版本：v1。

## 1. 传输层

- 子进程常驻，`stdin` / `stdout` 做**逐行 JSON RPC**。
- 一行 = 一个请求（Go → worker）或一个响应（worker → Go）。
- **stdout 只传协议 JSON**；日志、调试输出一律写 `stderr`。
- 编码统一 UTF-8（启动时 `-Dfile.encoding=UTF-8`，IO 用 UTF-8）。

## 2. 握手

worker 启动后第一行输出握手帧：

```json
{ "ok": true, "ready": true, "dir": "<spiderDir绝对路径>", "global": "<全局jar文件名>" }
```

Go 侧读到此帧即认为 worker 就绪。若 30s 内未读到，判定启动失败。

## 3. 请求帧

Go → worker：

```json
{
  "method": "home | homeVideo | category | detail | search | play | ping | prepare",
  "site": "爬虫类名（不含包名）",
  "ext": "站点扩展参数（可空字符串）",
  "jar": "站点专用 jar 文件名（可空，缺省用全局 jar）",
  "args": { "..." : "..." }
}
```

### method 与 args 字段映射

| method | 调用的 Spider 方法 | args 字段 |
|---|---|---|
| `home` | `homeContent(true)` | — |
| `homeVideo` | `homeVideoContent()` | — |
| `category` | `categoryContent(tid, pg, true, extend)` | `tid`(字符串), `pg`(字符串,默认"1"), `extend`(JSON 对象,可选) |
| `detail` | `detailContent(ids[])` | `ids`(字符串数组) |
| `search` | `searchContent(key, quick)` | `key`(字符串), `quick`(布尔,默认 false) |
| `play` | `playerContent(flag, id, vipFlags[])` | `flag`(字符串), `id`(字符串), `vipFlags`(字符串数组,可选) |
| `ping` | — | — |
| `prepare` | — | — |

## 4. 响应帧

worker → Go：

成功：

```json
{ "ok": true, "data": "<spider 返回的 JSON 字符串>", "elapsed": 123 }
```

失败：

```json
{ "ok": false, "error": "<错误信息>", "trace": "<堆栈摘要，可选>", "elapsed": 123 }
```

`ping` 返回 `data = "pong"`。

`prepare` 返回站点可用性报告：

```json
{
  "ok": true,
  "data": "{\"global\":\"<全局jar>\",\"jars\":[{\"jar\":\"<jar名>\",\"classes\":<类数量>,\"error\":\"<可选>\"}]}"
}
```

## 5. 关键约束

- 类名全称 = `com.github.catvod.spider.<site>`。
- 实例缓存 key = `site + "|" + ext.hashCode() + "|" + jar`，同 key 复用实例，避免重复 `init`。
- 全局 jar 与站点 jar 按「子优先」类加载；`VerifyError` / `ClassFormatError` 自动回落父加载器。
- 同一 worker 内存在全局状态的调用串行执行；不同站点用独立实例隔离。
- 播放解析优先于后台刷新；超时可终止 worker 并恢复。
- worker 进程隔离用于故障恢复，**不是安全沙箱**；受限权限 + 独立工作目录，不继承管理凭据。
- 二进制数据（图片、视频）**不经过 JSON RPC**，走回环 HTTP 代理通道。

## 6. Spider 基类 API（catvod 协议）

```java
public class Spider {
  public static final OkHttpClient httpClient;
  public void init(Context ctx, String ext);
  public void init(Context ctx);
  public String homeContent(boolean filter);
  public String homeVideoContent();
  public String categoryContent(String tid, String pg, boolean filter, HashMap<String,String> extend);
  public String detailContent(List<String> ids);
  public String searchContent(String key, boolean quick);
  public String playerContent(String flag, String id, List<String> vipFlags);
  public OkHttpClient client();
  public boolean manualVideoCheck();
  public boolean isVideoFormat(String url);
  public Object[] proxyLocal(HashMap<String,String> params);
  public String getName();
}
```

方法返回均为 JSON 字符串（Vod/分类结构），Go 侧按 TVBox/CatVod 数据模型解析。
