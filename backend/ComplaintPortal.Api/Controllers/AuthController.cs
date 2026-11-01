using ComplaintPortal.Api.Dtos;
using ComplaintPortal.Api.Infrastructure;
using ComplaintPortal.Api.Repositories;
using ComplaintPortal.Api.Security;
using ComplaintPortal.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace ComplaintPortal.Api.Controllers;

[ApiController]
[Route("api/auth")]
public sealed class AuthController : ControllerBase
{
    private const string RefreshCookieName = "cp_refresh";
    private const string RefreshCookiePath = "/api/auth";

    private readonly IAuthService _auth;
    private readonly IUserRepository _users;

    public AuthController(IAuthService auth, IUserRepository users)
    {
        _auth = auth;
        _users = users;
    }

    [HttpPost("register")]
    [AllowAnonymous]
    [EnableRateLimiting("auth")]
    public async Task<IActionResult> Register([FromBody] RegisterRequest request)
    {
        var result = await _auth.RegisterAsync(request, HttpContext.GetClientIp());
        return ToActionResult(result);
    }

    [HttpPost("login")]
    [AllowAnonymous]
    [EnableRateLimiting("auth")]
    public async Task<IActionResult> Login([FromBody] LoginRequest request)
    {
        var result = await _auth.LoginAsync(request, HttpContext.GetClientIp());
        return ToActionResult(result);
    }

    /// <summary>
    /// Exchanges the HttpOnly refresh cookie for a new access token (and rotates the cookie).
    /// The custom header requirement forces a CORS pre-flight, which blocks cross-site (CSRF) calls.
    /// </summary>
    [HttpPost("refresh")]
    [AllowAnonymous]
    [EnableRateLimiting("refresh")]
    public async Task<IActionResult> Refresh()
    {
        if (!HasAntiCsrfHeader()) return BadRequest();

        var token = Request.Cookies[RefreshCookieName];
        if (string.IsNullOrEmpty(token))
            return Problem(statusCode: StatusCodes.Status401Unauthorized, title: "Not signed in");

        var result = await _auth.RefreshAsync(token, HttpContext.GetClientIp());
        if (!result.Succeeded && !result.PreserveCookie) ClearRefreshCookie();
        return ToActionResult(result);
    }

    [HttpPost("logout")]
    [AllowAnonymous]
    public async Task<IActionResult> Logout()
    {
        if (!HasAntiCsrfHeader()) return BadRequest();

        int? userId = User.Identity?.IsAuthenticated == true ? User.GetUserId() : null;
        await _auth.LogoutAsync(Request.Cookies[RefreshCookieName], userId, HttpContext.GetClientIp());
        ClearRefreshCookie();
        return NoContent();
    }

    [HttpGet("me")]
    [Authorize]
    public async Task<ActionResult<UserDto>> Me()
    {
        var user = await _users.GetByIdAsync(User.GetUserId());
        if (user is null) return NotFound();
        return await _auth.BuildUserDtoAsync(user);
    }

    [HttpPost("change-password")]
    [Authorize]
    [EnableRateLimiting("auth")]
    public async Task<IActionResult> ChangePassword([FromBody] ChangePasswordRequest request)
    {
        var result = await _auth.ChangePasswordAsync(User.GetUserId(), request, HttpContext.GetClientIp());
        return ToActionResult(result);
    }

    private IActionResult ToActionResult(AuthResult result)
    {
        if (!result.Succeeded)
            return Problem(statusCode: result.StatusCode, title: result.Error);

        if (result.RefreshToken is not null && result.RefreshExpiresAtUtc is not null)
            SetRefreshCookie(result.RefreshToken, result.RefreshExpiresAtUtc.Value);

        return Ok(result.Response);
    }

    private bool HasAntiCsrfHeader() =>
        Request.Headers.TryGetValue("X-Requested-With", out var v) && v == "XMLHttpRequest";

    private void SetRefreshCookie(string token, DateTime expiresUtc) =>
        Response.Cookies.Append(RefreshCookieName, token, new CookieOptions
        {
            HttpOnly = true,                 // not readable from JavaScript (XSS can't steal it)
            Secure = true,                   // HTTPS only
            SameSite = SameSiteMode.Strict,  // never sent on cross-site requests (CSRF)
            Path = RefreshCookiePath,        // only sent to the auth endpoints
            Expires = expiresUtc,
            IsEssential = true,
        });

    private void ClearRefreshCookie() =>
        Response.Cookies.Delete(RefreshCookieName, new CookieOptions
        {
            HttpOnly = true,
            Secure = true,
            SameSite = SameSiteMode.Strict,
            Path = RefreshCookiePath,
        });
}
