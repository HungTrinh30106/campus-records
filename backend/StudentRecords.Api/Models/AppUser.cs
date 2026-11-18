using Microsoft.AspNetCore.Identity;
using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace StudentRecords.Api;

public class AppUser : Entity
{
    public string FullName { get; set; } = "";
    public string Email { get; set; } = "";
    public string NormalizedEmail { get; set; } = "";
    public string PasswordHash { get; set; } = "";
    public string SecurityStamp { get; set; } = Guid.NewGuid().ToString();
    public string ConcurrencyStamp { get; set; } = Guid.NewGuid().ToString();
    public string Role { get; set; } = "Student";
    public string? DepartmentId { get; set; }
    public string? StudentId { get; set; }
    public bool Enabled { get; set; } = true;
    public DateTime? LockedUntil { get; set; }
    public int AccessFailedCount { get; set; }
    public bool LockoutEnabled { get; set; } = true;
    public DateTime? LastLoginAt { get; set; }
}
