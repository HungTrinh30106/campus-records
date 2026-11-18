using Microsoft.AspNetCore.Identity;
using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace StudentRecords.Api;

public class Audit : Entity
{
    public string? UserId { get; set; }
    public string UserName { get; set; } = "";
    public string Action { get; set; } = "";
    public string Target { get; set; } = "";
    public string Result { get; set; } = "Success";
    public string IpAddress { get; set; } = "";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public string TraceId { get; set; } = "";
}
