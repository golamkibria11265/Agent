using Microsoft.Extensions.Options;

namespace PersonalAgent.Agent;

/// <summary>
/// The agent: sends the conversation to the LLM (whichever provider is configured); if the model asks
/// for tools, runs them via MCP, feeds results back, and repeats until the model gives a final answer.
/// </summary>
public sealed class AgentService(
    ILlmClient llm, McpToolClient mcp, SessionStore sessions,
    IOptions<AgentOptions> options, ILogger<AgentService> log)
{
    private static readonly string[] ReadOnlyHints = ["_list_", "_get_", "_search_"];

    private static bool IsReadOnly(string toolName) =>
        ReadOnlyHints.Any(h => toolName.Contains(h, StringComparison.OrdinalIgnoreCase));

    public async Task<ChatResponse> ChatAsync(string? sessionId, string message, CancellationToken ct)
    {
        llm.EnsureConfigured(); // fail early, before touching MCP or the session
        AgentOptions o = options.Value;
        Session session = sessions.GetOrCreate(sessionId);

        await session.Lock.WaitAsync(ct);
        int checkpoint = session.History.Count;
        try
        {
            // Only expose tools the policy allows (read-only unless AllowWriteTools is on).
            var mcpTools = (await mcp.GetToolsAsync(ct))
                .Where(t => o.AllowWriteTools || IsReadOnly(t.Name)).ToList();
            var allowed = mcpTools.Select(t => t.Name).ToHashSet();
            var toolDefs = mcpTools.Select(t => new ToolDef(t.Name, t.Description ?? "", t.JsonSchema)).ToList();
            var toolsUsed = new List<string>();

            session.History.Add(new UserItem(message));

            for (int turn = 1; turn <= o.MaxTurns; turn++)
            {
                LlmResponse response = await llm.CompleteAsync(session.History, toolDefs, o.SystemPrompt, ct);

                if (response.Stop == LlmStop.Refusal)
                {
                    session.History.RemoveRange(checkpoint, session.History.Count - checkpoint);
                    return new(session.Id, "The model declined to answer this request.", toolsUsed);
                }

                session.History.Add(new AssistantItem(response.Text, response.ToolCalls, response.Native));

                if (response.Stop != LlmStop.ToolUse || response.ToolCalls.Count == 0)
                {
                    string reply = response.Stop == LlmStop.MaxTokens
                        ? response.Text + "\n\n[Response was cut off.]" : response.Text;
                    return new(session.Id, reply, toolsUsed);
                }

                // Run all requested tools in parallel. Log tool names only, never user text or arguments.
                var results = await Task.WhenAll(response.ToolCalls.Select(async call =>
                {
                    if (!allowed.Contains(call.Name))
                    {
                        log.LogWarning("Model requested a tool that is not allowed: {Tool}", call.Name);
                        return new ToolResultItem(call.Id, "This tool is not available.", true);
                    }

                    var (output, isError) = await mcp.CallAsync(call.Name, call.Input, ct);
                    log.LogInformation("Session {Session} tool {Tool} error={IsError}", session.Id, call.Name, isError);
                    lock (toolsUsed) toolsUsed.Add(call.Name);
                    return new ToolResultItem(call.Id, output, isError);
                }));

                // ALL tool results go back together, one per tool call.
                session.History.Add(new ToolResultsItem(results));
            }

            return new(session.Id, $"I stopped after {o.MaxTurns} steps without finishing. Please try a simpler request.", toolsUsed);
        }
        catch
        {
            // Keep history valid: drop this partial exchange so the next message starts clean.
            session.History.RemoveRange(checkpoint, session.History.Count - checkpoint);
            throw;
        }
        finally
        {
            session.LastUsedUtc = DateTime.UtcNow;
            session.Lock.Release();
        }
    }
}
