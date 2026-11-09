using System.Text.Json;
using Anthropic;
using Anthropic.Models.Messages;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Client;

namespace PersonalAgent.Agent;

/// <summary>LLM client (singleton): thin wrapper around the Anthropic SDK (Claude Messages API).</summary>
public sealed class LlmClient(IOptions<AgentOptions> options, IOptions<AnthropicOptions> anthropic)
{
    // Created on first use, so the app can start without a key.
    private readonly Lazy<AnthropicClient> _client =
        new(() => new AnthropicClient { ApiKey = anthropic.Value.ApiKey });
    private readonly AgentOptions _opt = options.Value;

    public bool IsConfigured => anthropic.Value.HasKey;

    public void EnsureConfigured()
    {
        if (!IsConfigured)
            throw new LlmNotConfiguredException("Anthropic:ApiKey is not set.");
    }

    public Task<Message> CompleteAsync(
        IReadOnlyList<MessageParam> messages, IReadOnlyList<ToolUnion> tools, CancellationToken ct)
    {
        EnsureConfigured();
        return _client.Value.Messages.Create(new MessageCreateParams
        {
            Model = _opt.Model,
            MaxTokens = 16000,
            System = _opt.SystemPrompt,
            Messages = messages.ToList(),
            Tools = tools,
            // Thinking is adaptive by default on this model.
        }, ct);
    }

    /// <summary>Converts MCP tool definitions into Claude tool definitions.</summary>
    public static List<ToolUnion> ToClaudeTools(IEnumerable<McpClientTool> mcpTools)
    {
        var tools = new List<ToolUnion>();
        foreach (McpClientTool t in mcpTools)
        {
            var props = new Dictionary<string, JsonElement>();
            var required = new List<string>();
            JsonElement schema = t.JsonSchema;

            if (schema.ValueKind == JsonValueKind.Object)
            {
                if (schema.TryGetProperty("properties", out var p) && p.ValueKind == JsonValueKind.Object)
                    foreach (var prop in p.EnumerateObject()) props[prop.Name] = prop.Value.Clone();

                if (schema.TryGetProperty("required", out var r) && r.ValueKind == JsonValueKind.Array)
                    foreach (var item in r.EnumerateArray())
                        if (item.GetString() is { } s) required.Add(s);
            }

            tools.Add(new Tool
            {
                Name = t.Name,
                Description = t.Description ?? "",
                InputSchema = new() { Properties = props, Required = required },
            });
        }
        return tools;
    }
}
