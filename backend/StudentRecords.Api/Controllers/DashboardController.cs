using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace StudentRecords.Api;

[Authorize]
public sealed class DashboardController(DashboardService service) : ControllerBase
{
    [HttpGet("api/dashboard")]
    public Task<IResult> Index(CancellationToken ct) => service.Index(ct);
}
