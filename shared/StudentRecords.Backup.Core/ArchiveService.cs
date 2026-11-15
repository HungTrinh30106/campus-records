using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using MongoDB.Bson;
using MongoDB.Bson.IO;
using MongoDB.Driver;
namespace StudentRecords.Backup;
public sealed record Manifest(int Version, string Database, DateTime CreatedAt, Dictionary<string, long> Counts, int Documents);
public sealed class VerifiedArchive(ZipArchive zip, Manifest manifest, Dictionary<string, List<BsonDocument>> records, HashSet<string> files) : IDisposable
{
    internal ZipArchive Zip { get; } = zip;
    public Manifest Manifest { get; } = manifest;
    public Dictionary<string, List<BsonDocument>> Records { get; } = records;
    internal HashSet<string> Files { get; } = files;
    public void Dispose() => Zip.Dispose();
}
public static class ArchiveService
{
    public static readonly string[] RequiredCollections = ["users", "departments", "students", "documents", "audit"];
    // Deployment-bound resume tokens are never restored to a new database.
    public static readonly string[] OptionalCollections = ["operationAlerts", "databaseEvents", "backupRuns", "backupState"];
    private static void ValidateStorageName(string name)
    {
        if (!Regex.IsMatch(name, "^[0-9a-f]{32}\\.(pdf|png|jpg|jpeg)$")) throw new InvalidDataException("InvalidStorageName");
    }
    private static (string Name, long Size) DocumentMetadata(BsonDocument document)
    {
        if (!document.TryGetValue("StorageName", out var name) || !name.IsString ||
            !document.TryGetValue("Size", out var size) || !(size.IsInt32 || size.IsInt64))
            throw new InvalidDataException("InvalidDocumentMetadata");
        ValidateStorageName(name.AsString);
        var length = size.ToInt64();
        if (length <= 0 || length > 5 * 1024 * 1024) throw new InvalidDataException("InvalidDocumentSize");
        return (name.AsString, length);
    }
    public static async Task CreateAsync(IMongoDatabase db, string uploadRoot, string destination, string password, CancellationToken cancellationToken = default)
    {
        if (password.Length < 12) throw new ArgumentException("BackupPasswordTooShort");
        destination = Path.GetFullPath(destination);
        if (File.Exists(destination)) throw new IOException("BackupAlreadyExists");
        var databaseName = db.DatabaseNamespace.DatabaseName;
        var existingCollections = await (await db.ListCollectionNamesAsync(cancellationToken: cancellationToken)).ToListAsync(cancellationToken);
        if (RequiredCollections.Any(name => !existingCollections.Contains(name)))
            throw new InvalidDataException("Database nguồn thiếu collection của Campus Records. Kiểm tra tên database và khởi tạo ứng dụng trước khi sao lưu.");
        var collections = RequiredCollections.Concat(OptionalCollections.Where(existingCollections.Contains)).ToArray();
        var counts = new Dictionary<string, long>(); var storageNames = new HashSet<string>(); var fileSizes = new Dictionary<string, long>(); long totalSize = 0;
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, true))
        {
            foreach (var collection in collections)
            {
                var entry = zip.CreateEntry($"collections/{collection}.jsonl");
                using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false)) { NewLine = "\n" };
                using var cursor = await db.GetCollection<BsonDocument>(collection).FindAsync(FilterDefinition<BsonDocument>.Empty, cancellationToken: cancellationToken);
                long count = 0;
                while (await cursor.MoveNextAsync(cancellationToken)) foreach (var doc in cursor.Current)
                {
                    var line = doc.ToJson(new JsonWriterSettings { OutputMode = JsonOutputMode.CanonicalExtendedJson });
                    totalSize += Encoding.UTF8.GetByteCount(line) + 1;
                    if (totalSize > ArchiveCrypto.MaximumBytes) throw new InvalidDataException("Dữ liệu vượt giới hạn 256 MiB của công cụ học phần.");
                    await writer.WriteLineAsync(line.AsMemory(), cancellationToken); count++;
                    if (collection == "documents")
                    {
                        var (name, size) = DocumentMetadata(doc);
                        if (!storageNames.Add(name)) throw new InvalidDataException("Trùng tên tài liệu trong dữ liệu.");
                        fileSizes[name] = size;
                    }
                }
                counts[collection] = count;
            }
            foreach (var name in storageNames)
            {
                var path = Path.Combine(uploadRoot, name);
                if (!File.Exists(path)) throw new InvalidDataException("Thiếu tệp đính kèm; không thể tạo bản sao lưu nhất quán.");
                var length = new FileInfo(path).Length; totalSize += length;
                if (length != fileSizes[name]) throw new InvalidDataException("Kích thước tệp đính kèm không khớp metadata; hãy kiểm tra tài liệu gốc.");
                if (length > 5 * 1024 * 1024 || totalSize > ArchiveCrypto.MaximumBytes) throw new InvalidDataException("Tài liệu hoặc tổng dữ liệu vượt giới hạn sao lưu.");
                var entry = zip.CreateEntry($"uploads/{name}");
                await using var source = File.OpenRead(path); await using var target = entry.Open(); await source.CopyToAsync(target, cancellationToken);
            }
            var manifest = new Manifest(1, databaseName, DateTime.UtcNow, counts, storageNames.Count);
            var metadata = JsonSerializer.SerializeToUtf8Bytes(manifest);
            if (totalSize + metadata.LongLength > ArchiveCrypto.MaximumBytes) throw new InvalidDataException("ArchiveTooLarge");
            await using var meta = zip.CreateEntry("manifest.json").Open(); await meta.WriteAsync(metadata, cancellationToken);
        }
        var encrypted = ArchiveCrypto.Encrypt(buffer.ToArray(), password);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        // Publish only a fully flushed encrypted file. A crash cannot expose a
        // partially written archive under its final name.
        var temporary = destination + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                await output.WriteAsync(encrypted, cancellationToken);
                await output.FlushAsync(cancellationToken); output.Flush(flushToDisk: true);
            }
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, destination, overwrite: false);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    public static async Task<VerifiedArchive> VerifyAsync(string source, string password, CancellationToken cancellationToken = default)
    {
        if (new FileInfo(source).Length > ArchiveCrypto.MaximumBytes + 48) throw new InvalidDataException("Tệp bản sao lưu quá lớn.");
        var decrypted = ArchiveCrypto.Decrypt(await File.ReadAllBytesAsync(source, cancellationToken), password);
        var zip = new ZipArchive(new MemoryStream(decrypted), ZipArchiveMode.Read);
        try
        {
            if (zip.Entries.Select(e => e.FullName).Distinct().Count() != zip.Entries.Count) throw new InvalidDataException("Bản sao lưu chứa mục trùng lặp.");
            if (zip.Entries.Sum(e => e.Length) > ArchiveCrypto.MaximumBytes) throw new InvalidDataException("Dữ liệu giải nén vượt 256 MiB.");
            var manifestEntry = zip.GetEntry("manifest.json") ?? throw new InvalidDataException("Thiếu manifest.");
            await using var manifestStream = manifestEntry.Open();
            var manifest = await JsonSerializer.DeserializeAsync<Manifest>(manifestStream, cancellationToken: cancellationToken) ?? throw new InvalidDataException("Manifest không hợp lệ.");
            if (manifest.Counts == null || manifest.Counts.Values.Any(x => x < 0) || string.IsNullOrWhiteSpace(manifest.Database) || manifest.Documents < 0) throw new InvalidDataException("InvalidManifest");
            if (manifest.Version != 1) throw new InvalidDataException("Phiên bản backup chưa được hỗ trợ.");
            var records = new Dictionary<string, List<BsonDocument>>(); var fileNames = new HashSet<string>(); var fileSizes = new Dictionary<string, long>();
            var collections = manifest.Counts.Keys.ToArray();
            if (RequiredCollections.Any(x => !collections.Contains(x)) || collections.Any(x => !RequiredCollections.Concat(OptionalCollections).Contains(x))) throw new InvalidDataException("UnexpectedCollections");
            foreach (var collection in collections)
            {
                var entry = zip.GetEntry($"collections/{collection}.jsonl") ?? throw new InvalidDataException("Thiếu collection trong bản sao lưu.");
                using var reader = new StreamReader(entry.Open()); var rows = new List<BsonDocument>();
                while (await reader.ReadLineAsync(cancellationToken) is { } line)
                {
                    var row = BsonDocument.Parse(line); rows.Add(row);
                    if (collection == "documents")
                    {
                        var (name, size) = DocumentMetadata(row);
                        if (!fileNames.Add(name)) throw new InvalidDataException("Trùng tên tài liệu.");
                        fileSizes[name] = size;
                    }
                }
                if (!manifest.Counts.TryGetValue(collection, out var count) || count != rows.Count) throw new InvalidDataException("Số bản ghi không khớp manifest.");
                records[collection] = rows;
            }
            if (manifest.Documents != fileNames.Count || zip.Entries.Count != collections.Length + 1 + fileNames.Count) throw new InvalidDataException("Danh sách tài liệu không khớp manifest.");
            foreach (var name in fileNames)
            {
                var entry = zip.GetEntry($"uploads/{name}") ?? throw new InvalidDataException("Thiếu tài liệu đính kèm.");
                if (entry.Length != fileSizes[name]) throw new InvalidDataException("Kích thước tài liệu trong backup không khớp metadata.");
                if (entry.Length > 5 * 1024 * 1024) throw new InvalidDataException("Tài liệu vượt giới hạn kích thước.");
            }
            return new VerifiedArchive(zip, manifest, records, fileNames);
        }
        catch { zip.Dispose(); throw; }
    }
    public static async Task RestoreAsync(VerifiedArchive archive, IMongoDatabase db, string uploadRoot)
    {
        var manifest = archive.Manifest; var records = archive.Records; var fileNames = archive.Files;
        var zip = archive.Zip; var collections = records.Keys.ToArray();
        var databaseName = db.DatabaseNamespace.DatabaseName;
        if (databaseName == manifest.Database) throw new ArgumentException("Khôi phục vào database MỚI có tên khác bản gốc để bảo toàn dữ liệu hiện tại.");
        if ((await (await db.ListCollectionNamesAsync()).ToListAsync()).Count != 0) throw new ArgumentException("Database đích phải hoàn toàn trống. Công cụ không xóa hoặc ghi đè dữ liệu có sẵn.");
        if (Directory.Exists(uploadRoot) && Directory.EnumerateFileSystemEntries(uploadRoot).Any()) throw new ArgumentException("Thư mục tài liệu đích phải trống.");
        foreach (var row in records["users"]) { row["SecurityStamp"] = Guid.NewGuid().ToString(); row["ConcurrencyStamp"] = Guid.NewGuid().ToString(); }
        // Restored history is never authority to delete source archive media,
        // including after a later restore back into a previously used DB name.
        if (records.TryGetValue("backupRuns", out var history))
            foreach (var row in history) row["OwnerDatabase"] = "";
        Directory.CreateDirectory(uploadRoot);
        foreach (var collection in collections) if (records[collection].Count > 0) await db.GetCollection<BsonDocument>(collection).InsertManyAsync(records[collection]);
        foreach (var name in fileNames)
        {
            await using var input = zip.GetEntry($"uploads/{name}")!.Open();
            await using var output = new FileStream(Path.Combine(uploadRoot, name), FileMode.CreateNew, FileAccess.Write, FileShare.None); await input.CopyToAsync(output);
        }
        foreach (var collection in collections) if (await db.GetCollection<BsonDocument>(collection).CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty) != records[collection].Count) throw new InvalidDataException("Số lượng sau khôi phục không khớp.");
    }
}
