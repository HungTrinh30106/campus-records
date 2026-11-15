using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.AspNetCore.Hosting;
using MongoDB.Driver;
using Xunit;

public class ApiFactory : WebApplicationFactory<Program>
{
    public static readonly string Password = "Test-" + Guid.NewGuid() + "Aa9!";
    public readonly string Database;
    internal bool PreserveDatabase { get; init; }
    private readonly Dictionary<string, string?> overrides;
    private string MongoUri => overrides.GetValueOrDefault("Mongo:ConnectionString") ?? Environment.GetEnvironmentVariable("TEST_MONGO_URI") ?? "mongodb://localhost:27017";
    public ApiFactory() : this(new()) { }
    internal ApiFactory(Dictionary<string, string?> overrides)
    {
        this.overrides = overrides;
        Database = overrides.GetValueOrDefault("Mongo:Database") ?? "student_records_test_" + Guid.NewGuid().ToString("N");
    }
    protected override void ConfigureWebHost(IWebHostBuilder builder) => builder.UseEnvironment("Development").ConfigureAppConfiguration((_, c) => c.AddInMemoryCollection(new Dictionary<string, string?>
    {
        ["Mongo:ConnectionString"] = MongoUri,
        ["Mongo:Database"] = Database,
        ["Seed:Enabled"] = "true",
        ["Seed:Password"] = Password,
        ["Storage:Path"] = Path.Combine(Path.GetTempPath(), Database),
        ["RateLimiting:Disabled"] = "true",
        ["Backup:Password"] = null, ["Backup:Path"] = null,
        ["Backup:Enabled"] = "false", ["ChangeStreams:Enabled"] = "false"
    }.Concat(overrides).GroupBy(x => x.Key).ToDictionary(x => x.Key, x => x.Last().Value)));
    public HttpClient Client() => CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true, AllowAutoRedirect = false });
    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing)
        {
            if (!PreserveDatabase) new MongoClient(MongoUri).DropDatabase(Database);
            var path = Path.GetFullPath(Path.Combine(Path.GetTempPath(), Database));
            if (path.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase) && Directory.Exists(path)) Directory.Delete(path, true);
        }
    }
}

public class SecurityTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory factory;
    public SecurityTests(ApiFactory factory) => this.factory = factory;
    private static async Task<JsonObject> Json(HttpResponseMessage r) { r.EnsureSuccessStatusCode(); return (await r.Content.ReadFromJsonAsync<JsonObject>())!; }
    private static async Task Csrf(HttpClient c) { var j = await Json(await c.GetAsync("/api/auth/csrf")); c.DefaultRequestHeaders.Remove("X-CSRF-TOKEN"); c.DefaultRequestHeaders.Add("X-CSRF-TOKEN", j["token"]!.ToString()); }
    private async Task<HttpClient> Login(string who) { var c = factory.Client(); await Csrf(c); (await c.PostAsJsonAsync("/api/auth/login", new { email = who + "@campus.local", password = ApiFactory.Password })).EnsureSuccessStatusCode(); await Csrf(c); return c; }
    [Fact]
    public async Task Unsafe_auth_requires_csrf_and_anonymous_records_require_auth()
    {
        var c = factory.Client(); Assert.Equal(HttpStatusCode.BadRequest, (await c.PostAsJsonAsync("/api/auth/login", new { email = "admin@campus.local", password = ApiFactory.Password })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await c.GetAsync("/api/students")).StatusCode);
    }
    [Fact]
    public async Task Staff_scope_masks_sensitive_fields_and_rejects_PII_updates()
    {
        var c = await Login("staff"); var list = await Json(await c.GetAsync("/api/students?pageSize=50")); var rows = list["items"]!.AsArray(); Assert.True(rows.Count >= 6);
        Assert.All(rows, r => { Assert.True(r!["isMasked"]!.GetValue<bool>()); Assert.Null(r["gpa"]); Assert.Contains("*", r["email"]!.ToString()); });
        var id = rows[0]!["id"]!.ToString(); Assert.Equal(HttpStatusCode.Forbidden, (await c.PutAsJsonAsync("/api/students/" + id, new { email = "changed@example.org" })).StatusCode);
        var admin = await Login("admin"); var all = await Json(await admin.GetAsync("/api/students?pageSize=50")); var other = all["items"]!.AsArray().First(x => x!["departmentId"]!.ToString() != rows[0]!["departmentId"]!.ToString());
        Assert.Equal(HttpStatusCode.NotFound, (await c.GetAsync("/api/students/" + other!["id"])).StatusCode);
    }
    [Fact]
    public async Task Student_IDOR_and_public_registration_cannot_claim_a_record()
    {
        var c = await Login("student"); var me = await Json(await c.GetAsync("/api/auth/me")); var admin = await Login("admin"); var all = await Json(await admin.GetAsync("/api/students")); var other = all["items"]!.AsArray().First(x => x!["id"]!.ToString() != me["studentId"]!.ToString());
        Assert.Equal(HttpStatusCode.NotFound, (await c.GetAsync("/api/students/" + other!["id"])).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await c.GetAsync("/api/reports")).StatusCode);
        var anon = factory.Client(); await Csrf(anon); var response = await anon.PostAsJsonAsync("/api/auth/register", new { fullName = "Người mới", email = Guid.NewGuid() + "@example.org", password = ApiFactory.Password, role = "Admin", studentId = other!["id"]!.ToString() });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
    [Fact]
    public async Task Disabled_user_cookie_is_revoked_immediately()
    {
        var anon = factory.Client(); await Csrf(anon); var email = Guid.NewGuid() + "@example.org"; (await anon.PostAsJsonAsync("/api/auth/register", new { fullName = "Kiểm thử", email, password = ApiFactory.Password })).EnsureSuccessStatusCode();
        (await anon.PostAsJsonAsync("/api/auth/login", new { email, password = ApiFactory.Password })).EnsureSuccessStatusCode(); await Csrf(anon); var me = await Json(await anon.GetAsync("/api/auth/me")); var admin = await Login("admin");
        (await admin.PutAsJsonAsync("/api/users/" + me["id"], new { enabled = false })).EnsureSuccessStatusCode(); Assert.Equal(HttpStatusCode.Unauthorized, (await anon.GetAsync("/api/auth/me")).StatusCode);
    }
    [Fact]
    public async Task Five_wrong_passwords_lock_account()
    {
        var c = factory.Client(); await Csrf(c); var email = Guid.NewGuid() + "@example.org"; (await c.PostAsJsonAsync("/api/auth/register", new { fullName = "Khóa tài khoản", email, password = ApiFactory.Password })).EnsureSuccessStatusCode();
        for (var i = 0; i < 5; i++) Assert.Equal(HttpStatusCode.Unauthorized, (await c.PostAsJsonAsync("/api/auth/login", new { email, password = "Incorrect-password9!" })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await c.PostAsJsonAsync("/api/auth/login", new { email, password = ApiFactory.Password })).StatusCode);
    }
    [Fact]
    public async Task Documents_validate_bytes_enforce_scope_and_export_masks_csv()
    {
        var admin = await Login("admin"); var all = await Json(await admin.GetAsync("/api/students?pageSize=50")); var own = all["items"]!.AsArray()[1]; var id = own!["id"]!.ToString();
        using var bad = new MultipartFormDataContent(); var badBytes = new ByteArrayContent("not a pdf"u8.ToArray()); badBytes.Headers.ContentType = new("application/pdf"); bad.Add(badBytes, "file", "bad.pdf"); Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsync($"/api/students/{id}/documents", bad)).StatusCode);
        using var good = new MultipartFormDataContent(); var bytes = new ByteArrayContent("%PDF-1.4\n%%EOF"u8.ToArray()); bytes.Headers.ContentType = new("application/pdf"); good.Add(bytes, "file", "test.pdf"); var doc = await Json(await admin.PostAsync($"/api/students/{id}/documents", good));
        var download = await admin.GetAsync($"/api/documents/{doc["id"]}/download"); download.EnsureSuccessStatusCode(); Assert.Equal("attachment", download.Content.Headers.ContentDisposition!.DispositionType);
        var student = await Login("student"); var me = await Json(await student.GetAsync("/api/auth/me")); if (me["studentId"]!.ToString() != id) Assert.Equal(HttpStatusCode.NotFound, (await student.GetAsync($"/api/documents/{doc["id"]}/download")).StatusCode);
        var staff = await Login("staff"); var csv = await staff.GetStringAsync("/api/reports/export"); Assert.Contains("***", csv); Assert.DoesNotContain("@campus.local", csv);
    }
    [Fact]
    public async Task Staff_can_enter_own_department_record_but_cannot_reassign_or_set_sensitive_fields()
    {
        var staff = await Login("staff"); var me = await Json(await staff.GetAsync("/api/auth/me"));
        var row = new { studentCode = "TEST" + Guid.NewGuid().ToString("N")[..8], fullName = "=HYPERLINK(\"bad\")", email = "entry@example.org", phone = "0901234567", dateOfBirth = "2003-05-12", gender = "Other", address = "Hà Nội", departmentId = me["departmentId"]!.ToString(), className = "CNTT24A", enrollmentYear = 2024, status = "Active" };
        var created = await Json(await staff.PostAsJsonAsync("/api/students", row)); Assert.True(created["isMasked"]!.GetValue<bool>()); var id = created["id"]!.ToString();
        (await staff.PutAsJsonAsync("/api/students/" + id, new { fullName = "=HYPERLINK(\"bad\")", status = "Paused" })).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.Forbidden, (await staff.PutAsJsonAsync("/api/students/" + id, new { nationalId = "079123456789" })).StatusCode);
        var export = await staff.GetStringAsync("/api/reports/export"); Assert.Contains("\"'=HYPERLINK", export);
        var admin = await Login("admin"); (await admin.DeleteAsync("/api/students/" + id)).EnsureSuccessStatusCode();
    }
    [Fact]
    public async Task Password_change_invalidates_old_sessions_and_signs_out_current_session()
    {
        var email = Guid.NewGuid() + "@example.org"; var first = factory.Client(); await Csrf(first); (await first.PostAsJsonAsync("/api/auth/register", new { fullName = "Đổi mật khẩu", email, password = ApiFactory.Password })).EnsureSuccessStatusCode();
        (await first.PostAsJsonAsync("/api/auth/login", new { email, password = ApiFactory.Password })).EnsureSuccessStatusCode(); await Csrf(first);
        var second = factory.Client(); await Csrf(second); (await second.PostAsJsonAsync("/api/auth/login", new { email, password = ApiFactory.Password })).EnsureSuccessStatusCode();
        (await first.PostAsJsonAsync("/api/auth/change-password", new { currentPassword = ApiFactory.Password, newPassword = "New-" + ApiFactory.Password })).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.Unauthorized, (await first.GetAsync("/api/auth/me")).StatusCode); Assert.Equal(HttpStatusCode.Unauthorized, (await second.GetAsync("/api/auth/me")).StatusCode);
    }
    [Fact]
    public async Task Invalid_sort_regex_search_duplicate_and_oversized_input_are_handled()
    {
        var admin = await Login("admin"); Assert.Equal(HttpStatusCode.BadRequest, (await admin.GetAsync("/api/students?sort=passwordHash")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.GetAsync("/api/students?pageSize=51")).StatusCode);
        var literal = await Json(await admin.GetAsync("/api/students?search=" + Uri.EscapeDataString(".*"))); Assert.Equal(0, literal["total"]!.GetValue<int>());
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.GetAsync("/api/students?search=" + new string('a', 101))).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await admin.PostAsJsonAsync("/api/departments", new { code = "CNTT", name = "Trùng khoa" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.PutAsJsonAsync("/api/students/not-an-id", new { fullName = "x" })).StatusCode);
    }
    [Fact]
    public async Task Concurrent_login_failures_cannot_bypass_five_attempt_lockout()
    {
        var c = factory.Client(); await Csrf(c); var email = Guid.NewGuid() + "@example.org"; (await c.PostAsJsonAsync("/api/auth/register", new { fullName = "Đồng thời", email, password = ApiFactory.Password })).EnsureSuccessStatusCode();
        var requests = Enumerable.Range(0, 5).Select(_ => c.PostAsJsonAsync("/api/auth/login", new { email, password = "Incorrect-password9!" }));
        foreach (var response in await Task.WhenAll(requests)) Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await c.PostAsJsonAsync("/api/auth/login", new { email, password = ApiFactory.Password })).StatusCode);
    }
    [Fact]
    public async Task Csrf_is_required_for_register_logout_updates_and_multipart()
    {
        var anon = factory.Client(); Assert.Equal(HttpStatusCode.BadRequest, (await anon.PostAsJsonAsync("/api/auth/register", new { fullName = "a", email = "a@example.org", password = ApiFactory.Password })).StatusCode);
        var admin = await Login("admin"); var rows = await Json(await admin.GetAsync("/api/students")); var id = rows["items"]![0]!["id"]!.ToString(); admin.DefaultRequestHeaders.Remove("X-CSRF-TOKEN");
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync("/api/auth/logout", new { })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PutAsJsonAsync("/api/students/" + id, new { fullName = "Other" })).StatusCode);
        using var multipart = new MultipartFormDataContent(); multipart.Add(new ByteArrayContent("%PDF-1.4"u8.ToArray()), "file", "test.pdf"); Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsync($"/api/students/{id}/documents", multipart)).StatusCode);
    }
    [Fact]
    public async Task Role_change_revokes_sessions_and_admin_cannot_disable_itself()
    {
        var c = factory.Client(); await Csrf(c); var email = Guid.NewGuid() + "@example.org"; (await c.PostAsJsonAsync("/api/auth/register", new { fullName = "Vai trò", email, password = ApiFactory.Password })).EnsureSuccessStatusCode(); (await c.PostAsJsonAsync("/api/auth/login", new { email, password = ApiFactory.Password })).EnsureSuccessStatusCode(); await Csrf(c); var me = await Json(await c.GetAsync("/api/auth/me")); var admin = await Login("admin"); var adminMe = await Json(await admin.GetAsync("/api/auth/me"));
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PutAsJsonAsync("/api/users/" + adminMe["id"], new { enabled = false })).StatusCode);
        (await admin.PutAsJsonAsync("/api/users/" + me["id"], new { role = "Admin" })).EnsureSuccessStatusCode(); Assert.Equal(HttpStatusCode.Unauthorized, (await c.GetAsync("/api/auth/me")).StatusCode);
    }
    [Fact]
    public async Task Student_can_edit_contacts_but_cannot_edit_academic_fields()
    {
        var c = await Login("student"); var me = await Json(await c.GetAsync("/api/auth/me")); var id = me["studentId"]!.ToString();
        var result = await Json(await c.PutAsJsonAsync("/api/students/" + id, new { address = "Thành phố Hồ Chí Minh" })); Assert.False(result["isMasked"]!.GetValue<bool>());
        Assert.Equal(HttpStatusCode.Forbidden, (await c.PutAsJsonAsync("/api/students/" + id, new { status = "Graduated" })).StatusCode); Assert.Equal(HttpStatusCode.Forbidden, (await c.DeleteAsync("/api/students/" + id)).StatusCode);
        var dashboard = await Json(await c.GetAsync("/api/dashboard")); Assert.Equal(1, dashboard["totalStudents"]!.GetValue<int>());
    }
    [Fact]
    public async Task Malformed_typed_fields_return_validation_problem_instead_of_500()
    {
        var staff = await Login("staff"); var me = await Json(await staff.GetAsync("/api/auth/me")); var payload = new { studentCode = "BAD001", fullName = "Kiểm thử", email = "a@example.org", phone = "0901234567", dateOfBirth = "2003-05-12", gender = "Other", address = "Huế", departmentId = me["departmentId"]!.ToString(), className = "CNTT24A", enrollmentYear = 2024, status = "Active", nationalId = 123 };
        var response = await staff.PostAsJsonAsync("/api/students", payload); Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode); Assert.Equal("application/problem+json", response.Content.Headers.ContentType!.MediaType); var error = (await response.Content.ReadFromJsonAsync<JsonObject>())!; Assert.NotNull(error["traceId"]);
    }
    [Fact]
    public async Task Admin_department_and_student_lifecycle_is_persisted_and_audited()
    {
        var admin = await Login("admin"); var department = await Json(await admin.PostAsJsonAsync("/api/departments", new { code = "QA" + Guid.NewGuid().ToString("N")[..6], name = "Khoa kiểm thử" })); var depId = department["id"]!.ToString();
        var row = new { studentCode = "QA" + Guid.NewGuid().ToString("N")[..10], fullName = "Sinh viên kiểm thử", email = "qa@example.org", phone = "0901234567", nationalId = "079123456789", dateOfBirth = "2003-01-23", gender = "Female", address = "Đà Nẵng", departmentId = depId, className = "QA24A", enrollmentYear = 2024, status = "Active", gpa = 3.4 };
        var created = await Json(await admin.PostAsJsonAsync("/api/students", row)); var id = created["id"]!.ToString(); Assert.False(created["isMasked"]!.GetValue<bool>()); Assert.Equal(3.4, created["gpa"]!.GetValue<double>());
        Assert.Equal(HttpStatusCode.Conflict, (await admin.DeleteAsync("/api/departments/" + depId)).StatusCode);
        (await admin.PutAsJsonAsync("/api/students/" + id, new { gpa = 3.8, status = "Graduated" })).EnsureSuccessStatusCode(); var read = await Json(await admin.GetAsync("/api/students/" + id)); Assert.Equal(3.8, read["gpa"]!.GetValue<double>());
        var audit = await Json(await admin.GetAsync("/api/audit?action=CreateStudent&pageSize=50")); Assert.Contains(audit["items"]!.AsArray(), a => a!["target"]!.ToString() == id);
        (await admin.DeleteAsync("/api/students/" + id)).EnsureSuccessStatusCode(); Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync("/api/students/" + id)).StatusCode); (await admin.DeleteAsync("/api/departments/" + depId)).EnsureSuccessStatusCode();
    }
    [Fact]
    public async Task Unlinked_registration_has_zero_records_and_cannot_access_admin_routes()
    {
        var c = factory.Client(); await Csrf(c); var email = Guid.NewGuid() + "@example.org"; var registered = await Json(await c.PostAsJsonAsync("/api/auth/register", new { fullName = "Chưa xác minh", email, password = ApiFactory.Password })); Assert.Equal("Student", registered["role"]!.ToString()); Assert.Null(registered["studentId"]); Assert.Equal(HttpStatusCode.Unauthorized, (await c.GetAsync("/api/auth/me")).StatusCode);
        (await c.PostAsJsonAsync("/api/auth/login", new { email, password = ApiFactory.Password })).EnsureSuccessStatusCode(); await Csrf(c); var list = await Json(await c.GetAsync("/api/students")); Assert.Empty(list["items"]!.AsArray()); var dashboard = await Json(await c.GetAsync("/api/dashboard")); Assert.Equal(0, dashboard["totalStudents"]!.GetValue<int>());
        foreach (var path in new[] { "/api/users", "/api/audit" }) Assert.Equal(HttpStatusCode.Forbidden, (await c.GetAsync(path)).StatusCode);
    }
    [Fact]
    public async Task Files_over_five_MiB_are_rejected_and_valid_delete_removes_download()
    {
        var admin = await Login("admin"); var all = await Json(await admin.GetAsync("/api/students")); var id = all["items"]![1]!["id"]!.ToString();
        using var large = new MultipartFormDataContent(); var data = new byte[5 * 1024 * 1024 + 1]; "%PDF-"u8.CopyTo(data); var bytes = new ByteArrayContent(data); bytes.Headers.ContentType = new("application/pdf"); large.Add(bytes, "file", "oversized.pdf"); Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsync($"/api/students/{id}/documents", large)).StatusCode);
        using var valid = new MultipartFormDataContent(); var pdf = new ByteArrayContent("%PDF-1.4\n%%EOF"u8.ToArray()); pdf.Headers.ContentType = new("application/pdf"); valid.Add(pdf, "file", "delete.pdf"); var doc = await Json(await admin.PostAsync($"/api/students/{id}/documents", valid));
        (await admin.DeleteAsync("/api/documents/" + doc["id"])).EnsureSuccessStatusCode(); Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync($"/api/documents/{doc["id"]}/download")).StatusCode);
    }
    [Fact]
    public async Task Missing_database_health_is_503_and_does_not_leak_connection_details()
    {
        using var unavailable = factory.WithWebHostBuilder(b => b.ConfigureAppConfiguration((_, c) => c.AddInMemoryCollection(new Dictionary<string, string?> { ["Mongo:ConnectionString"] = "mongodb://127.0.0.1:1", ["Seed:Enabled"] = "false" })));
        var client = unavailable.CreateClient(); var response = await client.GetAsync("/api/health"); Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode); var body = await response.Content.ReadAsStringAsync(); Assert.DoesNotContain("mongodb", body); Assert.Contains("unhealthy", body);
    }
    [Fact]
    public async Task Login_rate_limit_returns_429_and_security_headers_are_set()
    {
        using var limited = factory.WithWebHostBuilder(b => b.ConfigureAppConfiguration((_, c) => c.AddInMemoryCollection(new Dictionary<string, string?> { ["RateLimiting:Disabled"] = "false" })));
        var client = limited.CreateClient(); await Csrf(client); HttpResponseMessage? response = null; for (var i = 0; i < 21; i++) response = await client.PostAsJsonAsync("/api/auth/login", new { email = "missing@example.org", password = ApiFactory.Password }); Assert.Equal(HttpStatusCode.TooManyRequests, response!.StatusCode); Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single()); Assert.Contains("frame-ancestors 'none'", response.Headers.GetValues("Content-Security-Policy").Single());
    }
    [Fact]
    public async Task Password_bytes_are_preserved_instead_of_trimmed()
    {
        var c = factory.Client(); await Csrf(c); var email = Guid.NewGuid() + "@example.org"; var password = " " + ApiFactory.Password + " "; (await c.PostAsJsonAsync("/api/auth/register", new { fullName = "Mật khẩu chính xác", email, password })).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.Unauthorized, (await c.PostAsJsonAsync("/api/auth/login", new { email, password = ApiFactory.Password })).StatusCode);
        (await c.PostAsJsonAsync("/api/auth/login", new { email, password })).EnsureSuccessStatusCode();
    }
    [Fact]
    public async Task Concurrent_partial_updates_do_not_overwrite_fields_outside_caller_permissions()
    {
        var admin=await Login("admin");var student=await Login("student");var me=await Json(await student.GetAsync("/api/auth/me"));var id=me["studentId"]!.ToString();
        for(var i=0;i<12;i++) {
            var address="Cập nhật đồng thời "+i;var gpa=2.0+i/10.0;
            var responses=await Task.WhenAll(admin.PutAsJsonAsync("/api/students/"+id,new{gpa}),student.PutAsJsonAsync("/api/students/"+id,new{address}));foreach(var r in responses)r.EnsureSuccessStatusCode();
            var row=await Json(await admin.GetAsync("/api/students/"+id));Assert.Equal(gpa,row["gpa"]!.GetValue<double>());Assert.Equal(address,row["address"]!.ToString());
        }
    }
    [Fact]
    public async Task Admin_can_create_without_national_id_and_new_password_must_differ()
    {
        var admin=await Login("admin");var list=await Json(await admin.GetAsync("/api/students"));var dep=list["items"]![0]!["departmentId"]!.ToString();
        var created=await Json(await admin.PostAsJsonAsync("/api/students",new{studentCode="OPTIONAL"+Guid.NewGuid().ToString("N")[..5],fullName="CCCD chưa cung cấp",email="optional@example.org",phone="0901234567",nationalId="",dateOfBirth="2003-05-12",gender="Other",address="Hà Nội",departmentId=dep,className="CNTT24A",enrollmentYear=2024,status="Active"}));
        Assert.Equal("",created["nationalId"]!.ToString());(await admin.DeleteAsync("/api/students/"+created["id"])).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.BadRequest,(await admin.PostAsJsonAsync("/api/auth/change-password",new{currentPassword=ApiFactory.Password,newPassword=ApiFactory.Password})).StatusCode);
    }
}
