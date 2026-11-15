using System.Diagnostics;
using MongoDB.Driver;

namespace StudentRecords.Api;

public sealed class OperatingMetrics(Database db, IConfiguration config, ILogger<OperatingMetrics> logger)
{
    public DateTime StartedAt { get; } = DateTime.UtcNow;
    private long total, failed, denied, limited, durationTicks;
    private readonly object sync = new();
    private readonly Dictionary<string, (int Count, DateTime Since)> windows = new();
    private TimeSpan? previousCpu;
    private long previousCpuTimestamp;
    public object Snapshot(MaintenanceGate gate, DatabaseWatcher watcher, BackupCoordinator backup, DepartmentCache cache, string databaseStatus)
    {
        var count = Interlocked.Read(ref total);
        using var process = Process.GetCurrentProcess();
        double cpuPercent;
        lock (sync)
        {
            var now = Stopwatch.GetTimestamp();
            var cpu = process.TotalProcessorTime;
            var elapsed = Stopwatch.GetElapsedTime(previousCpuTimestamp, now).TotalMilliseconds;
            cpuPercent = previousCpu.HasValue && elapsed > 0 ? Math.Clamp((cpu - previousCpu.Value).TotalMilliseconds / elapsed / Environment.ProcessorCount * 100, 0, 100) : 0;
            previousCpu = cpu; previousCpuTimestamp = now;
        }
        return new
        {
            StartedAt, uptimeSeconds = (DateTime.UtcNow - StartedAt).TotalSeconds,
            totalRequests = count, failedRequests = Interlocked.Read(ref failed), deniedRequests = Interlocked.Read(ref denied),
            rateLimitedRequests = Interlocked.Read(ref limited),
            averageDurationMs = count == 0 ? 0 : TimeSpan.FromTicks(Interlocked.Read(ref durationTicks)).TotalMilliseconds / count,
            cpuPercent, workingSetMb = process.WorkingSet64 / (1024d * 1024), databaseStatus,
            maintenance = gate.IsActive, changeStream = watcher.Snapshot, backup = backup.Snapshot,
            cache = new { hits = cache.Hits, misses = cache.Misses }
        };
    }
    public async Task Observe(HttpContext context, TimeSpan duration, bool persistAlerts = true)
    {
        if (!config.GetValue("Monitoring:Enabled", true)) return;
        Interlocked.Increment(ref total); Interlocked.Add(ref durationTicks, duration.Ticks);
        var code = context.Response.StatusCode;
        if (code >= 500) Interlocked.Increment(ref failed);
        if (code is 401 or 403) Interlocked.Increment(ref denied);
        if (code == 429) Interlocked.Increment(ref limited);
        if (!persistAlerts) return;
        string? type = null;
        if (code >= 500) type = "requestFailure";
        else if (context.Request.Path == "/api/auth/login" && code == 401) type = "loginFailure";
        else if (code == 429) type = "rateLimited";
        else if (context.Request.Path.Value?.EndsWith("/documents", StringComparison.Ordinal) == true && context.Request.Method == "POST" && code >= 400) type = "uploadRejected";
        else if (code is 401 or 403) type = "accessDenied";
        if (type == null) return;
        int occurrences;
        lock (sync)
        {
            var window = windows.GetValueOrDefault(type);
            if (window.Since < DateTime.UtcNow.AddMinutes(-5)) window = (0, DateTime.UtcNow);
            window.Count++;
            occurrences = window.Count >= (type is "requestFailure" or "uploadRejected" or "rateLimited" ? 3 : 5) ? window.Count : 0;
            windows[type] = occurrences == 0 ? window : (0, DateTime.UtcNow);
        }
        if (occurrences > 0)
            try { await Alert(type, "Repeated security or service events within five minutes.", occurrences); }
            catch (Exception ex) { logger.LogWarning("Alert persistence failed: {ErrorType}", ex.GetType().Name); }
    }
    public Task Alert(string type, string message, int count = 1)
    {
        var now = DateTime.UtcNow;
        var update = Builders<OperationAlert>.Update.SetOnInsert(x => x.Id, MongoDB.Bson.ObjectId.GenerateNewId().ToString())
            .SetOnInsert(x => x.Type, type).SetOnInsert(x => x.CreatedAt, now).SetOnInsert(x => x.Severity, "warning")
            .Set(x => x.Message, message).Set(x => x.LastSeenAt, now).Set(x => x.Acknowledged, false).Inc(x => x.Count, count);
        return db.Alerts.UpdateOneAsync(x => x.Type == type, update, new UpdateOptions { IsUpsert = true });
    }
}
