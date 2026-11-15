using System.Diagnostics;
using System.IO.Compression;
using StudentRecords.Backup;
using MongoDB.Bson;
using MongoDB.Driver;
using Xunit;

// Each case creates its own disposable database and private temporary directory.
public class BackupIntegrationTests : IAsyncLifetime
{
    private readonly string sourceDb = "backup_test_" + Guid.NewGuid().ToString("N")[..16];
    private readonly string restoredDb = "restore_test_" + Guid.NewGuid().ToString("N")[..16];
    private readonly string root = Path.Combine(Path.GetTempPath(), "campus-backup-test-" + Guid.NewGuid().ToString("N"));
    private readonly MongoClient client = new(Environment.GetEnvironmentVariable("TEST_MONGO_URI") ?? "mongodb://localhost:27017");
    private string Uploads => Path.Combine(root, "uploads");
    private string Archive => Path.Combine(root, "test.crbackup");
    private readonly string fileName = Guid.NewGuid().ToString("N") + ".png";
    private readonly byte[] fileBytes = [137, 80, 78, 71, 13, 10, 26, 10, 1, 2, 3];

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(Uploads);
        var database = client.GetDatabase(sourceDb);
        foreach (var collection in new[] { "users", "departments", "students", "documents", "audit" }) await database.CreateCollectionAsync(collection);
        await database.GetCollection<BsonDocument>("users").InsertOneAsync(new BsonDocument { { "_id", ObjectId.GenerateNewId() }, { "SecurityStamp", "old-session-stamp" } });
        await database.GetCollection<BsonDocument>("students").InsertOneAsync(new BsonDocument { { "_id", ObjectId.GenerateNewId() }, { "FullName", "Nguyễn Thử Nghiệm" }, { "CreatedAt", new BsonDateTime(new DateTime(2026, 11, 3, 0, 0, 0, DateTimeKind.Utc)) } });
        await database.GetCollection<BsonDocument>("documents").InsertOneAsync(new BsonDocument { { "_id", ObjectId.GenerateNewId() }, { "StorageName", fileName }, { "Size", fileBytes.LongLength } });
        await File.WriteAllBytesAsync(Path.Combine(Uploads, fileName), fileBytes);
        foreach (var collection in ArchiveService.OptionalCollections)
        {
            var row = new BsonDocument { { "_id", ObjectId.GenerateNewId() }, { "Marker", "runtime-state-dummy" } };
            if (collection == "backupRuns") row["OwnerDatabase"] = sourceDb;
            await database.GetCollection<BsonDocument>(collection).InsertOneAsync(row);
        }
        await database.GetCollection<BsonDocument>("watcherResume").InsertOneAsync(new BsonDocument { { "_id", "business" }, { "Token", new BsonDocument("_data", "source-token-never-restore") } });
    }
    public async Task DisposeAsync()
    {
        await client.DropDatabaseAsync(sourceDb); await client.DropDatabaseAsync(restoredDb);
        var absolute = Path.GetFullPath(root);
        if (absolute.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase)) Directory.Delete(absolute, true);
    }
    private async Task<(int Code, string Output)> Run(params string[] arguments)
    {
        var repository = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../"));
#if DEBUG
        const string configuration = "Debug";
#else
        const string configuration = "Release";
#endif
        var tool = Path.Combine(repository, "tools", "StudentRecords.Backup", "bin", configuration, "net10.0", "StudentRecords.Backup.dll");
        var start = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        start.ArgumentList.Add(tool); foreach (var argument in arguments) start.ArgumentList.Add(argument);
        start.Environment["Mongo__ConnectionString"] = Environment.GetEnvironmentVariable("TEST_MONGO_URI") ?? "mongodb://localhost:27017";
        start.Environment["Mongo__Database"] = sourceDb; start.Environment["Backup__Password"] = "BackupTest2026!Separate";
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return (process.ExitCode, await stdout + await stderr);
    }
    [Fact]
    public async Task MistypedSourceDatabaseCannotCreateAValidEmptyBackup()
    {
        var result = await Run("backup", "--database", sourceDb + "_typo", "--uploads", Uploads, "--output", Archive, "--acknowledge-api-stopped");
        Assert.Equal(1, result.Code); Assert.False(File.Exists(Archive));
    }
    [Fact]
    public async Task TruncatedAttachmentCannotCreateAValidBackup()
    {
        await File.WriteAllBytesAsync(Path.Combine(Uploads, fileName), []);
        var result = await Run("backup", "--uploads", Uploads, "--output", Archive, "--acknowledge-api-stopped");
        Assert.Equal(1, result.Code); Assert.False(File.Exists(Archive));
    }
    [Fact]
    public async Task VerifyRejectsNullManifestCountsAsValidationErrorWithoutUnhandledException()
    {
        var backup = await Run("backup", "--uploads", Uploads, "--output", Archive, "--acknowledge-api-stopped");
        Assert.True(backup.Code == 0, backup.Output);
        const string password = "BackupTest2026!Separate";
        using var plaintext = new MemoryStream();
        await plaintext.WriteAsync(ArchiveCrypto.Decrypt(await File.ReadAllBytesAsync(Archive), password)); plaintext.Position = 0;
        using (var zip = new ZipArchive(plaintext, ZipArchiveMode.Update, true))
        {
            var entry = zip.GetEntry("manifest.json")!;
            System.Text.Json.Nodes.JsonObject manifest;
            using (var reader = new StreamReader(entry.Open())) manifest = System.Text.Json.Nodes.JsonNode.Parse(await reader.ReadToEndAsync())!.AsObject();
            manifest["Counts"] = null; entry.Delete();
            using var writer = new StreamWriter(zip.CreateEntry("manifest.json").Open()); await writer.WriteAsync(manifest.ToJsonString());
        }
        await File.WriteAllBytesAsync(Archive, ArchiveCrypto.Encrypt(plaintext.ToArray(), password));
        var verify = await Run("verify", "--input", Archive);
        Assert.Equal(1, verify.Code); Assert.DoesNotContain("Unhandled exception", verify.Output);
    }
    [Fact]
    public async Task VerifyRejectsArchiveWhoseDocumentSizeDoesNotMatchAttachment()
    {
        var backup = await Run("backup", "--uploads", Uploads, "--output", Archive, "--acknowledge-api-stopped");
        Assert.True(backup.Code == 0, backup.Output);
        var password = "BackupTest2026!Separate";
        using var plaintext = new MemoryStream();
        await plaintext.WriteAsync(ArchiveCrypto.Decrypt(await File.ReadAllBytesAsync(Archive), password));
        plaintext.Position = 0;
        using (var zip = new ZipArchive(plaintext, ZipArchiveMode.Update, true))
        {
            var entry = zip.GetEntry("collections/documents.jsonl")!;
            string line; using (var reader = new StreamReader(entry.Open())) line = (await reader.ReadLineAsync())!;
            var doc = BsonDocument.Parse(line); doc["Size"] = 999L; entry.Delete();
            using var writer = new StreamWriter(zip.CreateEntry("collections/documents.jsonl").Open());
            await writer.WriteLineAsync(doc.ToJson());
        }
        await File.WriteAllBytesAsync(Archive, ArchiveCrypto.Encrypt(plaintext.ToArray(), password));
        var verify = await Run("verify", "--input", Archive);
        Assert.Equal(1, verify.Code);
    }
    [Fact]
    public async Task RoundtripPreservesBsonAndFilesRevokesSessionsAndRefusesOverwrite()
    {
        var backup = await Run("backup", "--uploads", Uploads, "--output", Archive, "--acknowledge-api-stopped");
        Assert.True(backup.Code == 0, backup.Output);
        var verify = await Run("verify", "--input", Archive); Assert.True(verify.Code == 0, verify.Output);
        var restoredUploads = Path.Combine(root, "restored-uploads");
        var restore = await Run("restore", "--input", Archive, "--database", restoredDb, "--uploads", restoredUploads, "--confirm-restore", "--acknowledge-api-stopped");
        Assert.True(restore.Code == 0, restore.Output);
        Assert.Equal(fileBytes, await File.ReadAllBytesAsync(Path.Combine(restoredUploads, fileName)));
        var record = await client.GetDatabase(restoredDb).GetCollection<BsonDocument>("students").Find(FilterDefinition<BsonDocument>.Empty).SingleAsync();
        Assert.Equal("Nguyễn Thử Nghiệm", record["FullName"].AsString); Assert.Equal(BsonType.DateTime, record["CreatedAt"].BsonType);
        var user = await client.GetDatabase(restoredDb).GetCollection<BsonDocument>("users").Find(FilterDefinition<BsonDocument>.Empty).SingleAsync();
        Assert.NotEqual("old-session-stamp", user["SecurityStamp"].AsString);
        foreach (var collection in ArchiveService.OptionalCollections)
            Assert.Equal("runtime-state-dummy", (await client.GetDatabase(restoredDb).GetCollection<BsonDocument>(collection).Find(FilterDefinition<BsonDocument>.Empty).SingleAsync())["Marker"].AsString);
        Assert.Equal(0, await client.GetDatabase(restoredDb).GetCollection<BsonDocument>("watcherResume").CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty));
        Assert.Equal("", (await client.GetDatabase(restoredDb).GetCollection<BsonDocument>("backupRuns").Find(FilterDefinition<BsonDocument>.Empty).SingleAsync())["OwnerDatabase"].AsString);
        var again = await Run("restore", "--input", Archive, "--database", restoredDb, "--uploads", Path.Combine(root, "other"), "--confirm-restore", "--acknowledge-api-stopped");
        Assert.NotEqual(0, again.Code);
        Assert.Equal(1, await client.GetDatabase(restoredDb).GetCollection<BsonDocument>("students").CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty));
    }
}
