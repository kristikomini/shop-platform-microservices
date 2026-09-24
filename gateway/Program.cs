using System.Threading.RateLimiting;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

var builder = WebApplication.CreateBuilder(args);

// The gateway is the single public entry point. Clients never call services
// directly. Cross-cutting concerns (auth, rate limiting, TLS) live here.
builder.Services.AddReverseProxy()
    .LoadFromConfig(builder.Configuration.GetSection("ReverseProxy"));

// Rate limiting: a fixed window per client IP, so one caller can't flood the
// whole platform. Configurable via RateLimit:PermitLimit / RateLimit:WindowSeconds.
var permitLimit = builder.Configuration.GetValue("RateLimit:PermitLimit", 100);
var windowSeconds = builder.Configuration.GetValue("RateLimit:WindowSeconds", 10);
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
    {
        var clientKey = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        return RateLimitPartition.GetFixedWindowLimiter(clientKey, _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = permitLimit,
            Window = TimeSpan.FromSeconds(windowSeconds),
            QueueLimit = 0
        });
    });
});

// Distributed tracing: the gateway is the root span; HttpClient instrumentation
// injects the W3C traceparent header, which YARP forwards to the services.
builder.Services.AddOpenTelemetry()
    .ConfigureResource(r => r.AddService("gateway"))
    .WithTracing(t => t
        .AddAspNetCoreInstrumentation()
        .AddHttpClientInstrumentation()
        .AddOtlpExporter());

var app = builder.Build();

// Diagnostic endpoint. NOT "/" — the root path is proxied to the frontend SPA.
app.MapGet("/_gateway/info", () => Results.Ok(new
{
    gateway = "up",
    routes = new[] { "/ (frontend SPA)", "/catalog/*", "/orders/*" }
}));

app.UseRateLimiter();

app.MapReverseProxy();

app.Run();
