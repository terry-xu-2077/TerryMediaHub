using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Jellyfin.Plugin.TerryMediaHub.Model;

namespace Jellyfin.Plugin.TerryMediaHub.Library;

/// <summary>生成 Jellyfin 可识别的本地 NFO 元数据。</summary>
public static class Nfo
{
    public static string ForMovie(VodDetail vod, string siteName)
    {
        var sb = new StringBuilder();
        sb.Append("<?xml version=\"1.0\" encoding=\"utf-8\" standalone=\"yes\"?>\n<movie>\n");
        El(sb, "title", vod.Name);
        El(sb, "sorttitle", MediaSort.ForItem(vod.Score, YearOf(vod), vod.Id, vod.Name));
        if (!string.IsNullOrWhiteSpace(vod.Year))
        {
            El(sb, "year", YearOf(vod));
        }

        if (!string.IsNullOrWhiteSpace(vod.Content))
        {
            El(sb, "plot", Clean(vod.Content));
        }

        if (!string.IsNullOrWhiteSpace(vod.Remarks))
        {
            El(sb, "tagline", vod.Remarks);
        }

        El(sb, "genre", vod.CategoryName);
        El(sb, "country", vod.Area);
        El(sb, "director", vod.Director);
        foreach (var actor in SplitNames(vod.Actor))
        {
            sb.Append("  <actor><name>").Append(Escape(actor)).Append("</name></actor>\n");
        }

        if (double.TryParse(vod.Score, NumberStyles.Any, CultureInfo.InvariantCulture, out var score) && score > 0)
        {
            El(sb, "rating", score.ToString("0.0", CultureInfo.InvariantCulture));
        }

        El(sb, "studio", siteName);
        El(sb, "uniqueid", vod.Id, "type", "yingshicang");
        El(sb, "dateadded", DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
        sb.Append("</movie>\n");
        return sb.ToString();
    }

    public static string ForShow(VodDetail vod, string siteName)
    {
        var sb = new StringBuilder();
        sb.Append("<?xml version=\"1.0\" encoding=\"utf-8\" standalone=\"yes\"?>\n<tvshow>\n");
        El(sb, "title", vod.Name);
        El(sb, "sorttitle", MediaSort.ForItem(vod.Score, YearOf(vod), vod.Id, vod.Name));
        if (!string.IsNullOrWhiteSpace(vod.Year))
        {
            El(sb, "year", YearOf(vod));
        }

        if (!string.IsNullOrWhiteSpace(vod.Content))
        {
            El(sb, "plot", Clean(vod.Content));
        }

        El(sb, "genre", vod.CategoryName);
        El(sb, "country", vod.Area);
        El(sb, "director", vod.Director);
        foreach (var actor in SplitNames(vod.Actor))
        {
            sb.Append("  <actor><name>").Append(Escape(actor)).Append("</name></actor>\n");
        }

        if (double.TryParse(vod.Score, NumberStyles.Any, CultureInfo.InvariantCulture, out var score) && score > 0)
        {
            El(sb, "rating", score.ToString("0.0", CultureInfo.InvariantCulture));
        }

        El(sb, "studio", siteName);
        El(sb, "uniqueid", vod.Id, "type", "yingshicang");
        sb.Append("</tvshow>\n");
        return sb.ToString();
    }

    public static string ForEpisode(VodDetail vod, VodEpisode episode, int season, int number)
    {
        var sb = new StringBuilder();
        sb.Append("<?xml version=\"1.0\" encoding=\"utf-8\" standalone=\"yes\"?>\n<episodedetails>\n");
        El(sb, "title", string.IsNullOrWhiteSpace(episode.Name) ? $"第{number}集" : episode.Name);
        El(sb, "season", season.ToString(CultureInfo.InvariantCulture));
        El(sb, "episode", number.ToString(CultureInfo.InvariantCulture));
        El(sb, "plot", Clean(vod.Content));
        sb.Append("</episodedetails>\n");
        return sb.ToString();
    }

    public static string YearOf(VodSummary vod)
    {
        if (!string.IsNullOrWhiteSpace(vod.Year))
        {
            var y = vod.Year!.Trim();
            if (y.Length >= 4 && int.TryParse(y[..4], out _))
            {
                return y[..4];
            }
        }

        // 从备注里猜年份，如 "2024-07-01"
        var src = vod.Remarks ?? string.Empty;
        for (var i = 0; i + 4 <= src.Length; i++)
        {
            if (int.TryParse(src.AsSpan(i, 4), out var v) && v is >= 1900 and <= 2100)
            {
                return v.ToString(CultureInfo.InvariantCulture);
            }
        }

        return string.Empty;
    }

    private static IEnumerable<string> SplitNames(string? names)
    {
        if (string.IsNullOrWhiteSpace(names))
        {
            yield break;
        }

        foreach (var n in names.Split(new[] { ',', '，', '/', '、', '|' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var t = n.Trim();
            if (t.Length is > 0 and < 40)
            {
                yield return t;
            }
        }
    }

    private static string Clean(string s)
    {
        var t = s.Replace("\r", " ", StringComparison.Ordinal)
                 .Replace("\n", " ", StringComparison.Ordinal)
                 .Replace("&nbsp;", " ", StringComparison.Ordinal)
                 .Replace("<br />", " ", StringComparison.Ordinal)
                 .Replace("<br/>", " ", StringComparison.Ordinal)
                 .Replace("<br>", " ", StringComparison.Ordinal)
                 .Trim();
        while (t.Contains("  ", StringComparison.Ordinal))
        {
            t = t.Replace("  ", " ", StringComparison.Ordinal);
        }

        return t;
    }

    private static void El(StringBuilder sb, string name, string? value, string? attrName = null, string? attrValue = null)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        sb.Append("  <").Append(name);
        if (attrName is not null)
        {
            sb.Append(' ').Append(attrName).Append("=\"").Append(Escape(attrValue ?? string.Empty)).Append('"');
        }

        sb.Append('>').Append(Escape(value)).Append("</").Append(name).Append(">\n");
    }

    private static string Escape(string s) => System.Security.SecurityElement.Escape(s) ?? s;
}
