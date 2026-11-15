using System.Security.Cryptography;
using MongoDB.Bson;
using MongoDB.Driver;

namespace StudentRecords.Api;

public sealed class DatabaseWatcher(Database db, IConfiguration config, MaintenanceGate gate,
    DepartmentCache cache, OperatingMetrics metrics, ILogger<DatabaseWatcher> logger) : BackgroundService
{
    private string status = "disabled";
    private string? lastError;
    private DateTime? lastEventAt;
    private bool Enabled => config.GetValue("ChangeStreams:Enabled", false);
    public object Snapshot => new { enabled = Enabled, status = Volatile.Read(ref status), lastEventAt, lastError };
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        if (!Enabled) return;
        var backoff = 1;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                Volatile.Write(ref status, "connecting");
                var resume = await db.WatcherResumes.Find(x => x.Id == "business").FirstOrDefaultAsync(ct);
                // One database-level stream; journals are excluded server-side to prevent loops.
                PipelineDefinition<ChangeStreamDocument<BsonDocument>, ChangeStreamDocument<BsonDocument>> pipeline = new BsonDocument[]
                {
                    new("$match", new BsonDocument {
                        { "ns.coll", new BsonDocument("$in", new BsonArray { "users", "students", "departments", "documents" }) },
                        { "operationType", new BsonDocument("$in", new BsonArray { "insert", "update", "replace", "delete" }) }
                    })
                };
                using var cursor = await db.Db.WatchAsync(pipeline, new ChangeStreamOptions { ResumeAfter = resume?.Token, MaxAwaitTime = TimeSpan.FromSeconds(1), BatchSize = 100 }, ct);
                // Fresh streams become running only after their first durable token.
                if (resume?.Token != null) Volatile.Write(ref status, "running");
                lastError = null; backoff = 1;
                var checkpointAt = DateTime.MinValue;
                var checkpointToken = resume?.Token;
                while (await cursor.MoveNextAsync(ct))
                {
                    foreach (var change in cursor.Current)
                    {
                        IDisposable? lease;
                        while ((lease = gate.TryEnter()) == null) await Task.Delay(100, ct);
                        using (lease)
                        {
                            // Never persist fullDocument/updateDescription, user fields, or credentials.
                            var row = new DatabaseEvent
                            {
                                EventKey = Convert.ToHexString(SHA256.HashData(change.ResumeToken.ToBson())),
                                Collection = change.CollectionNamespace.CollectionName,
                                Operation = change.OperationType.ToString().ToLowerInvariant(),
                                DocumentId = SafeDocumentId(change.DocumentKey),
                                CreatedAt = change.WallTime ?? DateTime.UtcNow
                            };
                            await db.DatabaseEvents.UpdateOneAsync(x => x.EventKey == row.EventKey,
                                Builders<DatabaseEvent>.Update.SetOnInsert(x => x.Id, row.Id).SetOnInsert(x => x.EventKey, row.EventKey)
                                .SetOnInsert(x => x.Collection, row.Collection).SetOnInsert(x => x.Operation, row.Operation)
                                .SetOnInsert(x => x.DocumentId, row.DocumentId).SetOnInsert(x => x.CreatedAt, row.CreatedAt)
                                .SetOnInsert(x => x.ActorSource, row.ActorSource), new UpdateOptions { IsUpsert = true }, ct);
                            await db.WatcherResumes.ReplaceOneAsync(x => x.Id == "business", new WatcherResume { Token = change.ResumeToken }, new ReplaceOptions { IsUpsert = true }, ct);
                            checkpointAt = DateTime.UtcNow; checkpointToken = change.ResumeToken;
                            if (row.Collection == "departments") cache.Invalidate();
                            lastEventAt = row.CreatedAt;
                        }
                    }
                    // Persist the initial/idle high-water mark even if no business
                    // event ever matched. Only checkpoint AFTER consuming the full
                    // batch, so a crash cannot skip an unprocessed matching event.
                    var highWater = cursor.GetResumeToken();
                    if (highWater != null && (checkpointToken == null || DateTime.UtcNow - checkpointAt >= TimeSpan.FromSeconds(30)))
                    {
                        IDisposable? lease;
                        while ((lease = gate.TryEnter()) == null) await Task.Delay(100, ct);
                        using (lease)
                        {
                            await db.WatcherResumes.ReplaceOneAsync(x => x.Id == "business", new WatcherResume { Token = highWater }, new ReplaceOptions { IsUpsert = true }, ct);
                            checkpointAt = DateTime.UtcNow; checkpointToken = highWater;
                        }
                    }
                    if (checkpointToken != null) Volatile.Write(ref status, "running");
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                lastError = ex is MongoCommandException mongo ? "MongoCommand" + mongo.Code : ex.GetType().Name;
                Volatile.Write(ref status, "degraded");
                logger.LogWarning("Database change stream degraded: {ErrorCode}", lastError);
                using (var lease = gate.TryEnter())
                    if (lease != null)
                        try
                        {
                            // History-lost tokens cannot be reused; expose the observation gap.
                            if (ex is MongoCommandException command && command.Code is 260 or 280 or 286)
                                await db.WatcherResumes.DeleteOneAsync(x => x.Id == "business", ct);
                            await metrics.Alert("changeStreamDegraded", "Database change monitoring is degraded; an observation gap may exist.");
                        }
                        catch (Exception alertError) when (alertError is not OperationCanceledException) { logger.LogWarning("Watcher status persistence failed: {ErrorType}", alertError.GetType().Name); }
                try { await Task.Delay(TimeSpan.FromSeconds(backoff), ct); }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
                backoff = Math.Min(backoff * 2, 60);
            }
        }
        Volatile.Write(ref status, "stopped");
    }
    private static string? SafeDocumentId(BsonDocument? key)
    {
        // Business collections use ObjectId. An arbitrary direct writer's string _id
        // could itself contain PII, so do not journal untrusted non-ObjectId values.
        return key?.TryGetValue("_id", out var id) == true && id.IsObjectId ? id.AsObjectId.ToString() : null;
    }
}
