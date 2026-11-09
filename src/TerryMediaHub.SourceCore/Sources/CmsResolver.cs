using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using Jellyfin.Plugin.TerryMediaHub.Configuration;
using Jellyfin.Plugin.TerryMediaHub.Model;
using Jellyfin.Plugin.TerryMediaHub.Util;

namespace Jellyfin.Plugin.TerryMediaHub.Sources;

/// <summary>解析上下文。</summary>
public sealed class ResolveContext
{
    public ResolveContext(TvBoxSite site, PluginConfiguration config)
    {
        Site = site;
        Config = config;
    }

    public TvBoxSite Site { get; }

    public PluginConfiguration Config { get; }
}

/// <summary>站点解析器。</summary>
public interface ISiteResolver
{
    ResolverKind Kind { get; }

    string DisplayName { get; }

    bool CanHandle(TvBoxSite site);

    Task<IReadOnlyList<VodCategory>> GetCategoriesAsync(ResolveContext ctx, CancellationToken ct);

    Task<IReadOnlyList<VodSummary>> GetListAsync(ResolveContext ctx, string categoryId, int page, CancellationToken ct);

    Task<VodDetail?> GetDetailAsync(ResolveContext ctx, string vodId, CancellationToken ct);
}

/// <summary>
/// 苹果CMS（MacCMS v10）JSON/XML 接口解析器。
/// 覆盖 type=0/1 以及 api 直接是 http 地址的站点。
/// </summary>
public sealed class CmsResolver : ISiteResolver
{
    public ResolverKind Kind => ResolverKind.Cms;

    public string DisplayName => "苹果CMS(v10)";

    public bool CanHandle(TvBoxSite site)
    {
        if (string.IsNullOrWhiteSpace(site.Api) || !site.Api.StartsWith("http", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        // .js / .py 结尾属于脚本 spider，不归这里
        var path = site.Api.Split('?')[0].ToLowerInvariant();
        if (path.EndsWith(".js") || path.EndsWith(".py") || path.EndsWith(".json") || path.EndsWith(".txt"))
        {
            return false;
        }

        return true;
    }

    public static string BuildUrl(TvBoxSite site, string query)
    {
        var api = site.Api.Trim();
        if (!api.Contains('?', StringComparison.Ordinal))
        {
            return api.TrimEnd('/') + "/?" + query;
        }

        // 已经有查询串：只在末尾追加
        var idx = api.IndexOf('?', StringComparison.Ordinal);
        var basePart = api[..idx].TrimEnd('/');
        var existing = api[(idx + 1)..];
        // 丢掉 ac= 之类的原有指令，避免冲突
        var kept = string.Join('&', existing.Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Where(p => !p.StartsWith("ac=", StringComparison.OrdinalIgnoreCase)
                        && !p.StartsWith("pg=", StringComparison.OrdinalIgnoreCase)
                        && !p.StartsWith("t=", StringComparison.OrdinalIgnoreCase)
                        && !p.StartsWith("ids=", StringComparison.OrdinalIgnoreCase)));
        return string.IsNullOrEmpty(kept)
            ? basePart + "/?" + query
            : basePart + "/?" + kept + "&" + query;
    }

    public async Task<IReadOnlyList<VodCategory>> GetCategoriesAsync(ResolveContext ctx, CancellationToken ct)
    {
        var url = BuildUrl(ctx.Site, "ac=list");
        var raw = await Http.GetStringAsync(url, ctx.Config.UserAgent, Http.OriginOf(ctx.Site.Api), ct)
            .ConfigureAwait(false);

        var result = new List<VodCategory>();
        if (LooksLikeXml(raw))
        {
            var doc = XDocument.Parse(raw);
            foreach (var c in doc.Descendants("class"))
            {
                var id = (string?)c.Element("ty") ?? (string?)c.Element("type_id");
                var name = (string?)c.Element("tyname") ?? (string?)c.Element("type_name");
                if (!string.IsNullOrWhiteSpace(id) && !string.IsNullOrWhiteSpace(name))
                {
                    result.Add(new VodCategory { Id = id!, Name = name! });
                }
            }
        }
        else
        {
            using var doc = JsonDocument.Parse(raw);
            if (doc.RootElement.TryGetProperty("class", out var classes) && classes.ValueKind == JsonValueKind.Array)
            {
                foreach (var c in classes.EnumerateArray())
                {
                    var id = Js.Str(c, "type_id");
                    var name = Js.Str(c, "type_name");
                    var pid = Js.Str(c, "type_pid");
                    if (!string.IsNullOrWhiteSpace(id) && !string.IsNullOrWhiteSpace(name))
                    {
                        result.Add(new VodCategory { Id = id!, Name = name!, ParentId = pid });
                    }
                }
            }
        }

        return result;
    }

    public async Task<IReadOnlyList<VodSummary>> GetListAsync(
        ResolveContext ctx,
        string categoryId,
        int page,
        CancellationToken ct)
    {
        var query = $"ac=videolist&pg={page}";
        if (!string.IsNullOrWhiteSpace(categoryId))
        {
            query += "&t=" + Uri.EscapeDataString(categoryId);
        }

        var url = BuildUrl(ctx.Site, query);
        var raw = await Http.GetStringAsync(url, ctx.Config.UserAgent, Http.OriginOf(ctx.Site.Api), ct)
            .ConfigureAwait(false);

        var list = new List<VodSummary>();
        if (LooksLikeXml(raw))
        {
            var doc = XDocument.Parse(raw);
            foreach (var v in doc.Descendants("video"))
            {
                var item = new VodSummary
                {
                    Id = (string?)v.Element("id") ?? string.Empty,
                    Name = (string?)v.Element("name") ?? string.Empty,
                    Pic = (string?)v.Element("pic"),
                    Remarks = (string?)v.Element("note") ?? (string?)v.Element("des"),
                    CategoryName = (string?)v.Element("type"),
                    Year = (string?)v.Element("year"),
                    Area = (string?)v.Element("area"),
                    Lang = (string?)v.Element("lang"),
                    Actor = (string?)v.Element("actor"),
                    Director = (string?)v.Element("director")
                };
                if (item.Id.Length > 0 && item.Name.Length > 0)
                {
                    list.Add(item);
                }
            }
        }
        else
        {
            using var doc = JsonDocument.Parse(raw);
            if (doc.RootElement.TryGetProperty("list", out var arr) && arr.ValueKind == JsonValueKind.Array)
            {
                foreach (var v in arr.EnumerateArray())
                {
                    var item = new VodSummary
                    {
                        Id = Js.Str(v, "vod_id") ?? string.Empty,
                        Name = Js.Str(v, "vod_name") ?? string.Empty,
                        Pic = Js.Str(v, "vod_pic"),
                        Remarks = Js.Str(v, "vod_remarks"),
                        CategoryName = Js.Str(v, "type_name") ?? Js.Str(v, "vod_class"),
                        Year = Js.Str(v, "vod_year"),
                        Area = Js.Str(v, "vod_area"),
                        Lang = Js.Str(v, "vod_lang"),
                        Actor = Js.Str(v, "vod_actor"),
                        Director = Js.Str(v, "vod_director"),
                        Content = Js.Str(v, "vod_content") ?? Js.Str(v, "vod_blurb"),
                        Score = Js.Str(v, "vod_score")
                    };
                    var pu = Js.Str(v, "vod_play_url");
                    if (!string.IsNullOrWhiteSpace(pu))
                    {
                        item.PlayFrom = Js.Str(v, "vod_play_from");
                        item.Episodes = ParsePlayUrls(pu!, item.PlayFrom);
                    }

                    if (item.Id.Length > 0 && item.Name.Length > 0)
                    {
                        list.Add(item);
                    }
                }
            }
        }

        return list;
    }

    public async Task<VodDetail?> GetDetailAsync(ResolveContext ctx, string vodId, CancellationToken ct)
    {
        var url = BuildUrl(ctx.Site, "ac=detail&ids=" + Uri.EscapeDataString(vodId));
        var raw = await Http.GetStringAsync(url, ctx.Config.UserAgent, Http.OriginOf(ctx.Site.Api), ct)
            .ConfigureAwait(false);

        VodDetail? detail = null;
        string? playUrl = null;
        string? playFrom = null;

        if (LooksLikeXml(raw))
        {
            var doc = XDocument.Parse(raw);
            var v = doc.Descendants("video").FirstOrDefault();
            if (v is null)
            {
                return null;
            }

            detail = new VodDetail
            {
                Id = vodId,
                Name = (string?)v.Element("name") ?? string.Empty,
                Pic = (string?)v.Element("pic"),
                Remarks = (string?)v.Element("note"),
                CategoryName = (string?)v.Element("type"),
                Year = (string?)v.Element("year"),
                Area = (string?)v.Element("area"),
                Lang = (string?)v.Element("lang"),
                Actor = (string?)v.Element("actor"),
                Director = (string?)v.Element("director"),
                Content = (string?)v.Element("des")
            };

            var dd = v.Descendants("dd").ToList();
            playUrl = string.Join("$$$", dd.Select(d => d.Value));
            playFrom = string.Join("$$$", dd.Select(d => (string?)d.Attribute("flag") ?? "line"));
        }
        else
        {
            using var doc = JsonDocument.Parse(raw);
            JsonElement v = default;
            var found = false;
            if (doc.RootElement.TryGetProperty("list", out var arr) && arr.ValueKind == JsonValueKind.Array && arr.GetArrayLength() > 0)
            {
                v = arr[0];
                found = true;
            }

            if (!found)
            {
                return null;
            }

            detail = new VodDetail
            {
                Id = vodId,
                Name = Js.Str(v, "vod_name") ?? string.Empty,
                Pic = Js.Str(v, "vod_pic"),
                Remarks = Js.Str(v, "vod_remarks"),
                CategoryName = Js.Str(v, "type_name") ?? Js.Str(v, "vod_class"),
                Year = Js.Str(v, "vod_year"),
                Area = Js.Str(v, "vod_area"),
                Lang = Js.Str(v, "vod_lang"),
                Actor = Js.Str(v, "vod_actor"),
                Director = Js.Str(v, "vod_director"),
                Content = Js.Str(v, "vod_content") ?? Js.Str(v, "vod_blurb"),
                Score = Js.Str(v, "vod_score")
            };
            playUrl = Js.Str(v, "vod_play_url");
            playFrom = Js.Str(v, "vod_play_from");
        }

        if (detail is null || string.IsNullOrWhiteSpace(playUrl))
        {
            return detail;
        }

        detail.Episodes = ParsePlayUrls(playUrl, playFrom);
        return detail;
    }

    /// <summary>
    /// 解析 "线路1$$$线路2" + "第01集$url#第02集$url"。
    /// 多线路时选择“可播放集数最多且是 m3u8/mp4 直链”的一条。
    /// </summary>
    public static List<VodEpisode> ParsePlayUrls(string playUrl, string? playFrom)
    {
        var tracks = playUrl.Split("$$$", StringSplitOptions.RemoveEmptyEntries);
        var flags = string.IsNullOrWhiteSpace(playFrom)
            ? Array.Empty<string>()
            : playFrom.Split("$$$", StringSplitOptions.RemoveEmptyEntries);

        List<VodEpisode>? best = null;
        var bestScore = -1;
        string? bestFlag = null;

        for (var i = 0; i < tracks.Length; i++)
        {
            var flag = i < flags.Length ? flags[i] : $"线路{i + 1}";
            var episodes = new List<VodEpisode>();
            foreach (var seg in tracks[i].Split('#', StringSplitOptions.RemoveEmptyEntries))
            {
                var trimmed = seg.Trim();
                if (trimmed.Length == 0)
                {
                    continue;
                }

                string name;
                string url;
                var d = trimmed.IndexOf('$', StringComparison.Ordinal);
                if (d > 0 && d < trimmed.Length - 1)
                {
                    name = trimmed[..d];
                    url = trimmed[(d + 1)..];
                }
                else
                {
                    name = "第" + (episodes.Count + 1).ToString("00", CultureInfo.InvariantCulture) + "集";
                    url = trimmed;
                }

                // 有些接口 url 里再带 "|" 拆分参数
                var pipe = url.IndexOf('|');
                if (pipe > 0)
                {
                    url = url[..pipe];
                }

                if (!url.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (url.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase)
                    || url.StartsWith("ed2k:", StringComparison.OrdinalIgnoreCase)
                    || url.StartsWith("thunder:", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                episodes.Add(new VodEpisode { Name = name.Trim(), Url = url.Trim(), Flag = flag });
            }

            if (episodes.Count == 0)
            {
                continue;
            }

            // 打分：可识别的直链越多越好，集数越多越好
            var direct = episodes.Count(e => LooksPlayable(e.Url));
            var score = (direct * 1000) + episodes.Count;
            if (score > bestScore)
            {
                bestScore = score;
                best = episodes;
                bestFlag = flag;
            }
        }

        if (best is null)
        {
            return new List<VodEpisode>();
        }

        foreach (var e in best)
        {
            e.Flag = bestFlag;
        }

        return best;
    }

    public static bool LooksPlayable(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return false;
        }

        var u = url.ToLowerInvariant();
        if (u.StartsWith("magnet:", StringComparison.Ordinal) || u.StartsWith("ed2k:", StringComparison.Ordinal))
        {
            return false;
        }

        return u.Contains(".m3u8", StringComparison.Ordinal)
               || u.Contains(".mp4", StringComparison.Ordinal)
               || u.Contains(".flv", StringComparison.Ordinal)
               || u.Contains(".mkv", StringComparison.Ordinal)
               || u.Contains(".ts?", StringComparison.Ordinal)
               || u.Contains("/play/", StringComparison.Ordinal)
               || u.Contains("m3u8", StringComparison.Ordinal);
    }

    private static bool LooksLikeXml(string raw)
    {
        var s = raw.TrimStart();
        return s.StartsWith("<?xml", StringComparison.OrdinalIgnoreCase)
               || s.StartsWith("<rss", StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>JSON 小工具。</summary>
public static class Js
{
    public static string? Str(JsonElement e, string name)
    {
        if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(name, out var v))
        {
            return null;
        }

        return v.ValueKind switch
        {
            JsonValueKind.String => v.GetString(),
            JsonValueKind.Number => v.ToString(),
            JsonValueKind.True => "1",
            JsonValueKind.False => "0",
            _ => null
        };
    }
}
