using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.TerryMediaHub.Configuration;

namespace Jellyfin.Plugin.TerryMediaHub.Health;

/// <summary>一次探测结果。</summary>
public sealed class ProbeResult
{
    public bool Ok { get; set; }

    public string? Reason { get; set; }

    public string? ContentType { get; set; }

    public long LatencyMs { get; set; }

    public int ResolutionHeight { get; set; }

    public long BandwidthBitsPerSecond { get; set; }

    public long ThroughputBytesPerSecond { get; set; }

    public static ProbeResult Fail(string reason, long ms = 0) => new() { Ok = false, Reason = reason, LatencyMs = ms };
}

/// <summary>
/// 播放地址有效性探测：不只判断 HTTP 200，还判断内容类型、m3u8 合法性、首片可下。
/// </summary>
public sealed class LinkProbe
{
    private static readonly Regex Resolution = new(@"RESOLUTION=\d+x(\d+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex Bandwidth = new(@"(?:^|[:,])BANDWIDTH=(\d+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private readonly PluginConfiguration _cfg;
    private readonly ConcurrentDictionary<string, (DateTime At, ProbeResult Result)> _cache = new(StringComparer.Ordinal);
    private readonly TimeSpan _ttl;

    public LinkProbe(PluginConfiguration cfg, TimeSpan? ttl = null)
    {
        _cfg = cfg;
        _ttl = ttl ?? TimeSpan.FromHours(6);
    }

    public async Task<ProbeResult> CheckAsync(string url, CancellationToken ct)
    {
        var originalUrl = url;
        string? userAgent = null;
        string? referer = null;
        if (_cache.TryGetValue(url, out var hit) && DateTime.UtcNow - hit.At < _ttl)
        {
            return hit.Result;
        }

        // csp 站点给的是“播放参数”，先换成真实地址再判断
        if (!url.StartsWith("http", StringComparison.OrdinalIgnoreCase))
        {
            var resolved = await Util.ParseResolver.ResolveAsync(url, _cfg, null, ct).ConfigureAwait(false);
            if (resolved is null)
            {
                var bad = ProbeResult.Fail("播放参数无法解析成有效地址");
                _cache[originalUrl] = (DateTime.UtcNow, bad);
                return bad;
            }

            url = resolved.Url;
            userAgent = resolved.UserAgent;
            referer = resolved.Referer;
        }

        var result = await CheckCoreAsync(url, ct, 0, userAgent, referer).ConfigureAwait(false);
        _cache[originalUrl] = (DateTime.UtcNow, result);
        return result;
    }

    private async Task<ProbeResult> CheckCoreAsync(string url, CancellationToken ct, int depth = 0,
        string? userAgent = null, string? referer = null)
    {
        var sw = Stopwatch.StartNew();
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(Math.Max(2000, _cfg.HealthTimeoutMs));

        try
        {
            var isM3u8 = url.Contains(".m3u8", StringComparison.OrdinalIgnoreCase);
            using var req = Util.Http.BuildRequest(url, userAgent ?? _cfg.UserAgent,
                referer ?? Util.Http.OriginOf(url));
            req.Headers.TryAddWithoutValidation("Range", isM3u8 ? "bytes=0-65535" : "bytes=0-8191");

            using var resp = await Util.Http.Client
                .SendAsync(req, HttpCompletionOption.ResponseHeadersRead, timeoutCts.Token)
                .ConfigureAwait(false);
            var headersMs = sw.ElapsedMilliseconds;

            var code = (int)resp.StatusCode;
            var ct2 = resp.Content.Headers.ContentType?.MediaType?.ToLowerInvariant() ?? string.Empty;

            if (code is 401 or 403)
            {
                return ProbeResult.Fail($"HTTP {code}（防盗链/需要鉴权）", sw.ElapsedMilliseconds);
            }

            if (code >= 400)
            {
                return ProbeResult.Fail($"HTTP {code}", sw.ElapsedMilliseconds);
            }

            if (Util.MediaResolver.LooksLikeHtml(ct2) && !isM3u8)
            {
                if (depth == 0)
                {
                    // 分享页 / 播放页：尝试从页面里抠出真正的 m3u8
                    var html = await ReadHeadAsync(resp, 262144, timeoutCts.Token).ConfigureAwait(false);
                    var inner = Util.MediaResolver.TryExtractStreamUrl(html, url);
                    if (!string.IsNullOrWhiteSpace(inner)
                        && !string.Equals(inner, url, StringComparison.OrdinalIgnoreCase))
                    {
                        var innerResult = await CheckCoreAsync(inner!, ct, depth + 1, userAgent, referer)
                            .ConfigureAwait(false);
                        if (innerResult.Ok)
                        {
                            innerResult.ContentType = "页面解析→" + (innerResult.ContentType ?? "stream");
                            innerResult.LatencyMs = sw.ElapsedMilliseconds;
                            return innerResult;
                        }

                        return ProbeResult.Fail("页面内流地址无效：" + innerResult.Reason, sw.ElapsedMilliseconds);
                    }
                }

                return ProbeResult.Fail("返回 HTML 页面而非视频流", sw.ElapsedMilliseconds);
            }

            if (isM3u8 || ct2.Contains("mpegurl", StringComparison.Ordinal))
            {
                var head = await ReadHeadAsync(resp, 65536, timeoutCts.Token).ConfigureAwait(false);
                if (!head.Contains("#EXTM3U", StringComparison.Ordinal))
                {
                    return ProbeResult.Fail("不是合法的 m3u8 播放列表", sw.ElapsedMilliseconds);
                }

                var variants = Variants(head, url);
                foreach (var variant in variants.Take(3))
                {
                    try
                    {
                        using var variantReq = Util.Http.BuildRequest(variant.Url, userAgent ?? _cfg.UserAgent,
                            referer ?? Util.Http.OriginOf(variant.Url));
                        using var variantResp = await Util.Http.Client.SendAsync(variantReq,
                            HttpCompletionOption.ResponseHeadersRead, timeoutCts.Token).ConfigureAwait(false);
                        if (!variantResp.IsSuccessStatusCode) continue;
                        var variantHead = await ReadHeadAsync(variantResp, 65536, timeoutCts.Token).ConfigureAwait(false);
                        if (!variantHead.Contains("#EXTM3U", StringComparison.Ordinal)) continue;
                        var segment = FirstSegment(variantHead, variant.Url);
                        if (segment is null) continue;
                        var checkedSegment = await CheckSegmentAsync(segment, timeoutCts.Token,
                            userAgent, referer).ConfigureAwait(false);
                        if (!checkedSegment.Ok) continue;
                        return new ProbeResult { Ok = true, ContentType = "m3u8", LatencyMs = headersMs,
                            ResolutionHeight = variant.Height, BandwidthBitsPerSecond = variant.Bandwidth,
                            ThroughputBytesPerSecond = checkedSegment.BytesPerSecond };
                    }
                    catch (OperationCanceledException) when (!ct.IsCancellationRequested) { break; }
                    catch (HttpRequestException) { }
                }
                if (variants.Count > 0)
                    return ProbeResult.Fail("m3u8 变体及首个分片不可下载", sw.ElapsedMilliseconds);

                // 校验首个分片并测量实际读取速度。
                var firstSeg = FirstSegment(head, url);
                if (firstSeg is null)
                    return ProbeResult.Fail("m3u8 未找到媒体分片", sw.ElapsedMilliseconds);
                var seg = await CheckSegmentAsync(firstSeg, timeoutCts.Token, userAgent, referer)
                    .ConfigureAwait(false);
                if (!seg.Ok)
                {
                    return ProbeResult.Fail("m3u8 存在但首个分片无法下载", sw.ElapsedMilliseconds);
                }

                return new ProbeResult { Ok = true, ContentType = "m3u8", LatencyMs = headersMs,
                    ThroughputBytesPerSecond = seg.BytesPerSecond };
            }

            var okType = ct2.StartsWith("video/", StringComparison.Ordinal)
                         || ct2.Contains("octet-stream", StringComparison.Ordinal)
                         || ct2.Contains("mp2t", StringComparison.Ordinal)
                         || ct2.Contains("mp4", StringComparison.Ordinal)
                         || ct2.Contains("flash", StringComparison.Ordinal);
            if (!okType && code != 206)
            {
                return ProbeResult.Fail($"内容类型不支持: {ct2}", sw.ElapsedMilliseconds);
            }

            // 读取少量视频数据，测量吞吐速度，不下载整个文件。
            using var stream = await resp.Content.ReadAsStreamAsync(timeoutCts.Token).ConfigureAwait(false);
            var (bytes, bytesPerSecond) = await MeasureAsync(stream, timeoutCts.Token).ConfigureAwait(false);
            if (bytes <= 0)
            {
                return ProbeResult.Fail("无数据返回", sw.ElapsedMilliseconds);
            }

            return new ProbeResult { Ok = true, ContentType = ct2, LatencyMs = headersMs,
                ThroughputBytesPerSecond = bytesPerSecond };
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return ProbeResult.Fail("超时", sw.ElapsedMilliseconds);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return ProbeResult.Fail(ex.Message.Length > 80 ? ex.Message[..80] : ex.Message, sw.ElapsedMilliseconds);
        }
    }

    private async Task<(bool Ok, long BytesPerSecond)> CheckSegmentAsync(string segUrl, CancellationToken ct,
        string? userAgent, string? referer)
    {
        try
        {
            using var req = Util.Http.BuildRequest(segUrl, userAgent ?? _cfg.UserAgent,
                referer ?? Util.Http.OriginOf(segUrl));
            req.Headers.TryAddWithoutValidation("Range", "bytes=0-131071");
            using var resp = await Util.Http.Client
                .SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct)
                .ConfigureAwait(false);
            if ((int)resp.StatusCode >= 400)
            {
                return (false, 0);
            }

            // 有些站会用图片/HTML 冒充分片（防盗链或已失效）
            var segType = resp.Content.Headers.ContentType?.MediaType?.ToLowerInvariant() ?? string.Empty;
            if (segType.StartsWith("image/", StringComparison.Ordinal)
                || Util.MediaResolver.LooksLikeHtml(segType)
                || segType.Contains("json", StringComparison.Ordinal))
            {
                return (false, 0);
            }

            using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            var buffer = new byte[256];
            var watch = Stopwatch.StartNew();
            var read = await stream.ReadAsync(buffer, ct).ConfigureAwait(false);
            if (read <= 0)
            {
                return (false, 0);
            }

            // 再按文件头兜底判一次（服务器可能不返回正确 Content-Type）
            if (read >= 8
                && ((buffer[0] == 0x89 && buffer[1] == 0x50 && buffer[2] == 0x4E && buffer[3] == 0x47)
                    || (buffer[0] == 0xFF && buffer[1] == 0xD8)
                    || (buffer[0] == 0x47 && buffer[1] == 0x49 && buffer[2] == 0x46)))
            {
                return (false, 0);
            }

            var (extra, _) = await MeasureAsync(stream, ct, watch).ConfigureAwait(false);
            var speed = (long)((read + extra) * 1000.0 / Math.Max(1, watch.ElapsedMilliseconds));
            return (true, speed);
        }
        catch
        {
            return (false, 0);
        }
    }

    private static async Task<(long Bytes, long BytesPerSecond)> MeasureAsync(
        System.IO.Stream stream, CancellationToken ct, Stopwatch? watch = null)
    {
        watch ??= Stopwatch.StartNew();
        var buffer = new byte[16384];
        long total = 0;
        while (total < 131072)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, 131072 - total)), ct)
                .ConfigureAwait(false);
            if (read == 0) break;
            total += read;
        }

        return (total, total * 1000 / Math.Max(1, watch.ElapsedMilliseconds));
    }

    private static List<(string Url, int Height, long Bandwidth)> Variants(string playlist, string baseUrl)
    {
        var lines = playlist.Split('\n');
        var variants = new List<(string Url, int Height, long Bandwidth)>();
        for (var i = 0; i < lines.Length - 1; i++)
        {
            var line = lines[i].Trim();
            if (!line.StartsWith("#EXT-X-STREAM-INF:", StringComparison.OrdinalIgnoreCase)) continue;
            var next = lines.Skip(i + 1).Select(x => x.Trim())
                .FirstOrDefault(x => x.Length > 0 && !x.StartsWith('#'));
            if (next is null) continue;
            var height = int.TryParse(Resolution.Match(line).Groups[1].Value, out var h) ? h : 0;
            var bandwidth = long.TryParse(Bandwidth.Match(line).Groups[1].Value, out var b) ? b : 0;
            variants.Add((Absolutize(next, baseUrl), height, bandwidth));
        }

        return variants.OrderByDescending(x => x.Height).ThenByDescending(x => x.Bandwidth).ToList();
    }

    private static async Task<string> ReadHeadAsync(HttpResponseMessage resp, int max, CancellationToken ct)
    {
        using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        var buffer = new byte[max];
        var total = 0;
        while (total < max)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(total, max - total), ct).ConfigureAwait(false);
            if (read <= 0)
            {
                break;
            }

            total += read;
        }

        return System.Text.Encoding.UTF8.GetString(buffer, 0, total);
    }

    public static string? FirstSegment(string playlist, string baseUrl)
    {
        var mediaPlaylist = playlist.Contains("#EXTINF", StringComparison.OrdinalIgnoreCase)
            || playlist.Contains("#EXT-X-PART", StringComparison.OrdinalIgnoreCase);
        foreach (var raw in playlist.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            if (line.Contains(".ts", StringComparison.OrdinalIgnoreCase)
                || line.Contains(".m4s", StringComparison.OrdinalIgnoreCase)
                || line.Contains(".mp4", StringComparison.OrdinalIgnoreCase)
                || mediaPlaylist && !line.Contains(".m3u8", StringComparison.OrdinalIgnoreCase))
            {
                return Absolutize(line, baseUrl);
            }
        }

        return null;
    }

    public static string Absolutize(string url, string baseUrl)
    {
        if (url.StartsWith("http", StringComparison.OrdinalIgnoreCase))
        {
            return url;
        }

        try
        {
            var b = new Uri(baseUrl);
            return new Uri(b, url).ToString();
        }
        catch
        {
            return url;
        }
    }
}
