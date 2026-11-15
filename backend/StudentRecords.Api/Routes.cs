using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Identity;
using MongoDB.Bson;
using MongoDB.Driver;
namespace StudentRecords.Api;

public static class Routes
{
    public static void Map(WebApplication app)
    {
        var api = app.MapGroup("/api").RequireAuthorization();
        api.MapGet("/departments", async (DepartmentCache cache, CancellationToken ct) => Results.Ok(await cache.Get(ct)));
        api.MapPost("/departments", async (JsonElement input, ApiSupport s, Database db, DepartmentCache cache) => { s.Roles("Admin"); var d = new Department(); DepartmentInput(d, input); await db.Departments.InsertOneAsync(d); cache.Invalidate(); await s.Audit("CreateDepartment", d.Id); return Results.Created("/api/departments/" + d.Id, d); });
        api.MapPut("/departments/{id}", async (string id, JsonElement input, ApiSupport s, Database db, DepartmentCache cache) => { s.Roles("Admin"); ApiSupport.Id(id); var d = await db.Departments.Find(x => x.Id == id).FirstOrDefaultAsync() ?? throw new ApiException(404, "Khoa không tồn tại."); DepartmentInput(d, input); await db.Departments.ReplaceOneAsync(x => x.Id == id, d); cache.Invalidate(); await s.Audit("UpdateDepartment", id); return Results.Ok(d); });
        api.MapDelete("/departments/{id}", async (string id, ApiSupport s, Database db, DepartmentCache cache) => { s.Roles("Admin"); ApiSupport.Id(id); if (await db.Students.Find(x => x.DepartmentId == id).AnyAsync() || await db.Users.Find(x => x.DepartmentId == id).AnyAsync()) throw new ApiException(409, "Khoa đang được sử dụng."); if ((await db.Departments.DeleteOneAsync(x => x.Id == id)).DeletedCount == 0) throw new ApiException(404, "Khoa không tồn tại."); cache.Invalidate(); await s.Audit("DeleteDepartment", id); return Results.NoContent(); });
        api.MapGet("/students", async (HttpRequest r, ApiSupport s) => Results.Ok(await s.Students(r)));
        api.MapGet("/students/{id}", async (string id, ApiSupport s) => Results.Ok(Dto.Student(await s.Student(id), s.User)));
        api.MapPost("/students", async (JsonElement input, ApiSupport s, Database db) => { s.Roles("Admin", "Staff"); var row = new Student(); await s.ApplyStudent(row, input, true); await db.Students.InsertOneAsync(row); await s.Audit("CreateStudent", row.Id); return Results.Created("/api/students/" + row.Id, Dto.Student(row, s.User)); });
        api.MapPut("/students/{id}", async (string id, JsonElement input, ApiSupport s, Database db) =>
        {
            var row = await s.Student(id);
            await s.ApplyStudent(row, input, false);
            var filter = Builders<Student>.Filter.Eq(x => x.Id, id) & s.Scope();
            var saved = await db.Students.FindOneAndUpdateAsync(filter, ApiSupport.StudentChanges(row, input),
                new FindOneAndUpdateOptions<Student> { ReturnDocument = ReturnDocument.After });
            if (saved is null) throw new ApiException(404, "Không tìm thấy dữ liệu.");
            await s.Audit("UpdateStudent", id);
            return Results.Ok(Dto.Student(saved, s.User));
        });
        api.MapDelete("/students/{id}", async (string id, ApiSupport s, Database db, IConfiguration config) => { s.Roles("Admin"); await s.Student(id); var docs = await db.Documents.Find(x => x.StudentId == id).ToListAsync(); foreach (var d in docs) File.Delete(Path.Combine(Storage(config), d.StorageName)); await db.Documents.DeleteManyAsync(x => x.StudentId == id); await db.Users.UpdateManyAsync(x => x.StudentId == id, Builders<AppUser>.Update.Set(x => x.StudentId, null).Set(x => x.SecurityStamp, Guid.NewGuid().ToString())); await db.Students.DeleteOneAsync(x => x.Id == id); await s.Audit("DeleteStudent", id); return Results.NoContent(); });
        api.MapGet("/students/{id}/documents", async (string id, ApiSupport s, Database db) => { await s.Student(id); return Results.Ok((await db.Documents.Find(x => x.StudentId == id).SortByDescending(x => x.UploadedAt).ToListAsync()).Select(Dto.Document)); });
        api.MapPost("/students/{id}/documents", async (string id, HttpRequest request, ApiSupport s, Database db, IConfiguration config) =>
        {
            await s.Student(id); if (!request.HasFormContentType) throw new ApiException(400, "Cần gửi tệp multipart."); var form = await request.ReadFormAsync(); if (form.Files.Count != 1 || form.Files[0].Name != "file") throw new ApiException(400, "Chọn một tệp."); var file = form.Files[0]; if (file.Length == 0 || file.Length > 5 * 1024 * 1024) throw new ApiException(400, "Tệp tối đa 5 MiB.");
            var name = Path.GetFileName(file.FileName.Replace('\\', '/')); if (name.Length > 180 || name.Any(char.IsControl) || name.Contains('<') || name.Contains('>') || string.IsNullOrWhiteSpace(name)) throw new ApiException(400, "Tên tệp không hợp lệ."); var ext = Path.GetExtension(name).ToLowerInvariant(); var mime = ext switch { ".pdf" => "application/pdf", ".png" => "image/png", ".jpg" or ".jpeg" => "image/jpeg", _ => "" }; if (mime == "" || file.ContentType != mime) throw new ApiException(400, "Chỉ chấp nhận PDF, PNG hoặc JPEG.");
            using var stream = file.OpenReadStream(); var head = new byte[8]; var n = await stream.ReadAsync(head); var valid = ext switch { ".pdf" => n >= 5 && head.AsSpan(0, 5).SequenceEqual("%PDF-"u8), ".png" => n == 8 && head.AsSpan().SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }), _ => n >= 3 && head[0] == 255 && head[1] == 216 && head[2] == 255 }; if (!valid) throw new ApiException(400, "Nội dung tệp không khớp định dạng.");
            var root = Storage(config); Directory.CreateDirectory(root); var d = new Document { StudentId = id, OriginalName = name, ContentType = mime, Size = file.Length, UploadedBy = s.User.Id, StorageName = Guid.NewGuid().ToString("N") + ext }; var path = Path.Combine(root, d.StorageName); try { await using (var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { await output.WriteAsync(head.AsMemory(0, n)); await stream.CopyToAsync(output); } await db.Documents.InsertOneAsync(d); } catch { File.Delete(path); throw; }
            await s.Audit("UploadDocument", d.Id); return Results.Created("/api/documents/" + d.Id + "/download", Dto.Document(d));
        });
        api.MapGet("/documents/{id}/download", async (string id, ApiSupport s, Database db, IConfiguration config) => { ApiSupport.Id(id); var d = await db.Documents.Find(x => x.Id == id).FirstOrDefaultAsync() ?? throw new ApiException(404, "Không tìm thấy dữ liệu."); await s.Student(d.StudentId); var path = Path.Combine(Storage(config), d.StorageName); if (!File.Exists(path)) throw new ApiException(404, "Không tìm thấy dữ liệu."); await s.Audit("DownloadDocument", id); return Results.File(path, d.ContentType, d.OriginalName, enableRangeProcessing: false); });
        api.MapDelete("/documents/{id}", async (string id, ApiSupport s, Database db, IConfiguration config) => { ApiSupport.Id(id); var d = await db.Documents.Find(x => x.Id == id).FirstOrDefaultAsync() ?? throw new ApiException(404, "Không tìm thấy dữ liệu."); await s.Student(d.StudentId); File.Delete(Path.Combine(Storage(config), d.StorageName)); await db.Documents.DeleteOneAsync(x => x.Id == id); await s.Audit("DeleteDocument", id); return Results.NoContent(); });
        api.MapGet("/reports", async (HttpRequest r, ApiSupport s) => { s.Roles("Admin", "Staff"); var data = await s.Students(r); await s.Audit("ReadReport"); return Results.Ok(data); });
        api.MapGet("/reports/export", async (HttpRequest r, ApiSupport s, Database db) => { s.Roles("Admin", "Staff"); var rows = await db.Students.Find(s.Query(r), new FindOptions { MaxTime = TimeSpan.FromSeconds(10) }).Sort(ApiSupport.Sort(r)).Limit(5000).ToListAsync(); var text = new StringBuilder("\uFEFFMã sinh viên,Họ tên,Email,Điện thoại,CCCD,Khoa,Lớp,Năm nhập học,Trạng thái,GPA\r\n"); foreach (var row in rows) { var mask = s.User.Role == "Staff"; text.AppendLine(string.Join(',', new[] { row.StudentCode, row.FullName, mask ? "***@***" : row.Email, mask ? Dto.Mask(row.Phone) : row.Phone, mask ? Dto.Mask(row.NationalId) : row.NationalId, row.DepartmentId, row.ClassName, row.EnrollmentYear.ToString(), row.Status, mask ? "" : row.Gpa?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "" }.Select(Csv))); } await s.Audit("ExportReport"); return Results.File(Encoding.UTF8.GetBytes(text.ToString()), "text/csv; charset=utf-8", "bao-cao-sinh-vien.csv"); });
        api.MapGet("/users", async (HttpRequest r, ApiSupport s, Database db) => { s.Roles("Admin"); var (page, size) = ApiSupport.Pagination(r); var search = r.Query["search"].ToString(); if (search.Length > 100) throw new ApiException(400, "Từ khóa tối đa 100 ký tự."); var rx = new BsonRegularExpression(Regex.Escape(search), "i"); var f = Builders<AppUser>.Filter.Regex(x => x.FullName, rx) | Builders<AppUser>.Filter.Regex(x => x.Email, rx); var total = await db.Users.CountDocumentsAsync(f); var rows = await db.Users.Find(f, new FindOptions { MaxTime = TimeSpan.FromSeconds(5) }).SortBy(x => x.Email).Skip((page - 1) * size).Limit(size).ToListAsync(); return Results.Ok(new { items = rows.Select(Dto.User), total, page, pageSize = size }); });
        api.MapPut("/users/{id}", async (string id, JsonElement input, ApiSupport s, Database db, UserManager<AppUser> manager) =>
        {
            s.Roles("Admin"); ApiSupport.Id(id); ApiSupport.Fields(input, "role", "departmentId", "studentId", "enabled", "unlock"); var u = await db.Users.Find(x => x.Id == id).FirstOrDefaultAsync() ?? throw new ApiException(404, "Tài khoản không tồn tại."); if (input.TryGetProperty("role", out _)) { u.Role = ApiSupport.Text(input, "role", 20); if (!new[] { "Admin", "Staff", "Student" }.Contains(u.Role)) throw new ApiException(400, "Vai trò không hợp lệ."); }
            if (input.TryGetProperty("enabled", out var enabled)) { if (enabled.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) throw new ApiException(400, "Trạng thái tài khoản không hợp lệ."); u.Enabled = enabled.GetBoolean(); }
            if (u.Id == s.User.Id && (!u.Enabled || u.Role != "Admin")) throw new ApiException(400, "Không thể tự vô hiệu hóa hoặc hạ quyền.");
            if (input.TryGetProperty("departmentId", out var dep)) u.DepartmentId = dep.ValueKind == JsonValueKind.Null ? null : ApiSupport.Text(input, "departmentId", 24, false); if (u.DepartmentId == "") u.DepartmentId = null;
            if (input.TryGetProperty("studentId", out var student)) u.StudentId = student.ValueKind == JsonValueKind.Null ? null : ApiSupport.Text(input, "studentId", 24, false); if (u.StudentId == "") u.StudentId = null;
            if (u.Role != "Staff") u.DepartmentId = null; if (u.Role != "Student") u.StudentId = null;
            if (u.Role == "Staff" && u.DepartmentId == null) throw new ApiException(400, "Nhân viên phải được liên kết với khoa."); if (u.DepartmentId != null) { ApiSupport.Id(u.DepartmentId); if (!await db.Departments.Find(x => x.Id == u.DepartmentId).AnyAsync()) throw new ApiException(400, "Khoa không tồn tại."); }
            if (u.StudentId != null) { ApiSupport.Id(u.StudentId); if (!await db.Students.Find(x => x.Id == u.StudentId).AnyAsync()) throw new ApiException(400, "Hồ sơ không tồn tại."); if (await db.Users.Find(x => x.StudentId == u.StudentId && x.Id != id).AnyAsync()) throw new ApiException(409, "Hồ sơ đã được liên kết với tài khoản khác."); }
            if (input.TryGetProperty("unlock", out var unlock)) { if (unlock.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) throw new ApiException(400, "unlock không hợp lệ."); if (unlock.GetBoolean()) { u.LockedUntil = null; u.AccessFailedCount = 0; } }
            u.SecurityStamp = Guid.NewGuid().ToString(); if (!(await manager.UpdateAsync(u)).Succeeded) throw new ApiException(409, "Tài khoản đã thay đổi. Vui lòng thử lại."); await s.Audit("UpdateUser", id); return Results.Ok(Dto.User(u));
        });
        api.MapGet("/audit", async (HttpRequest r, ApiSupport s, Database db) => { s.Roles("Admin"); var (page, size) = ApiSupport.Pagination(r); var f = Builders<Audit>.Filter.Empty; var action = r.Query["action"].ToString(); var result = r.Query["result"].ToString(); if (action.Length > 100) throw new ApiException(400, "Bộ lọc không hợp lệ."); if (action != "") f &= Builders<Audit>.Filter.Eq(x => x.Action, action); if (result != "") { if (!new[] { "Success", "Denied", "Failure" }.Contains(result)) throw new ApiException(400, "Kết quả không hợp lệ."); f &= Builders<Audit>.Filter.Eq(x => x.Result, result); } return Results.Ok(new { items = await db.Audits.Find(f).SortByDescending(x => x.CreatedAt).Skip((page - 1) * size).Limit(size).ToListAsync(), total = await db.Audits.CountDocumentsAsync(f), page, pageSize = size }); });
        api.MapGet("/dashboard", Dashboard);
        api.MapGet("/security", (ApiSupport s, IHostEnvironment env) => Results.Ok(new { checks = new[] { new { id = "identity", title = "Mật khẩu và khóa tài khoản", status = "Implemented", description = "ASP.NET Core Identity; khóa 15 phút sau 5 lần sai." }, new { id = "csrf", title = "Bảo vệ CSRF", status = "Implemented", description = "Mã chống giả mạo bắt buộc cho mọi POST/PUT/DELETE." }, new { id = "scope", title = "Phân quyền dữ liệu", status = "Implemented", description = "Kiểm tra scope và che dữ liệu tại API." }, new { id = "atlas", title = "Mã hóa và sao lưu Atlas", status = "Infrastructure", description = "Cần cấu hình và xác minh trong MongoDB Atlas." }, new { id = "https", title = "HTTPS và cookie", status = env.IsDevelopment() ? "Partial" : "Implemented", description = env.IsDevelopment() ? "Môi trường local; production bắt buộc HTTPS." : "Cookie Secure, HttpOnly, SameSite=Lax." } }, storage = "MongoDB + tài liệu riêng tư; backup và mã hóa Atlas do quản trị hạ tầng cấu hình.", authentication = "ASP.NET Core Identity / Mongo store, cookie 30 phút, kiểm tra security stamp mỗi request." }));
    }
    private static async Task<IResult> Dashboard(ApiSupport s, Database db, DepartmentCache cache, CancellationToken ct)
    {
        var filter = s.Scope();
        var options = new AggregateOptions { MaxTime = TimeSpan.FromSeconds(5) };
        var groups = await db.Students.Aggregate(options).Match(filter)
            .Group(x => new { x.DepartmentId, x.Status }, g => new { g.Key.DepartmentId, g.Key.Status, Count = g.Count() }).ToListAsync(ct);
        var recent = await db.Students.Find(filter, new FindOptions { MaxTime = TimeSpan.FromSeconds(5) })
            .SortByDescending(x => x.CreatedAt).Limit(5).ToListAsync(ct);
        var rendered = filter.Render(new RenderArgs<Student>(db.Students.DocumentSerializer, MongoDB.Bson.Serialization.BsonSerializer.SerializerRegistry));
        // Count attachments server-side, joining only scoped student IDs, never loading all profiles.
        var documentPipeline = new BsonDocument[]
        {
            new("$match", rendered),
            new("$project", new BsonDocument("_id", 1)),
            new("$lookup", new BsonDocument {
                { "from", "documents" }, { "let", new BsonDocument("studentId", new BsonDocument("$toString", "$_id")) },
                { "pipeline", new BsonArray {
                    new BsonDocument("$match", new BsonDocument("$expr", new BsonDocument("$eq", new BsonArray { "$StudentId", "$$studentId" }))),
                    new BsonDocument("$count", "count") } }, { "as", "counts" } }),
            new("$unwind", "$counts"),
            new("$group", new BsonDocument { { "_id", BsonNull.Value }, { "count", new BsonDocument("$sum", "$counts.count") } })
        };
        var documents = await db.Students.Aggregate<BsonDocument>(documentPipeline, options).FirstOrDefaultAsync(ct);
        var deps = await cache.Get(ct);
        var visibleDeps = s.User.Role == "Admin" ? deps : deps.Where(d => groups.Any(x => x.DepartmentId == d.Id)).ToArray();
        var auditFilter = s.User.Role == "Admin" ? Builders<Audit>.Filter.Empty : Builders<Audit>.Filter.Eq(x => x.UserId, s.User.Id);
        return Results.Ok(new
        {
            totalStudents = groups.Sum(x => x.Count), activeStudents = groups.Where(x => x.Status == "Active").Sum(x => x.Count),
            totalDepartments = visibleDeps.Length, totalDocuments = documents?["count"].ToInt64() ?? 0,
            byDepartment = visibleDeps.Select(d => new { departmentId = d.Id, name = d.Name, count = groups.Where(x => x.DepartmentId == d.Id).Sum(x => x.Count) }),
            byStatus = ApiSupport.Statuses.Select(status => new { status, count = groups.Where(x => x.Status == status).Sum(x => x.Count) }),
            recentStudents = recent.Select(x => Dto.Student(x, s.User)),
            recentActivity = await db.Audits.Find(auditFilter, new FindOptions { MaxTime = TimeSpan.FromSeconds(5) }).SortByDescending(x => x.CreatedAt).Limit(8).ToListAsync(ct)
        });
    }
    private static void DepartmentInput(Department d, JsonElement input) { ApiSupport.Fields(input, "code", "name"); d.Code = ApiSupport.Text(input, "code", 30).ToUpperInvariant(); if (!Regex.IsMatch(d.Code, "^[A-Z0-9_-]+$")) throw new ApiException(400, "Mã khoa không hợp lệ."); d.Name = ApiSupport.Text(input, "name", 120); }
    public static string Storage(IConfiguration config) => Path.GetFullPath(config["Storage:Path"] ?? Path.Combine(AppContext.BaseDirectory, "private-uploads"));
    public static string Csv(string value) { if (value.TrimStart().StartsWith('=') || value.TrimStart().StartsWith('+') || value.TrimStart().StartsWith('-') || value.TrimStart().StartsWith('@') || value.StartsWith('\t') || value.StartsWith('\r')) value = "'" + value; return "\"" + value.Replace("\"", "\"\"") + "\""; }
}


