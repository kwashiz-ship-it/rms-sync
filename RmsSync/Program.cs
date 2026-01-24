using Google.Cloud.SecretManager.V1;
using Microsoft.AspNetCore.Diagnostics;

var builder = WebApplication.CreateBuilder(args);

// Cloud Run は PORT 環境変数で待受ポートが渡される
var port = Environment.GetEnvironmentVariable("PORT") ?? "8080";
builder.WebHost.UseUrls($"http://0.0.0.0:{port}");

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

// ★ グローバル例外（ここに来たら必ずログ＋JSON返却）
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
}); // ←★ ここが必須（あなたのコードはこれが無くてCS1002）

// 既存の疎通用エンドポイント（残す）
var summaries = new[]
{
    "Freezing", "Bracing", "Chilly", "Cool", "Mild", "Warm", "Balmy", "Hot", "Sweltering", "Scorching"
};

app.MapGet("/weatherforecast", () =>
{
    var forecast = Enumerable.Range(1, 5).Select(index =>
        new WeatherForecast
        (
            DateOnly.FromDateTime(DateTime.Now.AddDays(index)),
            Random.Shared.Next(-20, 55),
            summaries[Random.Shared.Next(summaries.Length)]
        ))
        .ToArray();

    return forecast;
})
.WithName("GetWeatherForecast");

// ✅ 新規：RMSのテスト用（いまは Secret が取れるかだけ確認する）
// ※ ここで “RMSを叩かない” のがポイント。まず Secret疎通を確実にする。
app.MapGet("/rms/items/sample", async (string? tenant, int? limit) =>
{
    // tenant は許可リスト（将来マルチテナント化しても安全）
    tenant = string.IsNullOrWhiteSpace(tenant) ? "rinkan" : tenant.Trim().ToLowerInvariant();
    if (tenant != "rinkan")
        return Results.BadRequest(new { ok = false, message = "invalid tenant" });

    var hits = Math.Clamp(limit ?? 20, 1, 20);

    // Cloud Run では通常 GOOGLE_CLOUD_PROJECT が入る（入らない場合は環境変数で追加が必要）
    var projectId =
        Environment.GetEnvironmentVariable("GOOGLE_CLOUD_PROJECT")
        ?? Environment.GetEnvironmentVariable("GCP_PROJECT")
        ?? throw new InvalidOperationException("ProjectId が見つかりません。GOOGLE_CLOUD_PROJECT を確認してください。");

    var sm = await SecretManagerServiceClient.CreateAsync();

    static async Task<string> GetSecretAsync(SecretManagerServiceClient smClient, string projectId, string secretId)
    {
        // Secret の latest を参照
        var name = new SecretVersionName(projectId, secretId, "latest");
        var res = await smClient.AccessSecretVersionAsync(name);
        return res.Payload.Data.ToStringUtf8();
    }

    // Secret を取得（値そのものは返さない・ログにも出さない）
    var serviceSecretId = $"rms-{tenant}-serviceSecret";
    var licenseKeyId = $"rms-{tenant}-licenseKey";

    var serviceSecret = await GetSecretAsync(sm, projectId, serviceSecretId);
    var licenseKey = await GetSecretAsync(sm, projectId, licenseKeyId);

    // 返すのは “取れたかどうか” と “長さ” だけ（漏洩防止）
    return Results.Ok(new
    {
        ok = true,
        tenant,
        hits,
        secretOk = true,
        serviceSecretLength = serviceSecret?.Length ?? 0,
        licenseKeyLength = licenseKey?.Length ?? 0
    });
});

app.Run();

record WeatherForecast(DateOnly Date, int TemperatureC, string? Summary)
{
    public int TemperatureF => 32 + (int)(TemperatureC / 0.5556);
}
