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

public sealed class ReportsService(ApiSupport s, Database db)
{
    public async Task<IResult> List(HttpRequest r)
    {
        s.Roles("Admin", "Staff");
        var data = await s.Students(r);
        await s.Audit("ReadReport");
        return Results.Ok(data);
    }

    public async Task<IResult> Export(HttpRequest r)
    {
        s.Roles("Admin", "Staff");
        var rows = await db.Students.Find(s.Query(r), new FindOptions { MaxTime = TimeSpan.FromSeconds(10) }).Sort(ApiSupport.Sort(r)).Limit(5000).ToListAsync();
        var text = new StringBuilder("\uFEFFMã sinh viên,Họ tên,Email,Điện thoại,CCCD,Khoa,Lớp,Năm nhập học,Trạng thái,GPA\r\n");
        foreach (var row in rows)
        {
            var mask = s.User.Role == "Staff";
            text.AppendLine(string.Join(',', new[] { row.StudentCode, row.FullName, mask ? "***@***" : row.Email, mask ? Dto.Mask(row.Phone) : row.Phone, mask ? Dto.Mask(row.NationalId) : row.NationalId, row.DepartmentId, row.ClassName, row.EnrollmentYear.ToString(), row.Status, mask ? "" : row.Gpa?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "" }.Select(Csv)));
        }

        await s.Audit("ExportReport");
        return Results.File(Encoding.UTF8.GetBytes(text.ToString()), "text/csv; charset=utf-8", "bao-cao-sinh-vien.csv");
    }
}
