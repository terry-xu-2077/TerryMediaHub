using System;
using System.Collections.Generic;
using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.TerryMediaHub.Configuration;

/// <summary>一个源配置（TVBox 兼容的 JSON 地址）。</summary>
public sealed class SourceEntry
{
    public string Name { get; set; } = string.Empty;

    public string Url { get; set; } = string.Empty;

    public bool Enabled { get; set; } = true;
}

/// <summary>站点失败计数（XmlSerializer 不支持 Dictionary，用列表代替）。</summary>
public sealed class SiteFailEntry
{
    public string Key { get; set; } = string.Empty;

    public int Count { get; set; }
}

public sealed class PluginConfiguration : BasePluginConfiguration
{
    public PluginConfiguration()
    {
        Sources = new List<SourceEntry>();
    }

    /// <summary>源配置列表，可填多个，按顺序合并。</summary>
    public List<SourceEntry> Sources { get; set; }

    /// <summary>站点白名单（key）。为空表示所有可解析站点都启用。</summary>
    public List<string> EnabledSiteKeys { get; set; } = new();

    /// <summary>站点黑名单（key），优先级高于白名单。</summary>
    public List<string> BlockedSiteKeys { get; set; } = new();

    /// <summary>自动失效拉黑的站点（插件写入，用户可在 UI 一键恢复）。</summary>
    public List<string> AutoBlockedSiteKeys { get; set; } = new();

    /// <summary>是否启用 jar 爬虫（csp_ 站点）。需要本机有 Java 运行时。</summary>
    public bool EnableJarSpider { get; set; } = true;

    /// <summary>java.exe 路径，留空自动探测。</summary>
    public string JavaPath { get; set; } = string.Empty;

    /// <summary>spider jar 地址（含 ;md5;xxx），留空则用源配置里的 spider 字段。</summary>
    public string SpiderJarUrl { get; set; } = string.Empty;

    /// <summary>媒体库名称。</summary>
    public string LibraryName { get; set; } = "在线媒体";

    /// <summary>
    /// 落地的一级分类（文件夹名，类似影视仓主界面）。顺序即显示顺序。
    /// 条目会按「自带分类名」就近归到这些大分类下；「近期热播」收录聚合分类和其余无法归类的条目。
    /// </summary>
    public List<string> CategoryFolders { get; set; } = new()
    {
        "近期热播",
        "内地",
        "剧集",
        "综艺",
        "动漫",
        "电影"
    };

    /// <summary>媒体文件落地目录，留空则用 %ProgramData%\TerryMediaHub\OnlineLibrary。</summary>
    public string LibraryRoot { get; set; } = string.Empty;

    /// <summary>是否自动在 Jellyfin 中创建/维护媒体库。</summary>
    public bool AutoCreateLibrary { get; set; } = true;

    /// <summary>每个分类最多抓取的页数（每页约 20 条）。</summary>
    public int MaxPagesPerCategory { get; set; } = 3;

    /// <summary>每个站点最多同步的影片数。</summary>
    public int MaxItemsPerSite { get; set; } = 800;

    /// <summary>电影分类保留的最高排序条目数；其余条目移入备份目录。</summary>
    public int MaxMovieTitles { get; set; } = 400;

    /// <summary>单站点最多同步的分类数。</summary>
    public int MaxCategoriesPerSite { get; set; } = 8;

    /// <summary>自动刷新间隔（小时），0 表示只手动刷新。</summary>
    public int RefreshIntervalHours { get; set; } = 12;

    /// <summary>单个链接健康检查超时（毫秒）。</summary>
    public int HealthTimeoutMs { get; set; } = 8000;

    /// <summary>健康检查并发数。</summary>
    public int HealthConcurrency { get; set; } = 8;

    /// <summary>单个站点的抓取超时（秒），超时跳过该站点，避免一个坏源拖住整轮同步。</summary>
    public int SiteTimeoutSeconds { get; set; } = 90;

    /// <summary>连续失败多少次后判定链接失效。</summary>
    public int FailuresBeforeBlock { get; set; } = 2;

    /// <summary>是否通过插件代理播放（解决 Referer/UA 防盗链、m3u8 分片改写）。</summary>
    public bool ProxyStreams { get; set; } = true;

    /// <summary>请求源站使用的 User-Agent。</summary>
    public string UserAgent { get; set; } =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/122.0.0.0 Safari/537.36";

    /// <summary>写进 .strm 的服务地址，留空自动探测（推荐填内网 IP，例如 http://192.168.1.10:8096）。</summary>
    public string PublicBaseUrl { get; set; } = string.Empty;

    /// <summary>抓取源时是否使用系统代理（默认关闭，国内源直连更稳）。</summary>
    public bool UseSystemProxy { get; set; }

    /// <summary>不在媒体库里插入联网元数据（TMDb 等），避免国内网络下扫描卡住。</summary>
    public bool DisableInternetMetadata { get; set; } = true;

    /// <summary>是否在媒体库同步时探测播放地址（关闭会快很多，但无效源无法过滤）。</summary>
    public bool VerifyPlayUrls { get; set; } = true;

    /// <summary>旧版抽样设置，保留以兼容现有配置；启用链接校验时现会逐条测速择优。</summary>
    public int FirstRunProbeSample { get; set; } = 10;

    /// <summary>增量同步：已经入库的条目不再重复抓详情、试播（追求“配置好马上能看”就开着）。</summary>
    public bool IncrementalSync { get; set; } = true;

    /// <summary>库是否已经完整铺过一次（插件维护；铺满后恢复正常全量校验）。</summary>
    public bool LibraryPrimed { get; set; }

    /// <summary>仅同步最近 N 天更新的内容，0 表示不过滤。</summary>
    public int RecentDays { get; set; } = 0;

    /// <summary>站点连续失败次数（插件维护，用于自动拉黑）。</summary>
    public List<SiteFailEntry> SiteFailCounts { get; set; } = new();

    /// <summary>连续失败多少次后自动拉黑站点。</summary>
    public int SiteFailuresToBlock { get; set; } = 2;

    /// <summary>上次同步时间（UTC）。</summary>
    public DateTime? LastSyncUtc { get; set; }

    /// <summary>上次同步摘要。</summary>
    public string LastSyncSummary { get; set; } = string.Empty;

    /// <summary>上次媒体库重建结束时间（UTC）。</summary>
    public DateTime? LastLibraryRebuildUtc { get; set; }

    /// <summary>上次媒体库重建结果。</summary>
    public string LastLibraryRebuildSummary { get; set; } = string.Empty;

    /// <summary>流代理签名密钥（自动生成，勿手改）。</summary>
    public string StreamSecret { get; set; } = string.Empty;

    /// <summary>上次从浏览器请求里探测到的服务地址（自动写入）。</summary>
    public string DetectedBaseUrl { get; set; } = string.Empty;
}
