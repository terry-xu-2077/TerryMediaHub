using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.TerryMediaHub.Configuration;
using Jellyfin.Plugin.TerryMediaHub.Util;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.TerryMediaHub.Runtime;

/// <summary>
/// csp_ 爬虫的 JVM 宿主。
///
/// TVBox / 影视仓 的 csp_ 站点是「jar 里的 Java 类」，只能在 Java 运行时里跑。
/// 这里内置一个精简运行时（spider-host.jar：catvod Spider 基类 + Android 最小桩 + okhttp/gson），
/// 用子进程常驻 + stdin/stdout 一行一个 JSON 的方式做 RPC。
/// </summary>
public sealed class SpiderHost
{
    private const string ResourceName = "TerryMediaHub.SpiderHost.jar";
    private const string CompatResourceName = "TerryMediaHub.AndroidCompat.jar";

    private static readonly Lazy<SpiderHost> Lazy = new(() => new SpiderHost());

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _lock = new();
    private readonly Dictionary<string, DateTime> _seenMiss = new(StringComparer.OrdinalIgnoreCase);

    private readonly Dictionary<string, string> _jarCache = new(StringComparer.OrdinalIgnoreCase);

    private Process? _proc;
    private string? _java;
    private string? _hostJar;
    private string? _compatJar;
    private string? _spiderJar;
    private string _spiderSource = string.Empty;
    private string? _lastError;
    private ILogger? _logger;

    public static SpiderHost Instance => Lazy.Value;

    public bool Ready { get; private set; }

    /// <summary>本次同步里下载失败的站点专用 jar（源里的链接失效）。</summary>
    public IReadOnlyList<string> FailedSiteJars { get; private set; } = Array.Empty<string>();

    public string? JavaPath => _java;

    public string? SpiderJar => _spiderJar;

    public string? LastError => _lastError;

    public static string WorkDir
    {
        get
        {
            var common = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
            if (string.IsNullOrWhiteSpace(common))
            {
                common = AppContext.BaseDirectory;
            }

            return Path.Combine(common, "TerryMediaHub", "host");
        }
    }

    public void AttachLogger(ILogger logger) => _logger = logger;

    /// <summary>能否跑 csp（只做本地能力判断，不发网络请求）。</summary>
    public bool CanRun(PluginConfiguration cfg) => FindJava(cfg) is not null;

    public string? ProbeJavaVersion(PluginConfiguration cfg)
    {
        var java = FindJava(cfg);
        if (java is null)
        {
            return null;
        }

        try
        {
            using var p = Process.Start(new ProcessStartInfo(java, "-version")
            {
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            });
            if (p is null)
            {
                return null;
            }

            var err = p.StandardError.ReadToEnd();
            p.WaitForExit(8000);
            var line = err.Split('\n').FirstOrDefault()?.Trim();
            return string.IsNullOrWhiteSpace(line) ? "unknown" : line;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>准备 Java、内置运行时，以及全部 spider jar（全局 + 每站点专用）。</summary>
    public async Task<bool> PrepareAsync(
        PluginConfiguration cfg,
        string? spiderJarUrl,
        IEnumerable<string>? siteJarSpecs,
        CancellationToken ct)
    {
        Directory.CreateDirectory(WorkDir);

        _java = FindJava(cfg);
        if (_java is null)
        {
            Ready = false;
            _lastError = "未找到 Java 运行时（csp 站点需要 JRE 8+）";
            return false;
        }

        Nested.TryExtract(ResourceName, Path.Combine(WorkDir, "host-runtime.jar"), out var hostJar);
        _hostJar = hostJar;
        Nested.TryExtract(CompatResourceName, Path.Combine(WorkDir, "android-compat.jar"), out var compatJar);
        _compatJar = compatJar;
        if (_hostJar is null)
        {
            Ready = false;
            _lastError = "内置 spider 运行时解包失败";
            return false;
        }

        var url = string.IsNullOrWhiteSpace(cfg.SpiderJarUrl) ? spiderJarUrl : cfg.SpiderJarUrl;
        var jar = await EnsureSpiderJarAsync(url, cfg, ct).ConfigureAwait(false);
        if (jar is not null)
        {
            _spiderJar = jar;
        }

        // 站点可以用自己的 jar（配置里的 site.jar），这类 jar 常常包含全局 jar 里没有的 spider 类
        var extra = 0;
        var failed = new List<string>();
        foreach (var spec in (siteJarSpecs ?? Enumerable.Empty<string>())
                     .Where(s => !string.IsNullOrWhiteSpace(s))
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var local = await EnsureSiteJarAsync(spec, cfg, ct).ConfigureAwait(false);
            if (local is null)
            {
                failed.Add(SplitSpiderSpec(spec).Url);
            }
            else
            {
                extra++;
            }
        }

        FailedSiteJars = failed;
        if (extra > 0)
        {
            _logger?.LogInformation("已准备 {Extra} 个站点专用 spider jar（失败 {Fail} 个）", extra, failed.Count);
        }

        if (_spiderJar is null && extra == 0)
        {
            Ready = false;
            _lastError = "源配置里没有可用的 spider jar 地址";
            return false;
        }

        Kill();
        Ready = true;
        _lastError = null;
        return true;
    }

    /// <summary>某个站点的 jar 对应的本地文件名；没有专用 jar 时返回 null（用全局 jar）。</summary>
    public string? LocalJarName(string? siteJarSpec)
    {
        if (string.IsNullOrWhiteSpace(siteJarSpec))
        {
            return null;
        }

        lock (_lock)
        {
            return _jarCache.TryGetValue(siteJarSpec, out var v) ? Path.GetFileName(v) : null;
        }
    }

    /// <summary>调用 spider 的一个方法，返回 spider 原始 JSON 字符串。</summary>
    public async Task<string> CallAsync(
        string site,
        string ext,
        string method,
        JsonObject? args,
        CancellationToken ct,
        string? jarSpec = null)
    {
        if (_java is null || _hostJar is null || (_spiderJar is null && _jarCache.Count == 0))
        {
            throw new InvalidOperationException(_lastError ?? "csp 运行时未就绪");
        }

        var req = new JsonObject
        {
            ["site"] = site,
            ["ext"] = ext ?? string.Empty,
            ["method"] = method
        };

        var localJar = LocalJarName(jarSpec);
        if (!string.IsNullOrWhiteSpace(localJar))
        {
            req["jar"] = localJar;
        }

        if (args is not null)
        {
            req["args"] = args;
        }

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            for (var attempt = 0; attempt < 2; attempt++)
            {
                EnsureProcess();
                var line = await ExchangeAsync(req.ToJsonString(), ct).ConfigureAwait(false);
                if (line is null)
                {
                    // 进程挂了，重启再来一次
                    Kill();
                    continue;
                }

                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;
                if (root.TryGetProperty("ready", out _) && !root.TryGetProperty("data", out _))
                {
                    continue;
                }

                if (root.TryGetProperty("ok", out var ok) && ok.GetBoolean())
                {
                    return root.TryGetProperty("data", out var data) ? data.GetString() ?? string.Empty : string.Empty;
                }

                var err = root.TryGetProperty("error", out var e) ? e.GetString() : "unknown";
                var trace = root.TryGetProperty("trace", out var t) ? t.GetString() : null;
                throw new InvalidOperationException($"spider[{site}.{method}] 执行失败: {err}" +
                                                    (trace is null ? string.Empty : "\n" + FirstLines(trace, 4)));
            }

            throw new InvalidOperationException(_lastError ?? "spider 宿主启动失败");
        }
        finally
        {
            _gate.Release();
        }
    }

    private void EnsureProcess()
    {
        lock (_lock)
        {
            if (_proc is { HasExited: false })
            {
                return;
            }

            Kill();

            var psi = new ProcessStartInfo(_java!)
            {
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardInputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };
            psi.ArgumentList.Add("-Dfile.encoding=UTF-8");
            psi.ArgumentList.Add("-Djava.awt.headless=true");
            psi.ArgumentList.Add("-cp");
            psi.ArgumentList.Add(_compatJar is null ? _hostJar! : _compatJar + Path.PathSeparator + _hostJar);
            psi.ArgumentList.Add("com.terry.spiderhost.Host");
            psi.ArgumentList.Add(WorkDir);
            psi.ArgumentList.Add(Path.GetFileName(_spiderJar ?? string.Empty));

            var p = new Process { StartInfo = psi };
            p.ErrorDataReceived += (_, e) =>
            {
                if (!string.IsNullOrWhiteSpace(e.Data))
                {
                    _logger?.LogDebug("[spider-host] {Line}", e.Data);
                }
            };
            p.Start();
            p.BeginErrorReadLine();

            // 首行是 ready 握手
            var readyLine = p.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(30)).GetAwaiter().GetResult();
            if (readyLine is null)
            {
                try
                {
                    p.Kill(true);
                }
                catch
                {
                    // ignore
                }

                throw new InvalidOperationException("spider 宿主没有正常启动（可能是 Java 版本过低）");
            }

            _proc = p;
            _logger?.LogInformation("spider 宿主已启动（pid={Pid}）", p.Id);
        }
    }

    private async Task<string?> ExchangeAsync(string json, CancellationToken ct)
    {
        var p = _proc;
        if (p is null || p.HasExited)
        {
            return null;
        }

        try
        {
            await p.StandardInput.WriteLineAsync(json.AsMemory(), ct).ConfigureAwait(false);
            await p.StandardInput.FlushAsync(ct).ConfigureAwait(false);

            // 宿主可能往 stdout 打出一些非 JSON 的噪声（某些爬虫直接 System.out），跳过它们
            for (var noise = 0; noise < 64; noise++)
            {
                var line = await p.StandardOutput.ReadLineAsync().WaitAsync(CallTimeout, ct).ConfigureAwait(false);
                if (line is null)
                {
                    return null;
                }

                var trimmed = line.Trim();
                if (trimmed.Length == 0)
                {
                    continue;
                }

                if (!trimmed.StartsWith('{'))
                {
                    _logger?.LogDebug("[spider-host] 噪声输出: {Line}", trimmed.Length > 200 ? trimmed[..200] : trimmed);
                    continue;
                }

                return trimmed;
            }

            return null;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // 上层取消（站点超时等）：宿主多半正卡在某个爬虫里，直接重启保证管道干净
            Kill();
            throw;
        }
        catch (TimeoutException)
        {
            _logger?.LogWarning("spider 调用超过 {Sec}s 没有响应，重启宿主", (int)CallTimeout.TotalSeconds);
            Kill();
            return null;
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "spider 宿主通信失败");
            Kill();
            return null;
        }
    }

    private static readonly TimeSpan CallTimeout = TimeSpan.FromSeconds(60);

    private void Kill()
    {
        lock (_lock)
        {
            try
            {
                if (_proc is { HasExited: false })
                {
                    _proc.Kill(true);
                }
            }
            catch
            {
                // ignore
            }

            _proc?.Dispose();
            _proc = null;
        }
    }

    // ---------- Java 探测 ----------

    public static string? FindJava(PluginConfiguration cfg)
    {
        var candidates = new List<string>();

        if (!string.IsNullOrWhiteSpace(cfg.JavaPath))
        {
            candidates.Add(cfg.JavaPath.Trim());
        }

        var home = Environment.GetEnvironmentVariable("JAVA_HOME");
        if (!string.IsNullOrWhiteSpace(home))
        {
            candidates.Add(Path.Combine(home, "bin", "java.exe"));
        }

        var pathEnv = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var dir in pathEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                candidates.Add(Path.Combine(dir.Trim(), "java.exe"));
            }
            catch
            {
                // ignore
            }
        }

        candidates.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonProgramFiles), "Oracle", "Java", "javapath", "java.exe"));

        foreach (var root in new[]
                 {
                     @"C:\Program Files\Java", @"C:\Program Files\Eclipse Adoptium", @"C:\Program Files\Zulu",
                     @"C:\Program Files\Microsoft", @"C:\Program Files\BellSoft", @"C:\Program Files\Amazon Corretto",
                     @"C:\Program Files (x86)\Java"
                 })
        {
            try
            {
                if (!Directory.Exists(root))
                {
                    continue;
                }

                foreach (var d in Directory.EnumerateDirectories(root).OrderByDescending(x => x))
                {
                    candidates.Add(Path.Combine(d, "bin", "java.exe"));
                }
            }
            catch
            {
                // ignore
            }
        }

        // javapath 里的是 Windows 启动器（会再拉起一个真正的 JVM 子进程），
        // 优先用真正的 java.exe，方便进程树管理。
        return candidates.Where(File.Exists)
                   .OrderBy(x => x.Contains("javapath", StringComparison.OrdinalIgnoreCase) ? 1 : 0)
                   .FirstOrDefault();
    }

    // ---------- spider jar ----------

    private async Task<string?> EnsureSpiderJarAsync(string? raw, PluginConfiguration cfg, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            // 没有配置就用上次下载的
            var cached = Directory.Exists(WorkDir)
                ? Directory.EnumerateFiles(WorkDir, "spider-*.jar")
                    .Where(f => !f.EndsWith(".classes.jar", StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(File.GetLastWriteTimeUtc)
                    .FirstOrDefault()
                : null;
            return cached;
        }

        var (url, md5) = SplitSpiderSpec(raw);
        if (string.IsNullOrWhiteSpace(url))
        {
            return null;
        }

        var target = Path.Combine(WorkDir, "spider-" + (string.IsNullOrWhiteSpace(md5) ? HashOf(url) : md5.ToLowerInvariant()) + ".jar");
        if (File.Exists(target) && new FileInfo(target).Length > 1024)
        {
            _spiderSource = url;
            lock (_lock)
            {
                _jarCache[raw.Trim()] = target;
            }

            return target;
        }

        try
        {
            _logger?.LogInformation("下载 spider jar: {Url}", url);
            var bytes = await Http.GetBytesAsync(url, cfg.UserAgent, null, ct).ConfigureAwait(false);
            if (bytes.Length < 1024 || bytes[0] != 'P' || bytes[1] != 'K')
            {
                _lastError = "spider jar 不是有效的 jar/zip";
                return null;
            }

            await File.WriteAllBytesAsync(target, bytes, ct).ConfigureAwait(false);
            _spiderSource = url;
            lock (_lock)
            {
                _jarCache[raw.Trim()] = target;
            }

            return target;
        }
        catch (Exception ex)
        {
            _lastError = "下载 spider jar 失败: " + ex.Message;
            _logger?.LogWarning(ex, "下载 spider jar 失败");
            return null;
        }
    }

    /// <summary>下载某个站点专用 jar（配置里的 site.jar），按 md5/URL 哈希落盘复用。</summary>
    private async Task<string?> EnsureSiteJarAsync(string raw, PluginConfiguration cfg, CancellationToken ct)
    {
        var spec = raw.Trim();

        lock (_lock)
        {
            if (_jarCache.TryGetValue(spec, out var known) && File.Exists(known) && new FileInfo(known).Length > 1024)
            {
                return known;
            }
        }

        var (url, md5) = SplitSpiderSpec(spec);
        if (string.IsNullOrWhiteSpace(url))
        {
            return null;
        }

        var target = Path.Combine(
            WorkDir,
            "spider-" + (string.IsNullOrWhiteSpace(md5) ? HashOf(url) : md5.ToLowerInvariant()) + ".jar");

        if (File.Exists(target) && new FileInfo(target).Length > 1024)
        {
            lock (_lock)
            {
                _jarCache[spec] = target;
            }

            return target;
        }

        try
        {
            _logger?.LogInformation("下载站点专用 spider jar: {Url}", url);
            var bytes = await Http.GetBytesAsync(url, cfg.UserAgent, null, ct).ConfigureAwait(false);
            if (bytes.Length < 1024 || bytes[0] != 'P' || bytes[1] != 'K')
            {
                _logger?.LogWarning("站点 jar 不是有效的 zip: {Url}", url);
                return null;
            }

            var tmp = target + ".part";
            await File.WriteAllBytesAsync(tmp, bytes, ct).ConfigureAwait(false);
            File.Move(tmp, target, true);

            lock (_lock)
            {
                _jarCache[spec] = target;
            }

            return target;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "下载站点专用 spider jar 失败: {Url}", url);
            return null;
        }
    }

    /// <summary>spider 字段形如 https://x/y.png;md5;B63A0EB...</summary>
    public static (string Url, string? Md5) SplitSpiderSpec(string raw)
    {
        var s = raw.Trim();
        if (s.StartsWith('{'))
        {
            try
            {
                using var doc = JsonDocument.Parse(s);
                if (doc.RootElement.TryGetProperty("url", out var u) && u.ValueKind == JsonValueKind.String)
                {
                    s = u.GetString() ?? string.Empty;
                }
            }
            catch
            {
                // ignore
            }
        }

        var parts = s.Split(';');
        var url = parts[0].Trim();
        var md5 = parts.Length >= 3 && parts[1].Trim().Equals("md5", StringComparison.OrdinalIgnoreCase)
            ? parts[2].Trim()
            : null;
        return (url, md5);
    }

    private static string HashOf(string s)
    {
        var bytes = System.Security.Cryptography.MD5.HashData(Encoding.UTF8.GetBytes(s));
        return Convert.ToHexString(bytes).ToLowerInvariant()[..16];
    }

    private static string FirstLines(string s, int n)
    {
        var lines = s.Split('\n');
        return string.Join('\n', lines.Take(Math.Min(n, lines.Length)));
    }
}

/// <summary>把一个内嵌资源解包到磁盘（内容变了才重写）。</summary>
internal static class Nested
{
    public static bool TryExtract(string resourceName, string target, out string? path)
    {
        path = null;
        var asm = Assembly.GetExecutingAssembly();
        var name = asm.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith(resourceName, StringComparison.OrdinalIgnoreCase));
        if (name is null)
        {
            return false;
        }

        using var stream = asm.GetManifestResourceStream(name);
        if (stream is null)
        {
            return false;
        }

        var dir = Path.GetDirectoryName(target);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        var resourceHash = SHA256.HashData(stream);
        stream.Position = 0;
        var version = Convert.ToHexString(resourceHash)[..12].ToLowerInvariant();
        target = Path.Combine(dir!, Path.GetFileNameWithoutExtension(target) + "-" + version + Path.GetExtension(target));

        if (File.Exists(target) && new FileInfo(target).Length == stream.Length)
        {
            path = target;
            return true;
        }

        using var fs = File.Create(target);
        stream.CopyTo(fs);
        path = target;
        return true;
    }
}
