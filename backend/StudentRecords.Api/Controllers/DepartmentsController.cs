using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace StudentRecords.Api;

[Authorize]
public sealed class DepartmentsController(DepartmentsService service) : ControllerBase
{
    [HttpGet("api/departments")]
    public Task<IResult> List(CancellationToken ct) => service.List(ct);
    [HttpPost("api/departments")]
    public Task<IResult> Create([FromBody] JsonElement input) => service.Create(input);
    [HttpPut("api/departments/{id}")]
    public Task<IResult> Update([FromRoute] string id, [FromBody] JsonElement input) => service.Update(id, input);
    [HttpDelete("api/departments/{id}")]
    public Task<IResult> Delete([FromRoute] string id) => service.Delete(id);
}
