using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration;
using MongoDB.Bson;
using MongoDB.Bson.IO;
using MongoDB.Driver;
using StudentRecords.Backup;

// Logical coursework backup. Stop the API so records and files stay consistent.
try
{
    if (args.Length == 0 || args[0] is not ("backup" or "restore" or "verify"))
        throw new ArgumentException("Dùng: backup --output FILE --uploads DIR --acknowledge-api-stopped | verify --input FILE | restore --input FILE --database NEW_DB --uploads NEW_DIR --confirm-restore --acknowledge-api-stopped");
    var command = args[0];
    string? Option(string name) { var i = Array.IndexOf(args, name); return i >= 0 && i + 1 < args.Length && !args[i + 1].StartsWith("--") ? args[i + 1] : null; }
    string Required(string name) => Option(name) ?? throw new ArgumentException($"Thiếu {name}.");
    var config = new ConfigurationBuilder().AddUserSecrets("CampusRecords.LocalDevelopment").AddEnvironmentVariables().Build();
    var configuredPassword = Environment.GetEnvironmentVariable("Backup__Password") ?? config["Backup:Password"];
    var password = configuredPassword ?? ReadPassword(command == "backup" ? "Mật khẩu mã hóa mới (tối thiểu 12 ký tự): " : "Mật khẩu giải mã: ");
    if (password.Length < 12) throw new ArgumentException("Mật khẩu bản sao lưu cần ít nhất 12 ký tự.");
    if (command == "backup" && configuredPassword is null && password != ReadPassword("Nhập lại mật khẩu: "))
        throw new ArgumentException("Mật khẩu xác nhận không trùng khớp.");
    IMongoDatabase Database(string name)
    {
        if (!Regex.IsMatch(name, "^[a-zA-Z0-9_-]{1,63}$")) throw new ArgumentException("Tên database chỉ dùng chữ, số, gạch ngang và gạch dưới (1–63 ký tự).");
        var uri = config["Mongo:ConnectionString"] ?? throw new ArgumentException("Thiếu Mongo:ConnectionString trong user-secrets hoặc Mongo__ConnectionString.");
        var settings = MongoClientSettings.FromConnectionString(uri);
        settings.ServerSelectionTimeout = TimeSpan.FromSeconds(5);
        settings.ConnectTimeout = TimeSpan.FromSeconds(5);
        return new MongoClient(settings).GetDatabase(name);
    }
    void RequireStopped()
    {
        if (!args.Contains("--acknowledge-api-stopped")) throw new ArgumentException("Dừng API và xác nhận bằng --acknowledge-api-stopped để giữ dữ liệu/tài liệu nhất quán.");
    }
    if (command == "backup")
    {
        RequireStopped();
        var destination = Path.GetFullPath(Required("--output"));
        if (File.Exists(destination)) throw new IOException("Tệp đầu ra đã tồn tại; hãy chọn tên mới.");
        var uploadRoot = Path.GetFullPath(Option("--uploads") ?? config["Storage:Path"] ?? throw new ArgumentException("Cần --uploads DIR hoặc cấu hình Storage:Path."));
        var databaseName = Option("--database") ?? config["Mongo:Database"] ?? "campus_records";
        var db = Database(databaseName);
        await ArchiveService.CreateAsync(db, uploadRoot, destination, password);
        Console.WriteLine("Đã tạo bản sao lưu AES-256-GCM.");
    }
    else
    {
        using var archive = await ArchiveService.VerifyAsync(Path.GetFullPath(Required("--input")), password);
        Console.WriteLine($"Bản sao lưu hợp lệ: {archive.Manifest.CreatedAt:u}.");
        if (command == "restore")
        {
            RequireStopped();
            if (!args.Contains("--confirm-restore")) throw new ArgumentException("Cần --confirm-restore để ghi dữ liệu khôi phục.");
            await ArchiveService.RestoreAsync(archive, Database(Required("--database")), Path.GetFullPath(Required("--uploads")));
            Console.WriteLine("Đã khôi phục. Khởi động API để tạo lại indexes và xác minh dữ liệu.");
        }
    }
    return 0;
}
catch (CryptographicException) { Console.Error.WriteLine("Sai mật khẩu, tệp bị sửa hoặc định dạng mã hóa không hợp lệ. Không khôi phục dữ liệu."); return 2; }
catch (MongoException) { Console.Error.WriteLine("Không thao tác được MongoDB. Kiểm tra URI, quyền tài khoản và IP allowlist. Nếu restore đã bắt đầu, bỏ database đích chưa hoàn chỉnh và thử lại với một tên mới."); return 3; }
catch (Exception ex) when (ex is ArgumentException or IOException or InvalidDataException or FormatException or JsonException or KeyNotFoundException or InvalidOperationException)
{ Console.Error.WriteLine($"Không thực hiện được: {ex.Message}"); return 1; }

static string ReadPassword(string prompt)
{
    Console.Write(prompt); var text = new StringBuilder();
    while (true)
    {
        var key = Console.ReadKey(true);
        if (key.Key == ConsoleKey.Enter) { Console.WriteLine(); return text.ToString(); }
        if (key.Key == ConsoleKey.Backspace) { if (text.Length > 0) text.Length--; }
        else if (!char.IsControl(key.KeyChar)) text.Append(key.KeyChar);
    }
}

