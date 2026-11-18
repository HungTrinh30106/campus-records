using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace StudentRecords.Api;

[Authorize]
public sealed class HealthController(HealthService service) : ControllerBase
{
    [HttpGet("api/health")]
    [AllowAnonymous]
    public Task<IResult> Index() => service.Index();
}
