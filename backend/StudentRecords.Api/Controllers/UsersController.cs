using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace StudentRecords.Api;

[Authorize]
public sealed class UsersController(UsersService service) : ControllerBase
{
    [HttpGet("api/users")]
    public Task<IResult> List() => service.List(Request);
    [HttpPut("api/users/{id}")]
    public Task<IResult> Update([FromRoute] string id, [FromBody] JsonElement input) => service.Update(id, input);
}
