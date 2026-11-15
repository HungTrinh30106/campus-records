using Microsoft.AspNetCore.Identity;
using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace StudentRecords.Api;

public class Entity { [BsonId, BsonRepresentation(BsonType.ObjectId)] public string Id { get; set; } = ObjectId.GenerateNewId().ToString(); }
public class AppUser : Entity
{
    public string FullName { get; set; } = ""; public string Email { get; set; } = ""; public string NormalizedEmail { get; set; } = "";
    public string PasswordHash { get; set; } = ""; public string SecurityStamp { get; set; } = Guid.NewGuid().ToString(); public string ConcurrencyStamp { get; set; } = Guid.NewGuid().ToString();
    public string Role { get; set; } = "Student"; public string? DepartmentId { get; set; }
    public string? StudentId { get; set; }
    public bool Enabled { get; set; } = true;
    public DateTime? LockedUntil { get; set; }
    public int AccessFailedCount { get; set; }
    public bool LockoutEnabled { get; set; } = true; public DateTime? LastLoginAt { get; set; }
}
public class Department : Entity { public string Code { get; set; } = ""; public string Name { get; set; } = ""; }
public class Student : Entity
{
    public string StudentCode { get; set; } = ""; public string FullName { get; set; } = ""; public string Email { get; set; } = ""; public string Phone { get; set; } = ""; public string NationalId { get; set; } = "";
    public string DateOfBirth { get; set; } = ""; public string Gender { get; set; } = "Other"; public string Address { get; set; } = ""; public string DepartmentId { get; set; } = "";
    public string ClassName { get; set; } = ""; public int EnrollmentYear { get; set; }
    public string Status { get; set; } = "Active"; public double? Gpa { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow; public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
public class Document : Entity { public string StudentId { get; set; } = ""; public string OriginalName { get; set; } = ""; public string ContentType { get; set; } = ""; public long Size { get; set; } public DateTime UploadedAt { get; set; } = DateTime.UtcNow; public string UploadedBy { get; set; } = ""; public string StorageName { get; set; } = ""; }
public class Audit : Entity { public string? UserId { get; set; } public string UserName { get; set; } = ""; public string Action { get; set; } = ""; public string Target { get; set; } = ""; public string Result { get; set; } = "Success"; public string IpAddress { get; set; } = ""; public DateTime CreatedAt { get; set; } = DateTime.UtcNow; public string TraceId { get; set; } = ""; }
public static class Dto
{
    public static object User(AppUser u) => new { u.Id, u.FullName, u.Email, u.Role, u.DepartmentId, u.StudentId, u.Enabled, u.LockedUntil, u.LastLoginAt };
    public static object Student(Student s, AppUser u) { var mask = u.Role == "Staff"; return new { s.Id, s.StudentCode, s.FullName, email = mask ? "***@***" : s.Email, phone = mask ? Mask(s.Phone) : s.Phone, nationalId = mask ? Mask(s.NationalId) : s.NationalId, s.DateOfBirth, s.Gender, s.Address, s.DepartmentId, s.ClassName, s.EnrollmentYear, s.Status, gpa = mask ? null : s.Gpa, s.CreatedAt, s.UpdatedAt, isMasked = mask }; }
    public static object Document(Document d) => new { d.Id, d.StudentId, d.OriginalName, d.ContentType, d.Size, d.UploadedAt, d.UploadedBy };
    public static string Mask(string value) => value.Length < 4 ? "***" : "***" + value[^3..];
}
