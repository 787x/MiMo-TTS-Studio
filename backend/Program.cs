using System.Net;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Connections.Features;
using Microsoft.AspNetCore.Http.Features;
using Studio;

var builder = WebApplication.CreateBuilder(args);
builder.Logging.ClearProviders(); // No request text, headers, Key or upstream exceptions in logs.
const string prefix = "/app/mimotts";
bool development = false;
#if DEBUG
development = builder.Environment.IsDevelopment() && Environment.GetEnvironmentVariable("MIMO_DEV_LOCAL") == "1";
#endif
string Require(string variable) => Environment.GetEnvironmentVariable(variable) is { Length: > 0 } value ? value : throw new InvalidOperationException("Missing " + variable);
string root = development ? Path.GetFullPath(".dev") : Require("TRIM_PKGVAR");
var storage = new Storage(development ? Path.Combine(root, "config") : Require("TRIM_PKGETC"), Path.Combine(root, "audio"),
    Path.Combine(development ? root : Require("TRIM_PKGTMP"), "mimotts-work"));
string? socket = development ? null : Path.Combine(Require("TRIM_APPDEST"), "app.sock");
FileStream? instance = null;
if (!development)
{
    // OS file lock also protects against simultaneous lifecycle starts and stale PID files.
    instance = new FileStream(Path.Combine(root, "instance.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
    if (!OperatingSystem.IsLinux()) throw new InvalidOperationException("Production requires Linux Unix socket.");
    if (File.Exists(socket)) File.Delete(socket);
}
builder.WebHost.ConfigureKestrel(server =>
{
    server.Limits.MaxRequestBodySize = 128 * 1024;
    server.Limits.MaxConcurrentConnections = 32;
    server.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(15);
    if (development) server.Listen(IPAddress.Loopback, 5080);
    else server.ListenUnixSocket(socket!, listen => listen.Use(next => async connection =>
    {
        connection.Features.Set(new GatewayPeer(Gateway.IsRootPeer(connection.Features.Get<IConnectionSocketFeature>()?.Socket)));
        await next(connection);
    }));
});
builder.Services.AddSingleton(storage);
builder.Services.AddSingleton(new Mimo(new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false, ConnectTimeout = TimeSpan.FromSeconds(15) }) { Timeout = Timeout.InfiniteTimeSpan }));
builder.Services.AddSingleton<Jobs>();
var app = builder.Build();
string csrf = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
app.Use(async (context, next) =>
{
    if (development)
    {
        context.Features.Set(new GatewayPeer(true));
        context.Request.Headers["X-Trim-Userid"] = "1000";
        context.Request.Headers["X-Trim-Username"] = "developer";
        context.Request.Headers["X-Trim-Isadmin"] = "true";
    }
    context.Response.Headers.CacheControl = "no-store";
    context.Response.Headers.XContentTypeOptions = "nosniff";
    context.Response.Headers.ContentSecurityPolicy = "default-src 'self'; script-src 'self'; style-src 'self' 'unsafe-inline'; img-src 'self' data:; media-src 'self'; connect-src 'self'; frame-ancestors 'self'; object-src 'none'; base-uri 'self'";
    if (!Gateway.Admin(context)) { context.Response.StatusCode = 403; await context.Response.WriteAsJsonAsync(new { error = "仅允许通过 fnOS 可信网关登录的管理员访问。" }); return; }
    if (!HttpMethods.IsGet(context.Request.Method) && !HttpMethods.IsHead(context.Request.Method))
    {
        if (!Gateway.SafeOrigin(context.Request) || context.Request.Headers["X-Studio-CSRF"] != csrf)
        { context.Response.StatusCode = 403; await context.Response.WriteAsJsonAsync(new { error = "来源或 CSRF 校验失败，请从 fnOS 桌面重新打开应用。" }); return; }
        bool sampleUpload = HttpMethods.IsPost(context.Request.Method) && context.Request.Path == prefix + "/api/samples/upload";
        long limit = sampleUpload ? SampleAudio.MaxBytes : 128 * 1024;
        if (sampleUpload && context.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } feature)
            feature.MaxRequestBodySize = limit;
        if (context.Request.ContentLength > limit) { context.Response.StatusCode = 413; return; }
    }
    try { await next(context); }
    catch (InputError e) { context.Response.StatusCode = 400; await context.Response.WriteAsJsonAsync(new { error = e.Message }); }
    catch (BusyError) { context.Response.StatusCode = 409; await context.Response.WriteAsJsonAsync(new { error = "已有生成任务运行中，请等待或取消。" }); }
    catch (BadHttpRequestException) { context.Response.StatusCode = 400; await context.Response.WriteAsJsonAsync(new { error = "请求体无效或过大。" }); }
    catch (Exception) { context.Response.StatusCode = 500; await context.Response.WriteAsJsonAsync(new { error = "服务处理失败。请检查应用存储与运行状态。" }); }
});
app.UsePathBase(prefix);
app.Use(async (context, next) =>
{
    if (context.Request.PathBase != prefix) { context.Response.StatusCode = 404; return; }
    if (!context.Request.Path.HasValue) { context.Response.Redirect(prefix + "/"); return; }
    await next(context);
});
string Owner(HttpContext c) => c.Request.Headers["X-Trim-Userid"].ToString();
app.MapGet("/api/session", () => new { configured = storage.ReadKey() != null, csrf, voices = Composition.Voices, development });
app.MapPut("/api/key", (KeyInput input) => { storage.SetKey(input.Key); return Results.Ok(new { configured = storage.ReadKey() != null }); });
app.MapDelete("/api/key", () => { storage.SetKey(null); return Results.NoContent(); });
app.MapPost("/api/preview", (HttpContext c, ScriptRequest request) =>
{
    var plan = Composition.Plan(request);
    var sample = request.SynthesisMode == "clone" ? storage.GetSample(request.SourceSampleId!, Owner(c)) ?? throw new InputError("参考样本不存在或无权使用。") : null;
    return new { plan.User, plan.Prefix, plan.Segments, plan.Assistant, request.SynthesisMode, model = Composition.Model(request.SynthesisMode), sourceSample = sample };
});
app.MapPost("/api/jobs", (HttpContext c, ScriptRequest request, Jobs jobs) => Results.Json(jobs.Start(Owner(c), request), statusCode: 202));
app.MapGet("/api/jobs", (HttpContext c, Jobs jobs) => jobs.List(Owner(c)));
app.MapGet("/api/jobs/{id}", (HttpContext c, string id, Jobs jobs) => jobs.Get(id, Owner(c)) is { } job ? Results.Ok(job) : Results.NotFound());
app.MapPost("/api/jobs/{id}/cancel", (HttpContext c, string id, Jobs jobs) => jobs.Cancel(id, Owner(c)) is { } job ? Results.Ok(job) : Results.NotFound());
app.MapGet("/api/assets", (HttpContext c) => storage.List(Owner(c)));
app.MapGet("/api/assets/{id}/audio", (HttpContext c, string id) => storage.Open(id, Owner(c)) is { } file ?
    Results.Stream(file, "audio/wav", c.Request.Query.ContainsKey("download") ? $"mimo-{id}.wav" : null, enableRangeProcessing: true) : Results.NotFound());
app.MapPost("/api/assets/{id}/save", (HttpContext c, string id) => storage.Save(id, Owner(c)) is { } asset ? Results.Ok(asset) : Results.NotFound());
app.MapDelete("/api/assets/{id}", (HttpContext c, string id) => storage.Delete(id, Owner(c)) ? Results.NoContent() : Results.NotFound());
var transfers = new SampleTransfers(storage);
app.MapGet("/api/samples", (HttpContext c) => storage.ListSamples(Owner(c)));
app.MapPost("/api/samples/upload", (Func<HttpContext, Task<IResult>>)(c => transfers.Upload(c, Owner(c))));
app.MapGet("/api/samples/{id}/audio", (HttpContext c, string id) => storage.OpenSample(id, Owner(c)) is { } sample ?
    Results.Stream(sample.File, sample.Mime, c.Request.Query.ContainsKey("download") ? $"sample-{id}.{(sample.Mime == "audio/wav" ? "wav" : "mp3")}" : null, enableRangeProcessing: true) : Results.NotFound());
app.MapPut("/api/samples/{id}", (HttpContext c, string id, SampleNameInput input) => storage.RenameSample(id, Owner(c), input.Name) is { } sample ? Results.Ok(sample) : Results.NotFound());
app.MapDelete("/api/samples/{id}", (HttpContext c, string id) => storage.DeleteSample(id, Owner(c)) ? Results.NoContent() : Results.NotFound());
app.MapPost("/api/assets/{id}/sample", (HttpContext c, string id, SampleNameInput input) => transfers.FromAsset(c, id, Owner(c), input.Name));
app.UseDefaultFiles(); app.UseStaticFiles();
app.Lifetime.ApplicationStarted.Register(() =>
{
    if (socket != null && OperatingSystem.IsLinux()) File.SetUnixFileMode(socket, UnixFileMode.UserRead | UnixFileMode.UserWrite);
});
app.Lifetime.ApplicationStopped.Register(() => { if (socket != null) File.Delete(socket); instance?.Dispose(); });
await app.RunAsync();
public sealed record KeyInput(string? Key);
public sealed record SampleNameInput(string Name);
