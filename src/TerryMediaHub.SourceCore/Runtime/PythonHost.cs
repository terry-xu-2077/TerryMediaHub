using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.TerryMediaHub.Runtime;

/// <summary>TV6.2.9 的 Python Spider 方法通过独立 Python 3.8 进程执行。</summary>
public sealed class PythonHost
{
    private static readonly Lazy<PythonHost> Lazy = new(() => new PythonHost());
    private readonly SemaphoreSlim _gate = new(1, 1);
    private Process? _process;

    public static PythonHost Instance => Lazy.Value;

    public bool Available => File.Exists(PythonExe) && File.Exists(BridgePath);

    private static string PythonExe => @"C:\Program Files\python\python.exe";

    private static string BridgePath => Path.Combine(AppContext.BaseDirectory, "Python", "bridge.py");

    private static string CachePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "TerryMediaHub", "python");

    public async Task<string> CallAsync(string site, string api, string ext, string method,
        JsonObject? args, CancellationToken ct)
    {
        if (!Available) throw new InvalidOperationException("Python 3.8 或 bridge.py 不可用");
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            EnsureProcess();
            var request = new JsonObject
            {
                ["site"] = site,
                ["api"] = api,
                ["ext"] = ext,
                ["method"] = method,
                ["args"] = args
            };
            var proc = _process!;
            await proc.StandardInput.WriteLineAsync(request.ToJsonString().AsMemory(), ct).ConfigureAwait(false);
            await proc.StandardInput.FlushAsync(ct).ConfigureAwait(false);
            var line = await proc.StandardOutput.ReadLineAsync()
                .WaitAsync(TimeSpan.FromSeconds(45), ct).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(line)) throw new IOException("Python 宿主没有响应");
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            if (root.TryGetProperty("ok", out var ok) && ok.GetBoolean())
                return root.GetProperty("data").GetString() ?? string.Empty;
            throw new InvalidOperationException(root.TryGetProperty("error", out var error)
                ? error.GetString() ?? "Python 站点失败" : "Python 站点失败");
        }
        catch
        {
            Kill();
            throw;
        }
        finally { _gate.Release(); }
    }

    private void EnsureProcess()
    {
        if (_process is { HasExited: false }) return;
        Kill();
        Directory.CreateDirectory(CachePath);
        var start = new ProcessStartInfo(PythonExe)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false),
            WorkingDirectory = Path.GetDirectoryName(BridgePath)!,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        start.ArgumentList.Add(BridgePath);
        start.Environment["PYTHONIOENCODING"] = "utf-8";
        start.Environment["TERRY_PY_CACHE"] = CachePath;
        var proc = new Process { StartInfo = start };
        proc.ErrorDataReceived += (_, _) => { };
        proc.Start();
        proc.BeginErrorReadLine();
        _process = proc;
    }

    private void Kill()
    {
        try { if (_process is { HasExited: false }) _process.Kill(true); }
        catch { }
        _process?.Dispose();
        _process = null;
    }
}
