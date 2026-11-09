using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.TerryMediaHub.Configuration;
using Jellyfin.Plugin.TerryMediaHub.Health;
using Jellyfin.Plugin.TerryMediaHub.Model;
using Jellyfin.Plugin.TerryMediaHub.Util;

namespace Jellyfin.Plugin.TerryMediaHub.Library;

public sealed record QualityChoice(bool Ok, int Score, int Episodes, int Height,
    long LatencyMs, long BytesPerSecond, string UrlHash, int AlgorithmVersion);

/// <summary>将实测连接速度、HLS 清晰度及分集完整度用于重复资源择优。</summary>
public static class MediaQuality
{
    private const string FileName = ".terrymediahub-quality.json";
    private static readonly Regex HeightHint = new(@"(?<!\d)(2160p|1080p|720p|4k)(?!\d)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static async Task<QualityChoice> CheckAsync(VodEpisode episode, int episodeCount,
        LinkProbe probe, SemaphoreSlim gate, CancellationToken ct)
    {
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var result = await probe.CheckAsync(episode.Url, ct).ConfigureAwait(false);
            return FromResult(result, episode, episodeCount);
        }
        finally { gate.Release(); }
    }

    public static async Task<QualityChoice?> ExistingAsync(string directory, PluginConfiguration cfg,
        LinkProbe probe, SemaphoreSlim gate, CancellationToken ct)
    {
        if (!Directory.Exists(directory)) return null;
        var files = Directory.EnumerateFiles(directory, "*.strm", SearchOption.AllDirectories)
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
        if (files.Count == 0) return null;
        string? line;
        try { line = File.ReadLines(files[0]).FirstOrDefault()?.Trim(); }
        catch (IOException) { return null; }
        if (string.IsNullOrWhiteSpace(line)) return null;
        var url = DecodeStream(line, cfg);
        if (url is null) return null;
        var hash = Hash(url);
        var path = Path.Combine(directory, FileName);
        try
        {
            if (File.Exists(path))
            {
                var cached = JsonSerializer.Deserialize<QualityChoice>(await File.ReadAllTextAsync(path, ct).ConfigureAwait(false));
                if (cached is not null && cached.UrlHash == hash
                    && cached.AlgorithmVersion == 2
                    && File.GetLastWriteTimeUtc(path) > DateTime.UtcNow.AddHours(-12))
                    return cached with { Episodes = files.Count };
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException) { }

        var checkedChoice = await CheckAsync(new VodEpisode { Name = Path.GetFileName(files[0]), Url = url },
            files.Count, probe, gate, ct).ConfigureAwait(false);
        await SaveAsync(directory, checkedChoice, ct).ConfigureAwait(false);
        return checkedChoice;
    }

    public static async Task SaveAsync(string directory, QualityChoice choice, CancellationToken ct)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, FileName);
        var temp = path + ".tmp";
        await File.WriteAllTextAsync(temp, JsonSerializer.Serialize(choice), ct).ConfigureAwait(false);
        File.Move(temp, path, true);
    }

    public static bool Better(QualityChoice candidate, QualityChoice? existing)
    {
        if (!candidate.Ok) return false;
        if (existing is null || !existing.Ok) return true;
        if (candidate.Episodes < existing.Episodes) return false;
        if (candidate.Episodes > existing.Episodes) return true;
        return candidate.Score >= existing.Score + 5;
    }

    private static QualityChoice FromResult(ProbeResult result, VodEpisode episode, int episodeCount)
    {
        if (!result.Ok) return new QualityChoice(false, 0, episodeCount, 0,
            result.LatencyMs, 0, Hash(episode.Url), 2);
        var height = result.ResolutionHeight;
        var verified = height > 0;
        if (!verified)
        {
            var hint = HeightHint.Match((episode.Name ?? string.Empty) + " " + (episode.Flag ?? string.Empty)
                + " " + episode.Url);
            height = hint.Value.ToLowerInvariant() switch
            {
                "4k" or "2160p" => 2160,
                "1080p" => 1080,
                "720p" => 720,
                _ => 0
            };
        }

        var quality = height >= 2160 ? (verified ? 75 : 55)
            : height >= 1080 ? (verified ? 55 : 40)
            : height >= 720 ? (verified ? 40 : 30) : 20;
        var mbps = result.ThroughputBytesPerSecond * 8.0 / 1_000_000;
        // 旧曲线在约 9 Mbps 就封顶，实际大多数 CDN 因此同分。
        // 对数曲线保留低速线路的区分度，同时允许高速线路继续拉开差距。
        var speed = (int)Math.Min(55, 7 * Math.Log2(1 + Math.Max(0, mbps)));
        var latency = (int)Math.Max(0, 20 - result.LatencyMs / 100);
        var score = quality + speed + latency;
        if (result.BandwidthBitsPerSecond > 0 && result.ThroughputBytesPerSecond > 0)
        {
            var ratio = result.ThroughputBytesPerSecond * 8.0 / result.BandwidthBitsPerSecond;
            if (ratio < 0.5) score -= 50;
            else if (ratio < 1.2) score -= 25;
        }

        return new QualityChoice(true, Math.Max(1, score), episodeCount, height,
            result.LatencyMs, result.ThroughputBytesPerSecond, Hash(episode.Url), 2);
    }

    private static string? DecodeStream(string line, PluginConfiguration cfg)
    {
        if (line.Contains("/TerryMediaHub/Stream/", StringComparison.OrdinalIgnoreCase))
        {
            var token = line[(line.LastIndexOf('/') + 1)..];
            return Token.TryDecode(token, cfg.StreamSecret, out var url, out _) ? url : null;
        }

        return line.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? line : null;
    }

    private static string Hash(string url) => Convert.ToHexString(
        SHA256.HashData(Encoding.UTF8.GetBytes(url)).AsSpan(0, 8));
}
