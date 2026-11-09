using System;
using System.Security.Cryptography;
using System.Text;

namespace Jellyfin.Plugin.TerryMediaHub.Util;

/// <summary>
/// 把播放地址编码进 .strm 里的 URL。无状态，重启不失效；带 HMAC 签名，防止被当成开放代理。
/// </summary>
public static class Token
{
    /// <summary>生成 token：base64url(url + "\n" + referer) + "-" + 签名前 8 字节 hex。</summary>
    public static string Encode(string url, string? referer, string secret, string? userAgent = null)
    {
        var payload = url + "\n" + (referer ?? string.Empty)
            + (string.IsNullOrWhiteSpace(userAgent) ? string.Empty : "\n" + userAgent);
        var b64 = Base64UrlEncode(Encoding.UTF8.GetBytes(payload));
        return b64 + "-" + Sign(b64, secret);
    }

    public static bool TryDecode(string token, string secret, out string url, out string? referer)
    {
        return TryDecode(token, secret, out url, out referer, out _);
    }

    public static bool TryDecode(string token, string secret, out string url, out string? referer,
        out string? userAgent)
    {
        url = string.Empty;
        referer = null;
        userAgent = null;
        if (string.IsNullOrWhiteSpace(token))
        {
            return false;
        }

        // token 里可能有 URL 编码过的 base64url
        token = Uri.UnescapeDataString(token);
        // HLS 的 FFmpeg 只接受带媒体扩展名的分片 URL；扩展名不属于签名内容。
        var dot = token.LastIndexOf('.');
        if (dot >= 0 && IsMediaExtension(token[(dot + 1)..]))
        {
            token = token[..dot];
        }
        var idx = token.LastIndexOf('-');
        if (idx <= 0 || idx == token.Length - 1)
        {
            return false;
        }

        var b64 = token[..idx];
        var sig = token[(idx + 1)..];
        if (!string.Equals(Sign(b64, secret), sig, StringComparison.Ordinal))
        {
            return false;
        }

        try
        {
            var text = Encoding.UTF8.GetString(Base64UrlDecode(b64));
            var nl = text.IndexOf('\n', StringComparison.Ordinal);
            if (nl < 0)
            {
                url = text;
                return true;
            }

            url = text[..nl];
            var rest = text[(nl + 1)..];
            var next = rest.IndexOf('\n', StringComparison.Ordinal);
            var r = next < 0 ? rest : rest[..next];
            referer = string.IsNullOrWhiteSpace(r) ? null : r;
            if (next >= 0)
            {
                var ua = rest[(next + 1)..];
                userAgent = string.IsNullOrWhiteSpace(ua) ? null : ua;
            }
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static string MediaExtension(string url)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            var path = uri.AbsolutePath;
            var dot = path.LastIndexOf('.');
            if (dot >= 0)
            {
                var extension = path[(dot + 1)..].ToLowerInvariant();
                if (IsMediaExtension(extension))
                {
                    return "." + extension;
                }
            }
        }

        return ".ts";
    }

    private static bool IsMediaExtension(string extension) => extension is
        "m3u8" or "ts" or "m4s" or "mp4" or "m4a" or "aac" or "mp3" or "vtt" or "webvtt" or "key" or "bin" or "jpg" or "jpeg" or "png" or "flv" or "mkv";

    private static string Sign(string data, string secret)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret ?? string.Empty));
        var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(data));
        return Convert.ToHexString(hash.AsSpan(0, 4)).ToLowerInvariant();
    }

    public static string Base64UrlEncode(byte[] data) =>
        Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static byte[] Base64UrlDecode(string input)
    {
        var s = input.Replace('-', '+').Replace('_', '/');
        switch (s.Length % 4)
        {
            case 2: s += "=="; break;
            case 3: s += "="; break;
        }

        return Convert.FromBase64String(s);
    }
}
