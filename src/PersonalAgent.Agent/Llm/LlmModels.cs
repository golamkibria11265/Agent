using System.Text.Json;

namespace PersonalAgent.Agent;

// Provider-neutral conversation model. The agent loop only knows these types;
// each provider client translates them to and from its own wire format.

public sealed record ToolDef(string Name, string Description, JsonElement InputSchema);

public sealed record ToolCall(string Id, string Name, IReadOnlyDictionary<string, JsonElement> Input);

public sealed record ToolResultItem(string ToolCallId, string Content, bool IsError);

public abstract record ConversationItem;

public sealed record UserItem(string Text) : ConversationItem;

/// <param name="Native">Opaque provider data to replay exactly (e.g. Anthropic thinking blocks). Other providers ignore it.</param>
public sealed record AssistantItem(string Text, IReadOnlyList<ToolCall> ToolCalls, object? Native = null) : ConversationItem;

public sealed record ToolResultsItem(IReadOnlyList<ToolResultItem> Results) : ConversationItem;

public enum LlmStop { End, ToolUse, MaxTokens, Refusal }

public sealed record LlmResponse(string Text, IReadOnlyList<ToolCall> ToolCalls, LlmStop Stop, object? Native = null);

public interface ILlmClient
{
    /// <summary>"Anthropic" or "OpenRouter". Also the name of its configuration section.</summary>
    string Provider { get; }
    string Model { get; }
    bool IsConfigured { get; }

    /// <exception cref="LlmNotConfiguredException">Key or model is missing.</exception>
    void EnsureConfigured();

    /// <exception cref="LlmProviderException">The provider call failed.</exception>
    Task<LlmResponse> CompleteAsync(
        IReadOnlyList<ConversationItem> history, IReadOnlyList<ToolDef> tools,
        string systemPrompt, CancellationToken ct);
}
