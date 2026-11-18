using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace StudentRecords.Api;

[Authorize]
public sealed class DocumentsController(DocumentsService service) : ControllerBase
{
    [HttpGet("api/students/{id}/documents")]
    public Task<IResult> List([FromRoute] string id) => service.List(id);
    [HttpPost("api/students/{id}/documents")]
    public Task<IResult> Upload([FromRoute] string id) => service.Upload(id, Request);
    [HttpGet("api/documents/{id}/download")]
    public Task<IResult> Download([FromRoute] string id) => service.Download(id);
    [HttpDelete("api/documents/{id}")]
    public Task<IResult> Delete([FromRoute] string id) => service.Delete(id);
}
