using MongoDB.Bson;
using MongoDB.Driver;

namespace StudentRecords.Api;

public static class OperationsRoutes
{
    public static void Map(WebApplication app)
    {
        var api = app.MapGroup("/api/operations").RequireAuthorization(policy => policy.RequireRole("Admin"));
        api.MapGet("", async (Database db, OperatingMetrics metrics, MaintenanceGate gate, DatabaseWatcher watcher, BackupCoordinator backup, DepartmentCache cache, CancellationToken ct) =>
        {
            var status = "unhealthy";
            try { await db.Db.RunCommandAsync<BsonDocument>(new BsonDocument("ping", 1), cancellationToken: ct); status = db.Ready ? "healthy" : "unhealthy"; }
            catch (MongoException) { } catch (TimeoutException) { }
            return Results.Ok(metrics.Snapshot(gate, watcher, backup, cache, status));
        });
        api.MapGet("/alerts", (HttpRequest request, Database db, CancellationToken ct) => Page(db.Alerts, request, Builders<OperationAlert>.Sort.Descending(x => x.LastSeenAt), ct));
        api.MapPost("/alerts/{id}/acknowledge", async (string id, Database db, CancellationToken ct) =>
        {
            ApiSupport.Id(id);
            var result = await db.Alerts.UpdateOneAsync(x => x.Id == id, Builders<OperationAlert>.Update.Set(x => x.Acknowledged, true), cancellationToken: ct);
            if (result.MatchedCount == 0) throw new ApiException(404, "Không tìm thấy cảnh báo.");
            return Results.NoContent();
        });
        api.MapGet("/database-events", async (HttpRequest request, Database db, CancellationToken ct) =>
        {
            var (page, size) = ApiSupport.Pagination(request);
            var filter = Builders<DatabaseEvent>.Filter.Empty;
            var rows = await db.DatabaseEvents.Find(filter, new FindOptions { MaxTime = TimeSpan.FromSeconds(5) }).SortByDescending(x => x.CreatedAt).Skip((page - 1) * size).Limit(size).ToListAsync(ct);
            return Results.Ok(new { items = rows.Select(x => new { x.Id, x.Collection, x.Operation, x.DocumentId, x.CreatedAt, x.ActorSource }), total = await db.DatabaseEvents.CountDocumentsAsync(filter, new CountOptions { MaxTime = TimeSpan.FromSeconds(5) }, ct), page, pageSize = size });
        });
        api.MapGet("/backups", (HttpRequest request, Database db, CancellationToken ct) => Page(db.BackupRuns, request, Builders<BackupRun>.Sort.Descending(x => x.StartedAt), ct));
        api.MapPost("/backups", async (BackupCoordinator backup, CancellationToken ct) => Results.Json(new { id = await backup.Queue("manual", ct) }, statusCode: 202));
    }
    private static async Task<IResult> Page<T>(IMongoCollection<T> collection, HttpRequest request, SortDefinition<T> sort, CancellationToken ct)
    {
        var (page, size) = ApiSupport.Pagination(request);
        var filter = Builders<T>.Filter.Empty;
        return Results.Ok(new { items = await collection.Find(filter, new FindOptions { MaxTime = TimeSpan.FromSeconds(5) }).Sort(sort).Skip((page - 1) * size).Limit(size).ToListAsync(ct), total = await collection.CountDocumentsAsync(filter, new CountOptions { MaxTime = TimeSpan.FromSeconds(5) }, ct), page, pageSize = size });
    }
}
