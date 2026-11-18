using System.Threading.Channels;
using MongoDB.Driver;
using StudentRecords.Backup;

namespace StudentRecords.Api;

public sealed class BackupCoordinator(Database db, IConfiguration config, IHostEnvironment environment, MaintenanceGate gate, OperatingMetrics metrics, ILogger<BackupCoordinator> logger) : BackgroundService
{
    private readonly Channel<BackupRun> queue = Channel.CreateBounded<BackupRun>(1);
    private int busy;
    private readonly TaskCompletionSource initialized = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private string status = "idle";
    private DateTime? lastRunAt, nextRunAt;
    private bool Enabled => config.GetValue("Backup:Enabled", false);
    private int IntervalHours => Math.Clamp(config.GetValue("Backup:IntervalHours", 24), 1, 8760);
    private int RetentionCount => Math.Clamp(config.GetValue("Backup:RetentionCount", 7), 1, 100);

    private bool Configured
    {
        get
        {
            if (config["Backup:Password"] is not { Length: >= 12 } || string.IsNullOrWhiteSpace(config["Backup:Path"]))
                return false;
            try
            {
                var root = Path.GetFullPath(config["Backup:Path"]!);
                var web = Path.GetFullPath(Path.Combine(environment.ContentRootPath, "wwwroot"));
                var uploads = RecordHelpers.Storage(config);
                return !Within(root, web) && !Within(root, uploads) && !Within(uploads, root);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException)
            {
                return false;
            }
        }
    }

    private static bool Within(string path, string root) => path.Equals(root, StringComparison.OrdinalIgnoreCase) || path.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    public object Snapshot => new
    {
        enabled = Enabled,
        intervalHours = IntervalHours,
        retentionCount = RetentionCount,
        status = Configured ? Volatile.Read(ref status) : "unconfigured",
        lastRunAt,
        nextRunAt
    };

    public async Task<string> Queue(string trigger, CancellationToken cancellationToken = default)
    {
        if (!Configured)
            throw new ApiException(503, "Sao lưu chưa được cấu hình đầy đủ.");
        await initialized.Task.WaitAsync(cancellationToken);
        if (Interlocked.CompareExchange(ref busy, 1, 0) != 0)
            throw new ApiException(409, "Một tác vụ sao lưu đang chờ hoặc đang chạy.");
        var run = new BackupRun
        {
            Trigger = trigger,
            OwnerDatabase = db.Db.DatabaseNamespace.DatabaseName
        };
        try
        {
            await db.BackupRuns.InsertOneAsync(run, cancellationToken: cancellationToken);
            Volatile.Write(ref status, "queued");
            if (!queue.Writer.TryWrite(run))
                throw new InvalidOperationException("BackupQueueUnavailable");
            return run.Id;
        }
        catch
        {
            Interlocked.Exchange(ref busy, 0);
            throw;
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            // Retry safely if startup Mongo is unavailable; health stays reachable.
            var initializationBackoff = 1;
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    using var lease = gate.TryEnter();
                    if (lease == null)
                    {
                        await Task.Delay(100, stoppingToken);
                        continue;
                    }

                    // Resume the schedule across restarts; never replay an interrupted job.
                    await db.BackupRuns.UpdateManyAsync(x => x.Status == "queued" || x.Status == "running", Builders<BackupRun>.Update.Set(x => x.Status, "failed").Set(x => x.ErrorCode, "ProcessRestarted").Set(x => x.FinishedAt, DateTime.UtcNow), cancellationToken: stoppingToken);
                    var previous = await db.BackupRuns.Find(x => x.FinishedAt != null).SortByDescending(x => x.FinishedAt).FirstOrDefaultAsync(stoppingToken);
                    lastRunAt = previous?.FinishedAt;
                    if (previous != null)
                        Volatile.Write(ref status, previous.Status);
                    if (Enabled && Configured)
                        await LoadSchedule(stoppingToken);
                    break;
                }
                catch (Exception ex) when (ex is MongoException or TimeoutException)
                {
                    Volatile.Write(ref status, "degraded");
                    logger.LogWarning("Backup startup state unavailable: {ErrorType}", ex.GetType().Name);
                    await Task.Delay(TimeSpan.FromSeconds(initializationBackoff), stoppingToken);
                    initializationBackoff = Math.Min(initializationBackoff * 2, 60);
                }
            }

            initialized.TrySetResult();
            while (!stoppingToken.IsCancellationRequested)
            {
                if (queue.Reader.TryRead(out var run))
                {
                    await Run(run, stoppingToken);
                    continue;
                }

                if (Enabled && Configured && nextRunAt == null)
                {
                    using var scheduleLease = gate.TryEnter();
                    if (scheduleLease != null)
                        try
                        {
                            await LoadSchedule(stoppingToken);
                        }
                        catch (Exception ex) when (ex is MongoException or TimeoutException)
                        {
                            logger.LogWarning("Backup schedule unavailable: {ErrorType}", ex.GetType().Name);
                            await Task.Delay(1000, stoppingToken);
                            continue;
                        }
                }
                else if (!Enabled || !Configured)
                    nextRunAt = null;
                if (Enabled && Configured && nextRunAt <= DateTime.UtcNow)
                {
                    using var lease = gate.TryEnter();
                    if (lease != null)
                        try
                        {
                            await Queue("scheduled", stoppingToken);
                        }
                        catch (Exception ex) when (ex is not OperationCanceledException)
                        {
                            logger.LogWarning("Backup scheduling failed: {ErrorType}", ex.GetType().Name);
                        }

                    nextRunAt = DateTime.UtcNow.AddMinutes(1);
                    continue;
                }

                // WaitToReadAsync returns promptly for a manual request; the periodic wake handles schedule.
                using var wake = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                wake.CancelAfter(TimeSpan.FromSeconds(1));
                try
                {
                    await queue.Reader.WaitToReadAsync(wake.Token);
                }
                catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
                {
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    private async Task Run(BackupRun run, CancellationToken ct)
    {
        string? destination = null;
        var ownsMaintenance = false;
        var createdDestination = false;
        try
        {
            // This executes on the worker, never on the endpoint's active request lease.
            await gate.BeginAsync(ct);
            ownsMaintenance = true;
            Volatile.Write(ref status, "running");
            run.Status = "running";
            run.StartedAt = DateTime.UtcNow;
            await db.BackupRuns.ReplaceOneAsync(x => x.Id == run.Id, run, cancellationToken: ct);
            var root = Path.GetFullPath(config["Backup:Path"]!);
            Directory.CreateDirectory(root);
            destination = Path.Combine(root, "campus-" + run.Id + ".crbackup");
            await ArchiveService.CreateAsync(db.Db, RecordHelpers.Storage(config), destination, config["Backup:Password"]!, ct);
            createdDestination = true;
            // Verify with the same parser used by CLI restore before publishing success.
            using var verified = await ArchiveService.VerifyAsync(destination, config["Backup:Password"]!, ct);
            run.SizeBytes = new FileInfo(destination).Length;
            run.Status = "completed";
            try
            {
                // Retain the new verified archive plus the newest completed runs
                // owned by this database. Unowned/partial files are never candidates.
                var owner = db.Db.DatabaseNamespace.DatabaseName;
                var old = await db.BackupRuns.Find(x => x.Status == "completed" && x.OwnerDatabase == owner && x.Id != run.Id).SortByDescending(x => x.FinishedAt).Skip(RetentionCount - 1).ToListAsync(ct);
                foreach (var previous in old)
                    if (MongoDB.Bson.ObjectId.TryParse(previous.Id, out _))
                        File.Delete(Path.Combine(root, "campus-" + previous.Id + ".crbackup"));
            }
            catch (Exception retentionError)
            {
                logger.LogWarning("Backup retention failed: {ErrorType}", retentionError.GetType().Name);
                try
                {
                    await metrics.Alert("backupRetentionFailure", "A verified backup completed, but old archive cleanup failed.");
                }
                catch (Exception alertError)
                {
                    logger.LogWarning("Retention alert persistence failed: {ErrorType}", alertError.GetType().Name);
                }
            }
        }
        catch (Exception ex)
        {
            run.Status = "failed";
            run.ErrorCode = ex.GetType().Name;
            logger.LogWarning("Backup failed: {ErrorType}", ex.GetType().Name);
            try
            {
                if (createdDestination && destination != null && File.Exists(destination))
                    File.Delete(destination);
            }
            catch (Exception cleanupError) when (cleanupError is IOException or UnauthorizedAccessException)
            {
                logger.LogWarning("Partial backup cleanup failed: {ErrorType}", cleanupError.GetType().Name);
            }
        }
        finally
        {
            run.FinishedAt = DateTime.UtcNow;
            lastRunAt = run.FinishedAt;
            if (Enabled && Configured)
                nextRunAt = DateTime.UtcNow.AddHours(IntervalHours);
            try
            {
                // Backup bookkeeping is the only writer allowed while the archive is closed.
                await db.BackupRuns.ReplaceOneAsync(x => x.Id == run.Id, run, cancellationToken: CancellationToken.None);
                if (Enabled && Configured && nextRunAt.HasValue)
                    await db.BackupState.UpdateOneAsync(x => x.Id == "schedule", Builders<BackupSchedule>.Update.Set(x => x.NextRunAt, nextRunAt.Value), new UpdateOptions { IsUpsert = true });
                if (run.Status == "failed")
                    await metrics.Alert("backupFailure", "An encrypted backup failed; inspect the safe error code.");
            }
            catch (Exception ex)
            {
                logger.LogWarning("Backup status persistence failed: {ErrorType}", ex.GetType().Name);
            }
            finally
            {
                Volatile.Write(ref status, run.Status);
                Interlocked.Exchange(ref busy, 0);
                if (ownsMaintenance)
                    gate.End();
            }
        }
    }

    private async Task LoadSchedule(CancellationToken ct)
    {
        var state = await db.BackupState.FindOneAndUpdateAsync(x => x.Id == "schedule", Builders<BackupSchedule>.Update.SetOnInsert(x => x.NextRunAt, (lastRunAt ?? DateTime.UtcNow).AddHours(IntervalHours)), new FindOneAndUpdateOptions<BackupSchedule> { IsUpsert = true, ReturnDocument = ReturnDocument.After }, ct);
        nextRunAt = state.NextRunAt;
    }
}
