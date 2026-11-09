using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.TerryMediaHub.Configuration;
using Jellyfin.Plugin.TerryMediaHub.Runtime;
using System.Text.Json.Nodes;

namespace Jellyfin.Plugin.TerryMediaHub.Util;

/// <summary>解析出来的可播放地址。</summary>
public sealed record ResolvedPlay(string Url, string? Referer, string? UserAgent, string How);

/// <summary>
/// TVBox 的“播放参数”解析。
///
/// csp_ 爬虫返回的播放项不一定是直链，通常是两种形态之一：
///   1) 直接是 m3u8 / mp4 地址；
///   2) 一段 URL 编码的 JSON：{"p":["解析接口"],"r":"referer","u":"UA","url":"加密后的片源标识"}，
///      需要拿 url 去解 p 里的解析接口换出真实地址。
/// 这里把两种情况统一成真实地址，顺带带上 Referer / UA。
/// </summary>
public static class ParseResolver
{
    private static readonly Regex DirectUrl = new(
        @"https?://[^\s""'<>\\]+?\.(?:m3u8|mp4|flv|mkv)(?:\?[^\s""'<>\\]*)?",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex AnyHttp = new(
        @"https?://[^\s""'<>\\]+",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static async Task<ResolvedPlay?> ResolveAsync(
        string raw,
        PluginConfiguration cfg,
        string? referer,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var s = raw.Trim();

        if (s.StartsWith("json:", StringComparison.OrdinalIgnoreCase))
        {
            s = s[5..];
        }

        // 编码过的 JSON（catvod 常见做法）
        if (s.Contains("%7B", StringComparison.OrdinalIgnoreCase) || s.Contains("%22", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                s = Uri.UnescapeDataString(s);
            }
            catch
            {
                // 保持原样
            }
        }

        if (s.StartsWith("http", StringComparison.OrdinalIgnoreCase))
        {
            return new ResolvedPlay(s, referer ?? Http.OriginOf(s), null, "direct");
        }

        if (s.StartsWith('{'))
        {
            try
            {
                using var doc = JsonDocument.Parse(s);
                var root = doc.RootElement;
                if (root.TryGetProperty("terryPy", out var python))
                {
                    var site = Str(python, "site") ?? string.Empty;
                    var api = Str(python, "api") ?? string.Empty;
                    var ext = Str(python, "ext") ?? string.Empty;
                    var flag = Str(python, "flag") ?? string.Empty;
                    var id = Str(python, "id") ?? string.Empty;
                    if (api.Length == 0 || id.Length == 0) return null;
                    var args = new JsonObject
                    {
                        ["flag"] = flag,
                        ["id"] = id,
                        ["vipFlags"] = new JsonArray()
                    };
                    var result = await PythonHost.Instance.CallAsync(site, api, ext, "play", args, ct)
                        .ConfigureAwait(false);
                    using var playDoc = JsonDocument.Parse(result);
                    var play = playDoc.RootElement;
                    var playUrl = Str(play, "url");
                    if (string.IsNullOrWhiteSpace(playUrl)) return null;
                    var headers = play.TryGetProperty("header", out var h) && h.ValueKind == JsonValueKind.Object
                        ? h : default;
                    var playReferer = headers.ValueKind == JsonValueKind.Object
                        ? Str(headers, "Referer") ?? Str(headers, "referer") : null;
                    var playUserAgent = headers.ValueKind == JsonValueKind.Object
                        ? Str(headers, "User-Agent") ?? Str(headers, "user-agent") : null;
                    return new ResolvedPlay(playUrl, playReferer ?? referer, playUserAgent, "python-player");
                }
                if (root.TryGetProperty("terryJs", out var javascript))
                {
                    var site = Str(javascript, "site") ?? string.Empty;
                    var api = Str(javascript, "api") ?? string.Empty;
                    var ext = Str(javascript, "ext") ?? string.Empty;
                    var flag = Str(javascript, "flag") ?? string.Empty;
                    var id = Str(javascript, "id") ?? string.Empty;
                    var type = javascript.TryGetProperty("type", out var typeValue)
                        && typeValue.ValueKind == JsonValueKind.Number ? typeValue.GetInt32() : 3;
                    if (api.Length == 0 || id.Length == 0) return null;
                    var args = new JsonObject
                    {
                        ["flag"] = flag,
                        ["id"] = id,
                        ["vipFlags"] = new JsonArray()
                    };
                    var result = await JavaScriptHost.Instance.CallAsync(site, api, ext, type, "play", args, ct)
                        .ConfigureAwait(false);
                    using var playDoc = JsonDocument.Parse(result);
                    var play = playDoc.RootElement;
                    var playUrl = Str(play, "url");
                    if (string.IsNullOrWhiteSpace(playUrl)) return null;
                    var headers = play.TryGetProperty("header", out var h) && h.ValueKind == JsonValueKind.Object
                        ? h : default;
                    var playReferer = headers.ValueKind == JsonValueKind.Object
                        ? Str(headers, "Referer") ?? Str(headers, "referer") : null;
                    var playUserAgent = headers.ValueKind == JsonValueKind.Object
                        ? Str(headers, "User-Agent") ?? Str(headers, "user-agent") : null;
                    return new ResolvedPlay(playUrl, playReferer ?? referer, playUserAgent, "javascript-player");
                }
                var inner = Str(root, "url");
                var ref2 = Str(root, "r");
                var ua = Str(root, "u");
                var apis = new List<string>();
                if (root.TryGetProperty("p", out var p) && p.ValueKind == JsonValueKind.Array)
                {
                    apis.AddRange(p.EnumerateArray()
                        .Where(x => x.ValueKind == JsonValueKind.String)
                        .Select(x => x.GetString()!)
                        .Where(x => !string.IsNullOrWhiteSpace(x)));
                }

                if (!string.IsNullOrWhiteSpace(inner) && inner!.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                    && DirectUrl.IsMatch(inner))
                {
                    return new ResolvedPlay(inner, ref2 ?? referer, ua, "payload-url");
                }

                var probe = inner ?? s;
                foreach (var api in apis.Take(3))
                {
                    var hit = await CallParseApiAsync(api, probe, ref2 ?? referer, ua ?? cfg.UserAgent, cfg, ct)
                        .ConfigureAwait(false);
                    if (hit is not null)
                    {
                        return new ResolvedPlay(hit, ref2 ?? referer, ua, "parse:" + HostOf(api));
                    }
                }
            }
            catch (JsonException)
            {
                // 不是 JSON，继续走兜底
            }
        }

        var m = DirectUrl.Match(s);
        if (m.Success)
        {
            return new ResolvedPlay(m.Value, referer, null, "regex");
        }

        return null;
    }

    private static async Task<string?> CallParseApiAsync(
        string api,
        string payload,
        string? referer,
        string? userAgent,
        PluginConfiguration cfg,
        CancellationToken ct)
    {
        var url = api.Contains("{url}", StringComparison.OrdinalIgnoreCase)
            ? api.Replace("{url}", Uri.EscapeDataString(payload), StringComparison.OrdinalIgnoreCase)
            : api + Uri.EscapeDataString(payload);

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(Math.Max(4000, cfg.HealthTimeoutMs));

            using var req = Http.BuildRequest(url, userAgent ?? cfg.UserAgent, referer);
            using var resp = await Http.Client.SendAsync(req, HttpCompletionOption.ResponseContentRead, timeout.Token)
                .ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                return null;
            }

            var body = await resp.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
            return Extract(body);
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>从解析接口的响应里抠出真实地址（JSON 或纯文本都试）。</summary>
    public static string? Extract(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return null;
        }

        var t = body.Trim();
        if (t.StartsWith('{') || t.StartsWith('['))
        {
            try
            {
                using var doc = JsonDocument.Parse(t);
                var hit = FindUrl(doc.RootElement, 0);
                if (!string.IsNullOrWhiteSpace(hit))
                {
                    return hit;
                }
            }
            catch (JsonException)
            {
                // 落到正则
            }
        }

        var m = DirectUrl.Match(t);
        if (m.Success)
        {
            return m.Value;
        }

        // 有些接口把地址放在 HTML/JS 里
        var m2 = AnyHttp.Match(t);
        return m2.Success && m2.Value.Contains("m3u8", StringComparison.OrdinalIgnoreCase) ? m2.Value : null;
    }

    private static string? FindUrl(JsonElement e, int depth)
    {
        if (depth > 4)
        {
            return null;
        }

        switch (e.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var name in new[] { "url", "m3u8", "playUrl", "play_url", "src", "data" })
                {
                    if (!e.TryGetProperty(name, out var v))
                    {
                        continue;
                    }

                    if (v.ValueKind == JsonValueKind.String)
                    {
                        var s = v.GetString();
                        if (!string.IsNullOrWhiteSpace(s) && s.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                        {
                            return s;
                        }
                    }
                    else
                    {
                        var nested = FindUrl(v, depth + 1);
                        if (nested is not null)
                        {
                            return nested;
                        }
                    }
                }

                foreach (var prop in e.EnumerateObject())
                {
                    var nested = FindUrl(prop.Value, depth + 1);
                    if (nested is not null)
                    {
                        return nested;
                    }
                }

                return null;

            case JsonValueKind.Array:
                foreach (var item in e.EnumerateArray())
                {
                    var nested = FindUrl(item, depth + 1);
                    if (nested is not null)
                    {
                        return nested;
                    }
                }

                return null;

            case JsonValueKind.String:
                var str = e.GetString();
                return !string.IsNullOrWhiteSpace(str) && str.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? str : null;

            default:
                return null;
        }
    }

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static string HostOf(string url)
    {
        try
        {
            return new Uri(url).Host;
        }
        catch
        {
            return "parse";
        }
    }
}
