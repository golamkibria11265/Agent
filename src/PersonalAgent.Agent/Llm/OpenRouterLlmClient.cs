using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;

namespace PersonalAgent.Agent;

/// <summary>
/// LLM client for OpenRouter (https://openrouter.ai), which uses the OpenAI-style
/// "chat/completions" API. Works with any OpenRouter model that supports tool calling.
/// </summary>
public sealed class OpenRouterLlmClient(
    IHttpClientFactory httpFactory, IOptions<OpenRouterOptions> options, ILogger<OpenRouterLlmClient> log)
    : ILlmClient
{
    private readonly OpenRouterOptions _opt = options.Value;

    public string Provider => "OpenRouter";
    public string Model => _opt.Model;
    public bool IsConfigured => _opt.IsConfigured;

    public void EnsureConfigured()
    {
        if (!IsConfigured) throw new LlmNotConfiguredException("OpenRouter:ApiKey or OpenRouter:Model is not set.");
    }

    public async Task<LlmResponse> CompleteAsync(
        IReadOnlyList<ConversationItem> history, IReadOnlyList<ToolDef> tools,
        string systemPrompt, CancellationToken ct)
    {
        EnsureConfigured();

        var body = new JsonObject
        {
            ["model"] = _opt.Model,
            ["max_tokens"] = _opt.MaxTokens,
            ["messages"] = ToMessages(history, systemPrompt),
        };
        if (tools.Count > 0) body["tools"] = ToTools(tools);

        using var request = new HttpRequestMessage(HttpMethod.Post, _opt.BaseUrl.TrimEnd('/') + "/chat/completions")
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _opt.ApiKey);
        request.Headers.Add("X-Title", "PersonalAgent");

        string json;
        try
        {
            using HttpClient http = httpFactory.CreateClient("openrouter");
            using HttpResponseMessage response = await http.SendAsync(request, ct);
            json = await response.Content.ReadAsStringAsync(ct);

            if (!response.IsSuccessStatusCode)
            {
                log.LogError("OpenRouter returned {Status}: {Body}", (int)response.StatusCode, Truncate(json, 300));
                throw new LlmProviderException($"OpenRouter returned HTTP {(int)response.StatusCode}.");
            }
        }
        catch (HttpRequestException ex)
        {
            throw new LlmProviderException("OpenRouter request failed.", ex);
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new LlmProviderException("OpenRouter request timed out.", ex);
        }

        return Parse(json);
    }

    private LlmResponse Parse(string json)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(json);
            JsonElement root = doc.RootElement;

            // OpenRouter can answer HTTP 200 with an error object.
            if (root.TryGetProperty("error", out JsonElement err))
            {
                log.LogError("OpenRouter error: {Body}", Truncate(err.GetRawText(), 300));
                throw new LlmProviderException("OpenRouter returned an error.");
            }

            JsonElement choice = root.GetProperty("choices")[0];
            JsonElement message = choice.GetProperty("message");

            string text = message.TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.String
                ? c.GetString() ?? "" : "";

            var calls = new List<ToolCall>();
            if (message.TryGetProperty("tool_calls", out var tcs) && tcs.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement tc in tcs.EnumerateArray())
                {
                    JsonElement fn = tc.GetProperty("function");
                    calls.Add(new ToolCall(
                        tc.GetProperty("id").GetString() ?? Guid.NewGuid().ToString("N"),
                        fn.GetProperty("name").GetString() ?? "",
                        ParseArguments(fn.TryGetProperty("arguments", out var a) ? a.GetString() : null)));
                }
            }

            string? finish = choice.TryGetProperty("finish_reason", out var f) ? f.GetString() : null;
            LlmStop stop = finish == "content_filter" ? LlmStop.Refusal
                : calls.Count > 0 ? LlmStop.ToolUse
                : finish == "length" ? LlmStop.MaxTokens
                : LlmStop.End;

            return new LlmResponse(text.Trim(), calls, stop);
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or IndexOutOfRangeException)
        {
            log.LogError(ex, "Unexpected OpenRouter response: {Body}", Truncate(json, 300));
            throw new LlmProviderException("OpenRouter returned an unexpected response.", ex);
        }
    }

    private static IReadOnlyDictionary<string, JsonElement> ParseArguments(string? args)
    {
        if (string.IsNullOrWhiteSpace(args)) return new Dictionary<string, JsonElement>();
        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(args)
                   ?? new Dictionary<string, JsonElement>();
        }
        catch (JsonException)
        {
            return new Dictionary<string, JsonElement>(); // the tool will report missing arguments
        }
    }

    private static JsonArray ToMessages(IReadOnlyList<ConversationItem> history, string systemPrompt)
    {
        var messages = new JsonArray { new JsonObject { ["role"] = "system", ["content"] = systemPrompt } };

        foreach (ConversationItem item in history)
        {
            switch (item)
            {
                case UserItem u:
                    messages.Add(new JsonObject { ["role"] = "user", ["content"] = u.Text });
                    break;

                case AssistantItem a:
                    var m = new JsonObject { ["role"] = "assistant", ["content"] = a.Text.Length > 0 ? a.Text : null };
                    if (a.ToolCalls.Count > 0)
                    {
                        var calls = new JsonArray();
                        foreach (ToolCall c in a.ToolCalls)
                            calls.Add(new JsonObject
                            {
                                ["id"] = c.Id,
                                ["type"] = "function",
                                ["function"] = new JsonObject
                                {
                                    ["name"] = c.Name,
                                    ["arguments"] = JsonSerializer.Serialize(c.Input),
                                },
                            });
                        m["tool_calls"] = calls;
                    }
                    messages.Add(m);
                    break;

                case ToolResultsItem r:
                    foreach (ToolResultItem x in r.Results)
                        messages.Add(new JsonObject
                        {
                            ["role"] = "tool",
                            ["tool_call_id"] = x.ToolCallId,
                            ["content"] = x.IsError ? "Error: " + x.Content : x.Content,
                        });
                    break;
            }
        }
        return messages;
    }

    private static JsonArray ToTools(IReadOnlyList<ToolDef> tools)
    {
        var arr = new JsonArray();
        foreach (ToolDef t in tools)
            arr.Add(new JsonObject
            {
                ["type"] = "function",
                ["function"] = new JsonObject
                {
                    ["name"] = t.Name,
                    ["description"] = t.Description,
                    ["parameters"] = JsonNode.Parse(t.InputSchema.GetRawText()),
                },
            });
        return arr;
    }

    private static string Truncate(string s, int max) =>
        s.Length <= max ? s : s[..max] + "...";
}
