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

public sealed class HealthService(Database db)
{
    public async Task<IResult> Index()
    {
        try
        {
            if (!db.Ready)
                return Results.Json(new { status = "unhealthy" }, statusCode: 503);
            await db.Db.RunCommandAsync<MongoDB.Bson.BsonDocument>(new MongoDB.Bson.BsonDocument("ping", 1));
            return Results.Ok(new { status = "healthy" });
        }
        catch
        {
            return Results.Json(new { status = "unhealthy" }, statusCode: 503);
        }
    }
}
