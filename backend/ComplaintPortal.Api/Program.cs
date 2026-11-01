using System.Security.Claims;
using System.Text;
using System.Threading.RateLimiting;
using ComplaintPortal.Api.Data;
using ComplaintPortal.Api.Infrastructure;
using ComplaintPortal.Api.Models;
using ComplaintPortal.Api.Options;
using ComplaintPortal.Api.Repositories;
using ComplaintPortal.Api.Security;
using ComplaintPortal.Api.Services;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi.Models;

var builder = WebApplication.CreateBuilder(args);

// ---------------------------------------------------------------- Kestrel
builder.WebHost.ConfigureKestrel(o =>
{
    o.AddServerHeader = false;                       // don't advertise the server stack
    o.Limits.MaxRequestBodySize = 20 * 1024 * 1024;  // hard cap on any request body
});

// ---------------------------------------------------------------- Options
builder.Services.Configure<JwtOptions>(builder.Configuration.GetSection(JwtOptions.SectionName));
builder.Services.Configure<SecurityOptions>(builder.Configuration.GetSection(SecurityOptions.SectionName));
builder.Services.Configure<FileStorageOptions>(builder.Configuration.GetSection(FileStorageOptions.SectionName));
builder.Services.Configure<SeedOptions>(builder.Configuration.GetSection(SeedOptions.SectionName));

var jwt = builder.Configuration.GetSection(JwtOptions.SectionName).Get<JwtOptions>()
          ?? throw new InvalidOperationException("Jwt configuration section is missing.");
jwt.Validate();
var security = builder.Configuration.GetSection(SecurityOptions.SectionName).Get<SecurityOptions>() ?? new SecurityOptions();

// ---------------------------------------------------------------- Data access (Dapper)
builder.Services.AddSingleton<IDbConnectionFactory, SqlConnectionFactory>();
builder.Services.AddScoped<IUserRepository, UserRepository>();
builder.Services.AddScoped<IComplaintRepository, ComplaintRepository>();
builder.Services.AddScoped<IRefreshTokenRepository, RefreshTokenRepository>();
builder.Services.AddScoped<IAuditRepository, AuditRepository>();
builder.Services.AddScoped<IRoleRepository, RoleRepository>();

// ---------------------------------------------------------------- Services
builder.Services.AddSingleton<IPasswordHasher<AppUser>, PasswordHasher<AppUser>>(); // PBKDF2-HMAC-SHA512, 100k iterations
builder.Services.AddSingleton<ITokenService, TokenService>();
builder.Services.AddSingleton<IFileStorageService, FileStorageService>();
builder.Services.AddScoped<IAuthService, AuthService>();
builder.Services.AddHostedService<AdminSeeder>();
builder.Services.AddMemoryCache();
builder.Services.AddSingleton<IAccessCache, AccessCache>();

// ---------------------------------------------------------------- Authentication (JWT)
builder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(o =>
    {
        o.MapInboundClaims = false;
        o.RequireHttpsMetadata = !builder.Environment.IsDevelopment();
        o.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = jwt.Issuer,
            ValidateAudience = true,
            ValidAudience = jwt.Audience,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwt.Key)),
            ValidAlgorithms = new[] { SecurityAlgorithms.HmacSha256 }, // block "alg" confusion
            ValidateLifetime = true,
            RequireExpirationTime = true,
            ClockSkew = TimeSpan.FromSeconds(30),
            NameClaimType = AppClaims.Name,
            RoleClaimType = AppClaims.Role,
        };
        o.Events = new JwtBearerEvents
        {
            // 1) Reject tokens of deactivated users, or issued before a role/password change (security stamp).
            // 2) Load the role's permissions from the DB (cached) and attach them as claims for this request only,
            //    so permission edits take effect without users having to sign in again.
            OnTokenValidated = async ctx =>
            {
                var sub = ctx.Principal?.FindFirst(AppClaims.Sub)?.Value;
                var stamp = ctx.Principal?.FindFirst(AppClaims.SecurityStamp)?.Value;
                if (!int.TryParse(sub, out var userId) || stamp is null || ctx.Principal?.Identity is not ClaimsIdentity identity)
                {
                    ctx.Fail("Invalid token claims.");
                    return;
                }

                var access = ctx.HttpContext.RequestServices.GetRequiredService<IAccessCache>();
                var state = await access.GetUserStateAsync(userId);
                if (state is null || !state.IsActive ||
                    !string.Equals(state.SecurityStamp, stamp, StringComparison.OrdinalIgnoreCase))
                {
                    ctx.Fail("Token has been revoked.");
                    return;
                }

                foreach (var permission in await access.GetRolePermissionsAsync(state.Role))
                    identity.AddClaim(new Claim(AppClaims.Permission, permission));
                if (state.MustChangePassword)
                    identity.AddClaim(new Claim(AppClaims.MustChangePassword, "true"));
            },
        };
    });

// ---------------------------------------------------------------- Authorization
builder.Services.AddAuthorization(o =>
{
    // One policy per permission, e.g. [Authorize(Policy = Permissions.UsersManage)].
    foreach (var permission in Permissions.All)
        o.AddPolicy(permission, p => p.RequireClaim(AppClaims.Permission, permission));

    o.AddPolicy(Policies.CanReadRoles, p => p.RequireClaim(AppClaims.Permission, Permissions.UsersManage, Permissions.RolesManage));
    o.AddPolicy(Policies.CanAssign, p => p.RequireClaim(AppClaims.Permission, Permissions.ComplaintsAssignSelf, Permissions.ComplaintsAssignAny));

    // Secure by default: any endpoint without explicit [AllowAnonymous] requires a signed-in user.
    o.FallbackPolicy = new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build();
});

// ---------------------------------------------------------------- Rate limiting
static string ClientKey(HttpContext ctx) => ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown";

builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    o.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(ctx =>
        RateLimitPartition.GetFixedWindowLimiter(ClientKey(ctx), _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = 300,
            Window = TimeSpan.FromMinutes(1),
        }));

    // Brute-force protection for login / register / change-password.
    o.AddPolicy("auth", ctx => RateLimitPartition.GetFixedWindowLimiter(ClientKey(ctx), _ => new FixedWindowRateLimiterOptions
    {
        PermitLimit = 10,
        Window = TimeSpan.FromMinutes(1),
    }));

    o.AddPolicy("refresh", ctx => RateLimitPartition.GetFixedWindowLimiter(ClientKey(ctx), _ => new FixedWindowRateLimiterOptions
    {
        PermitLimit = 30,
        Window = TimeSpan.FromMinutes(1),
    }));

    o.OnRejected = async (ctx, ct) =>
    {
        ctx.HttpContext.Response.Headers.RetryAfter = "60";
        await ctx.HttpContext.Response.WriteAsJsonAsync(new
        {
            status = 429,
            title = "Too many requests. Please wait a minute and try again.",
        }, ct);
    };
});

// ---------------------------------------------------------------- CORS (only needed if the SPA is on another origin)
builder.Services.AddCors(o => o.AddPolicy("frontend", p => p
    .WithOrigins(security.AllowedOrigins)
    .WithMethods("GET", "POST", "PATCH", "DELETE")
    .WithHeaders("Authorization", "Content-Type", "X-Requested-With")
    .AllowCredentials()));

// ---------------------------------------------------------------- MVC / errors / docs
builder.Services
    .AddControllers()
    .AddJsonOptions(o => o.JsonSerializerOptions.Converters.Add(new UtcDateTimeConverter()));
builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<GlobalExceptionHandler>();
builder.Services.AddHsts(o =>
{
    o.MaxAge = TimeSpan.FromDays(365);
    o.IncludeSubDomains = true;
});

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new OpenApiInfo { Title = "Complaint Portal API", Version = "v1" });
    c.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme
    {
        Type = SecuritySchemeType.Http,
        Scheme = "bearer",
        BearerFormat = "JWT",
        In = ParameterLocation.Header,
        Description = "Paste the access token returned by /api/auth/login",
    });
    c.AddSecurityRequirement(new OpenApiSecurityRequirement
    {
        {
            new OpenApiSecurityScheme { Reference = new OpenApiReference { Type = ReferenceType.SecurityScheme, Id = "Bearer" } },
            Array.Empty<string>()
        }
    });
});

var app = builder.Build();

// ---------------------------------------------------------------- Pipeline
app.UseExceptionHandler();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}
else
{
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseMiddleware<SecurityHeadersMiddleware>();

// Production: the React build is copied into wwwroot and served from the same origin.
app.UseDefaultFiles();
app.UseStaticFiles();

app.UseRouting();
app.UseCors("frontend");
app.UseAuthentication();
app.UseMiddleware<PasswordChangeRequiredMiddleware>();
app.UseRateLimiter();
app.UseAuthorization();

app.MapControllers();
app.MapGet("/api/health", () => Results.Ok(new { status = "ok" })).AllowAnonymous();

// Unknown API routes are 404s; everything else falls back to the SPA so client-side routing works.
app.MapFallback("/api/{**slug}", () => Results.NotFound()).AllowAnonymous();
app.MapFallbackToFile("index.html").AllowAnonymous();

app.Run();
