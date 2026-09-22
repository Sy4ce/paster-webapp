using System.Globalization;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.HttpOverrides;
using Paster;

var options = PasterOptions.FromEnvironment();

// Kestrel bounds the whole request body and multipart framing sits on top of the
// file itself. The ceiling is deliberately generous relative to the per-file cap:
// a body that is rejected by the transport while the client is still writing gets
// a reset connection, so anything up to the ceiling is read and answered with a
// real 413 in JSON. Absurd bodies are still cut off at the socket.
var transportCeiling = Math.Max(options.MaxFileBytes * 4, 16L * 1024 * 1024);
var bodyLimit = transportCeiling + 256 * 1024;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSingleton(options);
builder.Services.AddSingleton<TempStore>();
builder.Services.AddHostedService<ExpirySweeper>();

builder.Services.Configure<FormOptions>(form =>
{
    form.MultipartBodyLengthLimit = transportCeiling;
    form.ValueLengthLimit = 8 * 1024;
    form.MultipartHeadersLengthLimit = 16 * 1024;
});

builder.WebHost.ConfigureKestrel(kestrel => kestrel.Limits.MaxRequestBodySize = bodyLimit);

// App Service hands the listening port over in PORT. Binding it explicitly keeps
// the deployment independent of the platform's default, while local runs (which
// have no PORT) keep using ASPNETCORE_URLS and launchSettings.json.
if (int.TryParse(Environment.GetEnvironmentVariable("PORT"), out var platformPort) && platformPort > 0)
{
    builder.WebHost.UseUrls($"http://0.0.0.0:{platformPort}");
}

var app = builder.Build();

// App Service terminates TLS in front of the app: without forwarded headers the
// request looks like plain HTTP and every generated link would be http://.
var forwarded = new ForwardedHeadersOptions
{
    ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto,
};
forwarded.KnownIPNetworks.Clear();
forwarded.KnownProxies.Clear();
app.UseForwardedHeaders(forwarded);

app.Use(async (context, next) =>
{
    try
    {
        await next();
    }
    catch (BadHttpRequestException ex) when (ex.StatusCode == StatusCodes.Status413PayloadTooLarge && !context.Response.HasStarted)
    {
        await WriteProblemAsync(context, StatusCodes.Status413PayloadTooLarge,
            $"文件超过 {Human(options.MaxFileBytes)} 上限。");
    }
    catch (Exception ex) when (!context.Response.HasStarted)
    {
        app.Logger.LogError(ex, "Unhandled error while handling {Method} {Path}.",
            context.Request.Method, context.Request.Path);
        await WriteProblemAsync(context, StatusCodes.Status500InternalServerError, "服务器出错了，请稍后再试。");
    }
});

app.UseStaticFiles();

// An explicit route for the shell: with a catch-all fallback endpoint registered,
// the request already has an endpoint by the time middleware runs, which disables
// UseDefaultFiles' rewrite of "/" to "/index.html".
app.MapGet("/", (HttpContext context, IWebHostEnvironment environment) =>
{
    var shell = environment.WebRootFileProvider.GetFileInfo("index.html");
    if (!shell.Exists)
    {
        return Results.Content(NoPage, "text/html; charset=utf-8", statusCode: StatusCodes.Status404NotFound);
    }

    context.Response.Headers.CacheControl = "no-store";
    return Results.Stream(shell.CreateReadStream(), "text/html; charset=utf-8", enableRangeProcessing: false);
});

app.MapGet("/api/health", async (TempStore store, PasterOptions config, CancellationToken cancellationToken) =>
{
    var stats = await store.GetStatsAsync(cancellationToken);
    return Results.Json(new
    {
        status = "ok",
        fileCount = stats.FileCount,
        usedBytes = stats.UsedBytes,
        freeBytes = Math.Max(0, config.MaxTotalBytes - stats.UsedBytes),
        maxFileBytes = config.MaxFileBytes,
        maxTotalBytes = config.MaxTotalBytes,
        ttlSeconds = config.TtlSeconds,
    });
});

app.MapPost("/api/upload", async (
    HttpContext context,
    TempStore store,
    PasterOptions config,
    CancellationToken cancellationToken) =>
{
    if (!context.Request.HasFormContentType)
    {
        return Problem(StatusCodes.Status400BadRequest, "请求必须是 multipart/form-data 上传。");
    }

    IFormCollection form;
    try
    {
        form = await context.Request.ReadFormAsync(cancellationToken);
    }
    catch (Exception ex) when (ex is InvalidDataException or BadHttpRequestException)
    {
        return Problem(StatusCodes.Status413PayloadTooLarge, $"文件超过 {Human(config.MaxFileBytes)} 上限。");
    }

    var upload = form.Files["file"] ?? form.Files.FirstOrDefault();
    if (upload is null || upload.Length == 0)
    {
        return Problem(StatusCodes.Status400BadRequest, "没有收到文件，请选择一个非空文件。");
    }

    SaveOutcome outcome;
    await using (var stream = upload.OpenReadStream())
    {
        outcome = await store.SaveAsync(stream, upload.FileName, cancellationToken);
    }

    switch (outcome.Status)
    {
        case SaveStatus.TooLarge:
            return Problem(StatusCodes.Status413PayloadTooLarge,
                $"文件 {Human(upload.Length)} 超过 {Human(config.MaxFileBytes)} 上限。");
        case SaveStatus.QuotaExceeded:
            return Problem(StatusCodes.Status507InsufficientStorage,
                $"暂存空间已满，当前可用 {Human(outcome.FreeBytes)}，请稍后再试。");
    }

    var file = outcome.File!;
    var baseUrl = config.PublicBaseUrl ?? $"{context.Request.Scheme}://{context.Request.Host}";

    return Results.Json(new
    {
        token = file.Token,
        url = $"{baseUrl}/d/{file.Token}",
        name = file.OriginalName,
        size = file.Size,
        expiresAtUtc = file.ExpiresAtUtc,
        expiresInSeconds = Math.Max(0, (int)Math.Ceiling((file.ExpiresAtUtc - DateTimeOffset.UtcNow).TotalSeconds)),
        freeBytes = outcome.FreeBytes,
    }, statusCode: StatusCodes.Status201Created);
});

app.MapGet("/api/status/{token}", async (string token, TempStore store, CancellationToken cancellationToken) =>
{
    var lookup = await store.ResolveAsync(token, cancellationToken);
    if (lookup.Status == LookupStatus.Found && lookup.File is { } file)
    {
        return Results.Json(new
        {
            exists = true,
            name = file.OriginalName,
            size = file.Size,
            expiresAtUtc = file.ExpiresAtUtc,
            expiresInSeconds = Math.Max(0, (int)Math.Ceiling((file.ExpiresAtUtc - DateTimeOffset.UtcNow).TotalSeconds)),
        });
    }

    return Results.Json(new { exists = false, expired = lookup.Status == LookupStatus.Expired });
});

app.MapGet("/d/{token}", async (string token, HttpContext context, TempStore store, CancellationToken cancellationToken) =>
{
    var lookup = await store.ResolveAsync(token, cancellationToken);

    if (lookup.Status == LookupStatus.Found && lookup.File is { } file && lookup.Path is { } path)
    {
        // Always an attachment: an uploaded .html or .svg must never be rendered
        // on this origin.
        context.Response.Headers.CacheControl = "no-store";
        context.Response.Headers.XContentTypeOptions = "nosniff";
        return Results.File(
            path,
            contentType: "application/octet-stream",
            fileDownloadName: file.OriginalName,
            enableRangeProcessing: true,
            lastModified: file.CreatedAtUtc);
    }

    return lookup.Status == LookupStatus.Expired
        ? Results.Content(GonePage, "text/html; charset=utf-8", statusCode: StatusCodes.Status410Gone)
        : Results.Content(MissingPage, "text/html; charset=utf-8", statusCode: StatusCodes.Status404NotFound);
});

app.MapFallback((HttpContext context) => context.Request.Path.StartsWithSegments("/api")
    ? Results.Json(new { error = "没有这个接口。" }, statusCode: StatusCodes.Status404NotFound)
    : Results.Content(NoPage, "text/html; charset=utf-8", statusCode: StatusCodes.Status404NotFound));

app.Logger.LogInformation(
    "paster ready — storage {Root}, max {MaxFile} per file, {MaxTotal} total, ttl {Ttl}s.",
    options.StorageRoot, Human(options.MaxFileBytes), Human(options.MaxTotalBytes), options.TtlSeconds);

app.Run();

static IResult Problem(int statusCode, string message) => Results.Json(new { error = message }, statusCode: statusCode);

static async Task WriteProblemAsync(HttpContext context, int statusCode, string message)
{
    if (context.Response.HasStarted)
    {
        return;
    }

    context.Response.Clear();
    context.Response.StatusCode = statusCode;
    await context.Response.WriteAsJsonAsync(new { error = message });
}

static string Human(long bytes) => bytes switch
{
    >= 1024 * 1024 => string.Create(CultureInfo.InvariantCulture, $"{bytes / 1048576.0:0.#} MiB"),
    >= 1024 => string.Create(CultureInfo.InvariantCulture, $"{bytes / 1024.0:0.#} KiB"),
    _ => string.Create(CultureInfo.InvariantCulture, $"{bytes} B"),
};

partial class Program
{
    // Shared chrome for the two dead-end pages, kept inline so the app has no
    // runtime dependency on anything outside this repository.
    private const string PageShell = """
        <!doctype html>
        <html lang="zh-CN">
        <head>
          <meta charset="utf-8">
          <meta name="viewport" content="width=device-width, initial-scale=1">
          <title>{0} · Paster</title>
          <link rel="stylesheet" href="/app.css">
        </head>
        <body class="page--dead">
          <main class="dead">
            <p class="dead-stamp">{0}</p>
            <h1 class="dead-title">{1}</h1>
            <p class="dead-body">{2}</p>
            <a class="dead-link" href="/">去寄存一个新文件</a>
          </main>
        </body>
        </html>
        """;

    private static readonly string GonePage = string.Format(
        CultureInfo.InvariantCulture, PageShell, "已作废", "这条取件链接过期了",
        "寄存品在到期时已经从服务器上删除，无法再取回。需要的话，回到首页重新寄存一次。");

    private static readonly string MissingPage = string.Format(
        CultureInfo.InvariantCulture, PageShell, "查无此件", "没有这个取件码",
        "链接可能不完整，或者对应的文件早已销毁。检查一下链接是否被截断。");

    private static readonly string NoPage = string.Format(
        CultureInfo.InvariantCulture, PageShell, "查无此页", "这里没有东西",
        "这个地址不对。寄存文件的入口在首页。");
}
