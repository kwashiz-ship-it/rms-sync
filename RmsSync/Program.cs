using Google.Cloud.SecretManager.V1;
using Rakuten.RMS.Api;
using Rakuten.RMS.Api.ItemAPI20;

var builder = WebApplication.CreateBuilder(args);

var port = Environment.GetEnvironmentVariable("PORT") ?? "8080";
builder.WebHost.UseUrls($"http://0.0.0.0:{port}");

var app = builder.Build();

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

app.MapGet("/rms/items/sample", async (string? tenant = "rinkan", int? limit = 20) =>
{
    tenant = string.IsNullOrWhiteSpace(tenant) ? "rinkan" : tenant.Trim();
    var hits = Math.Clamp(limit ?? 20, 1, 20);

    var projectId =
        Environment.GetEnvironmentVariable("GOOGLE_CLOUD_PROJECT")
        ?? Environment.GetEnvironmentVariable("GCP_PROJECT")
        ?? throw new InvalidOperationException("ProjectId が見つかりません。GOOGLE_CLOUD_PROJECT を確認してください。");

    var sm = await SecretManagerServiceClient.CreateAsync();

    async Task<string> GetSecretAsync(string secretId)
    {
        var name = new SecretVersionName(projectId, secretId, "latest");
        var res = await sm.AccessSecretVersionAsync(name);
        return res.Payload.Data.ToStringUtf8();
    }

    var serviceSecret = await GetSecretAsync($"rms-{tenant}-serviceSecret");
    var licenseKey = await GetSecretAsync($"rms-{tenant}-licenseKey");

    var provider = new ServiceProvider(serviceSecret, licenseKey);
    var api = provider.GetItemAPI20();

    var condition = new SearchCondition
    {
        hits = hits,
        offset = 1,
        isInventoryIncluded = true
    };

    var result = api.Search(condition);

    var items = (result?.results ?? new List<Item>())
        .Select(x => new
        {
            manageNumber = x.manageNumber,
            itemNumber = x.itemNumber,
            title = x.title
        })
        .ToList();

    return Results.Ok(new
    {
        tenant,
        hits,
        numFound = result?.numFound ?? 0,
        returned = items.Count,
        items
    });
});

app.Run();

record WeatherForecast(DateOnly Date, int TemperatureC, string? Summary)
{
    public int TemperatureF => 32 + (int)(TemperatureC / 0.5556);
}
