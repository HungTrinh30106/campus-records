using MongoDB.Driver;
using MongoDB.Bson;

namespace StudentRecords.Api;

public sealed class Database
{
    public IMongoDatabase Db { get; }
    public bool Ready { get; private set; }
    public IMongoCollection<AppUser> Users => Db.GetCollection<AppUser>("users");
    public IMongoCollection<Student> Students => Db.GetCollection<Student>("students");
    public IMongoCollection<Department> Departments => Db.GetCollection<Department>("departments");
    public IMongoCollection<Document> Documents => Db.GetCollection<Document>("documents");
    public IMongoCollection<Audit> Audits => Db.GetCollection<Audit>("audit");
    public IMongoCollection<OperationAlert> Alerts => Db.GetCollection<OperationAlert>("operationAlerts");
    public IMongoCollection<DatabaseEvent> DatabaseEvents => Db.GetCollection<DatabaseEvent>("databaseEvents");
    public IMongoCollection<WatcherResume> WatcherResumes => Db.GetCollection<WatcherResume>("watcherResume");
    public IMongoCollection<BackupRun> BackupRuns => Db.GetCollection<BackupRun>("backupRuns");
    public IMongoCollection<BackupSchedule> BackupState => Db.GetCollection<BackupSchedule>("backupState");

    public Database(IConfiguration config)
    {
        var uri = config["Mongo:ConnectionString"];
        if (string.IsNullOrWhiteSpace(uri))
            throw new InvalidOperationException("Cần cấu hình Mongo:ConnectionString bằng biến môi trường hoặc user secrets.");
        var settings = MongoClientSettings.FromConnectionString(uri);
        settings.ServerSelectionTimeout = TimeSpan.FromSeconds(5);
        settings.ConnectTimeout = TimeSpan.FromSeconds(5);
        settings.SocketTimeout = TimeSpan.FromSeconds(15);
        Db = new MongoClient(settings).GetDatabase(config["Mongo:Database"] ?? "campus_records");
    }

    public async Task Indexes()
    {
        await Users.Indexes.CreateOneAsync(new CreateIndexModel<AppUser>(Builders<AppUser>.IndexKeys.Ascending(x => x.NormalizedEmail), new CreateIndexOptions { Unique = true }));
        await Users.Indexes.CreateOneAsync(new CreateIndexModel<AppUser>(Builders<AppUser>.IndexKeys.Ascending(x => x.StudentId), new CreateIndexOptions<AppUser> { Unique = true, PartialFilterExpression = Builders<AppUser>.Filter.Type(x => x.StudentId, BsonType.String) }));
        await Users.Indexes.CreateOneAsync(new CreateIndexModel<AppUser>(Builders<AppUser>.IndexKeys.Ascending(x => x.DepartmentId)));
        await Students.Indexes.CreateOneAsync(new CreateIndexModel<Student>(Builders<Student>.IndexKeys.Ascending(x => x.StudentCode), new CreateIndexOptions { Unique = true }));
        await Students.Indexes.CreateOneAsync(new CreateIndexModel<Student>(Builders<Student>.IndexKeys.Ascending(x => x.DepartmentId).Ascending(x => x.Status).Descending(x => x.CreatedAt)));
        await Departments.Indexes.CreateOneAsync(new CreateIndexModel<Department>(Builders<Department>.IndexKeys.Ascending(x => x.Code), new CreateIndexOptions { Unique = true }));
        await Documents.Indexes.CreateOneAsync(new CreateIndexModel<Document>(Builders<Document>.IndexKeys.Ascending(x => x.StudentId)));
        await Audits.Indexes.CreateOneAsync(new CreateIndexModel<Audit>(Builders<Audit>.IndexKeys.Descending(x => x.CreatedAt)));
        await Alerts.Indexes.CreateOneAsync(new CreateIndexModel<OperationAlert>(Builders<OperationAlert>.IndexKeys.Ascending(x => x.Type), new CreateIndexOptions { Unique = true }));
        await DatabaseEvents.Indexes.CreateOneAsync(new CreateIndexModel<DatabaseEvent>(Builders<DatabaseEvent>.IndexKeys.Ascending(x => x.EventKey), new CreateIndexOptions { Unique = true }));
        await DatabaseEvents.Indexes.CreateOneAsync(new CreateIndexModel<DatabaseEvent>(Builders<DatabaseEvent>.IndexKeys.Descending(x => x.CreatedAt)));
        await BackupRuns.Indexes.CreateOneAsync(new CreateIndexModel<BackupRun>(Builders<BackupRun>.IndexKeys.Descending(x => x.StartedAt)));
        Ready = true;
    }
}
