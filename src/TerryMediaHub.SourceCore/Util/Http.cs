using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.TerryMediaHub.Util;

/// <summary>共享 HTTP 客户端（自动解压、连接复用）。</summary>
public static class Http
{
    private static readonly HttpClient Direct = Build(false);

    private static readonly HttpClient Proxied = Build(true);

    /// <summary>是否使用系统代理（默认关闭；国内源直连更稳）。</summary>
    public static bool UseSystemProxy { get; set; }

    public static HttpClient Client => UseSystemProxy ? Proxied : Direct;

    private static HttpClient Build(bool useProxy)
    {
        var handler = new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 8,
            ServerCertificateCustomValidationCallback = (_, _, _, _) => true,
            UseCookies = false,
            UseProxy = useProxy
        };

        if (!useProxy)
        {
            handler.Proxy = null;
        }

        return new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(60) };
    }

    /// <summary>带 UA / Referer 的 GET，返回字符串。</summary>
    public static async Task<string> GetStringAsync(
        string url,
        string userAgent,
        string? referer,
        CancellationToken ct)
    {
        using var req = BuildRequest(url, userAgent, referer);
        using var resp = await Client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
    }

    /// <summary>带 UA / Referer 的 GET，返回字节（下载 jar / 图片用）。</summary>
    public static async Task<byte[]> GetBytesAsync(
        string url,
        string userAgent,
        string? referer,
        CancellationToken ct)
    {
        using var req = BuildRequest(url, userAgent, referer);
        using var resp = await Client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
    }

    public static HttpRequestMessage BuildRequest(string url, string userAgent, string? referer)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.TryAddWithoutValidation("User-Agent", userAgent);
        req.Headers.TryAddWithoutValidation("Accept", "*/*");
        req.Headers.TryAddWithoutValidation("Accept-Language", "zh-CN,zh;q=0.9,en;q=0.8");
        var r = string.IsNullOrWhiteSpace(referer) ? OriginOf(url) : referer;
        if (!string.IsNullOrWhiteSpace(r))
        {
            req.Headers.TryAddWithoutValidation("Referer", r.EndsWith("/", StringComparison.Ordinal) ? r : r + "/");
        }

        return req;
    }

    public static string? OriginOf(string url)
    {
        try
        {
            var u = new Uri(url);
            return u.GetLeftPart(UriPartial.Authority);
        }
        catch
        {
            return null;
        }
    }
}
