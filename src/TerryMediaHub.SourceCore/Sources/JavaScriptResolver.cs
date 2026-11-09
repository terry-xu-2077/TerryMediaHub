using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.TerryMediaHub.Model;
using Jellyfin.Plugin.TerryMediaHub.Runtime;

namespace Jellyfin.Plugin.TerryMediaHub.Sources;

/// <summary>通过 TV6.2.9 兼容的 JS worker 调用直接脚本或 drpy2 规则引擎。</summary>
public sealed class JavaScriptResolver : ISiteResolver
{
    public ResolverKind Kind => ResolverKind.JavaScript;
    public string DisplayName => "TVBox JS 爬虫";
    public bool CanHandle(TvBoxSite site) => (site.Api ?? string.Empty)
        .Split('?')[0].EndsWith(".js", StringComparison.OrdinalIgnoreCase);

    private static Task<string> CallAsync(ResolveContext ctx, string method, JsonObject? args, CancellationToken ct) =>
        JavaScriptHost.Instance.CallAsync(ctx.Site.Key, ctx.Site.Api, ctx.Site.ExtAsString(), ctx.Site.Type, method, args, ct);

    public async Task<IReadOnlyList<VodCategory>> GetCategoriesAsync(ResolveContext ctx, CancellationToken ct)
    {
        var json = await CallAsync(ctx, "home", null, ct).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json);
        var list = new List<VodCategory>();
        if (!doc.RootElement.TryGetProperty("class", out var arr) || arr.ValueKind != JsonValueKind.Array)
            return list;
        foreach (var c in arr.EnumerateArray())
        {
            var id = Js.Str(c, "type_id") ?? string.Empty;
            var name = Js.Str(c, "type_name") ?? string.Empty;
            if (id.Length > 0 && name.Length > 0) list.Add(new VodCategory { Id = id, Name = name });
        }
        return list;
    }

    public async Task<IReadOnlyList<VodSummary>> GetListAsync(ResolveContext ctx, string categoryId,
        int page, CancellationToken ct)
    {
        var args = new JsonObject { ["tid"] = categoryId, ["pg"] = page.ToString(CultureInfo.InvariantCulture) };
        var json = await CallAsync(ctx, "category", args, ct).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json);
        var list = new List<VodSummary>();
        if (!doc.RootElement.TryGetProperty("list", out var arr) || arr.ValueKind != JsonValueKind.Array)
            return list;
        foreach (var v in arr.EnumerateArray())
        {
            var item = new VodSummary
            {
                Id = Js.Str(v, "vod_id") ?? string.Empty,
                Name = Js.Str(v, "vod_name") ?? string.Empty,
                Pic = Js.Str(v, "vod_pic"),
                Remarks = Js.Str(v, "vod_remarks"),
                Year = Js.Str(v, "vod_year"),
                PlayFrom = Js.Str(v, "vod_play_from")
            };
            var play = Js.Str(v, "vod_play_url");
            if (!string.IsNullOrWhiteSpace(play)) item.Episodes = CspResolver.ParsePlayUrls(play, item.PlayFrom);
            WrapEpisodes(ctx, item.Episodes);
            if (item.Id.Length > 0 && item.Name.Length > 0) list.Add(item);
        }
        return list;
    }

    public async Task<VodDetail?> GetDetailAsync(ResolveContext ctx, string vodId, CancellationToken ct)
    {
        var args = new JsonObject { ["ids"] = new JsonArray(vodId) };
        var json = await CallAsync(ctx, "detail", args, ct).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json);
        if (!doc.RootElement.TryGetProperty("list", out var arr) || arr.ValueKind != JsonValueKind.Array
            || arr.GetArrayLength() == 0) return null;
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
        var play = Js.Str(v, "vod_play_url");
        if (!string.IsNullOrWhiteSpace(play)) detail.Episodes = CspResolver.ParsePlayUrls(play, detail.PlayFrom);
        WrapEpisodes(ctx, detail.Episodes);
        return detail;
    }

    private static void WrapEpisodes(ResolveContext ctx, IEnumerable<VodEpisode> episodes)
    {
        foreach (var episode in episodes)
        {
            episode.Url = JsonSerializer.Serialize(new
            {
                terryJs = new
                {
                    site = ctx.Site.Key,
                    api = ctx.Site.Api,
                    ext = ctx.Site.ExtAsString(),
                    type = ctx.Site.Type,
                    flag = episode.Flag ?? string.Empty,
                    id = episode.Url
                }
            });
        }
    }
}

