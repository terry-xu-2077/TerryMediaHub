using System;
using System.Collections.Generic;
using System.Linq;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;
using Jellyfin.Plugin.TerryMediaHub.Configuration;

namespace Jellyfin.Plugin.TerryMediaHub;

/// <summary>
/// 影视仓 / TVBox 点播源插件：把源解析成 Jellyfin 媒体库。
/// </summary>
public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    public const string PluginGuid = "3f5c8a41-9d2e-4b76-8c15-6a0e7d94b2f1";

    public const string PluginName = "TerryMediaHub";

    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;

        Util.Http.UseSystemProxy = Configuration.UseSystemProxy;

        if (string.IsNullOrWhiteSpace(Configuration.StreamSecret))
        {
            Configuration.StreamSecret = Guid.NewGuid().ToString("N");
            SaveConfiguration();
        }

        var changed = false;
        if (string.Equals(Configuration.LibraryName, "在线媒体库", StringComparison.Ordinal))
        {
            Configuration.LibraryName = "在线媒体";
            changed = true;
        }

        var oldFolders = Configuration.CategoryFolders;
        var standardFolders = new[] { "豆瓣热播", "近期热播", "内地", "剧集", "综艺", "动漫", "电影" };
        if (oldFolders is { Count: > 0 }
            && oldFolders.All(f => standardFolders.Contains(f, StringComparer.Ordinal))
            && (oldFolders.Contains("豆瓣热播", StringComparer.Ordinal)
                || oldFolders.Count != oldFolders.Distinct(StringComparer.Ordinal).Count()))
        {
            Configuration.CategoryFolders = new List<string> { "近期热播", "内地", "剧集", "综艺", "动漫", "电影" };
            changed = true;
        }

        if (changed)
        {
            SaveConfiguration();
        }
    }

    public static Plugin? Instance { get; private set; }

    public override string Name => PluginName;

    public override Guid Id => Guid.Parse(PluginGuid);

    public override string Description => "把影视仓 / TVBox 点播源同步到独立的「在线媒体」媒体库。";

    public override void UpdateConfiguration(BasePluginConfiguration configuration)
    {
        base.UpdateConfiguration(configuration);
        Util.Http.UseSystemProxy = Configuration.UseSystemProxy;
    }

    public IEnumerable<PluginPageInfo> GetPages()
    {
        var ns = GetType().Namespace;
        yield return new PluginPageInfo
        {
            Name = PluginName,
            DisplayName = "TerryMediaHub",
            EmbeddedResourcePath = ns + ".Configuration.configPage.html",
            MenuIcon = "video_library",
            MenuSection = "server",
            EnableInMainMenu = true
        };
    }
}
