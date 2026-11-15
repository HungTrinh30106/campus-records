using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using MongoDB.Bson;
using MongoDB.Driver;

// This lab deliberately accepts NO Mongo URI. Every command targets the private
// authenticated mongod process created here, with fictional data and random secrets.
var cases = new List<LabCase>();
var startedAt = DateTime.UtcNow;
var labId = Guid.NewGuid().ToString("N");
var dataRoot = Path.Combine(Path.GetTempPath(), "campus-security-lab-" + labId);
Process? server = null;
string? output = null;
try
{
    string? binary = null;
    for (var i = 0; i < args.Length; i += 2)
    {
        if (i + 1 >= args.Length || args[i] is not ("--output" or "--mongod")) throw new LabAssertionException("Only --output FILE and --mongod FILE are supported; cloud URI inputs are forbidden.");
        if (args[i] == "--output") output = Path.GetFullPath(args[i + 1]); else binary = Path.GetFullPath(args[i + 1]);
    }
    output ??= Path.GetFullPath("artifacts/evidence/native-db.json");
    binary ??= FindMongo();
    if (!File.Exists(binary)) throw new LabAssertionException("Install MongoDB Community or pass --mongod with its executable path.");
    Directory.CreateDirectory(dataRoot);
    using var listener = new TcpListener(IPAddress.Loopback, 0);
    listener.Start(); var port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop();
    var start = new ProcessStartInfo(binary) { UseShellExecute = false, CreateNoWindow = true };
    foreach (var value in new[] { "--dbpath", dataRoot, "--port", port.ToString(), "--bind_ip", "127.0.0.1", "--auth", "--logpath", Path.Combine(dataRoot, "mongo.log"), "--quiet", "--wiredTigerCacheSizeGB", "0.25" }) start.ArgumentList.Add(value);
    server = Process.Start(start) ?? throw new LabAssertionException("Cannot start the isolated MongoDB process.");
    // Bootstrap only after this exact child proves ownership of its listening port.
    var ready = false;
    var readyDeadline = DateTime.UtcNow.AddSeconds(20);
    while (DateTime.UtcNow < readyDeadline)
    {
        if (server.HasExited) throw new LabAssertionException("The isolated mongod exited before readiness.");
        var logPath = Path.Combine(dataRoot, "mongo.log");
        if (File.Exists(logPath))
        {
            using var logStream = new FileStream(logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var logReader = new StreamReader(logStream);
            foreach (var line in (await logReader.ReadToEndAsync()).Split('\n'))
            {
                try { using var entry = JsonDocument.Parse(line); var item = entry.RootElement; if (item.TryGetProperty("msg", out var msg) && msg.GetString() == "Waiting for connections" && item.GetProperty("attr").GetProperty("port").GetInt32() == port) ready = true; }
                catch (JsonException) { }
            }
        }
        if (ready) break;
        await Task.Delay(100);
    }
    if (!ready || server.HasExited) throw new LabAssertionException("Cannot prove readiness of the private MongoDB child.");
    var baseSettings = new MongoClientSettings { Server = new MongoServerAddress("127.0.0.1", port), DirectConnection = true, ServerSelectionTimeout = TimeSpan.FromSeconds(8), ConnectTimeout = TimeSpan.FromSeconds(5) };
    var initial = new MongoClient(baseSettings).GetDatabase("admin");
    await initial.RunCommandAsync<BsonDocument>(new BsonDocument("ping", 1));
    var adminPassword = Secret();
    await initial.RunCommandAsync<BsonDocument>(new BsonDocument { { "createUser", "lab_admin" }, { "pwd", adminPassword }, { "roles", new BsonArray { new BsonDocument { { "role", "root" }, { "db", "admin" } } } } });
    var rootSettings = baseSettings.Clone(); rootSettings.Credential = MongoCredential.CreateCredential("admin", "lab_admin", adminPassword);
    var root = new MongoClient(rootSettings);
    var databaseName = "campus_lab_" + labId[..12];
    var db = root.GetDatabase(databaseName);
    var schema = BsonDocument.Parse("""
    { "$jsonSchema": { "bsonType": "object", "required": ["StudentCode","FullName","DepartmentId","Email"], "properties": {
      "StudentCode": {"bsonType":"string","pattern":"^[A-Z0-9_-]{1,30}$"},
      "FullName": {"bsonType":"string","minLength":2,"maxLength":120},
      "DepartmentId": {"enum":["CNTT","KINHTE"]},
      "Email": {"bsonType":"string"},
      "Gpa": {"bsonType":["double","int","null"],"minimum":0,"maximum":4}
    } } }
    """);
    await db.CreateCollectionAsync("students", new CreateCollectionOptions<BsonDocument> { Validator = schema, ValidationAction = DocumentValidationAction.Error, ValidationLevel = DocumentValidationLevel.Strict });
    var students = db.GetCollection<BsonDocument>("students");
    BsonDocument Student(string code, string department) => new() { { "StudentCode", code }, { "FullName", "Sinh viên giả lập " + code }, { "DepartmentId", department }, { "Email", code.ToLowerInvariant() + "@example.edu.vn" }, { "Phone", "0901234567" }, { "NationalId", "079123456789" }, { "Gpa", 3.25 } };
    await students.InsertManyAsync(new[] { Student("SV001", "CNTT"), Student("SV002", "CNTT"), Student("SV003", "KINHTE") });
    await students.Indexes.CreateOneAsync(new CreateIndexModel<BsonDocument>(Builders<BsonDocument>.IndexKeys.Ascending("StudentCode"), new CreateIndexOptions { Unique = true }));
    var projection = new BsonDocument { { "_id", 1 }, { "StudentCode", 1 }, { "FullName", 1 }, { "DepartmentId", 1 }, { "Email", new BsonDocument("$literal", "***@***") }, { "Phone", new BsonDocument("$literal", "***") }, { "NationalId", new BsonDocument("$literal", "***") }, { "Gpa", new BsonDocument("$literal", BsonNull.Value) } };
    await db.RunCommandAsync<BsonDocument>(new BsonDocument { { "create", "students_cntt_masked" }, { "viewOn", "students" }, { "pipeline", new BsonArray { new BsonDocument("$match", new BsonDocument("DepartmentId", "CNTT")), new BsonDocument("$project", projection) } } });
    await db.RunCommandAsync<BsonDocument>(new BsonDocument { { "createRole", "reader_masked_view" }, { "privileges", new BsonArray { Privilege(databaseName, "students_cntt_masked", "find") } }, { "roles", new BsonArray() } });
    await db.RunCommandAsync<BsonDocument>(new BsonDocument { { "createRole", "writer_without_read_delete" }, { "privileges", new BsonArray { Privilege(databaseName, "students", "insert", "update") } }, { "roles", new BsonArray() } });
    var readerPassword = Secret(); var writerPassword = Secret();
    await CreateUser(db, "lab_reader", readerPassword, "reader_masked_view");
    await CreateUser(db, "lab_writer", writerPassword, "writer_without_read_delete");
    IMongoDatabase AsUser(string name, string password) { var settings = baseSettings.Clone(); settings.Credential = MongoCredential.CreateCredential(databaseName, name, password); return new MongoClient(settings).GetDatabase(databaseName); }
    var reader = AsUser("lab_reader", readerPassword); var writer = AsUser("lab_writer", writerPassword);
    var view = reader.GetCollection<BsonDocument>("students_cntt_masked");

    await Check("DB01", [1, 2], "Reader cannot SELECT the original collection", new { errorCode = 13 }, async () => new { errorCode = await ExpectError(() => reader.GetCollection<BsonDocument>("students").Find(FilterDefinition<BsonDocument>.Empty).ToListAsync(), 13) });
    await Check("DB02", [2, 3], "View enforces department scope without an application filter", new { count = 2, departments = new[] { "CNTT" } }, async () => { var rows = await view.Find(FilterDefinition<BsonDocument>.Empty).ToListAsync(); Require(rows.Count == 2 && rows.All(r => r["DepartmentId"] == "CNTT"), "View scope mismatch."); return new { count = rows.Count, departments = rows.Select(r => r["DepartmentId"].AsString).Distinct().ToArray() }; });
    await Check("DB03", [3, 18], "Changing the view query cannot reveal another department", new { count = 0 }, async () => { var count = await view.CountDocumentsAsync(new BsonDocument("DepartmentId", "KINHTE")); Require(count == 0, "Out-of-scope row exposed."); return new { count }; });
    await Check("DB04", [4, 21], "Mongo view removes raw contact data and grades", new { email = "***@***", phone = "***", nationalId = "***", gpa = "null" }, async () => { var row = await view.Find(FilterDefinition<BsonDocument>.Empty).FirstAsync(); Require(row["Email"] == "***@***" && row["Phone"] == "***" && row["NationalId"] == "***" && row["Gpa"].IsBsonNull, "View masking mismatch."); return new { email = row["Email"].AsString, phone = row["Phone"].AsString, nationalId = row["NationalId"].AsString, gpa = "null" }; });
    await Check("DB05", [1, 2], "Writer can insert but cannot read or delete the original collection", new { inserted = true, readDenied = 13, deleteDenied = 13 }, async () => { var rows = writer.GetCollection<BsonDocument>("students"); await rows.InsertOneAsync(Student("WRITE001", "CNTT")); var readDenied = await ExpectError(() => rows.Find(FilterDefinition<BsonDocument>.Empty).ToListAsync(), 13); var deleteDenied = await ExpectError(() => rows.DeleteOneAsync(new BsonDocument("StudentCode", "WRITE001")), 13); return new { inserted = true, readDenied, deleteDenied }; });
    await Check("DB06", [2, 29], "View is read-only even for the administrative client", new { errorCode = 166 }, async () => new { errorCode = await ExpectError(() => db.GetCollection<BsonDocument>("students_cntt_masked").InsertOneAsync(Student("NO_VIEW_WRITE", "CNTT")), 166) });
    await Check("DB07", [19, 22, 29], "Database schema rejects invalid GPA written directly without API", new { errorCode = 121 }, async () => { var row = Student("BAD_GPA", "CNTT"); row["Gpa"] = 99; return new { errorCode = await ExpectError(() => writer.GetCollection<BsonDocument>("students").InsertOneAsync(row), 121) }; });
    await Check("DB08", [19, 22, 29], "Database schema rejects operator-shaped input instead of a student-code string", new { errorCode = 121 }, async () => { var row = Student("BAD_TYPE", "CNTT"); row["StudentCode"] = new BsonDocument("$ne", BsonNull.Value); return new { errorCode = await ExpectError(() => writer.GetCollection<BsonDocument>("students").InsertOneAsync(row), 121) }; });
    await Check("DB09", [1, 22], "Scoped database user cannot read an unrelated database", new { errorCode = 13 }, async () => new { errorCode = await ExpectError(() => new MongoClient(reader.Client.Settings).GetDatabase(databaseName + "_other").GetCollection<BsonDocument>("students").Find(FilterDefinition<BsonDocument>.Empty).ToListAsync(), 13) });
    await Check("DB10", [10, 19], "Isolated NoSQL injection comparison: raw operator versus typed literal", new { unsafeMatches = 4, safeMatches = 0 }, async () => { const string payload = "{\"$ne\":null}"; var raw = new BsonDocument("StudentCode", BsonDocument.Parse(payload)); var unsafeMatches = await students.CountDocumentsAsync(raw); var safeMatches = await students.CountDocumentsAsync(Builders<BsonDocument>.Filter.Eq("StudentCode", payload)); Require(unsafeMatches == 4 && safeMatches == 0, "Injection comparison mismatch."); return new { payload, unsafeMatches, safeMatches, note = "Unsafe query exists only in this disposable local lab." }; });
    await Check("DB11", [19, 27], "Unique index rejects a duplicate student code", new { errorCode = 11000 }, async () => new { errorCode = await ExpectError(() => students.InsertOneAsync(Student("SV001", "CNTT")), 11000) });
    await Check("DB12", [26, 27], "Explain proves fewer documents examined after adding an index", new { beforeDocsExamined = 10000, afterDocsExamined = 1 }, async () => {
        var performance = db.GetCollection<BsonDocument>("students_performance");
        await performance.InsertManyAsync(Enumerable.Range(0, 10000).Select(i => new BsonDocument { { "StudentCode", $"LAB{i:000000}" }, { "DepartmentId", i % 2 == 0 ? "CNTT" : "KINHTE" } }));
        async Task<BsonDocument> Explain() => await db.RunCommandAsync<BsonDocument>(new BsonDocument { { "explain", new BsonDocument { { "find", "students_performance" }, { "filter", new BsonDocument("StudentCode", "LAB009999") } } }, { "verbosity", "executionStats" } });
        var before = (await Explain())["executionStats"].AsBsonDocument;
        await performance.Indexes.CreateOneAsync(new CreateIndexModel<BsonDocument>(Builders<BsonDocument>.IndexKeys.Ascending("StudentCode")));
        var after = (await Explain())["executionStats"].AsBsonDocument;
        var beforeExamined = before["totalDocsExamined"].ToInt64(); var afterExamined = after["totalDocsExamined"].ToInt64();
        Require(beforeExamined == 10000 && afterExamined == 1, "Index did not reduce examined documents.");
        return new { beforeDocsExamined = beforeExamined, afterDocsExamined = afterExamined, beforeMilliseconds = before["executionTimeMillis"].ToInt64(), afterMilliseconds = after["executionTimeMillis"].ToInt64(), note = "Timing is observed, not asserted; examined documents prove the plan change." };
    });
}
catch (Exception ex) { cases.Add(new LabCase("SETUP", [], "Lab setup or invocation", "Failed", new { isolated = true }, new { errorType = ex.GetType().Name, errorCode = Code(ex), detail = ex is LabAssertionException ? ex.Message : "See the local MongoDB installation; no connection credential is printed." }, 0)); }
finally
{
    if (server is not null) { if (!server.HasExited) server.Kill(entireProcessTree: true); await server.WaitForExitAsync(); server.Dispose(); }
    var resolved = Path.GetFullPath(dataRoot); var temp = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
    if (!resolved.StartsWith(temp, StringComparison.OrdinalIgnoreCase) || Path.GetFileName(resolved) != "campus-security-lab-" + labId) throw new InvalidOperationException("Unsafe cleanup path.");
    for (var retry = 0; Directory.Exists(resolved) && retry < 5; retry++) { try { Directory.Delete(resolved, true); } catch (IOException) when (retry < 4) { await Task.Delay(100); } }
}
var report = new { schemaVersion = 1, startedAt, completedAt = DateTime.UtcNow, environment = "Disposable local mongod with authentication; never Atlas", success = cases.Count == 12 && cases.All(c => c.Status == "Passed"), cases, cleanupComplete = !Directory.Exists(dataRoot) };
if (output is not null) { Directory.CreateDirectory(Path.GetDirectoryName(output)!); await File.WriteAllTextAsync(output, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase })); }
foreach (var item in cases) Console.WriteLine($"{item.Status.ToUpperInvariant()} {item.Id}: {item.Title}");
Console.WriteLine($"Evidence: {output ?? "not written"}");
return report.success ? 0 : 1;

async Task Check(string id, int[] requirements, string title, object expected, Func<Task<object>> action)
{
    var watch = Stopwatch.StartNew();
    try { var actual = await action(); cases.Add(new LabCase(id, requirements, title, "Passed", expected, actual, watch.ElapsedMilliseconds)); }
    catch (Exception ex) { cases.Add(new LabCase(id, requirements, title, "Failed", expected, new { errorType = ex.GetType().Name, errorCode = Code(ex), detail = ex is LabAssertionException ? ex.Message : "Native operation failed; credential details omitted." }, watch.ElapsedMilliseconds)); }
}
static BsonDocument Privilege(string database, string collection, params string[] actions) => new() { { "resource", new BsonDocument { { "db", database }, { "collection", collection } } }, { "actions", new BsonArray(actions) } };
static Task<BsonDocument> CreateUser(IMongoDatabase db, string name, string password, string role) => db.RunCommandAsync<BsonDocument>(new BsonDocument { { "createUser", name }, { "pwd", password }, { "roles", new BsonArray { role } } });
static string Secret() => "Lab!" + Convert.ToHexString(RandomNumberGenerator.GetBytes(24)) + "a9";
static void Require(bool condition, string detail) { if (!condition) throw new LabAssertionException(detail); }
static int? Code(Exception exception) => exception switch { MongoCommandException e => e.Code, MongoWriteException e => e.WriteError.Code, _ => null };
static async Task<int> ExpectError(Func<Task> operation, int expected)
{
    try { await operation(); } catch (Exception exception) when (Code(exception) == expected) { return expected; }
    throw new LabAssertionException($"Expected Mongo error {expected} was not observed.");
}
static string FindMongo()
{
    var configured = Environment.GetEnvironmentVariable("MONGOD_PATH"); if (!string.IsNullOrEmpty(configured)) return Path.GetFullPath(configured);
    var windows = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "MongoDB", "Server");
    if (Directory.Exists(windows)) foreach (var version in Directory.GetDirectories(windows).OrderDescending()) { var binary = Path.Combine(version, "bin", "mongod.exe"); if (File.Exists(binary)) return binary; }
    foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator)) { var binary = Path.Combine(directory, OperatingSystem.IsWindows() ? "mongod.exe" : "mongod"); if (File.Exists(binary)) return binary; }
    return "";
}
record LabCase(string Id, int[] Requirements, string Title, string Status, object Expected, object Actual, long DurationMs);
sealed class LabAssertionException(string detail) : Exception(detail);
