using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace StudentRecords.Api;

[Authorize]
public sealed class StudentsController(StudentsService service) : ControllerBase
{
    [HttpGet("api/students")]
    public Task<IResult> List() => service.List(Request);
    [HttpGet("api/students/{id}")]
    public Task<IResult> Detail([FromRoute] string id) => service.Detail(id);
    [HttpPost("api/students")]
    public Task<IResult> Create([FromBody] JsonElement input) => service.Create(input);
    [HttpPut("api/students/{id}")]
    public Task<IResult> Update([FromRoute] string id, [FromBody] JsonElement input) => service.Update(id, input);
    [HttpDelete("api/students/{id}")]
    public Task<IResult> Delete([FromRoute] string id) => service.Delete(id);
}
