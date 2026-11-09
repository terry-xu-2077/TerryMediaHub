using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Jellyfin.Plugin.TerryMediaHub.Library;

/// <summary>按评分和发行年份生成 Jellyfin 的名称排序键。</summary>
public static class MediaSort
{
    private static readonly Regex YearPattern = new(@"(?<!\d)(19\d{2}|20\d{2})(?!\d)", RegexOptions.Compiled);
    private static readonly Regex HotRankPattern = new(@"-R(\d{5})-", RegexOptions.Compiled);

    public static string ForItem(string? rating, string? year, string? identity, string title, int hotRank = 99999)
    {
        var score = double.TryParse(rating, NumberStyles.Any, CultureInfo.InvariantCulture, out var parsed)
            && parsed is > 0 and <= 10 ? parsed : 6.0;
        var releaseYear = ReadYear(year);
        var age = releaseYear == 0 ? 26 : Math.Max(0, DateTime.UtcNow.Year - releaseYear);
        // 一年约抵 0.15 分；同分时新片优先，老片仍可凭高分排在前面。
        var weighted = score - Math.Min(age, 40) * 0.15;
        var scoreKey = Math.Clamp(100000 - (int)Math.Round(weighted * 1000), 0, 999999);
        var yearKey = releaseYear == 0 ? 9999 : 9999 - releaseYear;
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes((identity ?? string.Empty) + "|" + title)).AsSpan(0, 8));
        return $"{scoreKey:D6}-{yearKey:D4}-R{Math.Clamp(hotRank, 0, 99999):D5}-{hash}-{title}";
    }

    public static int FillExisting(string root)
    {
        if (!Directory.Exists(root)) return 0;
        var changed = 0;
        foreach (var category in Directory.EnumerateDirectories(root))
        {
            var hot = Path.GetFileName(category).Equals("近期热播", StringComparison.OrdinalIgnoreCase);
            foreach (var item in Directory.EnumerateDirectories(category))
            {
                foreach (var nfo in Directory.EnumerateFiles(item, "*.nfo", SearchOption.TopDirectoryOnly))
                {
                    if (SetForNfo(nfo, item, hot ? null : 99999)) changed++;
                }
            }
        }

        return changed;
    }

    public static bool SetForNfo(string nfo, string directory, int? hotRank)
    {
        XDocument doc;
        try { doc = XDocument.Load(nfo); }
        catch (Exception ex) when (ex is IOException or System.Xml.XmlException) { return false; }
        var root = doc.Root;
        if (root?.Name.LocalName is not ("movie" or "tvshow")) return false;

        var old = (string?)root.Element("sorttitle") ?? string.Empty;
        var rank = hotRank ?? ExistingHotRank(old);
        var title = (string?)root.Element("title") ?? Path.GetFileName(directory);
        var id = (string?)root.Elements("uniqueid").FirstOrDefault()?.Value
            ?? (string?)root.Element("yingshicangid") ?? title;
        var year = (string?)root.Element("year") ?? (string?)root.Element("premiered") ?? directory;
        var sortTitle = ForItem((string?)root.Element("rating"), year, id, title, rank);
        if (old == sortTitle) return false;

        root.SetElementValue("sorttitle", sortTitle);
        var temp = nfo + ".sorttmp";
        doc.Save(temp);
        File.Move(temp, nfo, true);
        return true;
    }

    private static int ExistingHotRank(string sortTitle)
    {
        var match = HotRankPattern.Match(sortTitle);
        if (match.Success && int.TryParse(match.Groups[1].Value, out var rank)) return rank;
        if (sortTitle.Length > 5 && int.TryParse(sortTitle[..5], out rank)) return rank;
        return 99999;
    }

    private static int ReadYear(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return 0;
        var match = YearPattern.Match(value);
        return match.Success && int.TryParse(match.Value, out var year) && year <= DateTime.UtcNow.Year + 1
            ? year : 0;
    }
}
