using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using MongoDB.Bson;
using MongoDB.Driver;
using static StudentRecords.Api.RecordHelpers;

namespace StudentRecords.Api;

public sealed class AuthService(IAntiforgery csrf, ApiSupport s, UserManager<AppUser> users)
{
    public IResult Csrf(HttpContext c)
    {
        return Results.Ok(new { token = csrf.GetAndStoreTokens(c).RequestToken });
    }

    public IResult Me()
    {
        return Results.Ok(Dto.User(s.User));
    }

    public async Task<IResult> Register(JsonElement input)
    {
        ApiSupport.Fields(input, "fullName", "email", "password");
        var email = ApiSupport.Text(input, "email", 254);
        ApiSupport.Email(email);
        var u = new AppUser
        {
            Email = email,
            FullName = ApiSupport.Text(input, "fullName", 120)
        };
        var result = await users.CreateAsync(u, ApiSupport.Password(input, "password"));
        if (!result.Succeeded)
            throw new ApiException(400, string.Join(" ", result.Errors.Select(e => IdentityMessage(e.Code))));
        await s.Audit("Register", u.Id);
        return Results.Created("/api/auth/me", Dto.User(u));
    }

    public async Task<IResult> Login(JsonElement input, HttpContext c)
    {
        ApiSupport.Fields(input, "email", "password");
        var email = ApiSupport.Text(input, "email", 254);
        var password = ApiSupport.Password(input, "password");
        var u = await users.FindByEmailAsync(email);
        if (u == null || !u.Enabled || await users.IsLockedOutAsync(u))
        {
            await s.Audit("Login", "", "Failure");
            throw new ApiException(401, "Thông tin đăng nhập không đúng hoặc tài khoản bị khóa.");
        }

        if (!await users.CheckPasswordAsync(u, password))
        {
            await IdentityOperations.RecordFailedLogin(users, u);
            await s.Audit("Login", u.Id, "Failure");
            throw new ApiException(401, "Thông tin đăng nhập không đúng hoặc tài khoản bị khóa.");
        }

        var reset = await users.ResetAccessFailedCountAsync(u);
        if (!reset.Succeeded)
            throw new ApiException(409, "Tài khoản đã thay đổi. Vui lòng thử lại.");
        u.LastLoginAt = DateTime.UtcNow;
        if (!(await users.UpdateAsync(u)).Succeeded)
            throw new ApiException(409, "Tài khoản đã thay đổi. Vui lòng thử lại.");
        await SignIn(c, u);
        c.Items["user"] = u;
        await s.Audit("Login", u.Id);
        return Results.Ok(Dto.User(u));
    }

    public async Task<IResult> Logout(HttpContext c)
    {
        await s.Audit("Logout");
        await c.SignOutAsync();
        return Results.NoContent();
    }

    public async Task<IResult> ChangePassword(JsonElement input, HttpContext c)
    {
        ApiSupport.Fields(input, "currentPassword", "newPassword");
        if (ApiSupport.Password(input, "currentPassword") == ApiSupport.Password(input, "newPassword"))
            throw new ApiException(400, "Mật khẩu mới phải khác mật khẩu hiện tại.");
        var result = await users.ChangePasswordAsync(s.User, ApiSupport.Password(input, "currentPassword"), ApiSupport.Password(input, "newPassword"));
        if (!result.Succeeded)
            throw new ApiException(400, string.Join(" ", result.Errors.Select(e => IdentityMessage(e.Code))));
        await c.SignOutAsync();
        await s.Audit("ChangePassword", s.User.Id);
        return Results.NoContent();
    }

    static Task SignIn(HttpContext c, AppUser u) => c.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, u.Id), new Claim(ClaimTypes.Name, u.Email), new Claim(ClaimTypes.Role, u.Role), new Claim("stamp", u.SecurityStamp)], CookieAuthenticationDefaults.AuthenticationScheme)));
    static string IdentityMessage(string code) => code switch
    {
        "DuplicateEmail" or "DuplicateUserName" => "Email đã tồn tại.",
        "PasswordMismatch" => "Mật khẩu hiện tại không đúng.",
        _ => "Mật khẩu tối thiểu 10 ký tự, gồm chữ hoa, chữ thường, chữ số và ký tự đặc biệt; hoặc dữ liệu tài khoản không hợp lệ."
    };
}
