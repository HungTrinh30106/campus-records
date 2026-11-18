using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace StudentRecords.Api;

[Authorize]
public sealed class AuditController(AuditService service) : ControllerBase
{
    [HttpGet("api/audit")]
    public Task<IResult> List() => service.List(Request);
}
