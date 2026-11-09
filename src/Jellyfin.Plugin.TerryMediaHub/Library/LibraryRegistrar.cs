using System;
using System.Collections.Generic;
using System.IO;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using Jellyfin.Plugin.TerryMediaHub.Configuration;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.TerryMediaHub.Library;

/// <summary>
/// 在 Jellyfin 里自动创建 / 维护媒体库（挂到插件生成的目录上）。
/// </summary>
public sealed class LibraryRegistrar
{
    private readonly ILibraryManager _libraryManager;
    private readonly ILogger<LibraryRegistrar> _logger;

    public LibraryRegistrar(ILibraryManager libraryManager, ILogger<LibraryRegistrar> logger)
    {
        _libraryManager = libraryManager;
        _logger = logger;
    }

    public async Task<bool> EnsureAsync(PluginConfiguration cfg)
    {
        var root = LibraryBuilder.ResolveRoot(cfg);
        Directory.CreateDirectory(root);

        try
        {
            var folders = _libraryManager.GetVirtualFolders();
            var existing = folders.FirstOrDefault(f =>
                string.Equals(f.Name, cfg.LibraryName, StringComparison.OrdinalIgnoreCase));

            var byPath = folders.FirstOrDefault(f => f.Locations?.Any(l =>
                string.Equals(Normalize(l), Normalize(root), StringComparison.OrdinalIgnoreCase)) == true);
            if (existing is null && byPath is not null)
            {
                _logger.LogWarning("媒体目录 {Root} 已属于媒体库 {Name}，请先将该库改名为 {Target}", root, byPath.Name, cfg.LibraryName);
                return false;
            }

            if (existing is not null)
            {
                var hasPath = existing.Locations?.Any(l =>
                    string.Equals(Normalize(l), Normalize(root), StringComparison.OrdinalIgnoreCase)) == true;
                if (!hasPath)
                {
                    _libraryManager.AddMediaPath(
                        cfg.LibraryName,
                        new MediaPathInfo(root));
                    _logger.LogInformation("已把 {Root} 追加到媒体库 {Name}", root, cfg.LibraryName);
                }

                return true;
            }

            var options = new LibraryOptions
            {
                PathInfos = new[] { new MediaPathInfo(root) },
                EnableRealtimeMonitor = false,
                SaveLocalMetadata = true,
                EnableAutomaticSeriesGrouping = false,
                MetadataCountryCode = "CN",
                PreferredMetadataLanguage = "zh-CN"
            };

            if (cfg.DisableInternetMetadata)
            {
                // 只信本地 NFO，别让 TMDb 拖慢扫描（国内多半连不上）
                options.TypeOptions = new[]
                {
                    NoFetchers("Movie"),
                    NoFetchers("Series"),
                    NoFetchers("Season"),
                    NoFetchers("Episode")
                };
            }

            await _libraryManager.AddVirtualFolder(
                cfg.LibraryName,
                CollectionTypeOptions.mixed,
                options,
                true).ConfigureAwait(false);

            _logger.LogInformation("已创建媒体库 {Name} -> {Root}", cfg.LibraryName, root);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "创建媒体库失败（{Root}）", root);
            return false;
        }
    }

    /// <summary>触发一次媒体库扫描，让新生成的 .strm 立刻出现在库里。</summary>
    public async Task RefreshAsync(CancellationToken ct)
    {
        try
        {
            await _libraryManager.ValidateMediaLibrary(new Progress<double>(), ct).ConfigureAwait(false);
            await ApplySortNamesAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "重建媒体库失败");
            throw;
        }
    }

    private async Task ApplySortNamesAsync(CancellationToken ct)
    {
        var cfg = Plugin.Instance!.Configuration;
        var root = Path.GetFullPath(LibraryBuilder.ResolveRoot(cfg))
            .TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var folders = _libraryManager.GetItemList(new InternalItemsQuery
        {
            IncludeItemTypes = new[] { BaseItemKind.Folder }
        });
        for (var index = 0; index < cfg.CategoryFolders.Count; index++)
        {
            var name = cfg.CategoryFolders[index];
            var path = Path.GetFullPath(Path.Combine(root, name));
            var folder = folders.FirstOrDefault(f => !string.IsNullOrWhiteSpace(f.Path)
                && string.Equals(Path.GetFullPath(f.Path), path, StringComparison.OrdinalIgnoreCase));
            if (folder is null) continue;
            var sortName = $"{index:D2}-{name}";
            if (folder.ForcedSortName == sortName) continue;
            folder.ForcedSortName = sortName;
            await folder.UpdateToRepositoryAsync(ItemUpdateType.MetadataEdit, ct).ConfigureAwait(false);
        }

        var items = _libraryManager.GetItemList(new InternalItemsQuery
        {
            IncludeItemTypes = new[] { BaseItemKind.Movie, BaseItemKind.Series }
        });
        var updated = 0;
        foreach (var item in items)
        {
            ct.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(item.Path)) continue;
            var path = Path.GetFullPath(item.Path);
            if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase)) continue;
            var directory = Directory.Exists(path) ? path : Path.GetDirectoryName(path);
            if (directory is null || !Directory.Exists(directory)) continue;

            var metadata = ReadSortMetadata(directory);
            if (string.IsNullOrWhiteSpace(metadata.SortTitle)) continue;
            var changed = false;
            if (item.ForcedSortName != metadata.SortTitle)
            {
                item.ForcedSortName = metadata.SortTitle;
                changed = true;
            }
            if (metadata.Rating is > 0 && item.CommunityRating != metadata.Rating)
            {
                item.CommunityRating = metadata.Rating;
                changed = true;
            }
            if (metadata.Year is > 0 && item.ProductionYear != metadata.Year)
            {
                item.ProductionYear = metadata.Year;
                changed = true;
            }
            if (!changed) continue;
            await item.UpdateToRepositoryAsync(ItemUpdateType.MetadataEdit, ct).ConfigureAwait(false);
            updated++;
        }

        _logger.LogInformation("在线媒体已更新 {Count} 个排序字段", updated);
    }

    private static (string? SortTitle, float? Rating, int? Year) ReadSortMetadata(string directory)
    {
        foreach (var nfo in Directory.EnumerateFiles(directory, "*.nfo", SearchOption.TopDirectoryOnly))
        {
            try
            {
                var root = XDocument.Load(nfo).Root;
                if (root?.Name.LocalName is "movie" or "tvshow")
                {
                    var rating = float.TryParse((string?)root.Element("rating"), NumberStyles.Any,
                        CultureInfo.InvariantCulture, out var parsedRating) && parsedRating is > 0 and <= 10
                        ? parsedRating : (float?)null;
                    var year = int.TryParse((string?)root.Element("year"), out var parsedYear)
                        && parsedYear is >= 1900 and <= 2100 ? parsedYear : (int?)null;
                    return ((string?)root.Element("sorttitle"), rating, year);
                }
            }
            catch (Exception ex) when (ex is IOException or System.Xml.XmlException)
            {
                // 个别损坏的 NFO 留给下次同步修复。
            }
        }

        return (null, null, null);
    }

    private static TypeOptions NoFetchers(string type) => new()
    {
        Type = type,
        MetadataFetchers = Array.Empty<string>(),
        MetadataFetcherOrder = Array.Empty<string>(),
        ImageFetchers = Array.Empty<string>(),
        ImageFetcherOrder = Array.Empty<string>()
    };

    private static string Normalize(string path) =>
        path.Replace('/', '\\').TrimEnd('\\');
}
