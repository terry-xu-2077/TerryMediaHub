using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.TerryMediaHub.Model;
using Jellyfin.Plugin.TerryMediaHub.Runtime;

namespace Jellyfin.Plugin.TerryMediaHub.Sources;

/// <summary>
/// csp_ 站点解析器：把站点交给 jar 里的 Java 爬虫，在自带 JVM 运行时里执行。
///
/// 协议对齐 catvod 的 Spider：homeContent / categoryContent / detailContent。
/// </summary>
public sealed class CspResolver : ISiteResolver
{
    public ResolverKind Kind => ResolverKind.JarSpider;

    public string DisplayName => "TVBox 爬虫(jar)";

    public bool CanHandle(TvBoxSite site) =>
        (site.Api ?? string.Empty).StartsWith("csp_", StringComparison.OrdinalIgnoreCase);

    /// <summary>csp_XXX -> XXX。</summary>
    public static string SpiderName(TvBoxSite site)
    {
        var api = site.Api ?? string.Empty;
        return api.Length <= 4 ? string.Empty : api[4..].Trim();
    }

    public async Task<IReadOnlyList<VodCategory>> GetCategoriesAsync(ResolveContext ctx, CancellationToken ct)
    {
        var name = SpiderName(ctx.Site);
        var json = await SpiderHost.Instance.CallAsync(name, ctx.Site.ExtAsString(), "home", null, ct, ctx.Site.Jar)
            .ConfigureAwait(false);

        var list = new List<VodCategory>();
        using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json);
        if (!doc.RootElement.TryGetProperty("class", out var arr) || arr.ValueKind != JsonValueKind.Array)
        {
            return list;
        }

        foreach (var c in arr.EnumerateArray())
        {
            var id = Js.Str(c, "type_id") ?? string.Empty;
            var nm = Js.Str(c, "type_name") ?? string.Empty;
            if (id.Length > 0 && nm.Length > 0)
            {
                list.Add(new VodCategory { Id = id, Name = nm });
            }
        }

        return list;
    }

    public async Task<IReadOnlyList<VodSummary>> GetListAsync(
        ResolveContext ctx,
        string categoryId,
        int page,
        CancellationToken ct)
    {
        var args = new JsonObject
        {
            ["tid"] = categoryId,
            ["pg"] = page.ToString(CultureInfo.InvariantCulture)
        };

        var json = await SpiderHost.Instance
            .CallAsync(SpiderName(ctx.Site), ctx.Site.ExtAsString(), "category", args, ct, ctx.Site.Jar)
            .ConfigureAwait(false);

        var list = new List<VodSummary>();
        using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json);
        if (!doc.RootElement.TryGetProperty("list", out var arr) || arr.ValueKind != JsonValueKind.Array)
        {
            return list;
        }

        foreach (var v in arr.EnumerateArray())
        {
            var item = new VodSummary
            {
                Id = Js.Str(v, "vod_id") ?? string.Empty,
                Name = Js.Str(v, "vod_name") ?? string.Empty,
                Pic = Js.Str(v, "vod_pic"),
                Remarks = Js.Str(v, "vod_remarks"),
                Year = Js.Str(v, "vod_year")
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

        return list;
    }

    public async Task<VodDetail?> GetDetailAsync(ResolveContext ctx, string vodId, CancellationToken ct)
    {
        var args = new JsonObject { ["ids"] = new JsonArray(vodId) };
        var json = await SpiderHost.Instance
            .CallAsync(SpiderName(ctx.Site), ctx.Site.ExtAsString(), "detail", args, ct, ctx.Site.Jar)
            .ConfigureAwait(false);

        using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json);
        if (!doc.RootElement.TryGetProperty("list", out var arr)
            || arr.ValueKind != JsonValueKind.Array
            || arr.GetArrayLength() == 0)
        {
            return null;
        }

        var v = arr[0];
        var detail = new VodDetail
        {
            Id = Js.Str(v, "vod_id") ?? vodId,
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
            Score = Js.Str(v, "vod_score"),
            PlayFrom = Js.Str(v, "vod_play_from")
        };

        var playUrl = Js.Str(v, "vod_play_url");
        detail.Episodes = string.IsNullOrWhiteSpace(playUrl)
            ? new List<VodEpisode>()
            : ParsePlayUrls(playUrl!, detail.PlayFrom);

        return detail;
    }

    /// <summary>
    /// 拆 catvod 的播放串。
    /// 与苹果CMS 不同，这里的地址可能是「播放参数 JSON」而不是直链，所以要原样保留。
    /// </summary>
    public static List<VodEpisode> ParsePlayUrls(string playUrl, string? playFrom)
    {
        var tracks = playUrl.Split("$$$", StringSplitOptions.RemoveEmptyEntries);
        var flags = string.IsNullOrWhiteSpace(playFrom)
            ? Array.Empty<string>()
            : playFrom.Split("$$$", StringSplitOptions.RemoveEmptyEntries);

        List<VodEpisode>? best = null;
        var bestCount = -1;
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

                url = url.Trim();
                if (url.Length == 0
                    || url.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase)
                    || url.StartsWith("ed2k:", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                // 直链才需要处理 "|" 备用地址；播放参数字符串里的 "|" 是内容的一部分
                if (url.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                {
                    var pipe = url.IndexOf('|');
                    if (pipe > 0)
                    {
                        url = url[..pipe];
                    }
                }

                episodes.Add(new VodEpisode { Name = name.Trim(), Url = url, Flag = flag });
            }

            if (episodes.Count > bestCount)
            {
                bestCount = episodes.Count;
                best = episodes;
                bestFlag = flag;
            }
        }

        if (best is null || best.Count == 0)
        {
            return new List<VodEpisode>();
        }

        foreach (var e in best)
        {
            e.Flag = bestFlag;
        }

        return best;
    }
}
