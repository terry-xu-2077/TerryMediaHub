using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Jellyfin.Plugin.TerryMediaHub.Library;

namespace Jellyfin.Plugin.TerryMediaHub.Runtime;

/// <summary>
/// 同步断点续传：把「每个站点是否已完成抓取」持久化到磁盘。
/// 进程重启后，已完成且源没变化的站点直接跳过，不用从头扫列表。
/// </summary>
public static class SyncProgress
{
    private sealed class ProgressFile
    {
        public int Version { get; set; } = 1;

        /// <summary>已完成的站点 key（有序，含来源标识，避免不同源同名站点误判）。</summary>
        public List<string> DoneSites { get; set; } = new();

        /// <summary>站点列表指纹：源地址/站点集合变化时作废续传，强制全量重抓。</summary>
        public string? Fingerprint { get; set; }

        public DateTime SavedUtc { get; set; } = DateTime.UtcNow;
    }

    private static readonly object Lock = new();
    private static ProgressFile? _cache;

    private static string FilePath(Jellyfin.Plugin.TerryMediaHub.Configuration.PluginConfiguration cfg) =>
        Path.Combine(LibraryBuilder.ResolveRoot(cfg), ".sync-progress.json");

    private static ProgressFile Load(Jellyfin.Plugin.TerryMediaHub.Configuration.PluginConfiguration cfg)
    {
        lock (Lock)
        {
            if (_cache is not null)
            {
                return _cache;
            }

            var path = FilePath(cfg);
            try
            {
                if (File.Exists(path))
                {
                    _cache = JsonSerializer.Deserialize<ProgressFile>(File.ReadAllText(path)) ?? new ProgressFile();
                }
                else
                {
                    _cache = new ProgressFile();
                }
            }
            catch
            {
                _cache = new ProgressFile();
            }

            return _cache;
        }
    }

    private static void Save(ProgressFile pf, Jellyfin.Plugin.TerryMediaHub.Configuration.PluginConfiguration cfg)
    {
        var path = FilePath(cfg);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(pf, new JsonSerializerOptions { WriteIndented = false }));
            File.Move(tmp, path, overwrite: true);
        }
        catch
        {
            // 持久化失败不致命：最多下轮重抓一部分站点
        }
    }

    /// <summary>计算当前站点列表的指纹；变化（增删源/站点）就返回与上次不同的值。</summary>
    public static string ComputeFingerprint(IEnumerable<Sources.SiteEntry> sites)
    {
        var keys = sites
            .Select(s => s.Key + "|" + s.Site.Api)
            .OrderBy(k => k, StringComparer.Ordinal)
            .ToList();
        var joined = string.Join(";", keys);
        return Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(joined))).ToLowerInvariant();
    }

    /// <summary>
    /// 判断某个站点是否「本轮可跳过」（上一轮已完整抓过、且站点列表没变化、增量同步开着）。
    /// </summary>
    public static bool IsSiteDone(Jellyfin.Plugin.TerryMediaHub.Configuration.PluginConfiguration cfg, Sources.SiteEntry site, string fingerprint)
    {
        if (!cfg.IncrementalSync)
        {
            return false;
        }

        var pf = Load(cfg);
        if (!string.Equals(pf.Fingerprint, fingerprint, StringComparison.Ordinal))
        {
            return false; // 站点集合变了，续传失效
        }

        return pf.DoneSites.Contains(site.Key + "|" + site.Site.Api, StringComparer.Ordinal);
    }

    /// <summary>标记一个站点已完成，并落盘（带指纹）。</summary>
    public static void MarkSiteDone(Jellyfin.Plugin.TerryMediaHub.Configuration.PluginConfiguration cfg, Sources.SiteEntry site, string fingerprint)
    {
        var pf = Load(cfg);
        var key = site.Key + "|" + site.Site.Api;
        if (!pf.DoneSites.Contains(key, StringComparer.Ordinal))
        {
            pf.DoneSites.Add(key);
        }

        pf.Fingerprint = fingerprint;
        pf.SavedUtc = DateTime.UtcNow;
        Save(pf, cfg);
    }

    /// <summary>整轮跑完清空续传状态（下次恢复全量校验）。</summary>
    public static void Reset(Jellyfin.Plugin.TerryMediaHub.Configuration.PluginConfiguration cfg)
    {
        lock (Lock)
        {
            _cache = null;
        }

        var path = FilePath(cfg);
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // ignore
        }
    }

    /// <summary>本轮已完成站点数（用于日志/进度显示）。</summary>
    public static int DoneCount(Jellyfin.Plugin.TerryMediaHub.Configuration.PluginConfiguration cfg) =>
        Load(cfg).DoneSites.Count;

    /// <summary>测试用：清内存缓存（配置路径变化时调用）。</summary>
    public static void InvalidateCache()
    {
        lock (Lock)
        {
            _cache = null;
        }
    }
}
