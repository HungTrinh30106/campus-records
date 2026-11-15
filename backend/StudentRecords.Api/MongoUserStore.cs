using Microsoft.AspNetCore.Identity;
using MongoDB.Driver;
namespace StudentRecords.Api;

public sealed class MongoUserStore(Database db) : IUserPasswordStore<AppUser>, IUserEmailStore<AppUser>, IUserSecurityStampStore<AppUser>, IUserLockoutStore<AppUser>
{
    public void Dispose() { }
    public Task<string> GetUserIdAsync(AppUser u, CancellationToken ct) => Task.FromResult(u.Id);
    public Task<string?> GetUserNameAsync(AppUser u, CancellationToken ct) => Task.FromResult<string?>(u.Email);
    public Task SetUserNameAsync(AppUser u, string? v, CancellationToken ct) { u.Email = v ?? ""; return Task.CompletedTask; }
    public Task<string?> GetNormalizedUserNameAsync(AppUser u, CancellationToken ct) => Task.FromResult<string?>(u.NormalizedEmail);
    public Task SetNormalizedUserNameAsync(AppUser u, string? v, CancellationToken ct) { u.NormalizedEmail = v ?? ""; return Task.CompletedTask; }
    public async Task<IdentityResult> CreateAsync(AppUser u, CancellationToken ct) { try { await db.Users.InsertOneAsync(u, cancellationToken: ct); return IdentityResult.Success; } catch (MongoWriteException ex) when (ex.WriteError.Category == ServerErrorCategory.DuplicateKey) { return IdentityResult.Failed(new IdentityError { Code = "DuplicateEmail", Description = "Email đã tồn tại." }); } }
    public async Task<IdentityResult> UpdateAsync(AppUser u, CancellationToken ct) { var old = u.ConcurrencyStamp; u.ConcurrencyStamp = Guid.NewGuid().ToString(); var r = await db.Users.ReplaceOneAsync(x => x.Id == u.Id && x.ConcurrencyStamp == old, u, cancellationToken: ct); return r.ModifiedCount == 1 ? IdentityResult.Success : IdentityResult.Failed(new IdentityError { Code = "ConcurrencyFailure", Description = "Dữ liệu đã thay đổi. Vui lòng thử lại." }); }
    public async Task<IdentityResult> DeleteAsync(AppUser u, CancellationToken ct) { await db.Users.DeleteOneAsync(x => x.Id == u.Id, ct); return IdentityResult.Success; }
    public Task<AppUser?> FindByIdAsync(string id, CancellationToken ct) => db.Users.Find(x => x.Id == id).FirstOrDefaultAsync(ct)!;
    public Task<AppUser?> FindByNameAsync(string name, CancellationToken ct) => db.Users.Find(x => x.NormalizedEmail == name).FirstOrDefaultAsync(ct)!;
    public Task SetPasswordHashAsync(AppUser u, string? v, CancellationToken ct) { u.PasswordHash = v ?? ""; return Task.CompletedTask; }
    public Task<string?> GetPasswordHashAsync(AppUser u, CancellationToken ct) => Task.FromResult<string?>(u.PasswordHash);
    public Task<bool> HasPasswordAsync(AppUser u, CancellationToken ct) => Task.FromResult(!string.IsNullOrEmpty(u.PasswordHash));
    public Task SetEmailAsync(AppUser u, string? v, CancellationToken ct) { u.Email = v ?? ""; return Task.CompletedTask; }
    public Task<string?> GetEmailAsync(AppUser u, CancellationToken ct) => Task.FromResult<string?>(u.Email);
    public Task<bool> GetEmailConfirmedAsync(AppUser u, CancellationToken ct) => Task.FromResult(false);
    public Task SetEmailConfirmedAsync(AppUser u, bool v, CancellationToken ct) => Task.CompletedTask;
    public Task<AppUser?> FindByEmailAsync(string v, CancellationToken ct) => db.Users.Find(x => x.NormalizedEmail == v).FirstOrDefaultAsync(ct)!;
    public Task<string?> GetNormalizedEmailAsync(AppUser u, CancellationToken ct) => Task.FromResult<string?>(u.NormalizedEmail);
    public Task SetNormalizedEmailAsync(AppUser u, string? v, CancellationToken ct) { u.NormalizedEmail = v ?? ""; return Task.CompletedTask; }
    public Task SetSecurityStampAsync(AppUser u, string v, CancellationToken ct) { u.SecurityStamp = v; return Task.CompletedTask; }
    public Task<string?> GetSecurityStampAsync(AppUser u, CancellationToken ct) => Task.FromResult<string?>(u.SecurityStamp);
    public Task<DateTimeOffset?> GetLockoutEndDateAsync(AppUser u, CancellationToken ct) => Task.FromResult<DateTimeOffset?>(u.LockedUntil.HasValue ? new DateTimeOffset(DateTime.SpecifyKind(u.LockedUntil.Value, DateTimeKind.Utc)) : null);
    public Task SetLockoutEndDateAsync(AppUser u, DateTimeOffset? v, CancellationToken ct) { u.LockedUntil = v?.UtcDateTime; return Task.CompletedTask; }
    public Task<int> IncrementAccessFailedCountAsync(AppUser u, CancellationToken ct) => Task.FromResult(++u.AccessFailedCount);
    public Task ResetAccessFailedCountAsync(AppUser u, CancellationToken ct) { u.AccessFailedCount = 0; return Task.CompletedTask; }
    public Task<int> GetAccessFailedCountAsync(AppUser u, CancellationToken ct) => Task.FromResult(u.AccessFailedCount);
    public Task<bool> GetLockoutEnabledAsync(AppUser u, CancellationToken ct) => Task.FromResult(u.LockoutEnabled);
    public Task SetLockoutEnabledAsync(AppUser u, bool v, CancellationToken ct) { u.LockoutEnabled = v; return Task.CompletedTask; }
}
