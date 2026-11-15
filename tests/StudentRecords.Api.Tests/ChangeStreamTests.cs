using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Bson;
using MongoDB.Driver;
using StudentRecords.Api;
using Xunit;

// A real, isolated one-node replica set, independent of TEST_MONGO_URI's standalone.
public class ChangeStreamTests : IAsyncLifetime
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "campus-replica-" + Guid.NewGuid().ToString("N"));
    private Process? process;
    private string uri = "";
    public async Task InitializeAsync()
    {
        var binary = Environment.GetEnvironmentVariable("TEST_MONGOD_PATH") ?? @"C:\Program Files\MongoDB\Server\8.2\bin\mongod.exe";
        if (!File.Exists(binary)) binary = "mongod";
        var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start(); var port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop();
        Directory.CreateDirectory(root);
        var start = new ProcessStartInfo(binary) { UseShellExecute = false, CreateNoWindow = true };
        foreach (var argument in new[] { "--dbpath", root, "--port", port.ToString(), "--bind_ip", "127.0.0.1", "--replSet", "campusTest", "--logpath", Path.Combine(root, "mongo.log"), "--quiet" }) start.ArgumentList.Add(argument);
        process = Process.Start(start)!;
        await BackupCoordinationTests.WaitUntil(async () =>
        {
            Assert.False(process.HasExited, "Isolated mongod exited before readiness; never bootstrap a different listener.");
            var log = Path.Combine(root, "mongo.log");
            if (!File.Exists(log)) return false;
            // Open with sharing while this exact child continues writing its own log.
            await using var input = new FileStream(log, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(input); var lines = await reader.ReadToEndAsync();
            return lines.Split('\n').Any(line => line.Contains("Waiting for connections", StringComparison.Ordinal) && line.Contains("\"port\":" + port, StringComparison.Ordinal));
        }, 20);
        Assert.False(process.HasExited);
        var direct = new MongoClient($"mongodb://127.0.0.1:{port}/?directConnection=true&serverSelectionTimeoutMS=15000");
        await direct.GetDatabase("admin").RunCommandAsync<BsonDocument>(new BsonDocument("replSetInitiate", new BsonDocument {
            { "_id", "campusTest" }, { "members", new BsonArray { new BsonDocument { { "_id", 0 }, { "host", $"127.0.0.1:{port}" } } } } }));
        uri = $"mongodb://127.0.0.1:{port}/?replicaSet=campusTest&serverSelectionTimeoutMS=15000";
        await BackupCoordinationTests.WaitUntil(async () => (await direct.GetDatabase("admin").RunCommandAsync<BsonDocument>(new BsonDocument("hello", 1))).GetValue("isWritablePrimary", false).ToBoolean(), 45);
    }
    public Task DisposeAsync()
    {
        if (process is { HasExited: false }) { process.Kill(true); process.WaitForExit(); }
        process?.Dispose();
        var absolute = Path.GetFullPath(root);
        if (absolute.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase) && Directory.Exists(absolute)) Directory.Delete(absolute, true);
        return Task.CompletedTask;
    }
    [Fact]
    public async Task Quiet_first_start_checkpoints_before_any_business_event_and_resumes_offline_write()
    {
        var name = "student_records_test_" + Guid.NewGuid().ToString("N");
        var settings = new Dictionary<string, string?> { ["Mongo:Database"] = name, ["Mongo:ConnectionString"] = uri, ["ChangeStreams:Enabled"] = "true" };
        var db = new MongoClient(uri).GetDatabase(name);
        using (var first = new ApiFactory(settings) { PreserveDatabase = true })
        {
            _ = first.Client(); await Running(first);
            await BackupCoordinationTests.WaitUntil(async () => await db.GetCollection<WatcherResume>("watcherResume").Find(x => x.Id == "business" && x.Token != null).AnyAsync(), 4);
            Assert.Equal(0, await db.GetCollection<DatabaseEvent>("databaseEvents").CountDocumentsAsync(FilterDefinition<DatabaseEvent>.Empty));
        }
        var offline = new Student { StudentCode = Guid.NewGuid().ToString("N"), FullName = "quiet restart dummy" };
        await db.GetCollection<Student>("students").InsertOneAsync(offline);
        using var restarted = new ApiFactory(settings); _ = restarted.Client(); await Running(restarted);
        await BackupCoordinationTests.WaitUntil(async () => await db.GetCollection<DatabaseEvent>("databaseEvents").CountDocumentsAsync(x => x.DocumentId == offline.Id) == 1);
    }
    [Fact]
    public async Task One_stream_filters_journals_stores_no_PII_and_resumes_across_restart_without_duplicates()
    {
        var name = "student_records_test_" + Guid.NewGuid().ToString("N");
        var settings = new Dictionary<string, string?> { ["Mongo:Database"] = name, ["Mongo:ConnectionString"] = uri, ["ChangeStreams:Enabled"] = "true" };
        var db = new MongoClient(uri).GetDatabase(name);
        using (var factory = new ApiFactory(settings) { PreserveDatabase = true })
        {
            _ = factory.Client();
            await Running(factory);
            var secret = "PII-must-never-be-recorded-" + Guid.NewGuid();
            var student = new Student { StudentCode = Guid.NewGuid().ToString("N"), FullName = secret, Email = secret + "@example.org" };
            await db.GetCollection<Student>("students").InsertOneAsync(student);
            await BackupCoordinationTests.WaitUntil(async () => await db.GetCollection<DatabaseEvent>("databaseEvents").CountDocumentsAsync(x => x.DocumentId == student.Id) == 1);
            var row = await db.GetCollection<BsonDocument>("databaseEvents").Find(new BsonDocument("DocumentId", student.Id)).SingleAsync();
            Assert.DoesNotContain(secret, row.ToJson()); Assert.False(row.Contains("FullDocument")); Assert.False(row.Contains("UpdateDescription"));
            Assert.Equal("unknown/directDatabase", row["ActorSource"].AsString);
            var cache = factory.Services.GetRequiredService<DepartmentCache>();
            var department = (await cache.Get()).First();
            await db.GetCollection<Department>("departments").UpdateOneAsync(x => x.Id == department.Id, Builders<Department>.Update.Set(x => x.Name, "Externally changed dummy department"));
            await BackupCoordinationTests.WaitUntil(async () => (await cache.Get()).Single(x => x.Id == department.Id).Name == "Externally changed dummy department");
            await BackupCoordinationTests.WaitUntil(async () => await db.GetCollection<WatcherResume>("watcherResume").CountDocumentsAsync(x => x.Id == "business") == 1);
            var before = await db.GetCollection<DatabaseEvent>("databaseEvents").CountDocumentsAsync(FilterDefinition<DatabaseEvent>.Empty);
            await db.GetCollection<BsonDocument>("audit").InsertOneAsync(new BsonDocument("Message", secret));
            await db.GetCollection<BsonDocument>("operationAlerts").InsertOneAsync(new BsonDocument("Type", Guid.NewGuid().ToString()));
            await Task.Delay(1300);
            Assert.Equal(before, await db.GetCollection<DatabaseEvent>("databaseEvents").CountDocumentsAsync(FilterDefinition<DatabaseEvent>.Empty));
        }
        // Changes while offline must be observed from the durable source token.
        var offline = new Student { StudentCode = Guid.NewGuid().ToString("N"), FullName = "offline dummy" };
        await db.GetCollection<Student>("students").InsertOneAsync(offline);
        using (var restarted = new ApiFactory(settings))
        {
            _ = restarted.Client(); await Running(restarted);
            await BackupCoordinationTests.WaitUntil(async () => await db.GetCollection<DatabaseEvent>("databaseEvents").CountDocumentsAsync(x => x.DocumentId == offline.Id) == 1);
            await db.GetCollection<Student>("students").UpdateOneAsync(x => x.Id == offline.Id, Builders<Student>.Update.Set(x => x.FullName, "changed dummy"));
            await BackupCoordinationTests.WaitUntil(async () => await db.GetCollection<DatabaseEvent>("databaseEvents").CountDocumentsAsync(x => x.DocumentId == offline.Id) == 2);
            var operations = await db.GetCollection<DatabaseEvent>("databaseEvents").Find(x => x.DocumentId == offline.Id).ToListAsync();
            Assert.Equal(new[] { "insert", "update" }, operations.OrderBy(x => x.CreatedAt).Select(x => x.Operation));
            Assert.Equal(2, operations.Select(x => x.EventKey).Distinct().Count());
            await db.GetCollection<BsonDocument>("users").InsertOneAsync(new BsonDocument { { "_id", "sensitive-direct-id@example.org" }, { "NormalizedEmail", "DIRECT-DUMMY-UNIQUE" }, { "PasswordHash", "NEVER-COPY-THIS" } });
            await BackupCoordinationTests.WaitUntil(async () => await db.GetCollection<DatabaseEvent>("databaseEvents").CountDocumentsAsync(x => x.Collection == "users" && x.DocumentId == null) == 1);
            var sanitized = await db.GetCollection<BsonDocument>("databaseEvents").Find(new BsonDocument { { "Collection", "users" }, { "DocumentId", BsonNull.Value } }).SingleAsync();
            Assert.DoesNotContain("sensitive-direct-id", sanitized.ToJson()); Assert.DoesNotContain("NEVER-COPY-THIS", sanitized.ToJson());
        }
    }
    private static Task Running(ApiFactory factory) => BackupCoordinationTests.WaitUntil(() =>
    {
        var snapshot = JsonSerializer.SerializeToElement(factory.Services.GetRequiredService<DatabaseWatcher>().Snapshot);
        return Task.FromResult(snapshot.GetProperty("status").GetString() == "running");
    });
}
