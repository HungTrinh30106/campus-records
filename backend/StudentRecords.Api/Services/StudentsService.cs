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

public sealed class StudentsService(ApiSupport s, Database db, IConfiguration config)
{
    public async Task<IResult> List(HttpRequest r)
    {
        return Results.Ok(await s.Students(r));
    }

    public async Task<IResult> Detail(string id)
    {
        return Results.Ok(Dto.Student(await s.Student(id), s.User));
    }

    public async Task<IResult> Create(JsonElement input)
    {
        s.Roles("Admin", "Staff");
        var row = new Student();
        await s.ApplyStudent(row, input, true);
        await db.Students.InsertOneAsync(row);
        await s.Audit("CreateStudent", row.Id);
        return Results.Created("/api/students/" + row.Id, Dto.Student(row, s.User));
    }

    public async Task<IResult> Update(string id, JsonElement input)
    {
        var row = await s.Student(id);
        await s.ApplyStudent(row, input, false);
        var filter = Builders<Student>.Filter.Eq(x => x.Id, id) & s.Scope();
        var saved = await db.Students.FindOneAndUpdateAsync(filter, ApiSupport.StudentChanges(row, input), new FindOneAndUpdateOptions<Student> { ReturnDocument = ReturnDocument.After });
        if (saved is null)
            throw new ApiException(404, "Không tìm thấy dữ liệu.");
        await s.Audit("UpdateStudent", id);
        return Results.Ok(Dto.Student(saved, s.User));
    }

    public async Task<IResult> Delete(string id)
    {
        s.Roles("Admin");
        await s.Student(id);
        var docs = await db.Documents.Find(x => x.StudentId == id).ToListAsync();
        foreach (var d in docs)
            File.Delete(Path.Combine(Storage(config), d.StorageName));
        await db.Documents.DeleteManyAsync(x => x.StudentId == id);
        await db.Users.UpdateManyAsync(x => x.StudentId == id, Builders<AppUser>.Update.Set(x => x.StudentId, null).Set(x => x.SecurityStamp, Guid.NewGuid().ToString()));
        await db.Students.DeleteOneAsync(x => x.Id == id);
        await s.Audit("DeleteStudent", id);
        return Results.NoContent();
    }
}
