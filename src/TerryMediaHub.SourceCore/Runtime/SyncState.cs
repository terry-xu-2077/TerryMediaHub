using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using Jellyfin.Plugin.TerryMediaHub.Configuration;

namespace Jellyfin.Plugin.TerryMediaHub.Runtime;

/// <summary>同步任务运行状态（进程内）。</summary>
public static class SyncState
{
    private static readonly object Lock = new();

    public static bool Running { get; private set; }

    public static DateTime? LastStartUtc { get; private set; }

    public static DateTime? LastEndUtc { get; private set; }

    public static string LastSummary { get; private set; } = string.Empty;

    public static List<string> LastNotes { get; private set; } = new();

    public static int Progress { get; private set; }

    public static string Phase { get; private set; } = "空闲";

    public static bool TryBegin(string phase)
    {
        lock (Lock)
        {
            if (Running)
            {
                return false;
            }

            Running = true;
            Progress = 0;
            Phase = phase;
            LastStartUtc = DateTime.UtcNow;
            return true;
        }
    }

    public static void Report(int progress, string phase)
    {
        lock (Lock)
        {
            Progress = Math.Clamp(progress, 0, 100);
            Phase = phase;
        }
    }

    public static void End(string summary, IEnumerable<string>? notes = null)
    {
        lock (Lock)
        {
            Running = false;
            Progress = 100;
            Phase = "空闲";
            LastEndUtc = DateTime.UtcNow;
            LastSummary = summary;
            LastNotes = notes?.Take(200).ToList() ?? new List<string>();
        }
    }
}

/// <summary>Jellyfin 媒体库扫描状态，与源数据同步独立。</summary>
public static class LibraryRebuildState
{
    private static readonly object Lock = new();

    public static bool Running { get; private set; }

    public static DateTime? LastStartUtc { get; private set; }

    public static DateTime? LastEndUtc { get; private set; }

    public static string LastSummary { get; private set; } = string.Empty;

    public static bool TryBegin()
    {
        lock (Lock)
        {
            if (Running)
            {
                return false;
            }

            Running = true;
            LastStartUtc = DateTime.UtcNow;
            return true;
        }
    }

    public static void End(string summary)
    {
        lock (Lock)
        {
            Running = false;
            LastEndUtc = DateTime.UtcNow;
            LastSummary = summary;
        }
    }
}

/// <summary>推断写进 .strm 的服务地址。</summary>
public static class Address
{
    public static string Resolve(PluginConfiguration cfg, string? requestBase)
    {
        if (!string.IsNullOrWhiteSpace(cfg.PublicBaseUrl))
        {
            return cfg.PublicBaseUrl.TrimEnd('/');
        }

        if (!string.IsNullOrWhiteSpace(requestBase))
        {
            cfg.DetectedBaseUrl = requestBase.TrimEnd('/');
            return cfg.DetectedBaseUrl;
        }

        if (!string.IsNullOrWhiteSpace(cfg.DetectedBaseUrl))
        {
            return cfg.DetectedBaseUrl.TrimEnd('/');
        }

        var ip = DetectLanIp();
        var port = 8096;
        var fromCfg = TryReadPortFromJellyfin();
        if (fromCfg is > 0)
        {
            port = fromCfg.Value;
        }

        return $"http://{ip}:{port}";
    }

    private static string DetectLanIp()
    {
        try
        {
            var candidates = Dns.GetHostAddresses(Dns.GetHostName())
                .Where(a => a.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a))
                .Select(a => a.ToString())
                .ToList();

            // 优先常见内网段
            string[] prefixes = { "192.168.", "10.", "172.16.", "172.17.", "172.18.", "172.19." };
            foreach (var p in prefixes)
            {
                var hit = candidates.FirstOrDefault(c => c.StartsWith(p, StringComparison.Ordinal));
                if (hit is not null)
                {
                    return hit;
                }
            }

            return candidates.FirstOrDefault() ?? "127.0.0.1";
        }
        catch
        {
            return "127.0.0.1";
        }
    }

    private static int? TryReadPortFromJellyfin()
    {
        try
        {
            var path = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "jellyfin",
                "config",
                "network.xml");
            if (!System.IO.File.Exists(path))
            {
                return null;
            }

            var doc = System.Xml.Linq.XDocument.Load(path);
            var el = doc.Root?.Element("InternalHttpPort") ?? doc.Root?.Element("PublicHttpPort");
            return el is not null && int.TryParse(el.Value, out var p) ? p : null;
        }
        catch
        {
            return null;
        }
    }
}
