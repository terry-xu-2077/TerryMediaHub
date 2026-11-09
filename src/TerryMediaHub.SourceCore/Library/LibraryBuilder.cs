using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using Jellyfin.Plugin.TerryMediaHub.Configuration;
using Jellyfin.Plugin.TerryMediaHub.Health;
using Jellyfin.Plugin.TerryMediaHub.Model;
using Jellyfin.Plugin.TerryMediaHub.Runtime;
using Jellyfin.Plugin.TerryMediaHub.Sources;
using Jellyfin.Plugin.TerryMediaHub.Util;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.TerryMediaHub.Library;

/// <summary>同步结果统计。</summary>
public sealed class SyncReport
{
    public DateTime StartedUtc = DateTime.UtcNow;

    public DateTime FinishedUtc = DateTime.UtcNow;

    public int SitesSupported;

    public int SitesSynced;

    public int SitesFailed;

    public int ItemsWritten;

    public int ItemsRejected;

    public int ItemsSkipped;

    public int QualityUpgrades;

    public int Episodes;

    public List<string> Notes = new();

    public string ToSummary() =>
        $"站点 可用/同步/失败 = {SitesSupported}/{SitesSynced}/{SitesFailed}，" +
        $"写入 {ItemsWritten} 部，择优替换 {QualityUpgrades} 部，剔除失效 {ItemsRejected} 部，跳过已存在 {ItemsSkipped} 部，共 {Episodes} 集，" +
        $"耗时 {(FinishedUtc - StartedUtc).TotalSeconds:0}s";
}

/// <summary>
/// 把“源解析结果”落地成 Jellyfin 能扫描的本地媒体文件（.strm + .nfo + 海报）。
/// </summary>
public sealed class LibraryBuilder
{
    private const string MarkerName = ".terrymediahub-managed";

    private static readonly Regex JunkChars = new("[\\\\/:*?\"<>|\\x00-\\x1f]", RegexOptions.Compiled);

    private readonly ILogger _logger;
    private readonly Func<string> _proxyBaseProvider;

    public LibraryBuilder(ILogger logger, Func<string> proxyBaseProvider)
    {
        _logger = logger;
        _proxyBaseProvider = proxyBaseProvider;
    }

    public static string ResolveRoot(PluginConfiguration cfg)
    {
        if (!string.IsNullOrWhiteSpace(cfg.LibraryRoot))
        {
            return cfg.LibraryRoot.Trim();
        }

        var common = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        if (string.IsNullOrWhiteSpace(common))
        {
            common = AppContext.BaseDirectory;
        }

        return Path.Combine(common, "TerryMediaHub", "OnlineLibrary");
    }

    /// <summary>迁移旧聚合目录，并准备配置中的一级文件夹。</summary>
    public static void PrepareFolders(PluginConfiguration cfg)
    {
        var root = Path.GetFullPath(ResolveRoot(cfg));
        Directory.CreateDirectory(root);
        var oldFolder = Path.GetFullPath(Path.Combine(root, "豆瓣热播"));
        var newFolder = Path.GetFullPath(Path.Combine(root, "近期热播"));
        var prefix = root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!oldFolder.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            || !newFolder.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("分类目录超出媒体根目录");
        }

        if (cfg.CategoryFolders.Contains("近期热播", StringComparer.Ordinal)
            && Directory.Exists(oldFolder) && !Directory.Exists(newFolder))
        {
            Directory.Move(oldFolder, newFolder);
        }

        foreach (var folder in cfg.CategoryFolders.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            Directory.CreateDirectory(Path.Combine(root, SafeName(folder)));
        }
    }

    public async Task<SyncReport> BuildAsync(PluginConfiguration cfg, CancellationToken ct)
    {
        var report = new SyncReport();
        var repo = SourceRepository.Instance;

        if (repo.Sites.Count == 0)
        {
            await repo.LoadAsync(cfg, ct).ConfigureAwait(false);
        }

        var entries = repo.Sites.ToList();
        report.SitesSupported = entries.Count(e => e.Supported);

        var usable = entries
            .Where(e => e.Supported && SourceRepository.IsEnabled(e, cfg))
            .ToList();

        if (usable.Count == 0)
        {
            report.Notes.Add("没有可用的站点：请检查源配置地址，或该配置里全是 csp_/js/py 爬虫站点。");
            report.FinishedUtc = DateTime.UtcNow;
            return report;
        }

        // ---------- 0. csp_ 站点需要 JVM 运行时，先准备好（失败就跳过这些站，不影响其它源） ----------
        var cspSites = usable.Where(e => e.Kind == ResolverKind.JarSpider).ToList();
        if (cspSites.Count > 0)
        {
            SyncState.Report(6, $"准备 jar 运行时（{cspSites.Count} 个 csp 站点）");
            var ok = false;
            if (!cfg.EnableJarSpider)
            {
                report.Notes.Add($"跳过 {cspSites.Count} 个 jar 爬虫站点：设置里关闭了「启用 jar 爬虫」");
            }
            else
            {
                var host = Runtime.SpiderHost.Instance;
                host.AttachLogger(_logger);
                try
                {
                    var siteJars = cspSites
                        .Select(e => e.Site.Jar)
                        .Where(j => !string.IsNullOrWhiteSpace(j))
                        .Select(j => j!)
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToList();
                    ok = await host.PrepareAsync(cfg, repo.SpiderJarUrl, siteJars, ct).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "准备 csp 运行时失败");
                }

                if (!ok)
                {
                    report.Notes.Add($"跳过 {cspSites.Count} 个 jar 爬虫站点：{host.LastError ?? "运行时不可用"}");
                }
                else
                {
                    _logger.LogInformation("csp 运行时就绪：{Java}", host.JavaPath);
                    if (host.FailedSiteJars.Count > 0)
                    {
                        report.Notes.Add(
                            $"{host.FailedSiteJars.Count} 个站点专用 jar 下载失败（源本身的链接已失效，这些站点会跳过）："
                            + string.Join("、", host.FailedSiteJars.Take(3)));
                    }
                }
            }

            if (!ok)
            {
                usable = usable.Where(e => e.Kind != ResolverKind.JarSpider).ToList();
                report.SitesSupported -= cspSites.Count;
            }
        }

        var root = ResolveRoot(cfg);
        PrepareFolders(cfg);
        await File.WriteAllTextAsync(
            Path.Combine(root, MarkerName),
            "本目录由 Jellyfin 插件 TerryMediaHub 生成（在线媒体库），请勿手工整理，否则下次同步会被覆盖。\n",
            ct).ConfigureAwait(false);

        var probe = new LinkProbe(cfg, TimeSpan.FromHours(12));
        var proxyBase = _proxyBaseProvider();
        var probeSemaphore = new SemaphoreSlim(Math.Max(1, cfg.HealthConcurrency));
        var posterCache = new ConcurrentDictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        var written = new ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase);

        // 断点续传：站点列表指纹。站点集合变了就作废上一轮的「已完成」记录，强制重抓。
        var fingerprint = Runtime.SyncProgress.ComputeFingerprint(usable)
            + "|" + string.Join("|", cfg.CategoryFolders) + "|quality-v1";

        // ---------- 1. 逐站抓取 + 落地（完成即标记，进程被杀后下次从断点继续） ----------
        var siteIndex = 0;
        var resumedSites = false;
        var siteTimeout = TimeSpan.FromSeconds(Math.Max(20, cfg.SiteTimeoutSeconds));
        foreach (var site in usable)
        {
            ct.ThrowIfCancellationRequested();
            siteIndex++;
            SyncState.Report(10 + (int)(80.0 * siteIndex / usable.Count), $"抓取站点 {siteIndex}/{usable.Count} · {site.Name}");

            // 断点续传：上一轮已完整抓取并落地的站点直接跳过（.strm 已落地）
            if (Runtime.SyncProgress.IsSiteDone(cfg, site, fingerprint))
            {
                resumedSites = true;
                report.SitesSynced++;
                report.Notes.Add($"[{site.Name}] 断点续传，跳过（上轮已完成）");
                _logger.LogInformation("站点 {Site} 断点续传，跳过", site.Name);
                continue;
            }

            // 单个站点限时：坏源（比如 init 里自己起线程池轮询的爬虫）不能拖住整轮同步
            using var siteCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            siteCts.CancelAfter(siteTimeout);
            var siteToken = siteCts.Token;

            List<(SiteEntry Site, VodSummary Item)> siteItems = new();
            try
            {
                var ctx = new ResolveContext(site.Site, cfg);
                var cats = await site.Resolver!.GetCategoriesAsync(ctx, siteToken)
                    .WaitAsync(siteToken).ConfigureAwait(false);
                if (cats.Count == 0)
                {
                    report.SitesFailed++;
                    MarkSiteFailure(cfg, site, report, "分类为空");
                    continue;
                }

                var picked = PickCategories(cats, cfg.MaxCategoriesPerSite);
                var siteCount = 0;

                foreach (var cat in picked)
                {
                    for (var page = 1; page <= Math.Max(1, cfg.MaxPagesPerCategory); page++)
                    {
                        if (siteCount >= cfg.MaxItemsPerSite)
                        {
                            break;
                        }

                        IReadOnlyList<VodSummary> list;
                        try
                        {
                            list = await site.Resolver.GetListAsync(ctx, cat.Id, page, siteToken)
                                .WaitAsync(siteToken).ConfigureAwait(false);
                        }
                        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                        {
                            throw;
                        }
                        catch (Exception ex)
                        {
                            report.Notes.Add($"[{site.Name}/{cat.Name}/p{page}] 列表失败: {Short(ex.Message)}");
                            break;
                        }

                        if (list.Count == 0)
                        {
                            break;
                        }

                        foreach (var item in list)
                        {
                            if (siteCount >= cfg.MaxItemsPerSite)
                            {
                                break;
                            }

                            if (string.IsNullOrWhiteSpace(item.CategoryName)) item.CategoryName = cat.Name;
                            siteItems.Add((site, item));
                            siteCount++;
                        }
                    }
                }

                _logger.LogInformation("站点 {Site} 抓到 {Count} 条", site.Name, siteCount);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                report.SitesFailed++;
                MarkSiteFailure(cfg, site, report, $"超时（>{siteTimeout.TotalSeconds:F0}s）");
                _logger.LogWarning("站点 {Site} 抓取超时，已跳过", site.Name);
                continue;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                report.SitesFailed++;
                report.Notes.Add($"[{site.Name}] 站点不可用: {Short(ex.Message)}");
                continue;
            }

            // 抓取成功 → 立即落地该站点条目（去重依据磁盘上已存在的同名条目）
            using var landCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var landTimeout = TimeSpan.FromSeconds(Math.Max(siteTimeout.TotalSeconds, Math.Min(900, siteItems.Count * 2.0)));
            landCts.CancelAfter(landTimeout);
            try
            {
                await LandSiteItemsAsync(
                    siteItems, root, cfg, site.Name, probe, probeSemaphore,
                    posterCache, proxyBase, written, report, landCts.Token).WaitAsync(landCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                report.SitesFailed++;
                report.Notes.Add($"[{site.Name}] 落地超时（>{landTimeout.TotalSeconds:F0}s），本轮已写入的条目保留，下次可续传");
                _logger.LogWarning("站点 {Site} 落地超时，已跳过", site.Name);
                continue;
            }

            report.SitesSynced++;
            cfg.SiteFailCounts.RemoveAll(e => string.Equals(e.Key, site.Key, StringComparison.OrdinalIgnoreCase));

            // 落地完成 → 标记站点 done，进程被杀后下次跳过它
            Runtime.SyncProgress.MarkSiteDone(cfg, site, fingerprint);
        }

        // ---------- 2. 清理过期条目（只清理自己生成的 .strm） ----------
        // 抓取失败或断点续传时，written 不包含所有已有条目；此时清理会误删整站媒体。
        if (report.SitesFailed == 0 && !resumedSites)
        {
            CleanupStale(root, written.Keys, cfg);
        }

        try
        {
            var hotCount = await DoubanHotBuilder.RefreshAsync(root, cfg, ct).ConfigureAwait(false);
            report.Notes.Add($"豆瓣近期热播已更新：{hotCount} 部（电影、剧集混合）");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            report.Notes.Add("豆瓣近期热播更新失败，保留原内容：" + Short(ex.Message));
            _logger.LogWarning(ex, "更新豆瓣近期热播失败");
        }

        // 整轮完整跑完：清空续传记录（下次恢复全量校验强度）
        Runtime.SyncProgress.Reset(cfg);

        // 铺满一次之后，后续同步恢复正常校验强度
        cfg.LibraryPrimed = true;

        report.FinishedUtc = DateTime.UtcNow;
        return report;
    }

    /// <summary>
    /// 把单个站点抓到的条目落地（去重依据磁盘上已存在的同名条目，跨站先落地者优先）。
    /// 完成后该站点即可被断点续传跳过。
    /// </summary>
    private async Task LandSiteItemsAsync(
        List<(SiteEntry Site, VodSummary Item)> siteItems,
        string root,
        PluginConfiguration cfg,
        string siteName,
        LinkProbe probe,
        SemaphoreSlim probeSemaphore,
        ConcurrentDictionary<string, string?> posterCache,
        string proxyBase,
        ConcurrentDictionary<string, byte> written,
        SyncReport report,
        CancellationToken ct)
    {
        var deduped = siteItems.GroupBy(pair =>
                CategoryDirOf(root, pair.Item.CategoryName, cfg) + "|" + DedupKey(pair.Item),
                StringComparer.OrdinalIgnoreCase)
            .ToList();

        var landed = 0;
        var total = Math.Max(1, deduped.Count);
        SyncState.Report(90, $"落地 {siteName}（{deduped.Count} 条）");

        await Parallel.ForEachAsync(
            deduped,
            new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, cfg.HealthConcurrency), CancellationToken = ct },
            async (group, token) =>
            {
                var handled = Interlocked.Increment(ref landed);
                if (handled % 10 == 0 || handled >= total)
                {
                    SyncState.Report(90, $"落地 {siteName} {handled}/{total}");
                }

                try
                {
                    var first = group.First();
                    var item = first.Item;
                    var year = Nfo.YearOf(item);
                    var baseName = SafeName(string.IsNullOrWhiteSpace(year) ? item.Name : $"{item.Name} ({year})");

                    // 按「自带分类名」归到用户配置的大分类文件夹（类似影视仓主界面）
                    var categoryDir = CategoryDirOf(root, item.CategoryName, cfg);
                    var existDir = Path.Combine(categoryDir, baseName);

                    if (cfg.IncrementalSync && !cfg.VerifyPlayUrls && HasAnyStrm(existDir))
                    {
                        foreach (var f in ExistingStrms(existDir)) written[f] = 1;
                        Interlocked.Increment(ref report.ItemsSkipped);
                        return;
                    }

                    var existing = cfg.VerifyPlayUrls
                        ? await MediaQuality.ExistingAsync(existDir, cfg, probe, probeSemaphore, token).ConfigureAwait(false)
                        : null;
                    (SiteEntry Site, VodSummary Item, List<VodEpisode> Episodes, QualityChoice Quality)? best = null;
                    foreach (var pair in group)
                    {
                        try
                        {
                            var checkSeriesDetail = pair.Item.Episodes.Count == 1
                                && IsSeriesCategory(pair.Item.CategoryName);
                            var detailEpisodes = pair.Item.Episodes.Count == 0 || checkSeriesDetail
                                ? (await pair.Site.Resolver!.GetDetailAsync(
                                    new ResolveContext(pair.Site.Site, cfg), pair.Item.Id, token)
                                    .WaitAsync(token).ConfigureAwait(false))?.Episodes
                                : null;
                            var candidateEpisodes = detailEpisodes is { Count: > 0 }
                                ? detailEpisodes : pair.Item.Episodes;
                            if (candidateEpisodes.Count == 0) continue;
                            var quality = cfg.VerifyPlayUrls
                                ? await MediaQuality.CheckAsync(candidateEpisodes[0], candidateEpisodes.Count,
                                    probe, probeSemaphore, token).ConfigureAwait(false)
                                : new QualityChoice(true, 1, candidateEpisodes.Count, 0, 0, 0, string.Empty, 2);
                            if (!quality.Ok) continue;
                            if (best is null || MediaQuality.Better(quality, best.Value.Quality))
                                best = (pair.Site, pair.Item, candidateEpisodes, quality);
                        }
                        catch (OperationCanceledException) { throw; }
                        catch (Exception ex) { _logger.LogDebug(ex, "候选资源探测失败 {Name}", pair.Item.Name); }
                    }

                    if (best is null || cfg.VerifyPlayUrls && !MediaQuality.Better(best.Value.Quality, existing))
                    {
                        if (HasAnyStrm(existDir))
                        {
                            foreach (var f in ExistingStrms(existDir)) written[f] = 1;
                            Interlocked.Increment(ref report.ItemsSkipped);
                            return;
                        }

                        Interlocked.Increment(ref report.ItemsRejected);
                        return;
                    }

                    var site = best.Value.Site;
                    item = best.Value.Item;
                    var episodes = best.Value.Episodes;
                    var chosenUrl = episodes[0].Url;
                    var previousStrms = ExistingStrms(existDir).ToList();
                    var currentStrms = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    var preserved = ReadPreservedMetadata(existDir);
                    var poster = await GetPosterAsync(item.Pic, posterCache, cfg, token).ConfigureAwait(false);
                    var isSeries = episodes.Count > 1;

                    if (isSeries)
                    {
                        var dir = Path.Combine(categoryDir, baseName);
                        Directory.CreateDirectory(dir);
                        foreach (var stale in Directory.EnumerateFiles(dir, "*.strm", SearchOption.TopDirectoryOnly))
                        {
                            File.Delete(stale);
                        }
                        foreach (var stale in Directory.EnumerateFiles(dir, "*.nfo", SearchOption.TopDirectoryOnly)
                                     .Where(f => !Path.GetFileName(f).Equals("tvshow.nfo", StringComparison.OrdinalIgnoreCase)))
                        {
                            File.Delete(stale);
                        }
                        var showNfo = Path.Combine(dir, "tvshow.nfo");
                        await WriteIfChangedAsync(showNfo,
                            MergeMetadata(Nfo.ForShow(ToDetail(item, episodes), site.Name), preserved), token)
                            .ConfigureAwait(false);
                        MediaSort.SetForNfo(showNfo, dir, 99999);
                        SavePoster(poster, dir, token);

                        var seasonDir = Path.Combine(dir, "Season 01");
                        Directory.CreateDirectory(seasonDir);
                        var idx = 0;
                        foreach (var ep in episodes)
                        {
                            idx++;
                            var epName = $"{SafeName(item.Name)} - S01E{idx:00}";
                            var strmPath = Path.Combine(seasonDir, epName + ".strm");
                            WriteStrm(strmPath, ep.Url, proxyBase, cfg);
                            currentStrms.Add(strmPath);
                            await WriteIfChangedAsync(
                                Path.Combine(seasonDir, epName + ".nfo"),
                                Nfo.ForEpisode(ToDetail(item, episodes), ep, 1, idx),
                                token).ConfigureAwait(false);
                            written[strmPath] = 1;
                        }

                        written[Path.Combine(dir, "tvshow.nfo")] = 1;
                        Interlocked.Increment(ref report.ItemsWritten);
                        Interlocked.Add(ref report.Episodes, episodes.Count);
                    }
                    else
                    {
                        var dir = Path.Combine(categoryDir, baseName);
                        Directory.CreateDirectory(dir);
                        var stem = Path.Combine(dir, baseName);
                        WriteStrm(stem + ".strm", chosenUrl!, proxyBase, cfg);
                        var movieNfo = stem + ".nfo";
                        await WriteIfChangedAsync(movieNfo,
                            MergeMetadata(Nfo.ForMovie(ToDetail(item, episodes), site.Name), preserved), token)
                            .ConfigureAwait(false);
                        MediaSort.SetForNfo(movieNfo, dir, 99999);
                        SavePoster(poster, dir, token);
                        written[stem + ".strm"] = 1;
                        currentStrms.Add(stem + ".strm");
                        written[movieNfo] = 1;
                        Interlocked.Increment(ref report.ItemsWritten);
                        Interlocked.Increment(ref report.Episodes);
                    }

                    foreach (var stale in previousStrms.Where(f => !currentStrms.Contains(f)))
                    {
                        File.Delete(stale);
                        var nfo = Path.ChangeExtension(stale, ".nfo");
                        if (File.Exists(nfo)) File.Delete(nfo);
                    }
                    if (!isSeries)
                    {
                        var oldShowNfo = Path.Combine(existDir, "tvshow.nfo");
                        if (File.Exists(oldShowNfo)) File.Delete(oldShowNfo);
                    }

                    if (cfg.VerifyPlayUrls)
                        await MediaQuality.SaveAsync(Path.Combine(categoryDir, baseName), best.Value.Quality, token)
                            .ConfigureAwait(false);
                    if (existing?.Ok == true) Interlocked.Increment(ref report.QualityUpgrades);

                }
                catch (OperationCanceledException)
                {
                    // ignore
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "写入 {Name} 失败", group.First().Item.Name);
                }
            }).ConfigureAwait(false);
    }

    /// <summary>连续失败达到阈值就把站点拉黑，避免每次都去撞死站。</summary>
    private void MarkSiteFailure(PluginConfiguration cfg, SiteEntry site, SyncReport report, string message)
    {
        report.Notes.Add($"[{site.Name}] 站点不可用: {Short(message)}");

        var entry = cfg.SiteFailCounts.FirstOrDefault(e => string.Equals(e.Key, site.Key, StringComparison.OrdinalIgnoreCase));
        if (entry is null)
        {
            entry = new SiteFailEntry { Key = site.Key };
            cfg.SiteFailCounts.Add(entry);
        }

        var count = ++entry.Count;

        var limit = Math.Max(1, cfg.SiteFailuresToBlock);
        if (count >= limit && !cfg.AutoBlockedSiteKeys.Contains(site.Key, StringComparer.OrdinalIgnoreCase))
        {
            cfg.AutoBlockedSiteKeys.Add(site.Key);
            report.Notes.Add($"[{site.Name}] 连续 {count} 次失败，已自动拉黑（可在设置里“清除自动拉黑”）");
            _logger.LogWarning("站点 {Site} 连续 {Count} 次失败，已自动拉黑", site.Name, count);
        }
    }

    private static VodDetail ToDetail(VodSummary s, List<VodEpisode> eps) => new()
    {
        Id = s.Id,
        Name = s.Name,
        Pic = s.Pic,
        Remarks = s.Remarks,
        CategoryName = s.CategoryName,
        Year = s.Year,
        Area = s.Area,
        Lang = s.Lang,
        Actor = s.Actor,
        Director = s.Director,
        Content = s.Content,
        Score = s.Score,
        SortTitle = s.SortTitle,
        Episodes = eps,
        PlayFrom = s.PlayFrom
    };

    private static List<VodCategory> PickCategories(IReadOnlyList<VodCategory> cats, int max)
    {
        // 先取首页的热播/推荐聚合分类，再取各内容分类。
        string[] preferred = { "电影", "剧", "连续剧", "动漫", "动画", "综艺", "记录", "片" };
        var ordered = cats
            .OrderByDescending(c => IsRecentCategory(c.Name) ? 2 : preferred.Any(p => c.Name.Contains(p, StringComparison.Ordinal)) ? 1 : 0)
            .ThenBy(c => c.Id)
            .ToList();
        return ordered.Take(Math.Max(1, max)).ToList();
    }

    /// <summary>
    /// 把条目按「自带分类名」归到用户配置的大分类文件夹，并确保目录已建。
    /// 匹配规则：分类名包含大分类关键词即归入；都匹配不到就归到第一个聚合分类。
    /// </summary>
    private static string CategoryDirOf(string root, string? categoryName, PluginConfiguration cfg)
    {
        var folders = cfg.CategoryFolders ?? new List<string>();
        if (folders.Count == 0)
        {
            folders = new List<string> { "近期热播" };
        }

        var name = categoryName ?? string.Empty;
        // 精确/包含匹配：分类名命中大分类名即归入（如"国产剧"→剧集、"电影"→电影）
        foreach (var f in folders)
        {
            if (f is "近期热播" or "豆瓣热播")
            {
                continue; // 聚合分类不参与关键词匹配，留作兜底
            }

            if (name.Length > 0 && name.Contains(f, StringComparison.OrdinalIgnoreCase))
            {
                return EnsureCategoryDir(root, f);
            }
        }

        // 关键词近似匹配（"连续剧/电视剧/韩剧/美剧" → 剧集，"综艺/真人秀" → 综艺 等）
        if (name.Length > 0)
        {
            if (IsMainland(name)) { return EnsureCategoryDir(root, Pick(folders, "内地")); }
            if (IsDrama(name)) { return EnsureCategoryDir(root, Pick(folders, "剧集")); }
            if (IsVariety(name)) { return EnsureCategoryDir(root, Pick(folders, "综艺")); }
            if (IsAnime(name)) { return EnsureCategoryDir(root, Pick(folders, "动漫")); }
            if (IsMovie(name)) { return EnsureCategoryDir(root, Pick(folders, "电影")); }
        }

        // 热播目录只由豆瓣榜单维护；来源分类不明的条目暂归入电影。
        return EnsureCategoryDir(root, Pick(folders, "电影"));
    }

    private static string Pick(List<string> folders, string want)
    {
        var hit = folders.FirstOrDefault(f => f == want);
        return string.IsNullOrEmpty(hit) ? folders[0] : hit;
    }

    private static string EnsureCategoryDir(string root, string name)
    {
        var dir = Path.Combine(root, SafeName(name));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static bool IsDrama(string n) =>
        n.Contains("剧", StringComparison.Ordinal) || n.Contains("连续", StringComparison.Ordinal)
        || n.Contains("电视", StringComparison.Ordinal);

    private static bool IsSeriesCategory(string? name) =>
        !string.IsNullOrWhiteSpace(name)
        && (IsDrama(name) || IsVariety(name) || IsAnime(name));

    private static bool IsMainland(string n) =>
        n.Contains("内地", StringComparison.Ordinal) || n.Contains("大陆", StringComparison.Ordinal)
        || n.Contains("国产", StringComparison.Ordinal);

    private static bool IsVariety(string n) =>
        n.Contains("综艺", StringComparison.Ordinal) || n.Contains("真人", StringComparison.Ordinal)
        || n.Contains("脱口", StringComparison.Ordinal);

    private static bool IsAnime(string n) =>
        n.Contains("动漫", StringComparison.Ordinal) || n.Contains("动画", StringComparison.Ordinal)
        || n.Contains("番", StringComparison.Ordinal) || n.Contains("卡通", StringComparison.Ordinal);

    private static bool IsMovie(string n) =>
        n.Contains("电影", StringComparison.Ordinal) || n.Contains("影片", StringComparison.Ordinal)
        || n.Contains("影院", StringComparison.Ordinal);

    private static bool IsRecentCategory(string n) =>
        n.Contains("热播", StringComparison.Ordinal) || n.Contains("豆瓣", StringComparison.Ordinal)
        || n.Contains("最新", StringComparison.Ordinal) || n.Contains("推荐", StringComparison.Ordinal);

    private static string DedupKey(VodSummary item)
    {
        var n = item.Name ?? string.Empty;
        n = Regex.Replace(n, "[\\s\\p{P}\\p{S}]", string.Empty);
        return n + "|" + Nfo.YearOf(item);
    }

    public static string SafeName(string name)
    {
        var s = JunkChars.Replace(name, " ").Trim().TrimEnd('.');
        s = Regex.Replace(s, "\\s+", " ");
        if (s.Length == 0)
        {
            s = "未命名";
        }

        return s.Length > 90 ? s[..90].Trim() : s;
    }

    private void WriteStrm(string path, string url, string proxyBase, PluginConfiguration cfg)
    {
        var target = cfg.ProxyStreams && !string.IsNullOrWhiteSpace(proxyBase)
            ? proxyBase.TrimEnd('/') + "/TerryMediaHub/Stream/" + Token.Encode(url, Http.OriginOf(url), cfg.StreamSecret)
            : url;

        var content = target + "\n";
        try
        {
            if (File.Exists(path) && File.ReadAllText(path) == content)
            {
                return;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, content, new UTF8Encoding(false));
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "写 strm 失败 {Path}", path);
        }
    }

    private static async Task WriteIfChangedAsync(string path, string content, CancellationToken ct)
    {
        if (File.Exists(path) && await File.ReadAllTextAsync(path, ct).ConfigureAwait(false) == content)
        {
            return;
        }

        await File.WriteAllTextAsync(path, content, new UTF8Encoding(false), ct).ConfigureAwait(false);
    }

    private static (string? Rating, string? Year, string? DoubanId) ReadPreservedMetadata(string dir)
    {
        if (!Directory.Exists(dir)) return default;
        foreach (var file in Directory.EnumerateFiles(dir, "*.nfo", SearchOption.TopDirectoryOnly))
        {
            try
            {
                var root = XDocument.Load(file).Root;
                if (root?.Name.LocalName is not ("movie" or "tvshow")) continue;
                return ((string?)root.Element("rating"), (string?)root.Element("year"),
                    root.Elements("uniqueid").FirstOrDefault(x =>
                        (string?)x.Attribute("type") == "douban")?.Value);
            }
            catch (Exception ex) when (ex is IOException or System.Xml.XmlException) { }
        }

        return default;
    }

    private static string MergeMetadata(string generated,
        (string? Rating, string? Year, string? DoubanId) preserved)
    {
        if (preserved == default) return generated;
        var doc = XDocument.Parse(generated);
        var root = doc.Root!;
        if (double.TryParse(preserved.Rating, NumberStyles.Any, CultureInfo.InvariantCulture, out var rating)
            && rating is > 0 and <= 10
            && (preserved.DoubanId is not null || root.Element("rating") is null))
            root.SetElementValue("rating", preserved.Rating);
        if (root.Element("year") is null && !string.IsNullOrWhiteSpace(preserved.Year))
            root.SetElementValue("year", preserved.Year);
        if (!string.IsNullOrWhiteSpace(preserved.DoubanId)
            && !root.Elements("uniqueid").Any(x => (string?)x.Attribute("type") == "douban"))
            root.Add(new XElement("uniqueid", new XAttribute("type", "douban"), preserved.DoubanId));
        return "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n" + root + "\n";
    }

    private static void SavePoster(string? localPath, string dir, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(localPath) || !File.Exists(localPath))
        {
            return;
        }

        var target = Path.Combine(dir, "poster.jpg");
        if (File.Exists(target))
        {
            return;
        }

        try
        {
            File.Copy(localPath, target, true);
        }
        catch
        {
            // ignore
        }
    }

    private async Task<string?> GetPosterAsync(
        string? url,
        ConcurrentDictionary<string, string?> cache,
        PluginConfiguration cfg,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return null;
        }

        if (cache.TryGetValue(url, out var cached))
        {
            return cached;
        }

        var dir = Path.Combine(Path.GetTempPath(), "terrymediahub-posters");
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, Math.Abs(url.GetHashCode(StringComparison.Ordinal)).ToString("x8") + ".jpg");

        try
        {
            if (!File.Exists(file))
            {
                using var req = Http.BuildRequest(url, cfg.UserAgent, Http.OriginOf(url));
                using var resp = await Http.Client.SendAsync(req, HttpCompletionOption.ResponseContentRead, ct)
                    .ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode)
                {
                    cache[url] = null;
                    return null;
                }

                var bytes = await resp.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
                if (bytes.Length < 1024)
                {
                    cache[url] = null;
                    return null;
                }

                await File.WriteAllBytesAsync(file, bytes, ct).ConfigureAwait(false);
            }

            cache[url] = file;
            return file;
        }
        catch
        {
            cache[url] = null;
            return null;
        }
    }

    private void CleanupStale(string dir, IEnumerable<string> keep, PluginConfiguration cfg)
    {
        try
        {
            if (!Directory.Exists(dir))
            {
                return;
            }

            var keepSet = new HashSet<string>(keep, StringComparer.OrdinalIgnoreCase);
            foreach (var strm in Directory.EnumerateFiles(dir, "*.strm", SearchOption.AllDirectories))
            {
                if (strm.StartsWith(Path.Combine(dir, "近期热播") + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                if (HasPreserveMarker(strm, dir)) continue;
                if (!keepSet.Contains(strm))
                {
                    File.Delete(strm);
                    var nfo = Path.ChangeExtension(strm, ".nfo");
                    if (File.Exists(nfo))
                    {
                        File.Delete(nfo);
                    }
                }
            }

            // 整个目录里已经没有 .strm 了，说明条目已失效/下架，连海报一起清掉
            foreach (var sub in Directory.EnumerateDirectories(dir, "*", SearchOption.AllDirectories)
                         .OrderByDescending(d => d.Length))
            {
                if (string.Equals(Path.GetDirectoryName(sub), dir, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                if (HasPreserveMarker(sub, dir)) continue;

                if (!Directory.EnumerateFiles(sub, "*.strm", SearchOption.AllDirectories).Any())
                {
                    Directory.Delete(sub, true);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "清理过期条目失败");
        }
    }

    private static string Short(string s) => s.Length > 120 ? s[..120] : s;

    private static bool HasPreserveMarker(string path, string root)
    {
        var directory = Directory.Exists(path) ? path : Path.GetDirectoryName(path);
        while (directory is not null && !string.Equals(directory, root, StringComparison.OrdinalIgnoreCase))
        {
            if (File.Exists(Path.Combine(directory, ".terrymediahub-preserve"))) return true;
            directory = Path.GetDirectoryName(directory);
        }

        return false;
    }

    /// <summary>目录里已经有 .strm 就算入库过（增量同步用）。</summary>
    private static bool HasAnyStrm(string dir)
    {
        try
        {
            return Directory.Exists(dir)
                   && Directory.EnumerateFiles(dir, "*.strm", SearchOption.AllDirectories).Any();
        }
        catch
        {
            return false;
        }
    }

    private static IEnumerable<string> ExistingStrms(string dir)
    {
        try
        {
            return Directory.Exists(dir)
                ? Directory.EnumerateFiles(dir, "*.strm", SearchOption.AllDirectories).ToList()
                : new List<string>();
        }
        catch
        {
            return new List<string>();
        }
    }

    /// <summary>从最多 3 集里挑一个能播的；并行探测，别串行等三个超时。</summary>
    private static async Task<VodEpisode?> PickPlayableAsync(
        IReadOnlyList<VodEpisode> episodes,
        LinkProbe probe,
        SemaphoreSlim gate,
        CancellationToken ct)
    {
        var tasks = episodes.Take(3).Select(async ep =>
        {
            await gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                var result = await probe.CheckAsync(ep.Url, ct).ConfigureAwait(false);
                return (Episode: ep, Ok: result.Ok);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                return (Episode: ep, Ok: false);
            }
            finally
            {
                gate.Release();
            }
        }).ToList();

        if (tasks.Count == 0)
        {
            return null;
        }

        var results = await Task.WhenAll(tasks).ConfigureAwait(false);
        foreach (var r in results)
        {
            if (r.Ok)
            {
                return r.Episode;
            }
        }

        return null;
    }
}
