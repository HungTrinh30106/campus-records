using Microsoft.AspNetCore.Identity;
using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace StudentRecords.Api;

public class Student : Entity
{
    public string StudentCode { get; set; } = "";
    public string FullName { get; set; } = "";
    public string Email { get; set; } = "";
    public string Phone { get; set; } = "";
    public string NationalId { get; set; } = "";
    public string DateOfBirth { get; set; } = "";
    public string Gender { get; set; } = "Other";
    public string Address { get; set; } = "";
    public string DepartmentId { get; set; } = "";
    public string ClassName { get; set; } = "";
    public int EnrollmentYear { get; set; }
    public string Status { get; set; } = "Active";
    public double? Gpa { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
