using MongoDB.Driver;
using StudentRecords.Api;

var builder = WebApplication.CreateBuilder(args);
builder.AddCampusServices();
var app = builder.Build();
app.UseCampusPipeline();
app.MapControllers();
app.MapFallback("/api/{**path}", () => Results.NotFound()).AllowAnonymous();
if (Directory.Exists(Path.Combine(app.Environment.ContentRootPath, "wwwroot")))
{
    app.UseDefaultFiles();
    app.UseStaticFiles();
    app.MapFallbackToFile("index.html");
}

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<Database>();
    try
    {
        await db.Indexes();
        await Seed.Run(scope.ServiceProvider, app.Configuration, app.Environment);
    }
    catch (Exception ex) when (ex is MongoConnectionException or TimeoutException)
    {
        app.Logger.LogWarning("MongoDB chưa sẵn sàng. API health trả 503; kiểm tra cấu hình Mongo và khởi động lại để tạo indexes/seed.");
    }
}

app.Run();
public partial class Program
{
}
