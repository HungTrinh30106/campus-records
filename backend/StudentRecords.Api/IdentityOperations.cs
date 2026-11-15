using Microsoft.AspNetCore.Identity;
namespace StudentRecords.Api;

public static class IdentityOperations
{
    // Optimistic concurrency prevents lost updates. A parallel failed login reloads
    // the user and retries so every failed attempt contributes to lockout.
    public static async Task RecordFailedLogin(UserManager<AppUser> manager, AppUser user)
    {
        for (var attempt = 0; attempt < 20; attempt++)
        {
            if (!user.Enabled || await manager.IsLockedOutAsync(user)) return;
            var result = await manager.AccessFailedAsync(user);
            if (result.Succeeded) return;
            if (!result.Errors.Any(e => e.Code == "ConcurrencyFailure"))
                throw new ApiException(409, "Tài khoản đã thay đổi. Vui lòng thử lại.");
            var current = await manager.FindByIdAsync(user.Id);
            if (current is null) return;
            user = current;
        }
        throw new ApiException(429, "Quá nhiều yêu cầu đăng nhập đồng thời.");
    }
}
