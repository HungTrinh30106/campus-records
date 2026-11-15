using System.Security.Claims;
using System.Text.Json;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.DataProtection;
using MongoDB.Driver;
using StudentRecords.Api;
var builder = WebApplication.CreateBuilder(args);
var dataProtection = builder.Services.AddDataProtection().SetApplicationName("CampusRecords");
if (builder.Configuration["DataProtection:KeysPath"] is { Length: > 0 } keysPath)
    dataProtection.PersistKeysToFileSystem(new DirectoryInfo(Path.GetFullPath(keysPath)));
builder.Logging.AddFilter("Microsoft.AspNetCore", LogLevel.Warning);
builder.Services.AddSingleton<Database>(); builder.Services.AddHttpContextAccessor(); builder.Services.AddScoped<ApiSupport>();
builder.Services.AddIdentityCore<AppUser>(o => { o.Password.RequiredLength = 10; o.Password.RequireDigit = true; o.Password.RequireLowercase = true; o.Password.RequireUppercase = true; o.Password.RequireNonAlphanumeric = true; o.User.RequireUniqueEmail = true; o.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(15); o.Lockout.MaxFailedAccessAttempts = 5; }).AddUserStore<MongoUserStore>();
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme).AddCookie(o =>
{
    o.Cookie.Name = "CampusRecords.Session"; o.Cookie.HttpOnly = true; o.Cookie.SameSite = SameSiteMode.Lax; o.Cookie.SecurePolicy = builder.Environment.IsDevelopment() ? CookieSecurePolicy.SameAsRequest : CookieSecurePolicy.Always; o.ExpireTimeSpan = TimeSpan.FromMinutes(30); o.SlidingExpiration = false;
    o.Events.OnRedirectToLogin = c => { c.Response.StatusCode = 401; return Task.CompletedTask; }; o.Events.OnRedirectToAccessDenied = c => { c.Response.StatusCode = 403; return Task.CompletedTask; };
    o.Events.OnValidatePrincipal = async c => { var id = c.Principal?.FindFirstValue(ClaimTypes.NameIdentifier); var db = c.HttpContext.RequestServices.GetRequiredService<Database>(); var u = id == null ? null : await db.Users.Find(x => x.Id == id).FirstOrDefaultAsync(); if (u == null || !u.Enabled || u.LockedUntil > DateTime.UtcNow || u.SecurityStamp != c.Principal?.FindFirstValue("stamp")) { c.RejectPrincipal(); await c.HttpContext.SignOutAsync(); return; } c.HttpContext.Items["user"] = u; };
});
builder.Services.AddAuthorization(); builder.Services.AddAntiforgery(o => { o.HeaderName = "X-CSRF-TOKEN"; o.Cookie.Name = "CampusRecords.Csrf"; o.Cookie.HttpOnly = true; o.Cookie.SameSite = SameSiteMode.Lax; o.Cookie.SecurePolicy = builder.Environment.IsDevelopment() ? CookieSecurePolicy.SameAsRequest : CookieSecurePolicy.Always; });
builder.Services.Configure<Microsoft.AspNetCore.Http.Features.FormOptions>(o => o.MultipartBodyLengthLimit = 6 * 1024 * 1024); builder.WebHost.ConfigureKestrel(o => o.Limits.MaxRequestBodySize = 6 * 1024 * 1024);
builder.Services.AddCors(o => o.AddDefaultPolicy(p => p.WithOrigins(builder.Configuration.GetSection("Cors:Origins").Get<string[]>() ?? (builder.Environment.IsDevelopment() ? ["http://localhost:5173", "http://127.0.0.1:5173"] : Array.Empty<string>())).AllowAnyHeader().AllowAnyMethod().AllowCredentials()));
builder.Services.AddRateLimiter(o => { o.RejectionStatusCode = 429; o.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(c => RateLimitPartition.GetFixedWindowLimiter(c.Connection.RemoteIpAddress?.ToString() ?? "unknown", _ => new FixedWindowRateLimiterOptions { PermitLimit = 300, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 })); o.AddPolicy("login", c => RateLimitPartition.GetFixedWindowLimiter(c.Connection.RemoteIpAddress?.ToString() ?? "unknown", _ => new FixedWindowRateLimiterOptions { PermitLimit = 20, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 })); });
builder.Services.AddSingleton<MaintenanceGate>();
builder.Services.AddSingleton<OperatingMetrics>();
builder.Services.AddSingleton<DepartmentCache>();
builder.Services.AddSingleton<DatabaseWatcher>();
builder.Services.AddSingleton<BackupCoordinator>();
builder.Services.AddHostedService(services => services.GetRequiredService<DatabaseWatcher>());
builder.Services.AddHostedService(services => services.GetRequiredService<BackupCoordinator>());
var app = builder.Build();
app.Use(async (context, next) =>
{
    if (!context.Request.Path.StartsWithSegments("/api")) { await next(); return; }
    var started = System.Diagnostics.Stopwatch.GetTimestamp();
    var gate = context.RequestServices.GetRequiredService<MaintenanceGate>();
    using var lease = gate.TryEnter();
    if (lease == null)
    {
        context.Response.Headers.RetryAfter = "5";
        context.Response.Headers.CacheControl = "no-cache, no-store";
        context.Response.Headers["X-Content-Type-Options"] = "nosniff";
        await Problem(context, 503, "Đang sao lưu dữ liệu. Vui lòng thử lại sau.");
        await context.RequestServices.GetRequiredService<OperatingMetrics>().Observe(context, System.Diagnostics.Stopwatch.GetElapsedTime(started), persistAlerts: false);
        return;
    }
    try { await next(); }
    finally { await context.RequestServices.GetRequiredService<OperatingMetrics>().Observe(context, System.Diagnostics.Stopwatch.GetElapsedTime(started)); }
});
app.Use(async (c, next) =>
{
    c.Response.Headers["X-Content-Type-Options"] = "nosniff"; c.Response.Headers["Referrer-Policy"] = "strict-origin-when-cross-origin"; c.Response.Headers["Content-Security-Policy"] = "default-src 'self'; script-src 'self'; style-src 'self' 'unsafe-inline'; img-src 'self' data:; connect-src 'self'; frame-ancestors 'none'; base-uri 'self'; form-action 'self'"; c.Response.Headers["X-Frame-Options"] = "DENY"; if (!app.Environment.IsDevelopment()) c.Response.Headers["Strict-Transport-Security"] = "max-age=31536000"; if (c.Request.Path.StartsWithSegments("/api")) c.Response.Headers.CacheControl = "no-cache, no-store";
    try { await next(); if (c.Response.StatusCode >= 400 && !c.Response.HasStarted && c.Response.ContentLength == null) { if (c.Response.StatusCode is 401 or 403) await c.RequestServices.GetRequiredService<ApiSupport>().Audit("Access", c.Request.Path, "Denied"); await Problem(c, c.Response.StatusCode, c.Response.StatusCode switch { 401 => "Vui lòng đăng nhập.", 403 => "Bạn không có quyền truy cập.", 404 => "Không tìm thấy dữ liệu.", 429 => "Quá nhiều yêu cầu. Vui lòng thử lại sau.", _ => "Yêu cầu không hợp lệ." }); } }
    catch (Exception ex) when (!c.Response.HasStarted) { app.Logger.LogWarning("API request failed: {ErrorType}; trace {TraceId}", ex.GetType().Name, c.TraceIdentifier); var code = ex switch { ApiException a => a.Status, AntiforgeryValidationException => 400, JsonException => 400, BadHttpRequestException => 400, MongoWriteException m when m.WriteError.Category == ServerErrorCategory.DuplicateKey => 409, MongoException => 503, TimeoutException => 503, _ => 500 }; var title = ex is ApiException ? ex.Message : code switch { 400 => "Dữ liệu hoặc mã CSRF không hợp lệ.", 409 => "Dữ liệu đã tồn tại.", 503 => "Dịch vụ cơ sở dữ liệu tạm thời không khả dụng.", _ => "Đã xảy ra lỗi. Vui lòng thử lại sau." }; try { await c.RequestServices.GetRequiredService<ApiSupport>().Audit("Request", c.Request.Path, code is 401 or 403 or 404 ? "Denied" : "Failure"); } catch { } await Problem(c, code, title); }
});
app.UseCors(); app.Use(async (c,next) => { if(c.Request.Path.StartsWithSegments("/api") && c.Request.Path != "/api/health" && c.Request.Path != "/api/auth/csrf" && !c.RequestServices.GetRequiredService<Database>().Ready) throw new ApiException(503,"Cơ sở dữ liệu chưa sẵn sàng. Vui lòng khởi động lại API sau khi kiểm tra kết nối."); await next(); }); if (!builder.Configuration.GetValue<bool>("RateLimiting:Disabled") || !app.Environment.IsDevelopment()) app.UseRateLimiter(); app.UseAuthentication(); app.UseAuthorization();
app.Use(async (c, next) => { if (c.Request.Path.StartsWithSegments("/api") && !HttpMethods.IsGet(c.Request.Method) && !HttpMethods.IsHead(c.Request.Method) && !HttpMethods.IsOptions(c.Request.Method)) await c.RequestServices.GetRequiredService<IAntiforgery>().ValidateRequestAsync(c); await next(); });
app.MapGet("/api/health", async (Database db) => { try { if(!db.Ready)return Results.Json(new{status="unhealthy"},statusCode:503); await db.Db.RunCommandAsync<MongoDB.Bson.BsonDocument>(new MongoDB.Bson.BsonDocument("ping", 1)); return Results.Ok(new { status = "healthy" }); } catch { return Results.Json(new { status = "unhealthy" }, statusCode: 503); } });
app.MapGet("/api/auth/csrf", (HttpContext c, IAntiforgery csrf) => Results.Ok(new { token = csrf.GetAndStoreTokens(c).RequestToken }));
app.MapGet("/api/auth/me", (ApiSupport s) => Results.Ok(Dto.User(s.User))).RequireAuthorization();
app.MapPost("/api/auth/register", async (JsonElement input, UserManager<AppUser> users, ApiSupport s) => { ApiSupport.Fields(input, "fullName", "email", "password"); var email = ApiSupport.Text(input, "email", 254); ApiSupport.Email(email); var u = new AppUser { Email = email, FullName = ApiSupport.Text(input, "fullName", 120) }; var result = await users.CreateAsync(u, ApiSupport.Password(input, "password")); if (!result.Succeeded) throw new ApiException(400, string.Join(" ", result.Errors.Select(e => IdentityMessage(e.Code)))); await s.Audit("Register", u.Id); return Results.Created("/api/auth/me", Dto.User(u)); }).RequireRateLimiting("login");
app.MapPost("/api/auth/login", async (JsonElement input, UserManager<AppUser> users, HttpContext c, ApiSupport s) => { ApiSupport.Fields(input, "email", "password"); var email = ApiSupport.Text(input, "email", 254); var password = ApiSupport.Password(input, "password"); var u = await users.FindByEmailAsync(email); if (u == null || !u.Enabled || await users.IsLockedOutAsync(u)) { await s.Audit("Login", "", "Failure"); throw new ApiException(401, "Thông tin đăng nhập không đúng hoặc tài khoản bị khóa."); } if (!await users.CheckPasswordAsync(u, password)) { await IdentityOperations.RecordFailedLogin(users, u); await s.Audit("Login", u.Id, "Failure"); throw new ApiException(401, "Thông tin đăng nhập không đúng hoặc tài khoản bị khóa."); } var reset = await users.ResetAccessFailedCountAsync(u); if (!reset.Succeeded) throw new ApiException(409, "Tài khoản đã thay đổi. Vui lòng thử lại."); u.LastLoginAt = DateTime.UtcNow; if (!(await users.UpdateAsync(u)).Succeeded) throw new ApiException(409, "Tài khoản đã thay đổi. Vui lòng thử lại."); await SignIn(c, u); c.Items["user"] = u; await s.Audit("Login", u.Id); return Results.Ok(Dto.User(u)); }).RequireRateLimiting("login");
app.MapPost("/api/auth/logout", async (HttpContext c, ApiSupport s) => { await s.Audit("Logout"); await c.SignOutAsync(); return Results.NoContent(); }).RequireAuthorization();
app.MapPost("/api/auth/change-password", async (JsonElement input, UserManager<AppUser> users, ApiSupport s, HttpContext c) => { ApiSupport.Fields(input, "currentPassword", "newPassword"); if(ApiSupport.Password(input,"currentPassword")==ApiSupport.Password(input,"newPassword"))throw new ApiException(400,"Mật khẩu mới phải khác mật khẩu hiện tại."); var result = await users.ChangePasswordAsync(s.User, ApiSupport.Password(input, "currentPassword"), ApiSupport.Password(input, "newPassword")); if (!result.Succeeded) throw new ApiException(400, string.Join(" ", result.Errors.Select(e => IdentityMessage(e.Code)))); await c.SignOutAsync(); await s.Audit("ChangePassword", s.User.Id); return Results.NoContent(); }).RequireAuthorization();
OperationsRoutes.Map(app); Routes.Map(app); app.MapFallback("/api/{**path}", () => Results.NotFound()).AllowAnonymous();
if (Directory.Exists(Path.Combine(app.Environment.ContentRootPath, "wwwroot"))) { app.UseDefaultFiles(); app.UseStaticFiles(); app.MapFallbackToFile("index.html"); }
using (var scope = app.Services.CreateScope()) { var db = scope.ServiceProvider.GetRequiredService<Database>(); try { await db.Indexes(); await Seed.Run(scope.ServiceProvider, app.Configuration, app.Environment); } catch (Exception ex) when (ex is MongoConnectionException or TimeoutException) { app.Logger.LogWarning("MongoDB chưa sẵn sàng. API health trả 503; kiểm tra cấu hình Mongo và khởi động lại để tạo indexes/seed."); } }
app.Run();
static Task Problem(HttpContext c, int status, string title) { c.Response.StatusCode = status; return c.Response.WriteAsJsonAsync(new ProblemDetails { Status = status, Title = title, Extensions = { ["traceId"] = c.TraceIdentifier } }, options: (JsonSerializerOptions?)null, contentType: "application/problem+json"); }
static Task SignIn(HttpContext c, AppUser u) => c.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, u.Id), new Claim(ClaimTypes.Name, u.Email), new Claim(ClaimTypes.Role, u.Role), new Claim("stamp", u.SecurityStamp)], CookieAuthenticationDefaults.AuthenticationScheme)));
static string IdentityMessage(string code) => code switch { "DuplicateEmail" or "DuplicateUserName" => "Email đã tồn tại.", "PasswordMismatch" => "Mật khẩu hiện tại không đúng.", _ => "Mật khẩu tối thiểu 10 ký tự, gồm chữ hoa, chữ thường, chữ số và ký tự đặc biệt; hoặc dữ liệu tài khoản không hợp lệ." };
public partial class Program { }




