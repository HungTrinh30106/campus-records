using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Driver;
using StudentRecords.Api;
using StudentRecords.Backup;
using Xunit;

public class BackupCoordinationTests
{
    [Fact]
    public async Task Restore_into_new_database_sharing_backup_directory_cannot_prune_source_archives()
    {
        var name = "student_records_test_" + Guid.NewGuid().ToString("N"); var root = Path.Combine(Path.GetTempPath(), name); var backups = Path.Combine(root, "backups");
        using var source = new ApiFactory(new() { ["Mongo:Database"] = name, ["Storage:Path"] = Path.Combine(root, "uploads"), ["Backup:Path"] = backups, ["Backup:Password"] = "BackupTest2026!Separate" });
        _ = source.Client(); var db = source.Services.GetRequiredService<Database>(); var gate = source.Services.GetRequiredService<MaintenanceGate>(); var coordinator = source.Services.GetRequiredService<BackupCoordinator>();
        var first = await coordinator.Queue("manual"); await WaitUntil(async () => (await db.BackupRuns.Find(x => x.Id == first).SingleAsync()).FinishedAt != null && !gate.IsActive);
        var second = await coordinator.Queue("manual"); await WaitUntil(async () => (await db.BackupRuns.Find(x => x.Id == second).SingleAsync()).FinishedAt != null && !gate.IsActive);
        var restoreName = "student_records_test_" + Guid.NewGuid().ToString("N"); var restoreRoot = Path.Combine(Path.GetTempPath(), restoreName);
        var uri = Environment.GetEnvironmentVariable("TEST_MONGO_URI") ?? "mongodb://localhost:27017";
        using (var verified = await ArchiveService.VerifyAsync(Path.Combine(backups, "campus-" + second + ".crbackup"), "BackupTest2026!Separate"))
            await ArchiveService.RestoreAsync(verified, new MongoClient(uri).GetDatabase(restoreName), Path.Combine(restoreRoot, "uploads"));
        using var restored = new ApiFactory(new() { ["Mongo:Database"] = restoreName, ["Storage:Path"] = Path.Combine(restoreRoot, "uploads"), ["Backup:Path"] = backups, ["Backup:Password"] = "BackupTest2026!Separate", ["Backup:RetentionCount"] = "1" });
        _ = restored.Client(); var restoredDb = restored.Services.GetRequiredService<Database>(); var restoredGate = restored.Services.GetRequiredService<MaintenanceGate>();
        var third = await restored.Services.GetRequiredService<BackupCoordinator>().Queue("manual");
        await WaitUntil(async () => (await restoredDb.BackupRuns.Find(x => x.Id == third).SingleAsync()).FinishedAt != null && !restoredGate.IsActive);
        Assert.True(File.Exists(Path.Combine(backups, "campus-" + first + ".crbackup")));
        Assert.True(File.Exists(Path.Combine(backups, "campus-" + second + ".crbackup")));
        Assert.True(File.Exists(Path.Combine(backups, "campus-" + third + ".crbackup")));
    }
    [Fact]
    public async Task Destination_collision_never_deletes_a_file_the_job_did_not_create()
    {
        var name = "student_records_test_" + Guid.NewGuid().ToString("N"); var root = Path.Combine(Path.GetTempPath(), name); var backups = Path.Combine(root, "backups");
        using var factory = new ApiFactory(new() { ["Mongo:Database"] = name, ["Storage:Path"] = Path.Combine(root, "uploads"), ["Backup:Path"] = backups, ["Backup:Password"] = "BackupTest2026!Separate" });
        _ = factory.Client(); var db = factory.Services.GetRequiredService<Database>(); var gate = factory.Services.GetRequiredService<MaintenanceGate>();
        var inFlight = gate.TryEnter();
        var id = await factory.Services.GetRequiredService<BackupCoordinator>().Queue("manual");
        await WaitUntil(() => Task.FromResult(gate.IsActive));
        Directory.CreateDirectory(backups); var collision = Path.Combine(backups, "campus-" + id + ".crbackup"); await File.WriteAllTextAsync(collision, "existing owned file");
        inFlight!.Dispose();
        await WaitUntil(async () => (await db.BackupRuns.Find(x => x.Id == id).SingleAsync()).FinishedAt != null && !gate.IsActive);
        Assert.Equal("failed", (await db.BackupRuns.Find(x => x.Id == id).SingleAsync()).Status);
        Assert.True(File.Exists(collision)); Assert.Equal("existing owned file", await File.ReadAllTextAsync(collision));
    }
    [Fact]
    public async Task First_scheduled_due_time_is_durable_even_when_no_backup_has_run()
    {
        var name = "student_records_test_" + Guid.NewGuid().ToString("N"); var root = Path.Combine(Path.GetTempPath(), name);
        var settings = new Dictionary<string, string?> { ["Mongo:Database"] = name, ["Storage:Path"] = Path.Combine(root, "uploads"), ["Backup:Path"] = Path.Combine(root, "backups"), ["Backup:Password"] = "BackupTest2026!Separate", ["Backup:Enabled"] = "true" };
        DateTime firstDue;
        using (var first = new ApiFactory(settings) { PreserveDatabase = true })
        {
            _ = first.Client(); var db = first.Services.GetRequiredService<Database>();
            await WaitUntil(() => Task.FromResult(System.Text.Json.JsonSerializer.SerializeToElement(first.Services.GetRequiredService<BackupCoordinator>().Snapshot).GetProperty("nextRunAt").ValueKind != System.Text.Json.JsonValueKind.Null));
            var state = await db.Db.GetCollection<MongoDB.Bson.BsonDocument>("backupState").Find(new MongoDB.Bson.BsonDocument("_id", "schedule")).FirstOrDefaultAsync();
            Assert.NotNull(state); firstDue = state["NextRunAt"].ToUniversalTime();
        }
        await Task.Delay(100);
        using var second = new ApiFactory(settings); _ = second.Client();
        var db2 = second.Services.GetRequiredService<Database>();
        await WaitUntil(() => Task.FromResult(System.Text.Json.JsonSerializer.SerializeToElement(second.Services.GetRequiredService<BackupCoordinator>().Snapshot).GetProperty("nextRunAt").ValueKind != System.Text.Json.JsonValueKind.Null));
        var restoredState = await db2.Db.GetCollection<MongoDB.Bson.BsonDocument>("backupState").Find(new MongoDB.Bson.BsonDocument("_id", "schedule")).SingleAsync();
        Assert.Equal(firstDue, restoredState["NextRunAt"].ToUniversalTime());
    }
    [Fact]
    public async Task Retention_failure_preserves_new_verified_archive_and_never_prunes_unowned_files()
    {
        var name = "student_records_test_" + Guid.NewGuid().ToString("N"); var root = Path.Combine(Path.GetTempPath(), name); var backups = Path.Combine(root, "backups");
        using var factory = new ApiFactory(new() { ["Mongo:Database"] = name, ["Storage:Path"] = Path.Combine(root, "uploads"), ["Backup:Path"] = backups, ["Backup:Password"] = "BackupTest2026!Separate", ["Backup:RetentionCount"] = "1" });
        _ = factory.Client(); var db = factory.Services.GetRequiredService<Database>(); var gate = factory.Services.GetRequiredService<MaintenanceGate>(); var coordinator = factory.Services.GetRequiredService<BackupCoordinator>();
        var first = await coordinator.Queue("manual"); await WaitUntil(async () => (await db.BackupRuns.Find(x => x.Id == first).SingleAsync()).FinishedAt != null && !gate.IsActive);
        var old = Path.Combine(backups, "campus-" + first + ".crbackup");
        var unowned = Path.Combine(backups, "campus-" + MongoDB.Bson.ObjectId.GenerateNewId() + ".crbackup"); await File.WriteAllTextAsync(unowned, "unowned partial data"); File.SetLastWriteTimeUtc(unowned, DateTime.UtcNow.AddDays(-5));
        using (var locked = new FileStream(old, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var second = await coordinator.Queue("manual"); await WaitUntil(async () => (await db.BackupRuns.Find(x => x.Id == second).SingleAsync()).FinishedAt != null && !gate.IsActive);
            Assert.Equal("completed", (await db.BackupRuns.Find(x => x.Id == second).SingleAsync()).Status);
            using var verified = await ArchiveService.VerifyAsync(Path.Combine(backups, "campus-" + second + ".crbackup"), "BackupTest2026!Separate");
            Assert.True(verified.Records["students"].Count > 0);
            Assert.True(await db.Alerts.Find(x => x.Type == "backupRetentionFailure").AnyAsync());
        }
        Assert.True(File.Exists(unowned));
    }
    [Fact]
    public async Task Scheduled_backup_uses_durable_last_run_and_rotates_only_owned_archives()
    {
        var name = "student_records_test_" + Guid.NewGuid().ToString("N");
        var root = Path.Combine(Path.GetTempPath(), name);
        var uri = Environment.GetEnvironmentVariable("TEST_MONGO_URI") ?? "mongodb://localhost:27017";
        var db = new MongoClient(uri).GetDatabase(name);
        await db.GetCollection<BackupRun>("backupRuns").InsertOneAsync(new BackupRun { Status = "completed", Trigger = "scheduled", StartedAt = DateTime.UtcNow.AddDays(-2), FinishedAt = DateTime.UtcNow.AddDays(-2) });
        var backupRoot = Path.Combine(root, "backups"); Directory.CreateDirectory(backupRoot);
        var unrelated = Path.Combine(backupRoot, "unrelated.crbackup"); await File.WriteAllTextAsync(unrelated, "keep");
        using var factory = new ApiFactory(new()
        {
            ["Mongo:Database"] = name, ["Storage:Path"] = Path.Combine(root, "uploads"), ["Backup:Path"] = backupRoot,
            ["Backup:Password"] = "BackupTest2026!Separate", ["Backup:Enabled"] = "true", ["Backup:RetentionCount"] = "2"
        });
        _ = factory.Client();
        var gate = factory.Services.GetRequiredService<MaintenanceGate>();
        var coordinator = factory.Services.GetRequiredService<BackupCoordinator>();
        await WaitUntil(async () => await db.GetCollection<BackupRun>("backupRuns").CountDocumentsAsync(x => x.Status == "completed") == 2 && !gate.IsActive);
        for (var i = 0; i < 2; i++)
        {
            var id = await coordinator.Queue("manual");
            await WaitUntil(async () => (await db.GetCollection<BackupRun>("backupRuns").Find(x => x.Id == id).SingleAsync()).Status == "completed" && !gate.IsActive);
        }
        Assert.Equal(2, Directory.GetFiles(backupRoot, "campus-*.crbackup").Length);
        Assert.Equal("keep", await File.ReadAllTextAsync(unrelated));
        foreach (var file in Directory.GetFiles(backupRoot, "campus-*.crbackup"))
        {
            using var verified = await ArchiveService.VerifyAsync(file, "BackupTest2026!Separate");
            Assert.True(verified.Records["students"].Count > 0);
        }
    }
    [Fact]
    public async Task Gate_drains_existing_work_rejects_new_work_and_reopens_after_cancel()
    {
        var gate = new MaintenanceGate();
        var lease = gate.TryEnter(); Assert.NotNull(lease);
        var drain = gate.BeginAsync(CancellationToken.None);
        Assert.True(gate.IsActive); Assert.False(drain.IsCompleted); Assert.Null(gate.TryEnter());
        lease.Dispose(); await drain.WaitAsync(TimeSpan.FromSeconds(3));
        gate.End(); using var reopened = gate.TryEnter(); Assert.NotNull(reopened);
        using var canceled = new CancellationTokenSource();
        var canceledDrain = gate.BeginAsync(canceled.Token); canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => canceledDrain);
        Assert.False(gate.IsActive);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Manual_job_returns_202_drains_requests_and_always_reopens_after_success_or_failure(bool fail)
    {
        var name = "student_records_test_" + Guid.NewGuid().ToString("N");
        var root = Path.Combine(Path.GetTempPath(), name);
        using var factory = new ApiFactory(new()
        {
            ["Mongo:Database"] = name, ["Storage:Path"] = Path.Combine(root, "uploads"),
            ["Backup:Path"] = Path.Combine(root, "backups"), ["Backup:Password"] = "BackupTest2026!Separate"
        });
        var admin = factory.Client(); await OperationsTests.Csrf(admin);
        (await admin.PostAsJsonAsync("/api/auth/login", new { email = "admin@campus.local", password = ApiFactory.Password })).EnsureSuccessStatusCode();
        await OperationsTests.Csrf(admin);
        var db = factory.Services.GetRequiredService<Database>();
        if (fail) await db.Documents.InsertOneAsync(new Document { StudentId = MongoDB.Bson.ObjectId.GenerateNewId().ToString(), StorageName = Guid.NewGuid().ToString("N") + ".pdf", Size = 10 });
        var gate = factory.Services.GetRequiredService<MaintenanceGate>();
        var inFlight = gate.TryEnter(); Assert.NotNull(inFlight);
        var response = await admin.PostAsJsonAsync("/api/operations/backups", new { }).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var id = (await response.Content.ReadFromJsonAsync<System.Text.Json.Nodes.JsonObject>())!["id"]!.ToString();
        await WaitUntil(() => Task.FromResult(gate.IsActive));
        var blocked = await admin.GetAsync("/api/students"); Assert.Equal(HttpStatusCode.ServiceUnavailable, blocked.StatusCode);
        Assert.Equal("5", blocked.Headers.RetryAfter!.ToString());
        Assert.Equal("queued", (await db.BackupRuns.Find(x => x.Id == id).SingleAsync()).Status);
        var conflict = await Assert.ThrowsAsync<ApiException>(() => factory.Services.GetRequiredService<BackupCoordinator>().Queue("manual"));
        Assert.Equal(409, conflict.Status);
        inFlight.Dispose();
        await WaitUntil(async () => (await db.BackupRuns.Find(x => x.Id == id).SingleAsync()).FinishedAt != null && !gate.IsActive);
        var run = await db.BackupRuns.Find(x => x.Id == id).SingleAsync();
        Assert.Equal(fail ? "failed" : "completed", run.Status);
        (await admin.GetAsync("/api/students")).EnsureSuccessStatusCode();
        if (fail) { Assert.Equal("InvalidDataException", run.ErrorCode); Assert.Empty(Directory.GetFiles(Path.Combine(root, "backups"))); }
        else
        {
            using var archive = await ArchiveService.VerifyAsync(Directory.GetFiles(Path.Combine(root, "backups")).Single(), "BackupTest2026!Separate");
            Assert.True(archive.Records["students"].Count > 0); Assert.Contains("backupRuns", archive.Records.Keys);
            Assert.DoesNotContain("watcherResume", archive.Records.Keys);
        }
    }
    [Fact]
    public async Task Backup_cannot_be_configured_under_wwwroot()
    {
        var repository = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../"));
        using var factory = new ApiFactory(new() { ["Backup:Path"] = Path.Combine(repository, "backend", "StudentRecords.Api", "wwwroot", "backups"), ["Backup:Password"] = "BackupTest2026!Separate" });
        _ = factory.Client();
        var exception = await Assert.ThrowsAsync<ApiException>(() => factory.Services.GetRequiredService<BackupCoordinator>().Queue("manual"));
        Assert.Equal(503, exception.Status);
    }
    internal static async Task WaitUntil(Func<Task<bool>> condition, int seconds = 30)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (DateTime.UtcNow < deadline) { if (await condition()) return; await Task.Delay(100); }
        Assert.True(await condition(), "Timed out waiting for background state.");
    }
}
