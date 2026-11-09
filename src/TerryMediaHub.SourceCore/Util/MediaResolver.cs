using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.TerryMediaHub.Util;

/// <summary>
/// 从“分享页 / 播放页”HTML 里二次解析出真正的流地址。
/// 影视仓的很多站点 (例如量子资源的 /share/xxx) 只给一个网页，m3u8 藏在页面里。
/// </summary>
public static class MediaResolver
{
    private static readonly Regex Unescape = new("\\\\/", RegexOptions.Compiled);

    private static readonly Regex StreamUrl = new(
        @"https?://[^\s""'<>\\)]+?\.(?:m3u8|mp4|flv|mkv|ts)(?:\?[^\s""'<>\\)]*)?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex JsonM3u8 = new(
        @"""url""\s*:\s*""(https?://[^""]+?\.m3u8[^""]*)""",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // 站内播放页常把地址写成相对路径，例如 var main = "/20261007/xxx/index.m3u8?sign=..."
    private static readonly Regex QuotedRelative = new(
        @"[""']((?:\.{0,2}/|/)[^""']*?\.(?:m3u8|mp4))(?:\?[^""']*)?[""']",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public static bool LooksLikeHtml(string? contentType) =>
        !string.IsNullOrEmpty(contentType)
        && (contentType.Contains("text/html", StringComparison.OrdinalIgnoreCase)
            || contentType.Contains("text/plain", StringComparison.OrdinalIgnoreCase)
            || contentType.Contains("javascript", StringComparison.OrdinalIgnoreCase));

    /// <summary>从 HTML/JSON 文本里找出第一个可用的流地址。</summary>
    public static string? TryExtractStreamUrl(string text, string baseUrl)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var normalized = Unescape.Replace(text, "/");

        var candidates = new List<string>();

        foreach (Match m in JsonM3u8.Matches(normalized))
        {
            candidates.Add(m.Groups[1].Value);
        }

        foreach (Match m in StreamUrl.Matches(normalized))
        {
            candidates.Add(m.Value);
        }

        foreach (Match m in QuotedRelative.Matches(normalized))
        {
            // 连同一个引号内的查询串一起取
            var start = m.Index + 1;
            var end = start + m.Groups[1].Value.Length;
            var tail = normalized.Substring(end);
            var q = string.Empty;
            if (tail.StartsWith("?", StringComparison.Ordinal))
            {
                var qEnd = tail.IndexOfAny(new[] { '"', '\'' });
                if (qEnd > 0)
                {
                    q = tail[..qEnd];
                }
            }

            candidates.Add(m.Groups[1].Value + q);
        }

        foreach (var raw in candidates)
        {
            var url = raw.TrimEnd('.', ',', ';', '"', '\'');
            if (url.Contains("ad.", StringComparison.OrdinalIgnoreCase)
                || url.Contains("/ads/", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            // 优先 m3u8
            if (url.Contains(".m3u8", StringComparison.OrdinalIgnoreCase))
            {
                return Absolutize(url, baseUrl);
            }
        }

        foreach (Match m in QuotedRelative.Matches(normalized))
        {
            // 连同一个引号内的查询串一起取
            var start = m.Index + 1;
            var end = start + m.Groups[1].Value.Length;
            var tail = normalized.Substring(end);
            var q = string.Empty;
            if (tail.StartsWith("?", StringComparison.Ordinal))
            {
                var qEnd = tail.IndexOfAny(new[] { '"', '\'' });
                if (qEnd > 0)
                {
                    q = tail[..qEnd];
                }
            }

            candidates.Add(m.Groups[1].Value + q);
        }

        foreach (var raw in candidates)
        {
            var url = raw.TrimEnd('.', ',', ';', '"', '\'');
            if (url.Contains(".mp4", StringComparison.OrdinalIgnoreCase)
                || url.Contains(".flv", StringComparison.OrdinalIgnoreCase))
            {
                return Absolutize(url, baseUrl);
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
            return new Uri(new Uri(baseUrl), url).ToString();
        }
        catch
        {
            return url;
        }
    }
}
