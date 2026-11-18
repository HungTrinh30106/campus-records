using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace StudentRecords.Api;

[Authorize]
public sealed class SecurityController(SecurityService service) : ControllerBase
{
    [HttpGet("api/security")]
    public IResult Index() => service.Index();
}
