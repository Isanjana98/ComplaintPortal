using System.Text.Json;
using System.Text.Json.Serialization;
using ComplaintPortal.Api.Models;
using ComplaintPortal.Api.Options;
using ComplaintPortal.Api.Repositories;
using ComplaintPortal.Api.Security;
using ComplaintPortal.Api.Services;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace ComplaintPortal.Api.Infrastructure;

/// <summary>Adds defensive HTTP security headers to every response.</summary>
public sealed class SecurityHeadersMiddleware
{
    private readonly RequestDelegate _next;
    public SecurityHeadersMiddleware(RequestDelegate next) => _next = next;

    public Task InvokeAsync(HttpContext context)
    {
        context.Response.OnStarting(() =>
        {
            var h = context.Response.Headers;
            h["X-Content-Type-Options"] = "nosniff";
            h["X-Frame-Options"] = "DENY";
            h["Referrer-Policy"] = "no-referrer";
            h["Permissions-Policy"] = "camera=(), microphone=(), geolocation=(), payment=()";
            h["Cross-Origin-Opener-Policy"] = "same-origin";
            h["Cross-Origin-Resource-Policy"] = "same-origin";
            // Swagger UI (development only) needs inline scripts, so it is excluded from the strict CSP.
            if (!context.Request.Path.StartsWithSegments("/swagger"))
            {
                h["Content-Security-Policy"] =
                    "default-src 'self'; script-src 'self'; style-src 'self'; img-src 'self' data: blob:; " +
                    "font-src 'self'; connect-src 'self'; object-src 'none'; base-uri 'self'; " +
                    "form-action 'self'; frame-ancestors 'none'";
            }

            // API responses carry personal data - never cache them.
            if (context.Request.Path.StartsWithSegments("/api"))
            {
                h["Cache-Control"] = "no-store";
                h["Pragma"] = "no-cache";
            }
            return Task.CompletedTask;
        });

        return _next(context);
    }
}

/// <summary>
/// Accounts created or reset by an administrator get a temporary password. Until the user picks their own,
/// every API call except the auth endpoints is refused with 403 "password_change_required".
/// </summary>
public sealed class PasswordChangeRequiredMiddleware
{
    private readonly RequestDelegate _next;
    public PasswordChangeRequiredMiddleware(RequestDelegate next) => _next = next;

    public async Task InvokeAsync(HttpContext context)
    {
        if (context.User.HasClaim(AppClaims.MustChangePassword, "true")
            && context.Request.Path.StartsWithSegments("/api")
            && !context.Request.Path.StartsWithSegments("/api/auth"))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            await context.Response.WriteAsJsonAsync(new ProblemDetails
            {
                Status = StatusCodes.Status403Forbidden,
                Title = "You must change your temporary password before continuing.",
                Type = "password_change_required",
            });
            return;
        }

        await _next(context);
    }
}

/// <summary>
/// Converts exceptions into RFC 7807 ProblemDetails without leaking stack traces or SQL details.
/// </summary>
public sealed class GlobalExceptionHandler : IExceptionHandler
{
    private readonly ILogger<GlobalExceptionHandler> _logger;
    private readonly IHostEnvironment _env;

    public GlobalExceptionHandler(ILogger<GlobalExceptionHandler> logger, IHostEnvironment env)
    {
        _logger = logger;
        _env = env;
    }

    public async ValueTask<bool> TryHandleAsync(HttpContext context, Exception exception, CancellationToken ct)
    {
        var (status, title, detail) = exception switch
        {
            AppValidationException v      => (StatusCodes.Status400BadRequest, "Validation failed", v.Message),
            UnauthorizedAccessException   => (StatusCodes.Status403Forbidden, "Forbidden", "You do not have access to this resource."),
            BadHttpRequestException b     => (b.StatusCode, "Bad request", "The request could not be processed."),
            OperationCanceledException    => (499, "Request cancelled", "The request was cancelled."),
            _                             => (StatusCodes.Status500InternalServerError, "Server error",
                                              "An unexpected error occurred. Please try again later."),
        };

        if (status >= 500)
            _logger.LogError(exception, "Unhandled exception for {Method} {Path}", context.Request.Method, context.Request.Path);
        else
            _logger.LogWarning("Handled {ExceptionType}: {Message}", exception.GetType().Name, exception.Message);

        var problem = new ProblemDetails
        {
            Status = status,
            Title = title,
            Detail = detail,
            Instance = context.Request.Path,
        };
        problem.Extensions["traceId"] = context.TraceIdentifier;
        if (_env.IsDevelopment() && status >= 500)
            problem.Extensions["debug"] = exception.Message;

        context.Response.StatusCode = status;
        await context.Response.WriteAsJsonAsync(problem, ct);
        return true;
    }
}

/// <summary>SQL Server DATETIME2 comes back as Kind=Unspecified; all our values are UTC, so emit them with a 'Z'.</summary>
public sealed class UtcDateTimeConverter : JsonConverter<DateTime>
{
    public override DateTime Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.GetDateTime().ToUniversalTime();

    public override void Write(Utf8JsonWriter writer, DateTime value, JsonSerializerOptions options) =>
        writer.WriteStringValue(DateTime.SpecifyKind(value, DateTimeKind.Utc).ToString("yyyy-MM-ddTHH:mm:ss'Z'"));
}

public static class HttpContextExtensions
{
    public static string? GetClientIp(this HttpContext context) =>
        context.Connection.RemoteIpAddress?.ToString();
}

/// <summary>Creates the first Admin from configuration if no admin exists yet.</summary>
public sealed class AdminSeeder : IHostedService
{
    private readonly IServiceProvider _services;
    private readonly ILogger<AdminSeeder> _logger;

    public AdminSeeder(IServiceProvider services, ILogger<AdminSeeder> logger)
    {
        _services = services;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        using var scope = _services.CreateScope();
        var seed = scope.ServiceProvider.GetRequiredService<IOptions<SeedOptions>>().Value;
        if (string.IsNullOrWhiteSpace(seed.AdminEmail) || string.IsNullOrWhiteSpace(seed.AdminPassword))
            return;

        try
        {
            var users = scope.ServiceProvider.GetRequiredService<IUserRepository>();
            if (await users.AnyAdminAsync()) return;

            var hasher = scope.ServiceProvider.GetRequiredService<IPasswordHasher<AppUser>>();
            var admin = new AppUser
            {
                FullName = seed.AdminName,
                Email = seed.AdminEmail.Trim().ToLowerInvariant(),
                Role = Roles.Admin,
                MustChangePassword = true, // the configured bootstrap password must be replaced on first sign-in
            };
            admin.PasswordHash = hasher.HashPassword(admin, seed.AdminPassword);
            await users.CreateAsync(admin);
            _logger.LogWarning("Seeded initial admin account {Email}. Change its password after first sign-in.", admin.Email);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Admin seeding failed. Is the database reachable and has 01_schema.sql been run?");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
