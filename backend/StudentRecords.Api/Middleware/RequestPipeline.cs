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

public static class RequestPipeline
{
    public static void UseCampusPipeline(this WebApplication app)
    {
        app.Use(async (context, next) =>
        {
            if (!context.Request.Path.StartsWithSegments("/api"))
            {
                await next();
                return;
            }

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

            try
            {
                await next();
            }
            finally
            {
                await context.RequestServices.GetRequiredService<OperatingMetrics>().Observe(context, System.Diagnostics.Stopwatch.GetElapsedTime(started));
            }
        });
        app.Use(async (c, next) =>
        {
            c.Response.Headers["X-Content-Type-Options"] = "nosniff";
            c.Response.Headers["Referrer-Policy"] = "strict-origin-when-cross-origin";
            c.Response.Headers["Content-Security-Policy"] = "default-src 'self'; script-src 'self'; style-src 'self' 'unsafe-inline'; img-src 'self' data:; connect-src 'self'; frame-ancestors 'none'; base-uri 'self'; form-action 'self'";
            c.Response.Headers["X-Frame-Options"] = "DENY";
            if (!app.Environment.IsDevelopment())
                c.Response.Headers["Strict-Transport-Security"] = "max-age=31536000";
            if (c.Request.Path.StartsWithSegments("/api"))
                c.Response.Headers.CacheControl = "no-cache, no-store";
            try
            {
                await next();
                if (c.Response.StatusCode >= 400 && !c.Response.HasStarted && c.Response.ContentLength == null)
                {
                    if (c.Response.StatusCode is 401 or 403)
                        await c.RequestServices.GetRequiredService<ApiSupport>().Audit("Access", c.Request.Path, "Denied");
                    await Problem(c, c.Response.StatusCode, c.Response.StatusCode switch
                    {
                        401 => "Vui lòng đăng nhập.",
                        403 => "Bạn không có quyền truy cập.",
                        404 => "Không tìm thấy dữ liệu.",
                        429 => "Quá nhiều yêu cầu. Vui lòng thử lại sau.",
                        _ => "Yêu cầu không hợp lệ."
                    });
                }
            }
            catch (Exception ex) when (!c.Response.HasStarted)
            {
                app.Logger.LogWarning("API request failed: {ErrorType}; trace {TraceId}", ex.GetType().Name, c.TraceIdentifier);
                var code = ex switch
                {
                    ApiException a => a.Status,
                    AntiforgeryValidationException => 400,
                    JsonException => 400,
                    BadHttpRequestException => 400,
                    MongoWriteException m when m.WriteError.Category == ServerErrorCategory.DuplicateKey => 409,
                    MongoException => 503,
                    TimeoutException => 503,
                    _ => 500
                };
                var title = ex is ApiException ? ex.Message : code switch
                {
                    400 => "Dữ liệu hoặc mã CSRF không hợp lệ.",
                    409 => "Dữ liệu đã tồn tại.",
                    503 => "Dịch vụ cơ sở dữ liệu tạm thời không khả dụng.",
                    _ => "Đã xảy ra lỗi. Vui lòng thử lại sau."
                };
                try
                {
                    await c.RequestServices.GetRequiredService<ApiSupport>().Audit("Request", c.Request.Path, code is 401 or 403 or 404 ? "Denied" : "Failure");
                }
                catch
                {
                }

                await Problem(c, code, title);
            }
        });
        app.UseCors();
        app.Use(async (c, next) =>
        {
            if (c.Request.Path.StartsWithSegments("/api") && c.Request.Path != "/api/health" && c.Request.Path != "/api/auth/csrf" && !c.RequestServices.GetRequiredService<Database>().Ready)
                throw new ApiException(503, "Cơ sở dữ liệu chưa sẵn sàng. Vui lòng khởi động lại API sau khi kiểm tra kết nối.");
            await next();
        });
        if (!app.Configuration.GetValue<bool>("RateLimiting:Disabled") || !app.Environment.IsDevelopment())
            app.UseRateLimiter();
        app.UseAuthentication();
        app.UseAuthorization();
        app.Use(async (c, next) =>
        {
            if (c.Request.Path.StartsWithSegments("/api") && !HttpMethods.IsGet(c.Request.Method) && !HttpMethods.IsHead(c.Request.Method) && !HttpMethods.IsOptions(c.Request.Method))
                await c.RequestServices.GetRequiredService<IAntiforgery>().ValidateRequestAsync(c);
            await next();
        });
    }

    static Task Problem(HttpContext c, int status, string title)
    {
        c.Response.StatusCode = status;
        return c.Response.WriteAsJsonAsync(new ProblemDetails { Status = status, Title = title, Extensions = { ["traceId"] = c.TraceIdentifier } }, options: (JsonSerializerOptions?)null, contentType: "application/problem+json");
    }
}
