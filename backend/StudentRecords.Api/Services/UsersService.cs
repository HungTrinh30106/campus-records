using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using MongoDB.Bson;
using MongoDB.Driver;
using static StudentRecords.Api.RecordHelpers;

namespace StudentRecords.Api;

public sealed class UsersService(ApiSupport s, Database db, UserManager<AppUser> manager)
{
    public async Task<IResult> List(HttpRequest r)
    {
        s.Roles("Admin");
        var (page, size) = ApiSupport.Pagination(r);
        var search = r.Query["search"].ToString();
        if (search.Length > 100)
            throw new ApiException(400, "Từ khóa tối đa 100 ký tự.");
        var rx = new BsonRegularExpression(Regex.Escape(search), "i");
        var f = Builders<AppUser>.Filter.Regex(x => x.FullName, rx) | Builders<AppUser>.Filter.Regex(x => x.Email, rx);
        var total = await db.Users.CountDocumentsAsync(f);
        var rows = await db.Users.Find(f, new FindOptions { MaxTime = TimeSpan.FromSeconds(5) }).SortBy(x => x.Email).Skip((page - 1) * size).Limit(size).ToListAsync();
        return Results.Ok(new { items = rows.Select(Dto.User), total, page, pageSize = size });
    }

    public async Task<IResult> Update(string id, JsonElement input)
    {
        s.Roles("Admin");
        ApiSupport.Id(id);
        ApiSupport.Fields(input, "role", "departmentId", "studentId", "enabled", "unlock");
        var u = await db.Users.Find(x => x.Id == id).FirstOrDefaultAsync() ?? throw new ApiException(404, "Tài khoản không tồn tại.");
        if (input.TryGetProperty("role", out _))
        {
            u.Role = ApiSupport.Text(input, "role", 20);
            if (!new[]
            {
                "Admin",
                "Staff",
                "Student"
            }.Contains(u.Role))
                throw new ApiException(400, "Vai trò không hợp lệ.");
        }

        if (input.TryGetProperty("enabled", out var enabled))
        {
            if (enabled.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                throw new ApiException(400, "Trạng thái tài khoản không hợp lệ.");
            u.Enabled = enabled.GetBoolean();
        }

        if (u.Id == s.User.Id && (!u.Enabled || u.Role != "Admin"))
            throw new ApiException(400, "Không thể tự vô hiệu hóa hoặc hạ quyền.");
        if (input.TryGetProperty("departmentId", out var dep))
            u.DepartmentId = dep.ValueKind == JsonValueKind.Null ? null : ApiSupport.Text(input, "departmentId", 24, false);
        if (u.DepartmentId == "")
            u.DepartmentId = null;
        if (input.TryGetProperty("studentId", out var student))
            u.StudentId = student.ValueKind == JsonValueKind.Null ? null : ApiSupport.Text(input, "studentId", 24, false);
        if (u.StudentId == "")
            u.StudentId = null;
        if (u.Role != "Staff")
            u.DepartmentId = null;
        if (u.Role != "Student")
            u.StudentId = null;
        if (u.Role == "Staff" && u.DepartmentId == null)
            throw new ApiException(400, "Nhân viên phải được liên kết với khoa.");
        if (u.DepartmentId != null)
        {
            ApiSupport.Id(u.DepartmentId);
            if (!await db.Departments.Find(x => x.Id == u.DepartmentId).AnyAsync())
                throw new ApiException(400, "Khoa không tồn tại.");
        }

        if (u.StudentId != null)
        {
            ApiSupport.Id(u.StudentId);
            if (!await db.Students.Find(x => x.Id == u.StudentId).AnyAsync())
                throw new ApiException(400, "Hồ sơ không tồn tại.");
            if (await db.Users.Find(x => x.StudentId == u.StudentId && x.Id != id).AnyAsync())
                throw new ApiException(409, "Hồ sơ đã được liên kết với tài khoản khác.");
        }

        if (input.TryGetProperty("unlock", out var unlock))
        {
            if (unlock.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                throw new ApiException(400, "unlock không hợp lệ.");
            if (unlock.GetBoolean())
            {
                u.LockedUntil = null;
                u.AccessFailedCount = 0;
            }
        }

        u.SecurityStamp = Guid.NewGuid().ToString();
        if (!(await manager.UpdateAsync(u)).Succeeded)
            throw new ApiException(409, "Tài khoản đã thay đổi. Vui lòng thử lại.");
        await s.Audit("UpdateUser", id);
        return Results.Ok(Dto.User(u));
    }
}
