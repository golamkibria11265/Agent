using System.Threading.RateLimiting;
using Anthropic.Exceptions;
using Microsoft.Extensions.Options;
using PersonalAgent.Agent;

var builder = WebApplication.CreateBuilder(args);

// Settings load in this order (later wins): appsettings.json, appsettings.{Environment}.json
// (Development or Production, chosen by ASPNETCORE_ENVIRONMENT), then environment variables.
// A missing key does NOT stop the app: it starts, logs a warning, and /api/chat answers 503
// until the key is configured.

builder.Services.Configure<AnthropicOptions>(builder.Configuration.GetSection("Anthropic"));
builder.Services.Configure<AgentOptions>(builder.Configuration.GetSection("Agent"));
builder.Services.Configure<McpOptions>(builder.Configuration.GetSection("Mcp"));
builder.Services.AddSingleton<McpToolClient>();
builder.Services.AddSingleton<LlmClient>();
builder.Services.AddSingleton<SessionStore>();
builder.Services.AddSingleton<AgentService>();

builder.Services.AddProblemDetails();
builder.Services.AddHealthChecks();

// Only the chat window's origin may call the agent from a browser.
string[] origins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? [];
builder.Services.AddCors(o => o.AddDefaultPolicy(p =>
    p.WithOrigins(origins).WithHeaders("Content-Type").WithMethods("GET", "POST")));

// Per-IP rate limit protects your LLM bill.
builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    o.AddPolicy("chat", ctx => RateLimitPartition.GetFixedWindowLimiter(
        ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = 20, Window = TimeSpan.FromMinutes(1), QueueLimit = 0,
        }));
});

builder.WebHost.ConfigureKestrel(k => k.Limits.MaxRequestBodySize = 64 * 1024);

var app = builder.Build();

if (!app.Services.GetRequiredService<IOptions<AnthropicOptions>>().Value.HasKey)
    app.Logger.LogWarning(
        "Anthropic:ApiKey is not set for the '{Environment}' environment. The agent will start, but chat requests " +
        "will return 503 until you set it in appsettings.{Environment}.json or the Anthropic__ApiKey environment variable.",
        app.Environment.EnvironmentName, app.Environment.EnvironmentName);

app.UseExceptionHandler();
app.UseCors();
app.UseRateLimiter();

app.MapHealthChecks("/health");

app.MapPost("/api/chat", async (
    ChatRequest req, AgentService agent, IOptions<AgentOptions> opt,
    ILogger<Program> log, CancellationToken ct) =>
{
    string message = req.Message?.Trim() ?? "";
    if (message.Length == 0)
        return Results.Problem("Message is required.", statusCode: 400);
    if (message.Length > opt.Value.MaxMessageChars)
        return Results.Problem($"Message is too long (max {opt.Value.MaxMessageChars} characters).", statusCode: 400);

    try
    {
        return Results.Ok(await agent.ChatAsync(req.SessionId, message, ct));
    }
    catch (LlmNotConfiguredException)
    {
        return Results.Problem(
            "The agent is not configured yet: the Anthropic API key is missing. Ask the administrator to set it.",
            statusCode: 503);
    }
    catch (McpUnavailableException ex)
    {
        log.LogError(ex, "MCP server unavailable");
        return Results.Problem("The tool server is unavailable. Try again shortly.", statusCode: 503);
    }
    catch (AnthropicApiException ex)
    {
        log.LogError(ex, "LLM request failed");
        return Results.Problem("The language model is unavailable. Try again shortly.", statusCode: 502);
    }
    catch (OperationCanceledException)
    {
        return Results.StatusCode(499); // client closed the request
    }
}).RequireRateLimiting("chat");

app.Run();
