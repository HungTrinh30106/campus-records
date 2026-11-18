using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace StudentRecords.Api;

public sealed class OperationAlert : Entity
{
    public string Type { get; set; } = "";
    public string Severity { get; set; } = "warning";
    public string Message { get; set; } = "";
    public long Count { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime LastSeenAt { get; set; } = DateTime.UtcNow;
    public bool Acknowledged { get; set; }
}

public sealed class DatabaseEvent : Entity
{
    // Stable hash of the resume token makes replay after a crash idempotent.
    public string EventKey { get; set; } = "";
    public string Collection { get; set; } = "";
    public string Operation { get; set; } = "";
    public string? DocumentId { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public string ActorSource { get; set; } = "unknown/directDatabase";
}

public sealed class WatcherResume
{
    [BsonId]
    public string Id { get; set; } = "business";
    public BsonDocument? Token { get; set; }
}

public sealed class BackupRun : Entity
{
    // Imported history is not ownership of the source database's archive files.
    [System.Text.Json.Serialization.JsonIgnore]
    public string OwnerDatabase { get; set; } = "";
    public string Status { get; set; } = "queued";
    public string Trigger { get; set; } = "manual";
    public DateTime StartedAt { get; set; } = DateTime.UtcNow;
    public DateTime? FinishedAt { get; set; }
    public long? SizeBytes { get; set; }
    public string? ErrorCode { get; set; }
}

public sealed class BackupSchedule
{
    [BsonId]
    public string Id { get; set; } = "schedule";
    public DateTime NextRunAt { get; set; }
}
