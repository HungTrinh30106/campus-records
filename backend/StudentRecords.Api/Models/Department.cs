using Microsoft.AspNetCore.Identity;
using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace StudentRecords.Api;

public class Department : Entity
{
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
}
