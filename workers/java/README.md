# JVM 解析宿主（workers/java）

## 作用

把 TVBox / 影视仓的 `csp_` 站点爬虫（`com.github.catvod.spider.*`，打包在 jar 里，只能在 JVM 运行）
通过 stdin/stdout 逐行 JSON RPC 暴露给 Go 主服务。

协议见 `contracts/worker/protocol.md`。

## 文件

- `src/com/terry/spiderhost/Host.java` —— 宿主主逻辑（RPC 循环、多 jar 类加载、方法分发、dex2jar 容错）。
  从上一版 `host-runtime.jar` 中的 `Host.class` 反编译还原，补齐丢失的源码。
- `host-runtime.jar` —— 运行时成品（**fat jar**，内含 Spider 基类 + 全套 Android 最小桩 + okhttp/gson/org.json/d2j）。

## host-runtime.jar 的构成（自包含）

一个 fat jar，打包了：

| 内容 | 说明 |
|---|---|
| `com/terry/spiderhost/Host` | 宿主主逻辑 |
| `com/github/catvod/crawler/Spider` | catvod Spider 基类（协议见 protocol.md） |
| `com/github/catvod/crawler/SpiderApi` | 注入给爬虫的 API 桩 |
| `android.*` | Android 最小桩（Context/SharedPreferences/TextUtils/Base64 等，功能性实现） |
| `com/google/gson/*` | JSON 序列化 |
| `okhttp3/*` | HTTP 客户端 |
| `org/json/*` | JSONObject/JSONArray |
| `com/googlecode/d2j/*` | dex2jar（把站点 jar 里的 classes.dex 转成 .class） |

## 运行

```bash
java -Dfile.encoding=UTF-8 -cp host-runtime.jar com.terry.spiderhost.Host <spiderDir> [globalJarName]
```

- `<spiderDir>`：存放 `spider-*.jar`（站点爬虫，含 `classes.dex`）的目录。
- `[globalJarName]`：全局 spider jar 文件名（可选，缺省取最新的 `spider-*.jar`）。

## 构建（完全可复现，2026-11-09）

`bash scripts/build-host.sh` 从仓库内容完整重建 `host-runtime.jar`（输出到 `apps/server/internal/assets/`，由 Go 服务内嵌）：

| 内容 | 位置 | 说明 |
|---|---|---|
| Host、SimplePrefs | `src/com/terry/spiderhost/` | 宿主逻辑（手工修复了反编译遗留的类型错误） |
| Spider / SpiderApi / SpiderDebug | `src/com/github/catvod/crawler/` | catvod 基类 |
| Android / androidx 桩、org.json | `src/android/`、`src/androidx/`、`src/org/json/` | 用 CFR 0.152 从旧 jar 反编译恢复，`scripts/fix_decompiled.py` 修正扁平内部类的引用 |
| okhttp 3.14.9 / okio / gson / ASM / dex2jar 等 | `lib/third-party.jar` | 第三方二进制，原样保留 |

已验证：用重建的 jar 冷启动（清空 dex 转换缓存）后，csp 站点的首页、搜索、详情、播放全部正常，冒烟测试 11/11 通过。

注意：恢复出的桩源码是反编译产物（变量名为 `string`/`object2` 之类），可读性一般，但可编译、可修改。
