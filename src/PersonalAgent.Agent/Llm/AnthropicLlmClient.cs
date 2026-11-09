using System.Text;
using System.Text.Json;
using Anthropic;
using Anthropic.Exceptions;
using Anthropic.Models.Messages;
using Microsoft.Extensions.Options;

namespace PersonalAgent.Agent;

/// <summary>LLM client for Claude through the official Anthropic SDK.</summary>
public sealed class AnthropicLlmClient(IOptions<AnthropicOptions> options) : ILlmClient
{
    private readonly AnthropicOptions _opt = options.Value;

    // Created on first use, so the app can start without a key.
    private readonly Lazy<AnthropicClient> _client =
        new(() => new AnthropicClient { ApiKey = options.Value.ApiKey });

    public string Provider => "Anthropic";
    public string Model => _opt.Model;
    public bool IsConfigured => _opt.IsConfigured;

    public void EnsureConfigured()
    {
        if (!IsConfigured) throw new LlmNotConfiguredException("Anthropic:ApiKey or Anthropic:Model is not set.");
    }

    public async Task<LlmResponse> CompleteAsync(
        IReadOnlyList<ConversationItem> history, IReadOnlyList<ToolDef> tools,
        string systemPrompt, CancellationToken ct)
    {
        EnsureConfigured();

        List<ToolUnion> toolUnions = tools.Select(ToTool).ToList();
        var request = new MessageCreateParams
        {
            Model = _opt.Model,
            MaxTokens = 16000,
            System = systemPrompt,
            Messages = ToMessages(history),
            Tools = toolUnions,
            // Thinking is adaptive by default on this model.
        };

        Message response;
        try
        {
            response = await _client.Value.Messages.Create(request, ct);
        }
        catch (Exception ex) when (ex is AnthropicApiException or HttpRequestException)
        {
            throw new LlmProviderException("Anthropic request failed.", ex);
        }

        var native = new List<ContentBlockParam>(); // replayed as-is next turn (keeps thinking blocks valid)
        var calls = new List<ToolCall>();
        var text = new StringBuilder();

        foreach (ContentBlock block in response.Content)
        {
            if (block.TryPickText(out TextBlock? t))
            {
                native.Add(new TextBlockParam { Text = t.Text });
                text.AppendLine(t.Text);
            }
            else if (block.TryPickThinking(out ThinkingBlock? th))
            {
                native.Add(new ThinkingBlockParam { Thinking = th.Thinking, Signature = th.Signature });
            }
            else if (block.TryPickRedactedThinking(out RedactedThinkingBlock? rt))
            {
                native.Add(new RedactedThinkingBlockParam { Data = rt.Data });
            }
            else if (block.TryPickToolUse(out ToolUseBlock? tu))
            {
                native.Add(new ToolUseBlockParam { ID = tu.ID, Name = tu.Name, Input = tu.Input });
                calls.Add(new ToolCall(tu.ID, tu.Name, tu.Input));
            }
        }

        LlmStop stop = response.StopReason == "refusal" ? LlmStop.Refusal
            : response.StopReason == "max_tokens" ? LlmStop.MaxTokens
            : calls.Count > 0 ? LlmStop.ToolUse
            : LlmStop.End;

        return new LlmResponse(text.ToString().Trim(), calls, stop, native);
    }

    private static List<MessageParam> ToMessages(IReadOnlyList<ConversationItem> history)
    {
        var messages = new List<MessageParam>();
        foreach (ConversationItem item in history)
        {
            switch (item)
            {
                case UserItem u:
                    messages.Add(new UserMessageParam { Content = u.Text });
                    break;

                case AssistantItem { Native: List<ContentBlockParam> native }:
                    messages.Add(new AssistantMessageParam { Content = native });
                    break;

                case AssistantItem a:
                    var blocks = new List<ContentBlockParam>();
                    if (a.Text.Length > 0) blocks.Add(new TextBlockParam { Text = a.Text });
                    foreach (ToolCall c in a.ToolCalls)
                        blocks.Add(new ToolUseBlockParam { ID = c.Id, Name = c.Name, Input = c.Input });
                    messages.Add(new AssistantMessageParam { Content = blocks });
                    break;

                case ToolResultsItem r:
                    messages.Add(new UserMessageParam
                    {
                        Content = r.Results.Select(x => (ContentBlockParam)new ToolResultBlockParam
                        {
                            ToolUseID = x.ToolCallId, Content = x.Content, IsError = x.IsError,
                        }).ToList(),
                    });
                    break;
            }
        }
        return messages;
    }

    private static ToolUnion ToTool(ToolDef def)
    {
        var props = new Dictionary<string, JsonElement>();
        var required = new List<string>();
        JsonElement schema = def.InputSchema;

        if (schema.ValueKind == JsonValueKind.Object)
        {
            if (schema.TryGetProperty("properties", out var p) && p.ValueKind == JsonValueKind.Object)
                foreach (var prop in p.EnumerateObject()) props[prop.Name] = prop.Value.Clone();

            if (schema.TryGetProperty("required", out var r) && r.ValueKind == JsonValueKind.Array)
                foreach (var item in r.EnumerateArray())
                    if (item.GetString() is { } s) required.Add(s);
        }

        return new Tool
        {
            Name = def.Name,
            Description = def.Description,
            InputSchema = new() { Properties = props, Required = required },
        };
    }
}
