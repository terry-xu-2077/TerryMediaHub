using System;
using System.IO;
using System.Globalization;
using System.Xml.Linq;

namespace Jellyfin.Plugin.TerryMediaHub.Library;

/// <summary>热播榜保留豆瓣位次作为同分同年的次序。</summary>
public static class HotSort
{
    public static bool SetDoubanRank(string directory, int rank, string title, string? rate = null)
    {
        var found = false;
        foreach (var nfo in Directory.EnumerateFiles(directory, "*.nfo", SearchOption.TopDirectoryOnly))
        {
            XDocument doc;
            try { doc = XDocument.Load(nfo); }
            catch (Exception ex) when (ex is IOException or System.Xml.XmlException) { continue; }
            if (doc.Root?.Name.LocalName is not ("movie" or "tvshow")) continue;
            if (double.TryParse(rate, NumberStyles.Any, CultureInfo.InvariantCulture, out var rating)
                && rating is > 0 and <= 10)
            {
                doc.Root.SetElementValue("rating", rating.ToString("0.0", CultureInfo.InvariantCulture));
                doc.Save(nfo);
            }
            MediaSort.SetForNfo(nfo, directory, rank);
            found = true;
        }

        return found;
    }

    /// <summary>兼容旧调用；同时更新在线媒体的全部分类。</summary>
    public static void FillExisting(string root) => MediaSort.FillExisting(root);
}
