using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace StudentRecords.Api;

[Authorize(Roles = "Admin")]
public sealed class OperationsController(OperationsService service) : ControllerBase
{
    [HttpGet("api/operations")]
    public Task<IResult> Index(CancellationToken ct) => service.Index(ct);
    [HttpGet("api/operations/alerts")]
    public Task<IResult> Alerts(CancellationToken ct) => service.Alerts(Request, ct);
    [HttpPost("api/operations/alerts/{id}/acknowledge")]
    public Task<IResult> Acknowledge([FromRoute] string id, CancellationToken ct) => service.Acknowledge(id, ct);
    [HttpGet("api/operations/database-events")]
    public Task<IResult> DatabaseEvents(CancellationToken ct) => service.DatabaseEvents(Request, ct);
    [HttpGet("api/operations/backups")]
    public Task<IResult> Backups(CancellationToken ct) => service.Backups(Request, ct);
    [HttpPost("api/operations/backups")]
    public Task<IResult> QueueBackup(CancellationToken ct) => service.QueueBackup(ct);
}
