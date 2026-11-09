using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.TerryMediaHub.Configuration;
using Jellyfin.Plugin.TerryMediaHub.Health;
using Jellyfin.Plugin.TerryMediaHub.Util;

namespace Jellyfin.Plugin.TerryMediaHub.Runtime;

/// <summary>本机解析服务的精简 HTTP 客户端。服务仅监听 127.0.0.1。</summary>
public static class LocalServiceClient
{
    private static readonly HttpClient Client = new() { Timeout = TimeSpan.FromSeconds(60) };
    private const string Base = "http://127.0.0.1:18766";

    public static async Task<ProbeResult> ProbeAsync(PluginConfiguration cfg, string url,
        CancellationToken ct)
    {
        await EnsureStartedAsync(ct).ConfigureAwait(false);
        using var request = new HttpRequestMessage(HttpMethod.Post, Base + "/inspect")
        {
            Content = JsonContent.Create(new { Config = cfg, SiteKey = "", Method = "probe", Url = url })
        };
        request.Headers.Add("X-Terry-Key", cfg.StreamSecret);
        using var response = await Client.SendAsync(request, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<ProbeResult>(ct).ConfigureAwait(false)
            ?? ProbeResult.Fail("本机服务没有返回探测结果");
    }

    public static async Task<ServiceSources> SourcesAsync(PluginConfiguration cfg, CancellationToken ct)
    {
        await EnsureStartedAsync(ct).ConfigureAwait(false);
        using var request = new HttpRequestMessage(HttpMethod.Post, Base + "/sources")
        {
            Content = JsonContent.Create(cfg)
        };
        request.Headers.Add("X-Terry-Key", cfg.StreamSecret);
        using var response = await Client.SendAsync(request, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<ServiceSources>(ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException("解析服务没有返回源列表");
    }

    public static async Task<ResolvedPlay?> ResolveAsync(PluginConfiguration cfg, string raw,
        string? referer, CancellationToken ct)
    {
        await EnsureStartedAsync(ct).ConfigureAwait(false);
        using var request = new HttpRequestMessage(HttpMethod.Post, Base + "/resolve")
        {
            Content = JsonContent.Create(new { Config = cfg, Raw = raw, Referer = referer })
        };
        request.Headers.Add("X-Terry-Key", cfg.StreamSecret);
        using var response = await Client.SendAsync(request, ct).ConfigureAwait(false);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<ResolvedPlay>(ct).ConfigureAwait(false);
    }

    public static async Task<ServiceStatus> RunSyncAsync(PluginConfiguration cfg, string proxyBase,
        CancellationToken ct)
    {
        await EnsureStartedAsync(ct).ConfigureAwait(false);
        using var request = new HttpRequestMessage(HttpMethod.Post, Base + "/sync")
        {
            Content = JsonContent.Create(new { Config = cfg, ProxyBase = proxyBase })
        };
        request.Headers.Add("X-Terry-Key", cfg.StreamSecret);
        using var response = await Client.SendAsync(request, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        while (true)
        {
            await Task.Delay(2000, ct).ConfigureAwait(false);
            using var statusRequest = new HttpRequestMessage(HttpMethod.Get, Base + "/status");
            statusRequest.Headers.Add("X-Terry-Key", cfg.StreamSecret);
            using var statusResponse = await Client.SendAsync(statusRequest, ct).ConfigureAwait(false);
            statusResponse.EnsureSuccessStatusCode();
            var status = await statusResponse.Content.ReadFromJsonAsync<ServiceStatus>(ct).ConfigureAwait(false)
                ?? throw new InvalidOperationException("解析服务没有返回状态");
            SyncState.Report(status.Progress, status.Phase ?? "同步中");
            if (status.Running) continue;
            if (status.SiteFailCounts is not null) cfg.SiteFailCounts = status.SiteFailCounts;
            if (status.AutoBlockedSiteKeys is not null) cfg.AutoBlockedSiteKeys = status.AutoBlockedSiteKeys;
            if (status.LibraryPrimed.HasValue) cfg.LibraryPrimed = status.LibraryPrimed.Value;
            if (!string.IsNullOrEmpty(status.DetectedBaseUrl)) cfg.DetectedBaseUrl = status.DetectedBaseUrl;
            if (string.IsNullOrWhiteSpace(status.LastSummary))
                throw new InvalidOperationException("解析服务在同步完成前丢失了任务状态");
            if (status.LastSummary.StartsWith("同步失败", StringComparison.Ordinal))
                throw new InvalidOperationException(status.LastSummary);
            return status;
        }
    }

    private static async Task EnsureStartedAsync(CancellationToken ct)
    {
        if (await HealthyAsync(ct).ConfigureAwait(false)) return;
        var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "TerryMediaHub", "service");
        var dll = Path.Combine(directory, "TerryMediaHub.LocalService.dll");
        if (!File.Exists(dll)) throw new FileNotFoundException("本机解析服务尚未安装", dll);
        var dotnet = @"C:\OS\tools\dotnet8\dotnet.exe";
        if (!File.Exists(dotnet)) dotnet = "dotnet";
        Process.Start(new ProcessStartInfo(dotnet)
        {
            ArgumentList = { dll },
            WorkingDirectory = directory,
            UseShellExecute = false,
            CreateNoWindow = true,
            Environment = { ["TERRY_SERVICE_KEY"] = Plugin.Instance!.Configuration.StreamSecret }
        });
        for (var i = 0; i < 20; i++)
        {
            await Task.Delay(500, ct).ConfigureAwait(false);
            if (await HealthyAsync(ct).ConfigureAwait(false)) return;
        }
        throw new InvalidOperationException("本机解析服务启动超时");
    }

    private static async Task<bool> HealthyAsync(CancellationToken ct)
    {
        try
        {
            using var response = await Client.GetAsync(Base + "/health", ct).ConfigureAwait(false);
            return response.IsSuccessStatusCode;
        }
        catch (HttpRequestException) { return false; }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested) { return false; }
    }
}

public sealed class ServiceStatus
{
    public bool Running { get; set; }
    public int Progress { get; set; }
    public string? Phase { get; set; }
    public string? LastSummary { get; set; }
    public List<string>? Notes { get; set; }
    public List<SiteFailEntry>? SiteFailCounts { get; set; }
    public List<string>? AutoBlockedSiteKeys { get; set; }
    public bool? LibraryPrimed { get; set; }
    public string? DetectedBaseUrl { get; set; }
}

public sealed class ServiceSources
{
    public List<ServiceSite> Sites { get; set; } = new();
    public string? SourceError { get; set; }
    public object? JarSpider { get; set; }
}

public sealed class ServiceSite
{
    public string? Key { get; set; }
    public string? Name { get; set; }
    public string? Source { get; set; }
    public string? Kind { get; set; }
    public bool Supported { get; set; }
    public bool Enabled { get; set; }
    public bool Blocked { get; set; }
    public bool AutoBlocked { get; set; }
    public string? Reason { get; set; }
}
