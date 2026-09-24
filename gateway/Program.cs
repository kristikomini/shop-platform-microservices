var builder = WebApplication.CreateBuilder(args);

// The gateway is the single public entry point. Clients never call services
// directly. Cross-cutting concerns (auth, rate limiting, TLS) would live here.
builder.Services.AddReverseProxy()
    .LoadFromConfig(builder.Configuration.GetSection("ReverseProxy"));

var app = builder.Build();

// Diagnostic endpoint. NOT "/" — the root path is proxied to the frontend SPA.
app.MapGet("/_gateway/info", () => Results.Ok(new
{
    gateway = "up",
    routes = new[] { "/ (frontend SPA)", "/catalog/*", "/orders/*" }
}));

app.MapReverseProxy();

app.Run();
