using System.Collections.Concurrent;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.IdentityModel.Tokens;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddHealthChecks();

builder.Services.AddOpenTelemetry()
    .ConfigureResource(r => r.AddService("auth"))
    .WithTracing(t => t.AddAspNetCoreInstrumentation().AddOtlpExporter())
    .WithMetrics(m => m
        .AddAspNetCoreInstrumentation()
        .AddRuntimeInstrumentation()
        .AddPrometheusExporter());

var app = builder.Build();

app.MapHealthChecks("/health");
app.MapPrometheusScrapingEndpoint();

// Exchange username + password for a short-lived access token + a refresh token.
app.MapPost("/login", (LoginRequest req, IConfiguration config, ILogger<Program> logger) =>
{
    var user = UserStore.Find(req.Username);
    if (user is null || !PasswordHasher.Verify(req.Password, user.PasswordHash))
    {
        logger.LogInformation("Failed login for '{Username}'.", req.Username);
        return Results.Unauthorized();
    }

    logger.LogInformation("Issued tokens for '{Username}' ({Role}).", user.Username, user.Role);
    return Results.Ok(Tokens.Issue(user.Username, user.Role, config));
});

// Exchange a valid (unexpired, unused) refresh token for a fresh token pair.
// Refresh tokens are single-use: consuming one rotates it, which limits the
// damage if a refresh token leaks.
app.MapPost("/refresh", (RefreshRequest req, IConfiguration config, ILogger<Program> logger) =>
{
    var entry = RefreshTokenStore.Consume(req.RefreshToken);
    if (entry is null)
    {
        logger.LogInformation("Refresh rejected (invalid or expired token).");
        return Results.Unauthorized();
    }

    logger.LogInformation("Refreshed tokens for '{Username}'.", entry.Username);
    return Results.Ok(Tokens.Issue(entry.Username, entry.Role, config));
});

app.Run();

// ---------------------------------------------------------------------------

public record LoginRequest(string Username, string Password);
public record RefreshRequest(string RefreshToken);
public record LoginResponse(string Token, string RefreshToken, string Username, string Role, int ExpiresIn);
public record AppUser(string Username, string PasswordHash, string Role);
public record RefreshEntry(string Username, string Role, DateTime ExpiresUtc);

public static class Tokens
{
    public static LoginResponse Issue(string username, string role, IConfiguration config)
    {
        var accessMinutes = config.GetValue("Jwt:AccessMinutes", 15);
        var refreshDays = config.GetValue("Jwt:RefreshDays", 7);

        var accessToken = TokenIssuer.Issue(username, role, config, TimeSpan.FromMinutes(accessMinutes));
        var refreshToken = RefreshTokenStore.Issue(username, role, TimeSpan.FromDays(refreshDays));
        return new LoginResponse(accessToken, refreshToken, username, role, accessMinutes * 60);
    }
}

// Demo user directory. A real system would use a database + an identity provider.
public static class UserStore
{
    private static readonly Dictionary<string, AppUser> Users = new(StringComparer.OrdinalIgnoreCase)
    {
        ["admin"]    = new("admin",    PasswordHasher.Hash("admin123"),    "Admin"),
        ["customer"] = new("customer", PasswordHasher.Hash("customer123"), "Customer"),
    };

    public static AppUser? Find(string username) =>
        Users.TryGetValue(username ?? "", out var user) ? user : null;
}

// Single-use, rotating refresh tokens held in memory. A real system would store
// these in a database (or Redis) so they survive restarts and scale out.
public static class RefreshTokenStore
{
    private static readonly ConcurrentDictionary<string, RefreshEntry> Tokens = new();

    public static string Issue(string username, string role, TimeSpan lifetime)
    {
        var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .Replace('+', '-').Replace('/', '_').TrimEnd('=');
        Tokens[token] = new RefreshEntry(username, role, DateTime.UtcNow.Add(lifetime));
        return token;
    }

    public static RefreshEntry? Consume(string token)
    {
        if (string.IsNullOrEmpty(token) || !Tokens.TryRemove(token, out var entry))
            return null;                       // unknown or already used
        return entry.ExpiresUtc > DateTime.UtcNow ? entry : null;  // reject expired
    }
}

// PBKDF2 salted hashing + constant-time verification — passwords are never
// stored or compared in clear text.
public static class PasswordHasher
{
    public static string Hash(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, 100_000, HashAlgorithmName.SHA256, 32);
        return $"{Convert.ToBase64String(salt)}.{Convert.ToBase64String(hash)}";
    }

    public static bool Verify(string password, string stored)
    {
        var parts = stored.Split('.');
        if (parts.Length != 2) return false;
        var salt = Convert.FromBase64String(parts[0]);
        var expected = Convert.FromBase64String(parts[1]);
        var actual = Rfc2898DeriveBytes.Pbkdf2(password, salt, 100_000, HashAlgorithmName.SHA256, 32);
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }
}

public static class TokenIssuer
{
    public static string Issue(string username, string role, IConfiguration config, TimeSpan lifetime)
    {
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(config["Jwt:Key"]!));
        var credentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

        var claims = new[]
        {
            new Claim(JwtRegisteredClaimNames.Sub, username),
            new Claim(ClaimTypes.Name, username),
            new Claim(ClaimTypes.Role, role),
        };

        var token = new JwtSecurityToken(
            issuer: config["Jwt:Issuer"],
            audience: config["Jwt:Audience"],
            claims: claims,
            expires: DateTime.UtcNow.Add(lifetime),
            signingCredentials: credentials);

        return new JwtSecurityTokenHandler().WriteToken(token);
    }
}
