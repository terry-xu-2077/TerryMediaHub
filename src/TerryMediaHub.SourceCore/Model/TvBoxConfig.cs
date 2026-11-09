using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.TerryMediaHub.Model;

/// <summary>
/// TVBox / 影视仓 兼容配置（只解析点播相关字段）。
/// </summary>
public sealed class TvBoxConfig
{
    [JsonPropertyName("spider")]
    public string? Spider { get; set; }

    [JsonPropertyName("wallpaper")]
    public string? Wallpaper { get; set; }

    [JsonPropertyName("logo")]
    public string? Logo { get; set; }

    [JsonPropertyName("sites")]
    public List<TvBoxSite> Sites { get; set; } = new();

    [JsonPropertyName("parses")]
    public List<TvBoxParse> Parses { get; set; } = new();

    [JsonPropertyName("lives")]
    public List<TvBoxLive> Lives { get; set; } = new();
}

public sealed class TvBoxParse
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("type")]
    public int Type { get; set; }

    [JsonPropertyName("url")]
    public string Url { get; set; } = string.Empty;

    [JsonPropertyName("ext")]
    public JsonElement? Ext { get; set; }
}

public sealed class TvBoxLive
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("type")]
    public int Type { get; set; }

    [JsonPropertyName("url")]
    public string Url { get; set; } = string.Empty;

    [JsonPropertyName("epg")]
    public string? Epg { get; set; }

    [JsonPropertyName("logo")]
    public string? Logo { get; set; }
}

public sealed class TvBoxSite
{
    [JsonPropertyName("key")]
    public string Key { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("type")]
    public int Type { get; set; }

    [JsonPropertyName("api")]
    public string Api { get; set; } = string.Empty;

    [JsonPropertyName("jar")]
    public string? Jar { get; set; }

    [JsonPropertyName("ext")]
    public JsonElement? Ext { get; set; }

    [JsonPropertyName("searchable")]
    public int? Searchable { get; set; }

    [JsonPropertyName("playerType")]
    public int? PlayerType { get; set; }

    [JsonPropertyName("timeout")]
    public int? Timeout { get; set; }

    /// <summary>
    /// 配置里的 ext 转为字符串（对象则序列化）。
    /// </summary>
    public string ExtAsString()
    {
        if (Ext is null || Ext.Value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return string.Empty;
        }

        return Ext.Value.ValueKind == JsonValueKind.String
            ? Ext.Value.GetString() ?? string.Empty
            : Ext.Value.GetRawText();
    }
}
