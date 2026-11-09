using System.Threading.RateLimiting;
using Microsoft.Extensions.Options;
using PersonalAgent.Agent;

var builder = WebApplication.CreateBuilder(args);

// Settings load in this order (later wins): appsettings.json, appsettings.{Environment}.json
// (Development or Production, chosen by ASPNETCORE_ENVIRONMENT), then environment variables.
// A missing key does NOT stop the app: it starts, logs a warning, and /api/chat answers 503
// until the key is configured.

builder.Services.Configure<LlmOptions>(builder.Configuration.GetSection("Llm"));
builder.Services.Configure<AnthropicOptions>(builder.Configuration.GetSection("Anthropic"));
builder.Services.Configure<OpenRouterOptions>(builder.Configuration.GetSection("OpenRouter"));
builder.Services.Configure<AgentOptions>(builder.Configuration.GetSection("Agent"));
builder.Services.Configure<McpOptions>(builder.Configuration.GetSection("Mcp"));
builder.Services.AddSingleton<McpToolClient>();

// The LLM provider is chosen by "Llm:Provider" in appsettings ("Anthropic" or "OpenRouter").
builder.Services.AddHttpClient("openrouter", c => c.Timeout = TimeSpan.FromSeconds(120));
builder.Services.AddSingleton<AnthropicLlmClient>();
builder.Services.AddSingleton<OpenRouterLlmClient>();
builder.Services.AddSingleton<ILlmClient>(sp =>
{
    string provider = sp.GetRequiredService<IOptions<LlmOptions>>().Value.Provider?.Trim() ?? "";
    return provider.ToLowerInvariant() switch
    {
        "anthropic" => sp.GetRequiredService<AnthropicLlmClient>(),
        "openrouter" => sp.GetRequiredService<OpenRouterLlmClient>(),
        _ => throw new InvalidOperationException(
            $"Unknown Llm:Provider '{provider}'. Use 'Anthropic' or 'OpenRouter'."),
    };
});
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

ILlmClient llmClient = app.Services.GetRequiredService<ILlmClient>(); // also validates Llm:Provider at startup
if (llmClient.IsConfigured)
    app.Logger.LogInformation("LLM provider: {Provider}, model: {Model}", llmClient.Provider, llmClient.Model);
else
    app.Logger.LogWarning(
        "LLM provider '{Provider}' is not configured for the '{Environment}' environment (missing key or model). " +
        "The agent will start, but chat requests return 503 until you set ApiKey and Model in the " +
        "'{Section}' section of appsettings.{EnvironmentFile}.json, or use environment variables like {EnvVar}.",
        llmClient.Provider, app.Environment.EnvironmentName, llmClient.Provider,
        app.Environment.EnvironmentName, llmClient.Provider + "__ApiKey");

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
    catch (LlmNotConfiguredException ex)
    {
        log.LogWarning("Chat refused: {Reason}", ex.Message);
        return Results.Problem(
            "The agent is not configured yet: the LLM API key or model is missing. Ask the administrator to set it.",
            statusCode: 503);
    }
    catch (McpUnavailableException ex)
    {
        log.LogError(ex, "MCP server unavailable");
        return Results.Problem("The tool server is unavailable. Try again shortly.", statusCode: 503);
    }
    catch (LlmProviderException ex)
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
