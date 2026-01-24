using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.AspNetCore.Diagnostics;

var builder = WebApplication.CreateBuilder(args);

// Cloud Run は PORT 環境変数で待受ポートが渡される
var port = Environment.GetEnvironmentVariable("PORT") ?? "8080";
builder.WebHost.UseUrls($"http://0.0.0.0:{port}");

// HttpClient
builder.Services.AddHttpClient("gcp", client =>
{
    client.Timeout = TimeSpan.FromSeconds(10);
});

var app = builder.Build();

// ★ 入口ログ（リクエストが来たら必ず出る）
app.Use(async (ctx, next) =>
{
    var logger = ctx.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("REQ");
    logger.LogInformation("IN {Method} {Path}{Query} trace={Trace}",
        ctx.Request.Method,
        ctx.Request.Path,
        ctx.Request.QueryString,
        ctx.Request.Headers["x-cloud-trace-context"].ToString());

    await next();

    logger.LogInformation("OUT {StatusCode} {Method} {Path} trace={Trace}",
        ctx.Response.StatusCode,
        ctx.Request.Method,
        ctx.Request.Path,
        ctx.Request.Headers["x-cloud-trace-context"].ToString());
});

// ★ グローバル例外（必ずログ＋JSON返却）
app.UseExceptionHandler(errorApp =>
{
    errorApp.Run(async context =>
    {
        var logger = context.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("EX");
        var feature = context.Features.Get<IExceptionHandlerFeature>();
        var ex = feature?.Error;

        logger.LogError(ex, "UNHANDLED trace={Trace}",
            context.Request.Headers["x-cloud-trace-context"].ToString());

        context.Response.ContentType = "application/json";
        context.Response.StatusCode = 500;

        await context.Response.WriteAsJsonAsync(new
        {
            ok = false,
            traceId = context.Request.Headers["x-cloud-trace-context"].ToString(),
            message = ex?.Message ?? "unknown",
            exceptionType = ex?.GetType().FullName ?? "unknown"
        });
    });
});

// 疎通用
app.MapGet("/weatherforecast", () => Results.Ok(new { ok = true, now = DateTimeOffset.UtcNow }));

// ✅ Secret 疎通（REST版：gRPC不使用）
app.MapGet("/rms/items/sample", async (HttpContext http, string? tenant, int? limit, IHttpClientFactory httpClientFactory) =>
{
    var logger = http.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("RMS_SAMPLE");

    tenant = string.IsNullOrWhiteSpace(tenant) ? "rinkan" : tenant.Trim().ToLowerInvariant();
    if (tenant != "rinkan")
        return Results.BadRequest(new { ok = false, message = "invalid tenant" });

    var hits = Math.Clamp(limit ?? 20, 1, 20);

    var projectId =
        Environment.GetEnvironmentVariable("GOOGLE_CLOUD_PROJECT")
        ?? Environment.GetEnvironmentVariable("GCP_PROJECT")
        ?? throw new InvalidOperationException("ProjectId が見つかりません。GOOGLE_CLOUD_PROJECT を確認してください。");

    // 1) Cloud Run メタデータサーバからアクセストークン取得（実行SA）
    var accessToken = await GetMetadataAccessTokenAsync(httpClientFactory, logger);

    // 2) Secret Manager REST で latest を access
    var serviceSecretId = $"rms-{tenant}-serviceSecret";
    var licenseKeyId = $"rms-{tenant}-licenseKey";

    var serviceSecret = await AccessSecretViaRestAsync(httpClientFactory, projectId, serviceSecretId, accessToken, logger);
    var licenseKey = await AccessSecretViaRestAsync(httpClientFactory, projectId, licenseKeyId, accessToken, logger);

    return Results.Ok(new
    {
        ok = true,
        tenant,
        hits,
        secretOk = true,
        serviceSecretLength = serviceSecret.Length,
        licenseKeyLength = licenseKey.Length
    });
});

app.Run();

static async Task<string> GetMetadataAccessTokenAsync(IHttpClientFactory factory, ILogger logger)
{
    // Cloud Run metadata server
    var url = "http://metadata.google.internal/computeMetadata/v1/instance/service-accounts/default/token";
    var client = factory.CreateClient("gcp");

    using var req = new HttpRequestMessage(HttpMethod.Get, url);
    req.Headers.Add("Metadata-Flavor", "Google");

    using var resp = await client.SendAsync(req);
    var body = await resp.Content.ReadAsStringAsync();

    if (!resp.IsSuccessStatusCode)
        throw new InvalidOperationException($"Failed to get access token from metadata. status={(int)resp.StatusCode} body={body}");

    using var doc = JsonDocument.Parse(body);
    if (!doc.RootElement.TryGetProperty("access_token", out var tokenEl))
        throw new InvalidOperationException("Metadata token response missing access_token.");

    var token = tokenEl.GetString();
    if (string.IsNullOrWhiteSpace(token))
        throw new InvalidOperationException("Metadata access_token is empty.");

    return token!;
}

static async Task<string> AccessSecretViaRestAsync(
    IHttpClientFactory factory,
    string projectId,
    string secretId,
    string accessToken,
    ILogger logger)
{
    // REST: projects/*/secrets/*/versions/latest:access
    var url = $"https://secretmanager.googleapis.com/v1/projects/{projectId}/secrets/{secretId}/versions/latest:access";
    var client = factory.CreateClient("gcp");

    using var req = new HttpRequestMessage(HttpMethod.Get, url);
    req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

    using var resp = await client.SendAsync(req);
    var body = await resp.Content.ReadAsStringAsync();

    if (!resp.IsSuccessStatusCode)
        throw new InvalidOperationException($"Secret access failed. secret={secretId} status={(int)resp.StatusCode} body={body}");

    using var doc = JsonDocument.Parse(body);

    // response: { payload: { data: "BASE64..." } }
    var dataB64 = doc.RootElement.GetProperty("payload").GetProperty("data").GetString();
    if (string.IsNullOrWhiteSpace(dataB64))
        throw new InvalidOperationException($"Secret payload is empty. secret={secretId}");

    var bytes = Convert.FromBase64String(dataB64);
    var value = System.Text.Encoding.UTF8.GetString(bytes).Trim();

    if (string.IsNullOrWhiteSpace(value))
        throw new InvalidOperationException($"Secret value is empty after decode. secret={secretId}");

    return value;
}
