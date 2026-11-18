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

namespace StudentRecords.Api;

public static class ServiceRegistration
{
    public static void AddCampusServices(this WebApplicationBuilder builder)
    {
        var dataProtection = builder.Services.AddDataProtection().SetApplicationName("CampusRecords");
        if (builder.Configuration["DataProtection:KeysPath"] is { Length: > 0 } keysPath)
            dataProtection.PersistKeysToFileSystem(new DirectoryInfo(Path.GetFullPath(keysPath)));
        builder.Logging.AddFilter("Microsoft.AspNetCore", LogLevel.Warning);
        builder.Services.AddSingleton<Database>();
        builder.Services.AddHttpContextAccessor();
        builder.Services.AddScoped<ApiSupport>();
        builder.Services.AddIdentityCore<AppUser>(o =>
        {
            o.Password.RequiredLength = 10;
            o.Password.RequireDigit = true;
            o.Password.RequireLowercase = true;
            o.Password.RequireUppercase = true;
            o.Password.RequireNonAlphanumeric = true;
            o.User.RequireUniqueEmail = true;
            o.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(15);
            o.Lockout.MaxFailedAccessAttempts = 5;
        }).AddUserStore<MongoUserStore>();
        builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme).AddCookie(o =>
        {
            o.Cookie.Name = "CampusRecords.Session";
            o.Cookie.HttpOnly = true;
            o.Cookie.SameSite = SameSiteMode.Lax;
            o.Cookie.SecurePolicy = builder.Environment.IsDevelopment() ? CookieSecurePolicy.SameAsRequest : CookieSecurePolicy.Always;
            o.ExpireTimeSpan = TimeSpan.FromMinutes(30);
            o.SlidingExpiration = false;
            o.Events.OnRedirectToLogin = c =>
            {
                c.Response.StatusCode = 401;
                return Task.CompletedTask;
            };
            o.Events.OnRedirectToAccessDenied = c =>
            {
                c.Response.StatusCode = 403;
                return Task.CompletedTask;
            };
            o.Events.OnValidatePrincipal = async c =>
            {
                var id = c.Principal?.FindFirstValue(ClaimTypes.NameIdentifier);
                var db = c.HttpContext.RequestServices.GetRequiredService<Database>();
                var u = id == null ? null : await db.Users.Find(x => x.Id == id).FirstOrDefaultAsync();
                if (u == null || !u.Enabled || u.LockedUntil > DateTime.UtcNow || u.SecurityStamp != c.Principal?.FindFirstValue("stamp"))
                {
                    c.RejectPrincipal();
                    await c.HttpContext.SignOutAsync();
                    return;
                }

                c.HttpContext.Items["user"] = u;
            };
        });
        builder.Services.AddAuthorization();
        builder.Services.AddAntiforgery(o =>
        {
            o.HeaderName = "X-CSRF-TOKEN";
            o.Cookie.Name = "CampusRecords.Csrf";
            o.Cookie.HttpOnly = true;
            o.Cookie.SameSite = SameSiteMode.Lax;
            o.Cookie.SecurePolicy = builder.Environment.IsDevelopment() ? CookieSecurePolicy.SameAsRequest : CookieSecurePolicy.Always;
        });
        builder.Services.Configure<Microsoft.AspNetCore.Http.Features.FormOptions>(o => o.MultipartBodyLengthLimit = 6 * 1024 * 1024);
        builder.WebHost.ConfigureKestrel(o => o.Limits.MaxRequestBodySize = 6 * 1024 * 1024);
        builder.Services.AddCors(o => o.AddDefaultPolicy(p => p.WithOrigins(builder.Configuration.GetSection("Cors:Origins").Get<string[]>() ?? (builder.Environment.IsDevelopment() ? ["http://localhost:5173", "http://127.0.0.1:5173"] : Array.Empty<string>())).AllowAnyHeader().AllowAnyMethod().AllowCredentials()));
        builder.Services.AddRateLimiter(o =>
        {
            o.RejectionStatusCode = 429;
            o.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(c => RateLimitPartition.GetFixedWindowLimiter(c.Connection.RemoteIpAddress?.ToString() ?? "unknown", _ => new FixedWindowRateLimiterOptions { PermitLimit = 300, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
            o.AddPolicy("login", c => RateLimitPartition.GetFixedWindowLimiter(c.Connection.RemoteIpAddress?.ToString() ?? "unknown", _ => new FixedWindowRateLimiterOptions { PermitLimit = 20, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
        });
        builder.Services.AddSingleton<MaintenanceGate>();
        builder.Services.AddSingleton<OperatingMetrics>();
        builder.Services.AddSingleton<DepartmentCache>();
        builder.Services.AddSingleton<DatabaseWatcher>();
        builder.Services.AddSingleton<BackupCoordinator>();
        builder.Services.AddHostedService(services => services.GetRequiredService<DatabaseWatcher>());
        builder.Services.AddHostedService(services => services.GetRequiredService<BackupCoordinator>());
        builder.Services.AddControllers(options =>
        {
            options.SuppressImplicitRequiredAttributeForNonNullableReferenceTypes = true;
            options.Filters.Add<RequestValidationFilter>();
        });
        builder.Services.AddScoped<DepartmentsService>();
        builder.Services.AddScoped<StudentsService>();
        builder.Services.AddScoped<DocumentsService>();
        builder.Services.AddScoped<ReportsService>();
        builder.Services.AddScoped<UsersService>();
        builder.Services.AddScoped<AuditService>();
        builder.Services.AddScoped<DashboardService>();
        builder.Services.AddScoped<SecurityService>();
        builder.Services.AddScoped<HealthService>();
        builder.Services.AddScoped<AuthService>();
        builder.Services.AddScoped<OperationsService>();
    }
}
