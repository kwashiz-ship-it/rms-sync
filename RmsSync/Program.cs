using Google.Cloud.SecretManager.V1;

var builder = WebApplication.CreateBuilder(args);

// Cloud Run は PORT 環境変数で待受ポートが渡される
var port = Environment.GetEnvironmentVariable("PORT") ?? "8080";
builder.WebHost.UseUrls($"http://0.0.0.0:{port}");

var app = builder.Build();

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
// ※ ここで “RMSを叩かない” のがポイント。まず Cloud Build を確実に通す。
app.MapGet("/rms/items/sample", async (string? tenant, int? limit) =>
{
    tenant = string.IsNullOrWhiteSpace(tenant) ? "rinkan" : tenant.Trim();
    var hits = Math.Clamp(limit ?? 20, 1, 20);

    // Cloud Run では通常 GOOGLE_CLOUD_PROJECT が入る（入らない場合は環境変数で追加が必要）
    var projectId =
        Environment.GetEnvironmentVariable("GOOGLE_CLOUD_PROJECT")
        ?? Environment.GetEnvironmentVariable("GCP_PROJECT")
        ?? throw new InvalidOperationException("ProjectId が見つかりません。GOOGLE_CLOUD_PROJECT を確認してください。");

    var sm = await SecretManagerServiceClient.CreateAsync();

    async Task<string> GetSecretAsync(string secretId)
    {
        // Secret の latest を参照
        var name = new SecretVersionName(projectId, secretId, "latest");
        var res = await sm.AccessSecretVersionAsync(name);
        return res.Payload.Data.ToStringUtf8();
    }

    // Secret を取得（値そのものは返さない・ログにも出さない）
    var serviceSecret = await GetSecretAsync($"rms-{tenant}-serviceSecret");
    var licenseKey = await GetSecretAsync($"rms-{tenant}-licenseKey");

    // 返すのは “取れたかどうか” と “長さ” だけ（漏洩防止）
    return Results.Ok(new
    {
        tenant,
        hits,
        secretOk = true,
        serviceSecretLength = serviceSecret.Length,
        licenseKeyLength = licenseKey.Length
    });
});

app.Run();

record WeatherForecast(DateOnly Date, int TemperatureC, string? Summary)
{
    public int TemperatureF => 32 + (int)(TemperatureC / 0.5556);
}
