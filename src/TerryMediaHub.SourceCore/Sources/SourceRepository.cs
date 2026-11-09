using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.TerryMediaHub.Configuration;
using Jellyfin.Plugin.TerryMediaHub.Model;
using Jellyfin.Plugin.TerryMediaHub.Util;

namespace Jellyfin.Plugin.TerryMediaHub.Sources;

/// <summary>一个可用 / 不可用的站点条目。</summary>
public sealed class SiteEntry
{
    public string Key { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public string SourceName { get; set; } = string.Empty;

    public TvBoxSite Site { get; set; } = new();

    public ISiteResolver? Resolver { get; set; }

    public ResolverKind Kind { get; set; }

    public string? UnsupportedReason { get; set; }

    public bool Supported => Resolver is not null;
}

/// <summary>根据 api 形态判定解析器类型。</summary>
public static class ResolverFactory
{
    public static (ISiteResolver? Resolver, ResolverKind Kind, string? Reason) Resolve(TvBoxSite site)
    {
        var api = site.Api ?? string.Empty;
        var lower = api.ToLowerInvariant();

        if (lower.StartsWith("csp_", StringComparison.Ordinal))
        {
            // csp_ 站点交给内置 JVM 运行时执行（见 Runtime/SpiderHost）。
            var csp = new CspResolver();
            return (csp, ResolverKind.JarSpider, null);
        }

        if (lower.EndsWith(".js", StringComparison.Ordinal) || lower.Contains("/js/", StringComparison.Ordinal))
        {
            return (new JavaScriptResolver(), ResolverKind.JavaScript, null);
        }

        if (lower.EndsWith(".py", StringComparison.Ordinal) || lower.Contains("/py/", StringComparison.Ordinal))
        {
            return (new PythonResolver(), ResolverKind.Python, null);
        }

        // type=4 是“外部自定义接口”，只有长得像苹果CMS 的才当点播源处理，
        // 否则会把赛事/直播之类的数据当影片灌进媒体库。
        if (site.Type == 4
            && !lower.Contains("api.php", StringComparison.Ordinal)
            && !lower.Contains("provide/vod", StringComparison.Ordinal)
            && !lower.Contains("seaxml", StringComparison.Ordinal))
        {
            return (null, ResolverKind.Unknown, "type=4 自定义接口，非苹果CMS，暂不解析");
        }

        if (lower.StartsWith("http", StringComparison.Ordinal))
        {
            var cms = new CmsResolver();
            return cms.CanHandle(site)
                ? (cms, ResolverKind.Cms, null)
                : (null, ResolverKind.Unknown, "无法识别的接口形态");
        }

        return (null, ResolverKind.Unknown, string.IsNullOrWhiteSpace(api) ? "api 为空" : "无法识别的接口形态: " + api);
    }
}

/// <summary>拉取并合并所有源配置。</summary>
public sealed class SourceRepository
{
    private static readonly Lazy<SourceRepository> Lazy = new(() => new SourceRepository());

    private readonly object _lock = new();
    private List<SiteEntry> _sites = new();
    private string _error = string.Empty;
    private string _spiderJarUrl = string.Empty;

    public static SourceRepository Instance => Lazy.Value;

    public IReadOnlyList<SiteEntry> Sites
    {
        get
        {
            lock (_lock)
            {
                return _sites.ToList();
            }
        }
    }

    /// <summary>源配置里声明的 spider jar 地址（csp 站点的 Java 运行时用）。</summary>
    public string SpiderJarUrl
    {
        get
        {
            lock (_lock)
            {
                return _spiderJarUrl;
            }
        }
    }

    public string LastError
    {
        get
        {
            lock (_lock)
            {
                return _error;
            }
        }
    }

    public async Task<int> LoadAsync(PluginConfiguration cfg, CancellationToken ct)
    {
        var merged = new List<SiteEntry>();
        var errors = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var src in cfg.Sources.Where(s => s.Enabled && !string.IsNullOrWhiteSpace(s.Url)))
        {
            try
            {
                var configUrl = src.Url;
                var raw = await Http.GetStringAsync(src.Url, cfg.UserAgent, null, ct).ConfigureAwait(false);
                var node = JsonNode.Parse(raw);
                if (node is null)
                {
                    errors.Add($"{src.Name}: 空配置");
                    continue;
                }

                // 有些配置外面套了一层 {"url": "..."} 指向真正配置
                if (node is JsonObject obj && obj.ContainsKey("url") && obj["sites"] is null)
                {
                    var inner = obj["url"]?.GetValue<string>();
                    if (!string.IsNullOrWhiteSpace(inner))
                    {
                        var raw2 = await Http.GetStringAsync(inner!, cfg.UserAgent, null, ct).ConfigureAwait(false);
                        node = JsonNode.Parse(raw2);
                        configUrl = inner!;
                    }
                }

                var config = node!.Deserialize<TvBoxConfig>();
                if (config?.Sites is null)
                {
                    errors.Add($"{src.Name}: 不是有效的 TVBox 配置");
                    continue;
                }

                if (string.IsNullOrWhiteSpace(_spiderJarUrl) && !string.IsNullOrWhiteSpace(config.Spider))
                {
                    _spiderJarUrl = config.Spider!;
                }

                foreach (var site in config.Sites)
                {
                    if (site.Api.Contains("drpy2", StringComparison.OrdinalIgnoreCase)
                        && site.Ext is { ValueKind: JsonValueKind.String } extValue)
                    {
                        var extPath = extValue.GetString();
                        if (!string.IsNullOrWhiteSpace(extPath)
                            && (extPath.StartsWith("./", StringComparison.Ordinal)
                                || extPath.StartsWith("../", StringComparison.Ordinal)
                                || extPath.StartsWith("/", StringComparison.Ordinal))
                            && Uri.TryCreate(configUrl, UriKind.Absolute, out var extBase))
                            site.Ext = JsonSerializer.SerializeToElement(new Uri(extBase, extPath).AbsoluteUri);
                    }
                    if ((site.Api.StartsWith("./", StringComparison.Ordinal)
                         || site.Api.StartsWith("../", StringComparison.Ordinal)
                         || site.Api.StartsWith("/", StringComparison.Ordinal))
                        && Uri.TryCreate(configUrl, UriKind.Absolute, out var baseUri))
                    {
                        site.Api = new Uri(baseUri, site.Api).AbsoluteUri;
                    }
                    if (string.IsNullOrWhiteSpace(site.Key))
                    {
                        site.Key = site.Name;
                    }

                    if (!seen.Add(site.Key + "|" + site.Api))
                    {
                        continue;
                    }

                    var (resolver, kind, reason) = ResolverFactory.Resolve(site);
                    merged.Add(new SiteEntry
                    {
                        Key = site.Key,
                        Name = site.Name,
                        SourceName = string.IsNullOrWhiteSpace(src.Name) ? src.Url : src.Name,
                        Site = site,
                        Resolver = resolver,
                        Kind = kind,
                        UnsupportedReason = reason
                    });
                }
            }
            catch (Exception ex)
            {
                errors.Add($"{src.Name}: {ex.Message}");
            }
        }

        lock (_lock)
        {
            _sites = merged;
            _error = string.Join("; ", errors);
        }

        return merged.Count;
    }

    /// <summary>站点是否启用（黑名单 > 白名单 > 默认）。</summary>
    public static bool IsEnabled(SiteEntry entry, PluginConfiguration cfg)
    {
        if (cfg.BlockedSiteKeys.Contains(entry.Key, StringComparer.OrdinalIgnoreCase)
            || cfg.AutoBlockedSiteKeys.Contains(entry.Key, StringComparer.OrdinalIgnoreCase))
        {
            return false;
        }

        if (cfg.EnabledSiteKeys.Count == 0)
        {
            return true;
        }

        return cfg.EnabledSiteKeys.Contains(entry.Key, StringComparer.OrdinalIgnoreCase);
    }
}
