using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace StudentRecords.Api;

[Authorize]
public sealed class AuthController(AuthService service) : ControllerBase
{
    [HttpGet("api/auth/csrf")]
    [AllowAnonymous]
    public IResult Csrf() => service.Csrf(HttpContext);
    [HttpGet("api/auth/me")]
    public IResult Me() => service.Me();
    [HttpPost("api/auth/register")]
    [AllowAnonymous]
    [EnableRateLimiting("login")]
    public Task<IResult> Register([FromBody] JsonElement input) => service.Register(input);
    [HttpPost("api/auth/login")]
    [AllowAnonymous]
    [EnableRateLimiting("login")]
    public Task<IResult> Login([FromBody] JsonElement input) => service.Login(input, HttpContext);
    [HttpPost("api/auth/logout")]
    public Task<IResult> Logout() => service.Logout(HttpContext);
    [HttpPost("api/auth/change-password")]
    public Task<IResult> ChangePassword([FromBody] JsonElement input) => service.ChangePassword(input, HttpContext);
}
