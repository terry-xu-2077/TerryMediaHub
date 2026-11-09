using System.Collections.Generic;

namespace Jellyfin.Plugin.TerryMediaHub.Model;

/// <summary>点播分类。</summary>
public sealed class VodCategory
{
    public string Id { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public string? ParentId { get; set; }
}

/// <summary>列表级影片信息。</summary>
public class VodSummary
{
    public string Id { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public string? Pic { get; set; }

    public string? Remarks { get; set; }

    public string? CategoryName { get; set; }

    public string? Year { get; set; }

    public string? Area { get; set; }

    public string? Lang { get; set; }

    public string? Actor { get; set; }

    public string? Director { get; set; }

    public string? Content { get; set; }

    public string? Score { get; set; }

    /// <summary>热播目录在 Jellyfin 按名称排序时使用的顺序键。</summary>
    public string? SortTitle { get; set; }

    /// <summary>列表接口若已带播放地址（苹果CMS ac=videolist 会带），可直接使用，省掉详情请求。</summary>
    public List<VodEpisode> Episodes { get; set; } = new();

    public string? PlayFrom { get; set; }
}

/// <summary>详情（含播放地址）。</summary>
public sealed class VodDetail : VodSummary
{
}

/// <summary>单集播放地址。</summary>
public sealed class VodEpisode
{
    public string Name { get; set; } = string.Empty;

    public string Url { get; set; } = string.Empty;

    public string? Flag { get; set; }
}

/// <summary>站点解析能力。</summary>
public enum ResolverKind
{
    /// <summary>苹果CMS / MacCMS v10 JSON 接口。</summary>
    Cms,

    /// <summary>JS spider，由独立 Node.js worker 执行。</summary>
    JavaScript,

    /// <summary>Python Spider，由独立 Python 3.8 worker 执行。</summary>
    Python,

    /// <summary>需要 jar 的 csp_ 爬虫。</summary>
    JarSpider,

    /// <summary>未知。</summary>
    Unknown
}
