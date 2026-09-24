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

// Same observability stack as the other services.
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

// Exchange username + password for a signed JWT.
app.MapPost("/login", (LoginRequest req, IConfiguration config, ILogger<Program> logger) =>
{
    var user = UserStore.Find(req.Username);
    if (user is null || !PasswordHasher.Verify(req.Password, user.PasswordHash))
    {
        logger.LogInformation("Failed login for '{Username}'.", req.Username);
        return Results.Unauthorized();
    }

    var token = TokenIssuer.Issue(user, config);
    logger.LogInformation("Issued token for '{Username}' ({Role}).", user.Username, user.Role);
    return Results.Ok(new LoginResponse(token, user.Username, user.Role));
});

app.Run();

// ---------------------------------------------------------------------------

public record LoginRequest(string Username, string Password);
public record LoginResponse(string Token, string Username, string Role);
public record AppUser(string Username, string PasswordHash, string Role);

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
    public static string Issue(AppUser user, IConfiguration config)
    {
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(config["Jwt:Key"]!));
        var credentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

        var claims = new[]
        {
            new Claim(JwtRegisteredClaimNames.Sub, user.Username),
            new Claim(ClaimTypes.Name, user.Username),
            new Claim(ClaimTypes.Role, user.Role),
        };

        var token = new JwtSecurityToken(
            issuer: config["Jwt:Issuer"],
            audience: config["Jwt:Audience"],
            claims: claims,
            expires: DateTime.UtcNow.AddHours(1),
            signingCredentials: credentials);

        return new JwtSecurityTokenHandler().WriteToken(token);
    }
}
