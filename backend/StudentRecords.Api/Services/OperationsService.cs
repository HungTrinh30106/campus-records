using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using MongoDB.Bson;
using MongoDB.Driver;
using static StudentRecords.Api.RecordHelpers;

namespace StudentRecords.Api;

public sealed class OperationsService(Database db, OperatingMetrics metrics, MaintenanceGate gate, DatabaseWatcher watcher, BackupCoordinator backup, DepartmentCache cache)
{
    public async Task<IResult> Index(CancellationToken ct)
    {
        var status = "unhealthy";
        try
        {
            await db.Db.RunCommandAsync<BsonDocument>(new BsonDocument("ping", 1), cancellationToken: ct);
            status = db.Ready ? "healthy" : "unhealthy";
        }
        catch (MongoException)
        {
        }
        catch (TimeoutException)
        {
        }

        return Results.Ok(metrics.Snapshot(gate, watcher, backup, cache, status));
    }

    public Task<IResult> Alerts(HttpRequest request, CancellationToken ct)
    {
        return Page(db.Alerts, request, Builders<OperationAlert>.Sort.Descending(x => x.LastSeenAt), ct);
    }

    public async Task<IResult> Acknowledge(string id, CancellationToken ct)
    {
        ApiSupport.Id(id);
        var result = await db.Alerts.UpdateOneAsync(x => x.Id == id, Builders<OperationAlert>.Update.Set(x => x.Acknowledged, true), cancellationToken: ct);
        if (result.MatchedCount == 0)
            throw new ApiException(404, "Không tìm thấy cảnh báo.");
        return Results.NoContent();
    }

    public async Task<IResult> DatabaseEvents(HttpRequest request, CancellationToken ct)
    {
        var (page, size) = ApiSupport.Pagination(request);
        var filter = Builders<DatabaseEvent>.Filter.Empty;
        var rows = await db.DatabaseEvents.Find(filter, new FindOptions { MaxTime = TimeSpan.FromSeconds(5) }).SortByDescending(x => x.CreatedAt).Skip((page - 1) * size).Limit(size).ToListAsync(ct);
        return Results.Ok(new { items = rows.Select(x => new { x.Id, x.Collection, x.Operation, x.DocumentId, x.CreatedAt, x.ActorSource }), total = await db.DatabaseEvents.CountDocumentsAsync(filter, new CountOptions { MaxTime = TimeSpan.FromSeconds(5) }, ct), page, pageSize = size });
    }

    public Task<IResult> Backups(HttpRequest request, CancellationToken ct)
    {
        return Page(db.BackupRuns, request, Builders<BackupRun>.Sort.Descending(x => x.StartedAt), ct);
    }

    public async Task<IResult> QueueBackup(CancellationToken ct)
    {
        return Results.Json(new { id = await backup.Queue("manual", ct) }, statusCode: 202);
    }

    private static async Task<IResult> Page<T>(IMongoCollection<T> collection, HttpRequest request, SortDefinition<T> sort, CancellationToken ct)
    {
        var (page, size) = ApiSupport.Pagination(request);
        var filter = Builders<T>.Filter.Empty;
        return Results.Ok(new { items = await collection.Find(filter, new FindOptions { MaxTime = TimeSpan.FromSeconds(5) }).Sort(sort).Skip((page - 1) * size).Limit(size).ToListAsync(ct), total = await collection.CountDocumentsAsync(filter, new CountOptions { MaxTime = TimeSpan.FromSeconds(5) }, ct), page, pageSize = size });
    }
}
