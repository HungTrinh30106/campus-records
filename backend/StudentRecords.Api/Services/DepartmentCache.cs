using MongoDB.Driver;

namespace StudentRecords.Api;
// Only the public department catalog is cached; never students or user-specific data.
public sealed class DepartmentCache(Database db)
{
    private readonly SemaphoreSlim mutex = new(1, 1);
    private readonly object publication = new();
    private Department[]? value;
    private DateTime expires;
    private long generation, hits, misses;
    public long Hits => Interlocked.Read(ref hits);
    public long Misses => Interlocked.Read(ref misses);

    public void Invalidate()
    {
        lock (publication)
        {
            generation++;
            Volatile.Write(ref value, null);
        }
    }

    public async Task<Department[]> Get(CancellationToken cancellationToken = default)
    {
        var cached = Volatile.Read(ref value);
        if (cached != null && DateTime.UtcNow < expires)
        {
            Interlocked.Increment(ref hits);
            return cached;
        }

        await mutex.WaitAsync(cancellationToken);
        try
        {
            cached = Volatile.Read(ref value);
            if (cached != null && DateTime.UtcNow < expires)
            {
                Interlocked.Increment(ref hits);
                return cached;
            }

            Interlocked.Increment(ref misses);
            long version;
            lock (publication)
                version = generation;
            var rows = (await db.Departments.Find(FilterDefinition<Department>.Empty, new FindOptions { MaxTime = TimeSpan.FromSeconds(5) }).SortBy(x => x.Name).ToListAsync(cancellationToken)).ToArray();
            lock (publication)
            {
                // Version validation and publication must be atomic with invalidation.
                if (version == generation)
                {
                    expires = DateTime.UtcNow.AddSeconds(30);
                    Volatile.Write(ref value, rows);
                }
            }

            return rows;
        }
        finally
        {
            mutex.Release();
        }
    }
}
