using Microsoft.AspNetCore.Identity;
using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace StudentRecords.Api;

public static class Dto
{
    public static object User(AppUser u) => new
    {
        u.Id,
        u.FullName,
        u.Email,
        u.Role,
        u.DepartmentId,
        u.StudentId,
        u.Enabled,
        u.LockedUntil,
        u.LastLoginAt
    };
    public static object Student(Student s, AppUser u)
    {
        var mask = u.Role == "Staff";
        return new
        {
            s.Id,
            s.StudentCode,
            s.FullName,
            email = mask ? "***@***" : s.Email,
            phone = mask ? Mask(s.Phone) : s.Phone,
            nationalId = mask ? Mask(s.NationalId) : s.NationalId,
            s.DateOfBirth,
            s.Gender,
            s.Address,
            s.DepartmentId,
            s.ClassName,
            s.EnrollmentYear,
            s.Status,
            gpa = mask ? null : s.Gpa,
            s.CreatedAt,
            s.UpdatedAt,
            isMasked = mask
        };
    }

    public static object Document(Document d) => new
    {
        d.Id,
        d.StudentId,
        d.OriginalName,
        d.ContentType,
        d.Size,
        d.UploadedAt,
        d.UploadedBy
    };
    public static string Mask(string value) => value.Length < 4 ? "***" : "***" + value[^3..];
}
