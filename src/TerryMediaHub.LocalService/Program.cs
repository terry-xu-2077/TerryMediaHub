using Jellyfin.Plugin.TerryMediaHub.Configuration;
using Jellyfin.Plugin.TerryMediaHub.Library;
using Jellyfin.Plugin.TerryMediaHub.Model;
using Jellyfin.Plugin.TerryMediaHub.Runtime;
using Jellyfin.Plugin.TerryMediaHub.Sources;
using Jellyfin.Plugin.TerryMediaHub.Util;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls("http://127.0.0.1:18766");
var app = builder.Build();
var state = new ServiceState();
var serviceKey = Environment.GetEnvironmentVariable("TERRY_SERVICE_KEY")
    ?? throw new InvalidOperationException("缺少本机服务密钥");
app.Use(async (context, next) =>
{
    if (context.Request.Path == "/health"
        || context.Request.Path.StartsWithSegments("/site-proxy")) { await next(); return; }
    var expected = Encoding.UTF8.GetBytes(serviceKey);
    var supplied = Encoding.UTF8.GetBytes(context.Request.Headers["X-Terry-Key"].ToString());
    if (!CryptographicOperations.FixedTimeEquals(expected, supplied))
    {
        context.Response.StatusCode = 401;
        return;
    }
    await next();
});

app.MapGet("/health", () => Results.Ok(new { ok = true, version = 1 }));
app.MapGet("/site-proxy/{token}", async (HttpContext context, string token, CancellationToken ct) =>
{
    if (!Token.TryDecode(token, serviceKey, out var descriptor, out _))
    {
        context.Response.StatusCode = 401;
        return;
    }
    try
    {
        using var identity = JsonDocument.Parse(descriptor);
        var root = identity.RootElement;
        var site = root.GetProperty("site").GetString() ?? string.Empty;
        var api = root.GetProperty("api").GetString() ?? string.Empty;
        var ext = root.TryGetProperty("ext", out var extValue) ? extValue.GetString() ?? string.Empty : string.Empty;
        var lang = root.GetProperty("lang").GetString();
        var type = root.TryGetProperty("type", out var typeValue) && typeValue.ValueKind == JsonValueKind.Number
            ? typeValue.GetInt32() : 3;
        if (site.Length == 0 || api.Length == 0 || (lang != "js" && lang != "py"))
        {
            context.Response.StatusCode = 400;
            return;
        }
        var parameters = new JsonObject();
        foreach (var pair in context.Request.Query)
            parameters[pair.Key] = pair.Value.ToString();
        var args = new JsonObject { ["params"] = parameters };
        var raw = lang == "py"
            ? await PythonHost.Instance.CallAsync(site, api, ext, "proxy", args, ct)
            : await JavaScriptHost.Instance.CallAsync(site, api, ext, type, "proxy", args, ct);
        using var proxyResponse = JsonDocument.Parse(raw);
        var value = proxyResponse.RootElement;
        var status = 200;
        var contentType = "application/octet-stream";
        var base64 = false;
        JsonElement body = default;
        if (value.ValueKind == JsonValueKind.Array && value.GetArrayLength() >= 3)
        {
            status = value[0].ValueKind == JsonValueKind.Number ? value[0].GetInt32() : 200;
            contentType = value[1].GetString() ?? contentType;
            body = value[2];
            foreach (var extra in value.EnumerateArray().Skip(3))
                if (extra.ValueKind == JsonValueKind.Object
                    && extra.TryGetProperty("terryBase64", out var marker) && marker.ValueKind == JsonValueKind.True)
                    base64 = true;
        }
        else if (value.ValueKind == JsonValueKind.Object)
        {
            if (value.TryGetProperty("code", out var code) && code.ValueKind == JsonValueKind.Number)
                status = code.GetInt32();
            else if (value.TryGetProperty("status", out var code2) && code2.ValueKind == JsonValueKind.Number)
                status = code2.GetInt32();
            if (value.TryGetProperty("content", out var content)) body = content;
            if (value.TryGetProperty("contentType", out var mime) && mime.ValueKind == JsonValueKind.String)
                contentType = mime.GetString() ?? contentType;
            if (value.TryGetProperty("headers", out var headers) && headers.ValueKind == JsonValueKind.Object)
            {
                foreach (var header in headers.EnumerateObject())
                {
                    if (header.Name.Equals("Content-Type", StringComparison.OrdinalIgnoreCase))
                        contentType = header.Value.GetString() ?? contentType;
                    else if (header.Name.Equals("Cache-Control", StringComparison.OrdinalIgnoreCase))
                        context.Response.Headers.CacheControl = header.Value.GetString();
                }
            }
            base64 = value.TryGetProperty("base64", out var encoding) && encoding.ValueKind == JsonValueKind.True;
        }
        else
        {
            context.Response.StatusCode = 502;
            return;
        }
        context.Response.StatusCode = status is >= 100 and <= 599 ? status : 502;
        context.Response.ContentType = contentType;
        var bodyText = body.ValueKind == JsonValueKind.String ? body.GetString() ?? string.Empty
            : body.ValueKind == JsonValueKind.Undefined ? string.Empty : body.GetRawText();
        var bytes = base64 ? Convert.FromBase64String(bodyText) : Encoding.UTF8.GetBytes(bodyText);
        await context.Response.Body.WriteAsync(bytes, ct);
    }
    catch (Exception)
    {
        if (!context.Response.HasStarted) context.Response.StatusCode = 502;
    }
});
app.MapGet("/status", () => Results.Ok(state.Snapshot()));
app.MapPost("/sources", async (PluginConfiguration cfg, CancellationToken ct) =>
{
    Http.UseSystemProxy = cfg.UseSystemProxy;
    if (SourceRepository.Instance.Sites.Count == 0)
        await SourceRepository.Instance.LoadAsync(cfg, ct);
    var sites = SourceRepository.Instance.Sites.Select(s => new
    {
        key = s.Key,
        name = s.Name,
        source = s.SourceName,
        kind = s.Kind.ToString(),
        supported = s.Supported,
        enabled = SourceRepository.IsEnabled(s, cfg),
        blocked = cfg.BlockedSiteKeys.Contains(s.Key) || cfg.AutoBlockedSiteKeys.Contains(s.Key),
        autoBlocked = cfg.AutoBlockedSiteKeys.Contains(s.Key),
        reason = s.UnsupportedReason
    }).ToList();
    return Results.Ok(new
    {
        sites,
        sourceError = SourceRepository.Instance.LastError,
        jarSpider = new
        {
            enabled = cfg.EnableJarSpider,
            java = SpiderHost.Instance.JavaPath,
            javaVersion = SpiderHost.Instance.ProbeJavaVersion(cfg),
            ready = SpiderHost.Instance.Ready,
            spiderJar = SpiderHost.Instance.SpiderJar,
            workDir = SpiderHost.WorkDir,
            error = SpiderHost.Instance.LastError
        }
    });
});
app.MapPost("/inspect", async (InspectRequest request, CancellationToken ct) =>
{
    var cfg = request.Config;
    Http.UseSystemProxy = cfg.UseSystemProxy;
    if (request.Method == "probe")
        return Results.Ok((object)await new Jellyfin.Plugin.TerryMediaHub.Health.LinkProbe(cfg)
            .CheckAsync(request.Url ?? string.Empty, ct));
    if (SourceRepository.Instance.Sites.Count == 0)
        await SourceRepository.Instance.LoadAsync(cfg, ct);
    var site = SourceRepository.Instance.Sites.FirstOrDefault(s => s.Key == request.SiteKey);
    if (site?.Resolver is null) return Results.NotFound(new { error = "站点不存在或解析器不可用" });
    if (site.Kind == ResolverKind.JarSpider && !SpiderHost.Instance.Ready)
    {
        if (!cfg.EnableJarSpider || !await SpiderHost.Instance.PrepareAsync(cfg,
            SourceRepository.Instance.SpiderJarUrl,
            SourceRepository.Instance.Sites.Where(s => s.Kind == ResolverKind.JarSpider)
                .Select(s => s.Site.Jar).Where(j => !string.IsNullOrWhiteSpace(j))
                .Select(j => j!).Distinct(StringComparer.OrdinalIgnoreCase), ct))
            return Results.Problem(SpiderHost.Instance.LastError ?? "CSP 宿主准备失败", statusCode: 503);
    }
    var context = new ResolveContext(site.Site, cfg);
    try
    {
        object result = request.Method switch
        {
            "categories" => await site.Resolver.GetCategoriesAsync(context, ct),
            "list" => await site.Resolver.GetListAsync(context, request.CategoryId ?? string.Empty,
                Math.Max(1, request.Page), ct),
            "detail" => await site.Resolver.GetDetailAsync(context, request.VodId ?? string.Empty, ct)
                ?? new object(),
            _ => new { error = "未知检查方法" }
        };
        return Results.Ok(result);
    }
    catch (Exception ex)
    {
        return Results.Problem(ex.Message, statusCode: 502);
    }
});
app.MapPost("/resolve", async (ResolveRequest request, CancellationToken ct) =>
{
    Http.UseSystemProxy = request.Config.UseSystemProxy;
    var resolved = await ParseResolver.ResolveAsync(request.Raw, request.Config, request.Referer, ct);
    return resolved is null ? Results.NotFound() : Results.Ok(resolved);
});
app.MapPost("/sync", (SyncRequest request, ILoggerFactory loggerFactory) =>
{
    if (!SyncState.TryBegin("准备中")) return Results.Conflict(new { started = false, message = "已有同步运行" });
    _ = Task.Run(async () =>
    {
        var cfg = request.Config;
        try
        {
            Http.UseSystemProxy = cfg.UseSystemProxy;
            SyncState.Report(5, "抓取源配置");
            await SourceRepository.Instance.LoadAsync(cfg, CancellationToken.None).ConfigureAwait(false);
            var report = await new LibraryBuilder(loggerFactory.CreateLogger<LibraryBuilder>(),
                () => Address.Resolve(cfg, request.ProxyBase)).BuildAsync(cfg, CancellationToken.None).ConfigureAwait(false);
            state.Finish(cfg, report.ToSummary(), report.Notes);
            SyncState.End(report.ToSummary(), report.Notes);
        }
        catch (Exception ex)
        {
            loggerFactory.CreateLogger("LocalSync").LogError(ex, "同步失败");
            state.Finish(cfg, "同步失败: " + ex.Message, new[] { ex.ToString() });
            SyncState.End("同步失败: " + ex.Message, new[] { ex.ToString() });
        }
    });
    return Results.Ok(new { started = true });
});

await app.RunAsync();

internal sealed record SyncRequest(PluginConfiguration Config, string? ProxyBase);
internal sealed record ResolveRequest(PluginConfiguration Config, string Raw, string? Referer);
internal sealed record InspectRequest(PluginConfiguration Config, string SiteKey, string Method,
    string? CategoryId, int Page, string? VodId, string? Url);

internal sealed class ServiceState
{
    private readonly object _gate = new();
    private PluginConfiguration? _config;
    private string _summary = string.Empty;
    private IReadOnlyList<string> _notes = Array.Empty<string>();

    public void Finish(PluginConfiguration cfg, string summary, IReadOnlyList<string> notes)
    {
        lock (_gate) { _config = cfg; _summary = summary; _notes = notes; }
    }

    public object Snapshot()
    {
        lock (_gate)
        {
            return new
            {
                running = SyncState.Running,
                progress = SyncState.Progress,
                phase = SyncState.Phase,
                lastSummary = _summary,
                notes = _notes,
                siteFailCounts = _config?.SiteFailCounts,
                autoBlockedSiteKeys = _config?.AutoBlockedSiteKeys,
                libraryPrimed = _config?.LibraryPrimed,
                detectedBaseUrl = _config?.DetectedBaseUrl
            };
        }
    }
}
