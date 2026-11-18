using System.Security.Claims;
using System.Text.Json;
using System.Text.RegularExpressions;
using MongoDB.Bson;
using MongoDB.Driver;

namespace StudentRecords.Api;

public sealed class ApiSupport(Database db, IHttpContextAccessor accessor)
{
    public static string Password(JsonElement data, string field)
    {
        if (!data.TryGetProperty(field, out var property) || property.ValueKind != JsonValueKind.String)
            throw new ApiException(400, "Thiếu mật khẩu.");
        var value = property.GetString()!;
        if (value.Length is < 1 or > 128 || value.Any(char.IsControl))
            throw new ApiException(400, "Mật khẩu không hợp lệ.");
        return value;
    }

    public static UpdateDefinition<Student> StudentChanges(Student row, JsonElement input)
    {
        var update = Builders<Student>.Update;
        var fields = new Dictionary<string, UpdateDefinition<Student>>
        {
            ["studentCode"] = update.Set(s => s.StudentCode, row.StudentCode),
            ["fullName"] = update.Set(s => s.FullName, row.FullName),
            ["email"] = update.Set(s => s.Email, row.Email),
            ["phone"] = update.Set(s => s.Phone, row.Phone),
            ["nationalId"] = update.Set(s => s.NationalId, row.NationalId),
            ["dateOfBirth"] = update.Set(s => s.DateOfBirth, row.DateOfBirth),
            ["gender"] = update.Set(s => s.Gender, row.Gender),
            ["address"] = update.Set(s => s.Address, row.Address),
            ["departmentId"] = update.Set(s => s.DepartmentId, row.DepartmentId),
            ["className"] = update.Set(s => s.ClassName, row.ClassName),
            ["enrollmentYear"] = update.Set(s => s.EnrollmentYear, row.EnrollmentYear),
            ["status"] = update.Set(s => s.Status, row.Status),
            ["gpa"] = update.Set(s => s.Gpa, row.Gpa)
        };
        return update.Combine(input.EnumerateObject().Select(p => fields[p.Name]).Append(update.Set(s => s.UpdatedAt, row.UpdatedAt)));
    }

    public HttpContext Context => accessor.HttpContext!;
    public AppUser User => Context.Items["user"] as AppUser ?? throw new ApiException(401, "Vui lòng đăng nhập.");

    public void Roles(params string[] roles)
    {
        if (!roles.Contains(User.Role))
            throw new ApiException(403, "Bạn không có quyền thực hiện thao tác này.");
    }

    public static void Id(string id)
    {
        if (!ObjectId.TryParse(id, out _))
            throw new ApiException(404, "Không tìm thấy dữ liệu.");
    }

    public FilterDefinition<Student> Scope()
    {
        var f = Builders<Student>.Filter;
        return User.Role switch
        {
            "Admin" => f.Empty,
            "Staff" when User.DepartmentId != null => f.Eq(s => s.DepartmentId, User.DepartmentId),
            "Student" when User.StudentId != null => f.Eq(s => s.Id, User.StudentId),
            _ => f.Eq(s => s.Id, ObjectId.Empty.ToString())
        };
    }

    public async Task<Student> Student(string id)
    {
        Id(id);
        return await db.Students.Find(Scope() & Builders<Student>.Filter.Eq(s => s.Id, id)).FirstOrDefaultAsync() ?? throw new ApiException(404, "Không tìm thấy dữ liệu.");
    }

    public async Task Audit(string action, string target = "", string result = "Success")
    {
        var u = Context.Items["user"] as AppUser;
        await db.Audits.InsertOneAsync(new Audit { UserId = u?.Id, UserName = u?.Email ?? "Ẩn danh", Action = action, Target = target, Result = result, IpAddress = Context.Connection.RemoteIpAddress?.ToString() ?? "", TraceId = Context.TraceIdentifier });
    }

    public static (int page, int size) Pagination(HttpRequest r)
    {
        int Parse(string k, int fallback, int max)
        {
            if (!r.Query.ContainsKey(k))
                return fallback;
            return int.TryParse(r.Query[k], out var n) && n >= 1 && n <= max ? n : throw new ApiException(400, "Phân trang không hợp lệ.");
        }

        return (Parse("page", 1, 100000), Parse("pageSize", 10, 50));
    }

    public FilterDefinition<Student> Query(HttpRequest r)
    {
        var f = Builders<Student>.Filter;
        var q = Scope();
        var search = r.Query["search"].ToString().Trim();
        if (search.Length > 100)
            throw new ApiException(400, "Từ khóa tối đa 100 ký tự.");
        if (search.Length > 0)
        {
            var rx = new BsonRegularExpression(Regex.Escape(search), "i");
            q &= f.Regex(x => x.FullName, rx) | f.Regex(x => x.StudentCode, rx);
        }

        if (r.Query.TryGetValue("departmentId", out var dep) && dep != "")
        {
            Id(dep!);
            q &= f.Eq(x => x.DepartmentId, dep.ToString());
        }

        if (r.Query.TryGetValue("status", out var status) && status != "")
        {
            if (!Statuses.Contains(status.ToString()))
                throw new ApiException(400, "Trạng thái không hợp lệ.");
            q &= f.Eq(x => x.Status, status.ToString());
        }

        if (r.Query.TryGetValue("year", out var year) && year != "")
        {
            if (!int.TryParse(year, out var y) || y < 1990 || y > 2100)
                throw new ApiException(400, "Năm không hợp lệ.");
            q &= f.Eq(x => x.EnrollmentYear, y);
        }

        return q;
    }

    public static readonly string[] Statuses = ["Active", "Paused", "Graduated"];
    public static SortDefinition<Student> Sort(HttpRequest r) => r.Query["sort"].ToString() switch
    {
        "" or "newest" => Builders<Student>.Sort.Descending(x => x.CreatedAt).Ascending(x => x.Id),
        "name" => Builders<Student>.Sort.Ascending(x => x.FullName).Ascending(x => x.Id),
        "code" => Builders<Student>.Sort.Ascending(x => x.StudentCode),
        _ => throw new ApiException(400, "Thứ tự sắp xếp không hợp lệ.")
    };
    public async Task<object> Students(HttpRequest request)
    {
        var (page, size) = Pagination(request);
        var q = Query(request);
        var total = await db.Students.CountDocumentsAsync(q, new CountOptions { MaxTime = TimeSpan.FromSeconds(5) });
        var items = await db.Students.Find(q, new FindOptions { MaxTime = TimeSpan.FromSeconds(5) }).Sort(Sort(request)).Skip((page - 1) * size).Limit(size).ToListAsync();
        return new
        {
            items = items.Select(s => Dto.Student(s, User)),
            total,
            page,
            pageSize = size
        };
    }

    public static void Fields(JsonElement data, params string[] allowed)
    {
        if (data.ValueKind != JsonValueKind.Object)
            throw new ApiException(400, "Dữ liệu phải là đối tượng JSON.");
        foreach (var p in data.EnumerateObject())
            if (!allowed.Contains(p.Name))
                throw new ApiException(400, $"Trường không hợp lệ: {p.Name}.");
    }

    public static string Text(JsonElement data, string field, int max = 200, bool required = true)
    {
        if (!data.TryGetProperty(field, out var p) || p.ValueKind != JsonValueKind.String)
        {
            if (!required)
                return "";
            throw new ApiException(400, $"Thiếu {field}.");
        }

        var v = p.GetString()!.Trim();
        if ((required && v.Length == 0) || v.Length > max || v.Any(char.IsControl))
            throw new ApiException(400, $"{field} không hợp lệ.");
        return v;
    }

    public static void Email(string email)
    {
        if (!System.Net.Mail.MailAddress.TryCreate(email, out var parsed) || parsed.Address != email || email.Length > 254)
            throw new ApiException(400, "Email không hợp lệ.");
    }

    public async Task ApplyStudent(Student s, JsonElement data, bool create)
    {
        string[] all = ["studentCode", "fullName", "email", "phone", "nationalId", "dateOfBirth", "gender", "address", "departmentId", "className", "enrollmentYear", "status", "gpa"];
        Fields(data, all);
        var allowed = User.Role switch
        {
            "Admin" => all,
            "Staff" when create => all,
            "Staff" => ["fullName", "className", "enrollmentYear", "status"],
            _ => new[]
            {
                "email",
                "phone",
                "address"
            }
        };
        foreach (var p in data.EnumerateObject())
            if (!allowed.Contains(p.Name))
                throw new ApiException(403, "Bạn không có quyền thay đổi trường này.");
        if (create && User.Role == "Student")
            throw new ApiException(403, "Bạn không có quyền tạo hồ sơ.");
        if (create && User.Role == "Staff")
        {
            if (!data.TryGetProperty("departmentId", out var department) || department.ValueKind != JsonValueKind.String || department.GetString() != User.DepartmentId)
                throw new ApiException(403, "Chỉ được nhập hồ sơ trong khoa của bạn.");
            if (data.TryGetProperty("nationalId", out var nid) && nid.ValueKind is not (JsonValueKind.String or JsonValueKind.Null))
                throw new ApiException(400, "CCCD không hợp lệ.");
            if (data.TryGetProperty("nationalId", out nid) && nid.ValueKind != JsonValueKind.Null && nid.GetString() != "")
                throw new ApiException(403, "Không được nhập CCCD.");
            if (data.TryGetProperty("gpa", out var gpa) && gpa.ValueKind != JsonValueKind.Null)
                throw new ApiException(403, "Không được nhập GPA.");
        }

        foreach (var p in data.EnumerateObject())
        {
            switch (p.Name)
            {
                case "studentCode":
                    s.StudentCode = Text(data, p.Name, 30).ToUpperInvariant();
                    break;
                case "fullName":
                    s.FullName = Text(data, p.Name, 120);
                    break;
                case "email":
                    s.Email = Text(data, p.Name, 254);
                    Email(s.Email);
                    break;
                case "phone":
                    s.Phone = Text(data, p.Name, 20);
                    if (!Regex.IsMatch(s.Phone, @"^\+?[0-9 .()-]{7,20}$"))
                        throw new ApiException(400, "Số điện thoại không hợp lệ.");
                    break;
                case "nationalId":
                    if (p.Value.ValueKind == JsonValueKind.Null)
                    {
                        s.NationalId = "";
                        break;
                    }

                    if (p.Value.ValueKind != JsonValueKind.String)
                        throw new ApiException(400, "CCCD không hợp lệ.");
                    s.NationalId = Text(data, p.Name, 20, false);
                    if (s.NationalId.Length > 0 && !Regex.IsMatch(s.NationalId, @"^[0-9]{9,12}$"))
                        throw new ApiException(400, "CCCD phải gồm 9–12 chữ số.");
                    break;
                case "dateOfBirth":
                    s.DateOfBirth = Text(data, p.Name, 10);
                    if (!DateOnly.TryParseExact(s.DateOfBirth, "yyyy-MM-dd", out var date) || date > DateOnly.FromDateTime(DateTime.UtcNow) || date.Year < 1900)
                        throw new ApiException(400, "Ngày sinh không hợp lệ.");
                    break;
                case "gender":
                    s.Gender = Text(data, p.Name, 10);
                    if (!new[]
                    {
                        "Male",
                        "Female",
                        "Other"
                    }.Contains(s.Gender))
                        throw new ApiException(400, "Giới tính không hợp lệ.");
                    break;
                case "address":
                    s.Address = Text(data, p.Name, 500, false);
                    break;
                case "departmentId":
                    s.DepartmentId = Text(data, p.Name, 24);
                    Id(s.DepartmentId);
                    if (!await db.Departments.Find(x => x.Id == s.DepartmentId).AnyAsync())
                        throw new ApiException(400, "Khoa không tồn tại.");
                    break;
                case "className":
                    s.ClassName = Text(data, p.Name, 50);
                    break;
                case "status":
                    s.Status = Text(data, p.Name, 20);
                    if (!Statuses.Contains(s.Status))
                        throw new ApiException(400, "Trạng thái không hợp lệ.");
                    break;
                case "enrollmentYear":
                    if (p.Value.ValueKind != JsonValueKind.Number || !p.Value.TryGetInt32(out var y) || y < 1990 || y > 2100)
                        throw new ApiException(400, "Năm nhập học không hợp lệ.");
                    s.EnrollmentYear = y;
                    break;
                case "gpa":
                    if (p.Value.ValueKind == JsonValueKind.Null)
                        s.Gpa = null;
                    else if (p.Value.ValueKind == JsonValueKind.Number && p.Value.TryGetDouble(out var g) && double.IsFinite(g) && g >= 0 && g <= 4)
                        s.Gpa = g;
                    else
                        throw new ApiException(400, "GPA phải từ 0 đến 4.");
                    break;
            }
        }

        if (create)
            foreach (var k in all.Where(k => k != "gpa" && k != "address" && k != "nationalId"))
                if (!data.TryGetProperty(k, out _))
                    throw new ApiException(400, $"Thiếu {k}.");
        s.UpdatedAt = DateTime.UtcNow;
    }
}
