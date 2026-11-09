var builder = WebApplication.CreateBuilder(args);
builder.Services.AddHealthChecks();
var app = builder.Build();

// The chat window is a static page. It learns where the agent lives from this config endpoint,
// so the same build works in dev and prod (set Agent__BaseUrl in the environment).
string agentBaseUrl = app.Configuration["Agent:BaseUrl"] ?? "http://localhost:5200";

app.Use(async (ctx, next) =>
{
    ctx.Response.Headers["X-Content-Type-Options"] = "nosniff";
    ctx.Response.Headers["Referrer-Policy"] = "no-referrer";
    await next();
});

app.MapGet("/config.json", () => Results.Json(new { agentBaseUrl }));
app.MapHealthChecks("/health");

app.UseDefaultFiles();
app.UseStaticFiles();

app.Run();
