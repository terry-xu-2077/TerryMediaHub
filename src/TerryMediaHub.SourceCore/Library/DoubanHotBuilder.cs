using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using Jellyfin.Plugin.TerryMediaHub.Configuration;
using Jellyfin.Plugin.TerryMediaHub.Model;
using Jellyfin.Plugin.TerryMediaHub.Runtime;
using Jellyfin.Plugin.TerryMediaHub.Sources;
using Jellyfin.Plugin.TerryMediaHub.Util;

namespace Jellyfin.Plugin.TerryMediaHub.Library;

/// <summary>用豆瓣当前热门电影和剧集榜单筛选本地可播放条目。</summary>
public static class DoubanHotBuilder
{
    private const string Referer = "https://movie.douban.com/";

    public static async Task<int> RefreshAsync(string root, PluginConfiguration cfg, CancellationToken ct)
    {
        var movies = await FetchAsync("movie", cfg.UserAgent, ct).ConfigureAwait(false);
        var shows = await FetchAsync("tv", cfg.UserAgent, ct).ConfigureAwait(false);
        if (movies.Count == 0 || shows.Count == 0)
        {
            throw new InvalidOperationException("豆瓣热门电影或剧集榜单为空");
        }

        var ranked = new List<(string Title, string Id, string Rate)>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < Math.Max(movies.Count, shows.Count); i++)
        {
            if (i < shows.Count && seen.Add(Normalize(shows[i].Title))) ranked.Add(shows[i]);
            if (i < movies.Count && seen.Add(Normalize(movies[i].Title))) ranked.Add(movies[i]);
        }

        var backupRoot = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(root))!, "LegacyBackup");
        var movieBackups = Directory.Exists(backupRoot)
            ? Directory.EnumerateDirectories(backupRoot, "MovieOverflow-*").OrderByDescending(x => x).ToList()
            : new List<string>();
        var categories = Directory.EnumerateDirectories(root)
            .Where(d => !Path.GetFileName(d).Equals("近期热播", StringComparison.OrdinalIgnoreCase))
            .Concat(movieBackups).ToList();
        var candidates = categories.SelectMany(Directory.EnumerateDirectories)
            .GroupBy(d => Normalize(Path.GetFileName(d)), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);

        var parent = Path.GetDirectoryName(Path.GetFullPath(root))
            ?? throw new InvalidOperationException("媒体根目录无效");
        var stage = Path.Combine(parent, "DoubanHotStage-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stage);
        var matched = 0;
        var matchedMovies = 0;
        var matchedShows = 0;

        foreach (var (title, id, rate) in ranked)
        {
            ct.ThrowIfCancellationRequested();
            if (!candidates.TryGetValue(Normalize(title), out var found)) continue;
            var source = found.FirstOrDefault(d => Directory.EnumerateFiles(d, "*.strm", SearchOption.AllDirectories).Any());
            if (source is null) continue;

            var destination = Path.Combine(stage, LibraryBuilder.SafeName(title) + " (Douban-" + id + ")");
            CopyDirectory(source, destination, ct);
            if (shows.Any(x => x.Id == id))
            {
                await CompleteShowEpisodesAsync(destination, cfg, ct).ConfigureAwait(false);
            }
            if (!HotSort.SetDoubanRank(destination, ranked.IndexOf((title, id, rate)) + 1, title, rate))
            {
                continue;
            }

            matched++;
            if (shows.Any(x => x.Id == id)) matchedShows++; else matchedMovies++;
        }

        if (matched < 10 || matchedMovies < 3 || matchedShows < 3)
        {
            throw new InvalidOperationException($"豆瓣榜单仅匹配到 {matched} 部（电影 {matchedMovies}、剧集 {matchedShows}），暂不替换原目录");
        }

        var hot = Path.Combine(root, "近期热播");
        Directory.CreateDirectory(backupRoot);
        var backup = Path.Combine(backupRoot, "RecentHot-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss"));
        var movedOld = false;
        try
        {
            if (Directory.Exists(hot))
            {
                Directory.Move(hot, backup);
                movedOld = true;
            }

            Directory.Move(stage, hot);
        }
        catch
        {
            if (movedOld && !Directory.Exists(hot)) Directory.Move(backup, hot);
            throw;
        }

        CurateMovies(root, cfg.MaxMovieTitles, ct);
        return matched;
    }

    /// <summary>列表接口可能只返回首集；热播剧集以详情接口的完整分集为准。</summary>
    private static async Task CompleteShowEpisodesAsync(string destination, PluginConfiguration cfg, CancellationToken ct)
    {
        var existing = Directory.EnumerateFiles(destination, "*.strm", SearchOption.AllDirectories).Count();
        if (existing > 1) return;

        var nfo = Directory.EnumerateFiles(destination, "*.nfo", SearchOption.TopDirectoryOnly).FirstOrDefault();
        if (nfo is null) return;
        var xml = XDocument.Load(nfo);
        var studio = xml.Root?.Element("studio")?.Value;
        var vodId = xml.Root?.Element("yingshicangid")?.Value
            ?? xml.Root?.Elements("uniqueid").FirstOrDefault(e => (string?)e.Attribute("type") == "yingshicang")?.Value;
        if (string.IsNullOrWhiteSpace(studio) || string.IsNullOrWhiteSpace(vodId)) return;

        var repo = SourceRepository.Instance;
        if (repo.Sites.Count == 0) await repo.LoadAsync(cfg, ct).ConfigureAwait(false);
        var site = repo.Sites.FirstOrDefault(s => s.Name == studio && s.Supported && SourceRepository.IsEnabled(s, cfg));
        if (site?.Resolver is null) return;

        try
        {
            if (site.Kind == ResolverKind.JarSpider && !SpiderHost.Instance.Ready)
            {
                if (!cfg.EnableJarSpider || !await SpiderHost.Instance.PrepareAsync(
                    cfg, repo.SpiderJarUrl, new[] { site.Site.Jar ?? string.Empty }, ct).ConfigureAwait(false)) return;
            }

            var detail = await site.Resolver.GetDetailAsync(new ResolveContext(site.Site, cfg), vodId, ct)
                .WaitAsync(ct).ConfigureAwait(false);
            if (detail is null || detail.Episodes.Count <= existing) return;

            var seasonDir = Path.Combine(destination, "Season 01");
            Directory.CreateDirectory(seasonDir);
            var title = xml.Root?.Element("title")?.Value ?? detail.Name;
            var baseUrl = Address.Resolve(cfg, null);
            for (var i = 0; i < detail.Episodes.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var episode = detail.Episodes[i];
                var stem = Path.Combine(seasonDir, $"{LibraryBuilder.SafeName(title)} - S01E{i + 1:00}");
                var target = cfg.ProxyStreams && !string.IsNullOrWhiteSpace(baseUrl)
                    ? baseUrl.TrimEnd('/') + "/TerryMediaHub/Stream/" + Token.Encode(episode.Url, Http.OriginOf(episode.Url), cfg.StreamSecret)
                    : episode.Url;
                await File.WriteAllTextAsync(stem + ".strm", target + "\n", new UTF8Encoding(false), ct).ConfigureAwait(false);
                await File.WriteAllTextAsync(stem + ".nfo", Nfo.ForEpisode(detail, episode, 1, i + 1),
                    new UTF8Encoding(false), ct).ConfigureAwait(false);
            }

            // 单集曾被当作电影写在根目录；转为剧集后移除这份重复播放文件。
            foreach (var file in Directory.EnumerateFiles(destination, "*.strm", SearchOption.TopDirectoryOnly)) File.Delete(file);
            foreach (var file in Directory.EnumerateFiles(destination, "*.nfo", SearchOption.TopDirectoryOnly)
                         .Where(f => !Path.GetFileName(f).Equals("tvshow.nfo", StringComparison.OrdinalIgnoreCase))) File.Delete(file);
            if (!File.Exists(Path.Combine(destination, "tvshow.nfo")))
            {
                await File.WriteAllTextAsync(Path.Combine(destination, "tvshow.nfo"), Nfo.ForShow(detail, studio),
                    new UTF8Encoding(false), ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception) { /* 单站点详情失效时保留已有剧集，不影响榜单更新。 */ }
    }

    private static async Task<List<(string Title, string Id, string Rate)>> FetchAsync(string type, string userAgent, CancellationToken ct)
    {
        var url = $"https://movie.douban.com/j/search_subjects?type={type}&tag=%E7%83%AD%E9%97%A8&sort=recommend&page_limit=100&page_start=0";
        var json = await Http.GetStringAsync(url, userAgent, Referer, ct).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.GetProperty("subjects").EnumerateArray()
            .Select(x => (Title: x.GetProperty("title").GetString() ?? string.Empty,
                Id: x.GetProperty("id").GetString() ?? string.Empty,
                Rate: x.TryGetProperty("rate", out var rate) ? rate.GetString() ?? string.Empty : string.Empty))
            .Where(x => x.Title.Length > 0 && x.Id.Length > 0)
            .ToList();
    }

    private static string Normalize(string title)
    {
        title = Regex.Replace(title, @"\s*\(\d{4}\)$", string.Empty);
        return string.Concat(title.Where(char.IsLetterOrDigit)).ToLowerInvariant();
    }

    private static void CurateMovies(string root, int maxTitles, CancellationToken ct)
    {
        var movie = Path.Combine(root, "电影");
        if (!Directory.Exists(movie)) return;
        var directories = Directory.EnumerateDirectories(movie).ToList();
        var limit = Math.Clamp(maxTitles <= 0 ? 400 : maxTitles, 50, 2000);
        if (directories.Count <= limit) return;

        var ranked = directories.Select(d => (Path: d, Key: MovieSortKey(d)))
            .OrderBy(x => x.Key, StringComparer.Ordinal)
            .ThenBy(x => x.Path, StringComparer.OrdinalIgnoreCase).ToList();
        var parent = Path.GetDirectoryName(Path.GetFullPath(root))!;
        var backupRoot = Path.Combine(parent, "LegacyBackup");
        var backup = Directory.Exists(backupRoot)
            ? Directory.EnumerateDirectories(backupRoot, "MovieOverflow-*")
                .OrderByDescending(x => x).FirstOrDefault()
            : null;
        backup ??= Path.Combine(backupRoot, "MovieOverflow-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss"));
        Directory.CreateDirectory(backup);
        foreach (var entry in ranked.Skip(limit))
        {
            ct.ThrowIfCancellationRequested();
            var target = Path.Combine(backup, Path.GetFileName(entry.Path));
            if (!Directory.Exists(target))
            {
                Directory.Move(entry.Path, target);
                continue;
            }

            var old = target + ".previous";
            if (Directory.Exists(old)) Directory.Delete(old, true);
            Directory.Move(target, old);
            try { Directory.Move(entry.Path, target); }
            catch
            {
                if (!Directory.Exists(target)) Directory.Move(old, target);
                throw;
            }
            Directory.Delete(old, true);
        }
    }

    private static string MovieSortKey(string directory)
    {
        var nfo = Directory.EnumerateFiles(directory, "*.nfo", SearchOption.TopDirectoryOnly).FirstOrDefault();
        if (nfo is null) return MediaSort.ForItem(null, directory, directory, Path.GetFileName(directory));
        try
        {
            var root = XDocument.Load(nfo).Root;
            var title = (string?)root?.Element("title") ?? Path.GetFileName(directory);
            var year = (string?)root?.Element("year") ?? (string?)root?.Element("premiered") ?? directory;
            var id = (string?)root?.Elements("uniqueid").FirstOrDefault()?.Value ?? title;
            return MediaSort.ForItem((string?)root?.Element("rating"), year, id, title);
        }
        catch (Exception ex) when (ex is IOException or System.Xml.XmlException)
        {
            return MediaSort.ForItem(null, directory, directory, Path.GetFileName(directory));
        }
    }

    private static void CopyDirectory(string source, string destination, CancellationToken ct)
    {
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            ct.ThrowIfCancellationRequested();
            var relative = Path.GetRelativePath(source, file);
            var target = Path.Combine(destination, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, true);
        }
    }
}
