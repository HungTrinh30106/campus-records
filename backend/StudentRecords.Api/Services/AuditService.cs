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

public sealed class AuditService(ApiSupport s, Database db)
{
    public async Task<IResult> List(HttpRequest r)
    {
        s.Roles("Admin");
        var (page, size) = ApiSupport.Pagination(r);
        var f = Builders<Audit>.Filter.Empty;
        var action = r.Query["action"].ToString();
        var result = r.Query["result"].ToString();
        if (action.Length > 100)
            throw new ApiException(400, "Bộ lọc không hợp lệ.");
        if (action != "")
            f &= Builders<Audit>.Filter.Eq(x => x.Action, action);
        if (result != "")
        {
            if (!new[]
            {
                "Success",
                "Denied",
                "Failure"
            }.Contains(result))
                throw new ApiException(400, "Kết quả không hợp lệ.");
            f &= Builders<Audit>.Filter.Eq(x => x.Result, result);
        }

        return Results.Ok(new { items = await db.Audits.Find(f).SortByDescending(x => x.CreatedAt).Skip((page - 1) * size).Limit(size).ToListAsync(), total = await db.Audits.CountDocumentsAsync(f), page, pageSize = size });
    }
}
