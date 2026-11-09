using System.Text;
using Anthropic.Models.Messages;
using Microsoft.Extensions.Options;

namespace PersonalAgent.Agent;

/// <summary>
/// The agent: sends the conversation to the LLM; if the model asks for tools, runs them via MCP,
/// feeds results back, and repeats until the model gives a final answer.
/// </summary>
public sealed class AgentService(
    LlmClient llm, McpToolClient mcp, SessionStore sessions,
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
            var tools = LlmClient.ToClaudeTools(mcpTools);
            var toolsUsed = new List<string>();

            session.History.Add(new UserMessageParam { Content = message });

            for (int turn = 1; turn <= o.MaxTurns; turn++)
            {
                Message response = await llm.CompleteAsync(session.History, tools, ct);

                if (response.StopReason == "refusal")
                {
                    session.History.RemoveRange(checkpoint, session.History.Count - checkpoint);
                    return new(session.Id, "The model declined to answer this request.", toolsUsed);
                }

                var assistantContent = new List<ContentBlockParam>();
                var toolCalls = new List<ToolUseBlock>();
                var text = new StringBuilder();

                foreach (ContentBlock block in response.Content)
                {
                    if (block.TryPickText(out TextBlock? t))
                    {
                        assistantContent.Add(new TextBlockParam { Text = t.Text });
                        text.AppendLine(t.Text);
                    }
                    else if (block.TryPickThinking(out ThinkingBlock? th))
                    {
                        // Thinking blocks must be sent back unchanged.
                        assistantContent.Add(new ThinkingBlockParam { Thinking = th.Thinking, Signature = th.Signature });
                    }
                    else if (block.TryPickRedactedThinking(out RedactedThinkingBlock? rt))
                    {
                        assistantContent.Add(new RedactedThinkingBlockParam { Data = rt.Data });
                    }
                    else if (block.TryPickToolUse(out ToolUseBlock? tu))
                    {
                        assistantContent.Add(new ToolUseBlockParam { ID = tu.ID, Name = tu.Name, Input = tu.Input });
                        toolCalls.Add(tu);
                    }
                }
                session.History.Add(new AssistantMessageParam { Content = assistantContent });

                if (response.StopReason != "tool_use" || toolCalls.Count == 0)
                {
                    if (response.StopReason == "max_tokens") text.AppendLine("\n[Response was cut off.]");
                    return new(session.Id, text.ToString().Trim(), toolsUsed);
                }

                // Run all requested tools in parallel. Log tool names only, never user text or arguments.
                var results = await Task.WhenAll(toolCalls.Select(async call =>
                {
                    if (!allowed.Contains(call.Name))
                    {
                        log.LogWarning("Model requested a tool that is not allowed: {Tool}", call.Name);
                        return new ToolResultBlockParam
                        {
                            ToolUseID = call.ID, Content = "This tool is not available.", IsError = true,
                        };
                    }

                    var (output, isError) = await mcp.CallAsync(call.Name, call.Input, ct);
                    log.LogInformation("Session {Session} tool {Tool} error={IsError}", session.Id, call.Name, isError);
                    lock (toolsUsed) toolsUsed.Add(call.Name);
                    return new ToolResultBlockParam { ToolUseID = call.ID, Content = output, IsError = isError };
                }));

                // ALL tool results go back in ONE user message.
                session.History.Add(new UserMessageParam { Content = results.Select(r => (ContentBlockParam)r).ToList() });
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
