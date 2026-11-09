using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.TerryMediaHub.Library;
using Jellyfin.Plugin.TerryMediaHub.Runtime;
using Jellyfin.Plugin.TerryMediaHub.Sources;
using Jellyfin.Plugin.TerryMediaHub.Util;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.TerryMediaHub.Api;

/// <summary>
/// 插件 REST 接口：流代理 + 状态/同步管理。
/// </summary>
[ApiController]
[Route("TerryMediaHub")]
public class TerryMediaHubController : ControllerBase
{
    private static readonly Regex UriAttr = new("URI=\"([^\"]+)\"", RegexOptions.Compiled);

    private readonly ILogger<TerryMediaHubController> _logger;
    private readonly ILoggerFactory _loggerFactory;
    private readonly LibraryRegistrar _registrar;
    private readonly RatingEnricher _ratingEnricher;

    public TerryMediaHubController(
        ILogger<TerryMediaHubController> logger,
        ILoggerFactory loggerFactory,
        LibraryRegistrar registrar,
        RatingEnricher ratingEnricher)
    {
        _logger = logger;
        _loggerFactory = loggerFactory;
        _registrar = registrar;
        _ratingEnricher = ratingEnricher;
    }

    private static Configuration.PluginConfiguration Cfg => Plugin.Instance!.Configuration;

    /// <summary>
    /// 媒体流代理：解决 Referer/UA 防盗链，并把 m3u8 里的分片地址改写成同样走代理。
    /// </summary>
    [HttpGet("Stream/{token}")]
    [AllowAnonymous]
    public async Task StreamAsync(string token, CancellationToken ct)
    {
        var cfg = Cfg;
        if (!Token.TryDecode(token, cfg.StreamSecret, out var url, out var referer, out var userAgent)
            || string.IsNullOrWhiteSpace(url))
        {
            Response.StatusCode = 403;
            return;
        }

        try
        {
            await PipeAsync(url, referer, userAgent, cfg, 0, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // 客户端断开
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "代理拉流失败 {Url}", url);
            if (!Response.HasStarted)
            {
                Response.StatusCode = 502;
            }
        }
    }

    /// <summary>真正搬流；遇到分享页会先解析出 m3u8 再继续。</summary>
    private async Task PipeAsync(string url, string? referer, string? userAgent,
        Configuration.PluginConfiguration cfg, int depth, CancellationToken ct)
    {
        // csp 站点给出的是 “播放参数”（可能是编码过的 JSON），先换成真实地址
        if (!url.StartsWith("http", StringComparison.OrdinalIgnoreCase))
        {
            var resolved = await LocalServiceClient.ResolveAsync(cfg, url, referer, ct).ConfigureAwait(false);
            if (resolved is null)
            {
                Response.StatusCode = 502;
                await Response.WriteAsync("无法解析播放参数", ct).ConfigureAwait(false);
                return;
            }

            url = resolved.Url;
            referer = resolved.Referer ?? referer;
            userAgent = resolved.UserAgent ?? userAgent;
        }

        using var req = Http.BuildRequest(url, userAgent ?? cfg.UserAgent, referer);
        var range = Request.Headers["Range"].ToString();
        if (!string.IsNullOrEmpty(range))
        {
            req.Headers.TryAddWithoutValidation("Range", range);
        }

        using var resp = await Http.Client
            .SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct)
            .ConfigureAwait(false);

        var mediaType = resp.Content.Headers.ContentType?.MediaType ?? string.Empty;
        var finalUrl = resp.RequestMessage?.RequestUri?.ToString() ?? url;
        var isPlaylist = mediaType.Contains("mpegurl", StringComparison.OrdinalIgnoreCase)
                         || finalUrl.Contains(".m3u8", StringComparison.OrdinalIgnoreCase);

        // 分享页 / 播放页：抠出真正的流地址再来一次
        if (!isPlaylist && depth < 2 && MediaResolver.LooksLikeHtml(mediaType) && resp.IsSuccessStatusCode)
        {
            var html = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            var inner = MediaResolver.TryExtractStreamUrl(html, finalUrl);
            if (!string.IsNullOrWhiteSpace(inner)
                && !string.Equals(inner, url, StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogDebug("分享页解析 {From} → {To}", url, inner);
                await PipeAsync(inner!, referer ?? Http.OriginOf(inner!), userAgent, cfg, depth + 1, ct)
                    .ConfigureAwait(false);
                return;
            }

            Response.StatusCode = 502;
            Response.ContentType = "text/plain; charset=utf-8";
            await Response.WriteAsync("无法从播放页解析视频地址", ct).ConfigureAwait(false);
            return;
        }

        Response.StatusCode = (int)resp.StatusCode;

        if (isPlaylist && resp.IsSuccessStatusCode)
        {
            var text = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (text.TrimStart('\uFEFF', ' ', '\r', '\n', '\t').StartsWith("#EXTM3U", StringComparison.Ordinal))
            {
                var rewritten = RewritePlaylist(text, finalUrl, referer, userAgent, cfg.StreamSecret);
                var bytes = Encoding.UTF8.GetBytes(rewritten);
                Response.ContentType = "application/vnd.apple.mpegurl";
                Response.ContentLength = bytes.Length;
                if (!HttpMethods.IsHead(Request.Method))
                {
                    await Response.Body.WriteAsync(bytes, ct).ConfigureAwait(false);
                }

                return;
            }

            Response.StatusCode = 502;
            await Response.WriteAsync("源站返回的不是 HLS 播放列表", ct).ConfigureAwait(false);
            return;
        }

        CopyHeader("Content-Type", resp.Content.Headers.ContentType?.ToString());
        CopyHeader("Content-Range", resp.Content.Headers.ContentRange?.ToString());
        CopyHeader("Accept-Ranges", resp.Headers.AcceptRanges.FirstOrDefault());
        CopyHeader("Last-Modified", resp.Content.Headers.LastModified?.ToString("R"));

        if (resp.Content.Headers.ContentLength is { } len)
        {
            Response.ContentLength = len;
        }

        if (!HttpMethods.IsHead(Request.Method))
        {
            await resp.Content.CopyToAsync(Response.Body, ct).ConfigureAwait(false);
        }
    }

    /// <summary>插件运行状态 + 站点清单。</summary>
    [HttpGet("Status")]
    [Authorize(Policy = "RequiresElevation")]
    public async Task<ActionResult<object>> Status(CancellationToken ct)
    {
        var cfg = Cfg;

        ServiceSources sources;
        try
        {
            sources = await LocalServiceClient.SourcesAsync(cfg, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "读取本机解析服务的源列表失败");
            sources = new ServiceSources { SourceError = ex.Message };
        }

        var sites = sources.Sites;

        return new
        {
            running = SyncState.Running,
            phase = SyncState.Phase,
            progress = SyncState.Progress,
            lastStartUtc = SyncState.LastStartUtc,
            lastEndUtc = SyncState.LastEndUtc,
            lastSummary = string.IsNullOrEmpty(SyncState.LastSummary) ? cfg.LastSyncSummary : SyncState.LastSummary,
            notes = SyncState.LastNotes,
            libraryRunning = LibraryRebuildState.Running,
            libraryLastStartUtc = LibraryRebuildState.LastStartUtc,
            libraryLastEndUtc = LibraryRebuildState.LastEndUtc ?? cfg.LastLibraryRebuildUtc,
            libraryLastSummary = string.IsNullOrEmpty(LibraryRebuildState.LastSummary)
                ? cfg.LastLibraryRebuildSummary : LibraryRebuildState.LastSummary,
            ratingEnrichment = _ratingEnricher.Status,
            sourceError = sources.SourceError,
            libraryRoot = LibraryBuilder.ResolveRoot(cfg),
            baseUrl = Address.Resolve(cfg, null),
            jarSpider = sources.JarSpider,
            sitesTotal = sites.Count,
            sitesSupported = sites.Count(s => s.Supported),
            sites
        };
    }

    /// <summary>启动一次同步（后台执行，前端轮询 Status）。</summary>
    [HttpPost("Sync")]
    [Authorize(Policy = "RequiresElevation")]
    public ActionResult<object> StartSync()
    {
        if (!SyncState.TryBegin("准备中"))
        {
            return new { started = false, message = "已有同步任务在运行" };
        }

        var cfg = Cfg;
        var requestBase = Request.Scheme + "://" + Request.Host + Request.PathBase;
        cfg.DetectedBaseUrl = requestBase;
        Plugin.Instance!.SaveConfiguration();

        var logger = _logger;

        _ = Task.Run(async () =>
        {
            try
            {
                var result = await LocalServiceClient.RunSyncAsync(cfg,
                    Address.Resolve(cfg, requestBase), CancellationToken.None).ConfigureAwait(false);

                cfg.LastSyncUtc = DateTime.UtcNow;
                cfg.LastSyncSummary = result.LastSummary ?? "同步完成";
                Plugin.Instance!.SaveConfiguration();

                SyncState.End(cfg.LastSyncSummary, result.Notes);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "同步失败");
                SyncState.End("同步失败: " + ex.Message, new[] { ex.ToString() });
            }
        });

        return new { started = true };
    }

    /// <summary>只根据已经同步到磁盘的媒体文件重建 Jellyfin 媒体库。</summary>
    [HttpPost("RebuildLibrary")]
    [Authorize(Policy = "RequiresElevation")]
    public ActionResult<object> RebuildLibrary()
    {
        if (!LibraryRebuildState.TryBegin())
        {
            return new { started = false, message = "媒体库正在重建" };
        }

        var cfg = Cfg;
        _ = Task.Run(async () =>
        {
            try
            {
                LibraryBuilder.PrepareFolders(cfg);
                HotSort.FillExisting(LibraryBuilder.ResolveRoot(cfg));
                if (!await _registrar.EnsureAsync(cfg).ConfigureAwait(false))
                {
                    throw new InvalidOperationException("媒体库创建或关联失败，请检查 Jellyfin 日志");
                }

                await _registrar.RefreshAsync(CancellationToken.None).ConfigureAwait(false);
                cfg.LastLibraryRebuildUtc = DateTime.UtcNow;
                cfg.LastLibraryRebuildSummary = "在线媒体重建完成";
                Plugin.Instance!.SaveConfiguration();
                LibraryRebuildState.End("在线媒体重建完成");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "重建媒体库失败");
                cfg.LastLibraryRebuildUtc = DateTime.UtcNow;
                cfg.LastLibraryRebuildSummary = "重建失败：" + ex.Message;
                Plugin.Instance!.SaveConfiguration();
                LibraryRebuildState.End(cfg.LastLibraryRebuildSummary);
            }
        });

        return new { started = true };
    }

    /// <summary>只更新磁盘上的豆瓣近期热播清单，媒体库扫描由重建按钮负责。</summary>
    [HttpPost("RefreshDoubanHot")]
    [Authorize(Policy = "RequiresElevation")]
    public async Task<ActionResult<object>> RefreshDoubanHot()
    {
        if (SyncState.Running || LibraryRebuildState.Running)
        {
            return new { updated = false, message = "请等待当前同步或媒体库重建完成" };
        }

        try
        {
            var count = await DoubanHotBuilder.RefreshAsync(LibraryBuilder.ResolveRoot(Cfg), Cfg, CancellationToken.None)
                .ConfigureAwait(false);
            return new { updated = true, count };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "更新豆瓣近期热播失败");
            return BadRequest(new { message = ex.Message });
        }
    }

    /// <summary>使用本机 MetaShark 的豆瓣客户端补齐缺失的评分和年份。</summary>
    [HttpPost("EnrichRatings")]
    [Authorize(Policy = "RequiresElevation")]
    public ActionResult<object> EnrichRatings([FromQuery] int limit = 0)
    {
        var started = _ratingEnricher.Start(Math.Clamp(limit, 0, 10000));
        return new { started, message = started ? "已开始查询缺失评分" : "评分补全正在进行" };
    }

    /// <summary>清除自动拉黑记录。</summary>
    [HttpPost("ResetAutoBlocked")]
    [Authorize(Policy = "RequiresElevation")]
    public ActionResult<object> ResetAutoBlocked()
    {
        var cfg = Cfg;
        var count = cfg.AutoBlockedSiteKeys.Count;
        cfg.AutoBlockedSiteKeys.Clear();
        cfg.SiteFailCounts.Clear();
        Plugin.Instance!.SaveConfiguration();
        return new { cleared = count };
    }

    /// <summary>探测一个播放地址是否有效（排查用）。</summary>
    [HttpGet("Probe")]
    [Authorize(Policy = "RequiresElevation")]
    public async Task<ActionResult<object>> ProbeAsync([FromQuery] string url, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return new { ok = false, reason = "地址为空" };
        }

        var r = await LocalServiceClient.ProbeAsync(Cfg, url, ct).ConfigureAwait(false);
        return new { ok = r.Ok, reason = r.Reason, contentType = r.ContentType,
            latencyMs = r.LatencyMs, height = r.ResolutionHeight,
            bandwidthBps = r.BandwidthBitsPerSecond, throughputBytesPerSecond = r.ThroughputBytesPerSecond };
    }

    /// <summary>测试源配置地址是否可用。</summary>
    [HttpGet("TestSource")]
    [Authorize(Policy = "RequiresElevation")]
    public async Task<ActionResult<object>> TestSourceAsync([FromQuery] string url, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return new { ok = false, message = "地址为空" };
        }

        try
        {
            var raw = await Http.GetStringAsync(url, Cfg.UserAgent, null, ct).ConfigureAwait(false);
            var config = System.Text.Json.JsonSerializer.Deserialize<Model.TvBoxConfig>(
                raw,
                new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            if (config?.Sites is null)
            {
                return new { ok = false, message = "不是有效的 TVBox 配置（缺少 sites）" };
            }

            var supported = config.Sites.Count(s => ResolverFactory.Resolve(s).Resolver is not null);
            return new
            {
                ok = true,
                sites = config.Sites.Count,
                supported,
                unsupported = config.Sites.Count - supported,
                lives = config.Lives.Count,
                bytes = raw.Length,
                message = $"共 {config.Sites.Count} 个站点，其中 {supported} 个可被服务端直接解析"
            };
        }
        catch (Exception ex)
        {
            return new { ok = false, message = ex.Message };
        }
    }

    private void CopyHeader(string name, string? value)
    {
        if (!string.IsNullOrEmpty(value))
        {
            Response.Headers[name] = value;
        }
    }

    private string RewritePlaylist(string text, string baseUrl, string? referer,
        string? userAgent, string secret)
    {
        var sb = new StringBuilder(text.Length + 512);
        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            if (line.Length == 0)
            {
                sb.Append('\n');
                continue;
            }

            if (line[0] == '#')
            {
                sb.Append(UriAttr.Replace(line, m =>
                {
                    var abs = LinkProbeAbsolute(m.Groups[1].Value, baseUrl);
                    return "URI=\"" + ProxyUrl(abs, referer, userAgent, secret) + "\"";
                })).Append('\n');
            }
            else
            {
                sb.Append(ProxyUrl(LinkProbeAbsolute(line, baseUrl), referer, userAgent, secret)).Append('\n');
            }
        }

        return sb.ToString();
    }

    private string ProxyUrl(string absoluteUrl, string? referer, string? userAgent, string secret)
    {
        var basePart = Request.Scheme + "://" + Request.Host + Request.PathBase;
        return basePart + "/TerryMediaHub/Stream/" + Token.Encode(absoluteUrl,
            referer ?? Http.OriginOf(absoluteUrl), secret, userAgent)
            + Token.MediaExtension(absoluteUrl);
    }

    private static string LinkProbeAbsolute(string url, string baseUrl)
    {
        if (url.StartsWith("http", StringComparison.OrdinalIgnoreCase))
        {
            return url;
        }

        try
        {
            return new Uri(new Uri(baseUrl), url).ToString();
        }
        catch
        {
            return url;
        }
    }
}
