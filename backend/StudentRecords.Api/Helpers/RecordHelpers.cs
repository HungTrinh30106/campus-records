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

public static class RecordHelpers
{
    public static void DepartmentInput(Department d, JsonElement input)
    {
        ApiSupport.Fields(input, "code", "name");
        d.Code = ApiSupport.Text(input, "code", 30).ToUpperInvariant();
        if (!Regex.IsMatch(d.Code, "^[A-Z0-9_-]+$"))
            throw new ApiException(400, "Mã khoa không hợp lệ.");
        d.Name = ApiSupport.Text(input, "name", 120);
    }

    public static string Storage(IConfiguration config) => Path.GetFullPath(config["Storage:Path"] ?? Path.Combine(AppContext.BaseDirectory, "private-uploads"));
    public static string Csv(string value)
    {
        if (value.TrimStart().StartsWith('=') || value.TrimStart().StartsWith('+') || value.TrimStart().StartsWith('-') || value.TrimStart().StartsWith('@') || value.StartsWith('\t') || value.StartsWith('\r'))
            value = "'" + value;
        return "\"" + value.Replace("\"", "\"\"") + "\"";
    }
}
