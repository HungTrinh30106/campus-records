using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace StudentRecords.Api;

[Authorize]
public sealed class ReportsController(ReportsService service) : ControllerBase
{
    [HttpGet("api/reports")]
    public Task<IResult> List() => service.List(Request);
    [HttpGet("api/reports/export")]
    public Task<IResult> Export() => service.Export(Request);
}
