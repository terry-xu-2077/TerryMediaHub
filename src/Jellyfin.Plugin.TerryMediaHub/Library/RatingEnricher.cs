using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.TerryMediaHub.Runtime;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Hosting;

namespace Jellyfin.Plugin.TerryMediaHub.Library;

/// <summary>复用已安装 MetaShark 的豆瓣客户端，仅补评分和缺失年份。</summary>
public sealed class RatingEnricher : IHostedService
{
    private static readonly Regex DoubanIdPattern = new(@"\(Douban-(\d+)\)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex YearPattern = new(@"(?<!\d)(19\d{2}|20\d{2})(?!\d)", RegexOptions.Compiled);
    private readonly ILibraryManager _libraryManager;
    private readonly IServiceProvider _services;
    private readonly ILogger<RatingEnricher> _logger;
    private readonly object _stateLock = new();
    private bool _running;
    private int _total;
    private int _checked;
    private int _filled;
    private int _failed;
    private string _summary = "尚未补全评分";
    private static string PendingPath => Path.Combine(
        Path.GetDirectoryName(LibraryBuilder.ResolveRoot(Plugin.Instance!.Configuration))!,
        "rating-enrichment.pending");
    private static string CheckpointPath => Path.Combine(
        Path.GetDirectoryName(LibraryBuilder.ResolveRoot(Plugin.Instance!.Configuration))!,
        "rating-enrichment.checked");

    public RatingEnricher(ILibraryManager libraryManager, IServiceProvider services, ILogger<RatingEnricher> logger)
    {
        _libraryManager = libraryManager;
        _services = services;
        _logger = logger;
    }

    public object Status
    {
        get { lock (_stateLock) return new { running = _running, total = _total, checkedCount = _checked,
            filled = _filled, failed = _failed, summary = _summary }; }
    }

    public bool Start(int limit = 0)
    {
        lock (_stateLock)
        {
            if (_running) return false;
            _running = true;
            _total = _checked = _filled = _failed = 0;
            _summary = "正在通过 MetaShark 查询豆瓣评分";
        }

        if (limit == 0)
        {
            if (!File.Exists(PendingPath) && File.Exists(CheckpointPath)) File.Delete(CheckpointPath);
            File.WriteAllText(PendingPath, "MetaShark 评分补全正在进行\n");
        }
        _ = Task.Run(() => RunAsync(limit, CancellationToken.None));
        return true;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (File.Exists(PendingPath)) Start();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "无法恢复上次的评分补全任务");
        }
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private async Task RunAsync(int limit, CancellationToken ct)
    {
        var completed = false;
        try
        {
            var apiType = AppDomain.CurrentDomain.GetAssemblies()
                .FirstOrDefault(a => a.GetName().Name == "Jellyfin.Plugin.MetaShark")
                ?.GetType("Jellyfin.Plugin.MetaShark.Api.DoubanApi");
            if (apiType is null) throw new InvalidOperationException("未找到 MetaShark 豆瓣客户端类型");
            var api = _services.GetService(apiType)
                ?? throw new InvalidOperationException("MetaShark 豆瓣服务未注册");
            var suggest = apiType.GetMethod("SearchBySuggestAsync", new[] { typeof(string), typeof(CancellationToken) });
            var detail = apiType.GetMethod("GetMovieAsync", new[] { typeof(string), typeof(CancellationToken) });
            if (suggest is null || detail is null) throw new InvalidOperationException("MetaShark 豆瓣接口版本不兼容");

            var root = Path.GetFullPath(LibraryBuilder.ResolveRoot(Plugin.Instance!.Configuration))
                .TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            var alreadyChecked = limit == 0 && File.Exists(CheckpointPath)
                ? File.ReadAllLines(CheckpointPath).ToHashSet(StringComparer.OrdinalIgnoreCase)
                : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var work = _libraryManager.GetItemList(new InternalItemsQuery
                { IncludeItemTypes = new[] { BaseItemKind.Movie, BaseItemKind.Series } })
                .Select(item => (Item: item, Directory: MediaDirectory(item, root)))
                .Where(x => x.Directory is not null)
                .Select(x => (x.Item, Nfo: MediaNfo(x.Directory!)))
                .Where(x => x.Nfo is not null)
                .Select(x => (x.Item, x.Nfo!.Value.Path, x.Nfo.Value.Doc,
                    Year: x.Item.ProductionYear ?? ParseYear((string?)x.Nfo.Value.Doc.Root?.Element("year"))
                        ?? ParseYear(x.Nfo.Value.Path),
                    DoubanId: DoubanIdPattern.Match(x.Nfo.Value.Path).Groups[1].Value))
                .Where(x => !HasRating(x.Doc))
                .Where(x => !alreadyChecked.Contains(x.Path))
                .OrderByDescending(x => x.DoubanId.Length > 0)
                .ThenByDescending(x => x.Year ?? 0)
                .ToList();
            if (limit > 0) work = work.Take(limit).ToList();
            lock (_stateLock) _total = work.Count;

            var cache = new Dictionary<string, (float Rating, int? Year)>(StringComparer.Ordinal);
            foreach (var entry in work)
            {
                ct.ThrowIfCancellationRequested();
                while (SyncState.Running) await Task.Delay(2000, ct).ConfigureAwait(false);
                var checkedSuccessfully = true;
                try
                {
                    var title = (string?)entry.Doc.Root?.Element("title") ?? entry.Item.Name;
                    var id = entry.DoubanId;
                    if (id.Length == 0)
                    {
                        var suggestions = await InvokeAsync(suggest, api, title, ct).ConfigureAwait(false) as IEnumerable;
                        var matches = (suggestions?.Cast<object>() ?? Enumerable.Empty<object>())
                            .Where(s => Normalize(ReadString(s, "Name")) == Normalize(title))
                            .Where(s => !entry.Year.HasValue || ReadInt(s, "Year") == entry.Year)
                            .Select(s => ReadString(s, "Sid"))
                            .Where(s => !string.IsNullOrWhiteSpace(s))
                            .Distinct(StringComparer.Ordinal)
                            .ToList();
                        if (matches.Count == 1) id = matches[0];
                    }

                    if (id.Length > 0)
                    {
                        if (!cache.TryGetValue(id, out var result))
                        {
                            var subject = await InvokeAsync(detail, api, id, ct).ConfigureAwait(false);
                            var subjectName = subject is null ? string.Empty : ReadString(subject, "Name");
                            var subjectYear = subject is null ? 0 : ReadInt(subject, "Year");
                            var score = subject is null ? 0 : ReadFloat(subject, "Rating");
                            result = Normalize(subjectName) == Normalize(title)
                                && (!entry.Year.HasValue || subjectYear == 0 || subjectYear == entry.Year)
                                && score is > 0 and <= 10
                                ? (score, subjectYear > 0 ? subjectYear : null) : (0, null);
                            cache[id] = result;
                        }

                        if (result.Rating > 0)
                        {
                            if (await SaveAsync(entry.Item, entry.Path, id, result, ct).ConfigureAwait(false))
                                lock (_stateLock) _filled++;
                        }
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogDebug(ex, "查询豆瓣评分失败：{Name}", entry.Item.Name);
                    checkedSuccessfully = false;
                    lock (_stateLock) _failed++;
                }
                finally
                {
                    if (limit == 0 && checkedSuccessfully)
                        File.AppendAllText(CheckpointPath, entry.Path + "\n");
                    lock (_stateLock) _checked++;
                }
            }

            lock (_stateLock) _summary = $"已检查 {_checked} 部，补全 {_filled} 部，查询失败 {_failed} 部";
            completed = true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "MetaShark 评分补全失败");
            lock (_stateLock) _summary = "评分补全失败：" + ex.Message;
        }
        finally
        {
            if (completed && limit == 0)
            {
                if (File.Exists(PendingPath)) File.Delete(PendingPath);
                if (File.Exists(CheckpointPath)) File.Delete(CheckpointPath);
            }
            lock (_stateLock) _running = false;
        }
    }

    private static async Task<object?> InvokeAsync(MethodInfo method, object target, string value, CancellationToken ct)
    {
        var task = (Task?)method.Invoke(target, new object[] { value, ct })
            ?? throw new InvalidOperationException("MetaShark 未返回查询任务");
        await task.WaitAsync(ct).ConfigureAwait(false);
        return task.GetType().GetProperty("Result")?.GetValue(task);
    }

    private static async Task<bool> SaveAsync(BaseItem item, string nfo, string id,
        (float Rating, int? Year) result, CancellationToken ct)
    {
        var doc = XDocument.Load(nfo);
        var root = doc.Root!;
        if (HasRating(doc)) return false;
        root.SetElementValue("rating", result.Rating.ToString("0.0", CultureInfo.InvariantCulture));
        if (root.Element("year") is null && result.Year.HasValue) root.SetElementValue("year", result.Year.Value);
        if (!root.Elements("uniqueid").Any(e => (string?)e.Attribute("type") == "douban"))
            root.Add(new XElement("uniqueid", new XAttribute("type", "douban"), id));
        var temp = nfo + ".ratingtmp";
        doc.Save(temp);
        File.Move(temp, nfo, true);
        MediaSort.SetForNfo(nfo, Path.GetDirectoryName(nfo)!, null);
        var sort = (string?)XDocument.Load(nfo).Root?.Element("sorttitle");
        item.CommunityRating = result.Rating;
        if (!item.ProductionYear.HasValue && result.Year.HasValue) item.ProductionYear = result.Year;
        if (!string.IsNullOrWhiteSpace(sort)) item.ForcedSortName = sort;
        await item.UpdateToRepositoryAsync(ItemUpdateType.MetadataEdit, ct).ConfigureAwait(false);
        return true;
    }

    private static string? MediaDirectory(BaseItem item, string root)
    {
        if (string.IsNullOrWhiteSpace(item.Path)) return null;
        var path = Path.GetFullPath(item.Path);
        if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase)) return null;
        return Directory.Exists(path) ? path : Path.GetDirectoryName(path);
    }

    private static (string Path, XDocument Doc)? MediaNfo(string directory)
    {
        if (!Directory.Exists(directory)) return null;
        foreach (var path in Directory.EnumerateFiles(directory, "*.nfo", SearchOption.TopDirectoryOnly))
        {
            try
            {
                var doc = XDocument.Load(path);
                if (doc.Root?.Name.LocalName is "movie" or "tvshow") return (path, doc);
            }
            catch (Exception ex) when (ex is IOException or System.Xml.XmlException) { }
        }

        return null;
    }

    private static bool HasRating(XDocument doc) => float.TryParse((string?)doc.Root?.Element("rating"),
        NumberStyles.Any, CultureInfo.InvariantCulture, out var value) && value is > 0 and <= 10;
    private static string Normalize(string value) => string.Concat(value.Where(char.IsLetterOrDigit)).ToLowerInvariant();
    private static int? ParseYear(string? value) => int.TryParse(YearPattern.Match(value ?? string.Empty).Value, out var year) ? year : null;
    private static string ReadString(object item, string name) => item.GetType().GetProperty(name)?.GetValue(item)?.ToString() ?? string.Empty;
    private static int ReadInt(object item, string name) => int.TryParse(ReadString(item, name), out var value) ? value : 0;
    private static float ReadFloat(object item, string name) => float.TryParse(ReadString(item, name), NumberStyles.Any,
        CultureInfo.InvariantCulture, out var value) ? value : 0;
}
