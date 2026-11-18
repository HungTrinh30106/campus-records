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

public sealed class DashboardService(ApiSupport s, Database db, DepartmentCache cache)
{
    public async Task<IResult> Index(CancellationToken ct)
    {
        var filter = s.Scope();
        var options = new AggregateOptions
        {
            MaxTime = TimeSpan.FromSeconds(5)
        };
        var groups = await db.Students.Aggregate(options).Match(filter).Group(x => new { x.DepartmentId, x.Status }, g => new { g.Key.DepartmentId, g.Key.Status, Count = g.Count() }).ToListAsync(ct);
        var recent = await db.Students.Find(filter, new FindOptions { MaxTime = TimeSpan.FromSeconds(5) }).SortByDescending(x => x.CreatedAt).Limit(5).ToListAsync(ct);
        var rendered = filter.Render(new RenderArgs<Student>(db.Students.DocumentSerializer, MongoDB.Bson.Serialization.BsonSerializer.SerializerRegistry));
        // Count attachments server-side, joining only scoped student IDs, never loading all profiles.
        var documentPipeline = new BsonDocument[]
        {
            new("$match", rendered),
            new("$project", new BsonDocument("_id", 1)),
            new("$lookup", new BsonDocument { { "from", "documents" }, { "let", new BsonDocument("studentId", new BsonDocument("$toString", "$_id")) }, { "pipeline", new BsonArray { new BsonDocument("$match", new BsonDocument("$expr", new BsonDocument("$eq", new BsonArray { "$StudentId", "$$studentId" }))), new BsonDocument("$count", "count") } }, { "as", "counts" } }),
            new("$unwind", "$counts"),
            new("$group", new BsonDocument { { "_id", BsonNull.Value }, { "count", new BsonDocument("$sum", "$counts.count") } })
        };
        var documents = await db.Students.Aggregate<BsonDocument>(documentPipeline, options).FirstOrDefaultAsync(ct);
        var deps = await cache.Get(ct);
        var visibleDeps = s.User.Role == "Admin" ? deps : deps.Where(d => groups.Any(x => x.DepartmentId == d.Id)).ToArray();
        var auditFilter = s.User.Role == "Admin" ? Builders<Audit>.Filter.Empty : Builders<Audit>.Filter.Eq(x => x.UserId, s.User.Id);
        return Results.Ok(new { totalStudents = groups.Sum(x => x.Count), activeStudents = groups.Where(x => x.Status == "Active").Sum(x => x.Count), totalDepartments = visibleDeps.Length, totalDocuments = documents?["count"].ToInt64() ?? 0, byDepartment = visibleDeps.Select(d => new { departmentId = d.Id, name = d.Name, count = groups.Where(x => x.DepartmentId == d.Id).Sum(x => x.Count) }), byStatus = ApiSupport.Statuses.Select(status => new { status, count = groups.Where(x => x.Status == status).Sum(x => x.Count) }), recentStudents = recent.Select(x => Dto.Student(x, s.User)), recentActivity = await db.Audits.Find(auditFilter, new FindOptions { MaxTime = TimeSpan.FromSeconds(5) }).SortByDescending(x => x.CreatedAt).Limit(8).ToListAsync(ct) });
    }
}
