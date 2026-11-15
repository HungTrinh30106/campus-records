using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Driver;
using StudentRecords.Api;
using Xunit;

public class OperationsTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory factory;
    public OperationsTests(ApiFactory factory) => this.factory = factory;
    internal static async Task<JsonObject> Json(HttpResponseMessage response)
    {
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<JsonObject>())!;
    }
    internal static async Task Csrf(HttpClient client)
    {
        var data = await Json(await client.GetAsync("/api/auth/csrf"));
        client.DefaultRequestHeaders.Remove("X-CSRF-TOKEN");
        client.DefaultRequestHeaders.Add("X-CSRF-TOKEN", data["token"]!.ToString());
    }
    private async Task<HttpClient> Login(string role)
    {
        var client = factory.Client(); await Csrf(client);
        (await client.PostAsJsonAsync("/api/auth/login", new { email = role + "@campus.local", password = ApiFactory.Password })).EnsureSuccessStatusCode();
        await Csrf(client); return client;
    }
    [Fact]
    public async Task Operations_are_admin_only_and_manual_backup_requires_configuration_and_csrf()
    {
        var anonymous = factory.Client(); var staff = await Login("staff"); var admin = await Login("admin");
        foreach (var path in new[] { "/api/operations", "/api/operations/alerts", "/api/operations/database-events", "/api/operations/backups" })
        {
            Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync(path)).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await staff.GetAsync(path)).StatusCode);
            (await admin.GetAsync(path)).EnsureSuccessStatusCode();
        }
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await admin.PostAsJsonAsync("/api/operations/backups", new { })).StatusCode);
        admin.DefaultRequestHeaders.Remove("X-CSRF-TOKEN");
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync("/api/operations/backups", new { })).StatusCode);
    }
    [Fact]
    public async Task Metrics_count_denials_and_errors_without_returning_credentials_or_personal_data()
    {
        var admin = await Login("admin"); var anonymous = factory.Client();
        for (var i = 0; i < 5; i++) await anonymous.GetAsync("/api/students");
        var data = await Json(await admin.GetAsync("/api/operations"));
        Assert.True(data["totalRequests"]!.GetValue<long>() >= 5);
        Assert.True(data["deniedRequests"]!.GetValue<long>() >= 5);
        Assert.True(data["uptimeSeconds"]!.GetValue<double>() >= 0);
        Assert.InRange(data["cpuPercent"]!.GetValue<double>(), 0, 100);
        Assert.Equal("healthy", data["databaseStatus"]!.ToString());
        Assert.NotNull(data["changeStream"]!["status"]); Assert.NotNull(data["cache"]!["misses"]);
        var json = data.ToJsonString(); Assert.DoesNotContain(ApiFactory.Password, json); Assert.DoesNotContain("@campus.local", json); Assert.DoesNotContain("mongodb://", json);
        var alerts = await Json(await admin.GetAsync("/api/operations/alerts"));
        Assert.Contains(alerts["items"]!.AsArray(), x => x!["type"]!.ToString() == "accessDenied");
        var id = alerts["items"]![0]!["id"]!.ToString();
        Assert.Equal(HttpStatusCode.NoContent, (await admin.PostAsJsonAsync($"/api/operations/alerts/{id}/acknowledge", new { })).StatusCode);
        var reread = await Json(await admin.GetAsync("/api/operations/alerts?pageSize=50"));
        Assert.True(reread["items"]!.AsArray().Single(x => x!["id"]!.ToString() == id)!["acknowledged"]!.GetValue<bool>());
    }
    [Fact]
    public async Task Login_rate_limit_and_upload_rejections_create_safe_persistent_alerts()
    {
        using var isolated = new ApiFactory(new() { ["RateLimiting:Disabled"] = "false" });
        var admin = isolated.Client(); await Csrf(admin);
        (await admin.PostAsJsonAsync("/api/auth/login", new { email = "admin@campus.local", password = ApiFactory.Password })).EnsureSuccessStatusCode();
        await Csrf(admin);
        var anonymous = isolated.Client(); await Csrf(anonymous);
        for (var i = 0; i < 25; i++) await anonymous.PostAsJsonAsync("/api/auth/login", new { email = "dummy@example.org", password = "Invalid-test-password9!" });
        var students = await Json(await admin.GetAsync("/api/students")); var id = students["items"]![0]!["id"]!.ToString();
        for (var i = 0; i < 3; i++)
        {
            using var form = new MultipartFormDataContent();
            var file = new ByteArrayContent(new byte[] { 1, 2, 3 }); file.Headers.ContentType = new("image/gif");
            form.Add(file, "file", "rejected.gif");
            Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsync($"/api/students/{id}/documents", form)).StatusCode);
        }
        var metrics = await Json(await admin.GetAsync("/api/operations"));
        Assert.True(metrics["rateLimitedRequests"]!.GetValue<long>() >= 3);
        var alerts = await Json(await admin.GetAsync("/api/operations/alerts?pageSize=50"));
        foreach (var type in new[] { "loginFailure", "rateLimited", "uploadRejected" }) Assert.Contains(alerts["items"]!.AsArray(), x => x!["type"]!.ToString() == type);
        Assert.DoesNotContain("dummy@example.org", alerts.ToJsonString()); Assert.DoesNotContain("Invalid-test-password9!", alerts.ToJsonString());
    }
    [Fact]
    public async Task Opt_in_change_stream_on_standalone_reports_degraded_and_persistent_safe_alert()
    {
        using var isolated = new ApiFactory(new() { ["ChangeStreams:Enabled"] = "true" });
        _ = isolated.Client();
        var watcher = isolated.Services.GetRequiredService<DatabaseWatcher>();
        var db = isolated.Services.GetRequiredService<Database>();
        await BackupCoordinationTests.WaitUntil(async () => await db.Alerts.CountDocumentsAsync(x => x.Type == "changeStreamDegraded") > 0);
        var snapshot = System.Text.Json.JsonSerializer.SerializeToElement(watcher.Snapshot);
        Assert.Equal("degraded", snapshot.GetProperty("status").GetString());
        Assert.StartsWith("MongoCommand", snapshot.GetProperty("lastError").GetString());
        Assert.DoesNotContain("mongodb://", snapshot.ToString());
    }
    [Fact]
    public async Task Department_cache_invalidates_create_update_delete_and_dashboard_keeps_staff_scope()
    {
        var admin = await Login("admin"); var staff = await Login("staff");
        await admin.GetAsync("/api/departments"); await admin.GetAsync("/api/departments");
        var before = await Json(await admin.GetAsync("/api/operations"));
        Assert.True(before["cache"]!["hits"]!.GetValue<long>() > 0);
        var created = await Json(await admin.PostAsJsonAsync("/api/departments", new { code = "CACHE" + Guid.NewGuid().ToString("N")[..6], name = "Cache original" }));
        var id = created["id"]!.ToString();
        async Task<JsonArray> Departments() => (await (await admin.GetAsync("/api/departments")).Content.ReadFromJsonAsync<JsonArray>())!;
        Assert.Contains(await Departments(), x => x!["id"]!.ToString() == id);
        (await admin.PutAsJsonAsync("/api/departments/" + id, new { code = created["code"]!.ToString(), name = "Cache changed" })).EnsureSuccessStatusCode();
        Assert.Equal("Cache changed", (await Departments()).Single(x => x!["id"]!.ToString() == id)!["name"]!.ToString());
        (await admin.DeleteAsync("/api/departments/" + id)).EnsureSuccessStatusCode();
        Assert.DoesNotContain(await Departments(), x => x!["id"]!.ToString() == id);
        var dashboard = await Json(await staff.GetAsync("/api/dashboard"));
        var rows = await Json(await staff.GetAsync("/api/students?pageSize=50"));
        Assert.Equal(rows["total"]!.GetValue<long>(), dashboard["totalStudents"]!.GetValue<long>());
        Assert.True(dashboard["recentStudents"]!.AsArray().Count <= 5);
        Assert.All(dashboard["recentStudents"]!.AsArray(), x => { Assert.True(x!["isMasked"]!.GetValue<bool>()); Assert.Equal("***@***", x["email"]!.ToString()); });
    }
}
