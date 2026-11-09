using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.TerryMediaHub.Library;
using Jellyfin.Plugin.TerryMediaHub.Runtime;
using Jellyfin.Plugin.TerryMediaHub.Sources;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.TerryMediaHub.Tasks;

/// <summary>定时把源同步成媒体库。</summary>
public sealed class RefreshTask : IScheduledTask
{
    private readonly ILogger<RefreshTask> _logger;
    private readonly ILoggerFactory _loggerFactory;

    public RefreshTask(ILogger<RefreshTask> logger, ILoggerFactory loggerFactory)
    {
        _logger = logger;
        _loggerFactory = loggerFactory;
    }

    public string Name => "影视仓源同步";

    public string Key => "TerryMediaHubRefresh";

    public string Description => "从配置的源拉取点播列表，生成媒体文件，并过滤失效播放地址。";

    public string Category => "媒体库";

    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        var cfg = Plugin.Instance!.Configuration;
        if (!SyncState.TryBegin("定时同步"))
        {
            _logger.LogInformation("已有同步任务在运行，跳过本次定时任务");
            return;
        }

        try
        {
            progress.Report(1);
            var result = await LocalServiceClient.RunSyncAsync(cfg,
                Address.Resolve(cfg, null), cancellationToken).ConfigureAwait(false);
            progress.Report(90);

            cfg.LastSyncUtc = DateTime.UtcNow;
            cfg.LastSyncSummary = result.LastSummary ?? "同步完成";
            Plugin.Instance.SaveConfiguration();

            progress.Report(100);
            SyncState.End(cfg.LastSyncSummary, result.Notes);
            _logger.LogInformation("影视仓同步完成：{Summary}", cfg.LastSyncSummary);
        }
        catch (Exception ex)
        {
            SyncState.End("同步失败: " + ex.Message, new[] { ex.ToString() });
            _logger.LogError(ex, "影视仓同步失败");
            throw;
        }
    }

    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        var cfg = Plugin.Instance?.Configuration;
        var hours = cfg?.RefreshIntervalHours ?? 12;
        if (hours <= 0)
        {
            yield break;
        }

        yield return new TaskTriggerInfo
        {
            Type = TaskTriggerInfo.TriggerInterval,
            IntervalTicks = TimeSpan.FromHours(hours).Ticks
        };
    }
}

