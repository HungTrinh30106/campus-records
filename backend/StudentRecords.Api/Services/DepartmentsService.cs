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

public sealed class DepartmentsService(DepartmentCache cache, ApiSupport s, Database db)
{
    public async Task<IResult> List(CancellationToken ct)
    {
        return Results.Ok(await cache.Get(ct));
    }

    public async Task<IResult> Create(JsonElement input)
    {
        s.Roles("Admin");
        var d = new Department();
        DepartmentInput(d, input);
        await db.Departments.InsertOneAsync(d);
        cache.Invalidate();
        await s.Audit("CreateDepartment", d.Id);
        return Results.Created("/api/departments/" + d.Id, d);
    }

    public async Task<IResult> Update(string id, JsonElement input)
    {
        s.Roles("Admin");
        ApiSupport.Id(id);
        var d = await db.Departments.Find(x => x.Id == id).FirstOrDefaultAsync() ?? throw new ApiException(404, "Khoa không tồn tại.");
        DepartmentInput(d, input);
        await db.Departments.ReplaceOneAsync(x => x.Id == id, d);
        cache.Invalidate();
        await s.Audit("UpdateDepartment", id);
        return Results.Ok(d);
    }

    public async Task<IResult> Delete(string id)
    {
        s.Roles("Admin");
        ApiSupport.Id(id);
        if (await db.Students.Find(x => x.DepartmentId == id).AnyAsync() || await db.Users.Find(x => x.DepartmentId == id).AnyAsync())
            throw new ApiException(409, "Khoa đang được sử dụng.");
        if ((await db.Departments.DeleteOneAsync(x => x.Id == id)).DeletedCount == 0)
            throw new ApiException(404, "Khoa không tồn tại.");
        cache.Invalidate();
        await s.Audit("DeleteDepartment", id);
        return Results.NoContent();
    }
}
