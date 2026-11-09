using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace PersonalAgent.Agent;

/// <summary>
/// MCP client (singleton). Connects lazily so the agent can start even if the MCP server is down,
/// and reconnects automatically after a failure.
/// </summary>
public sealed class McpToolClient(IOptions<McpOptions> options, ILogger<McpToolClient> log) : IAsyncDisposable
{
    private readonly McpOptions _opt = options.Value;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private McpClient? _client;
    private IList<McpClientTool> _tools = [];

    public async Task<IList<McpClientTool>> GetToolsAsync(CancellationToken ct)
    {
        await EnsureConnectedAsync(ct);
        return _tools;
    }

    public async Task<(string Text, bool IsError)> CallAsync(
        string name, IReadOnlyDictionary<string, JsonElement> input, CancellationToken ct)
    {
        try
        {
            await EnsureConnectedAsync(ct);

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(_opt.CallTimeoutSeconds));

            var args = input.ToDictionary(kv => kv.Key, kv => (object?)kv.Value);
            CallToolResult result = await _client!.CallToolAsync(name, args, cancellationToken: cts.Token);

            var sb = new StringBuilder();
            foreach (ContentBlock block in result.Content)
                if (block is TextContentBlock t) sb.AppendLine(t.Text);

            string text = sb.Length > 0 ? sb.ToString().TrimEnd() : "(no text content)";
            if (text.Length > _opt.MaxResultChars) text = text[.._opt.MaxResultChars] + "\n[truncated]";
            return (text, result.IsError ?? false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw; // the caller went away
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "MCP tool {Tool} failed", name);
            await ResetAsync(); // force a reconnect on the next call
            return ($"Tool '{name}' failed or timed out.", true);
        }
    }

    private async Task EnsureConnectedAsync(CancellationToken ct)
    {
        if (_client is not null) return;
        await _gate.WaitAsync(ct);
        try
        {
            if (_client is not null) return;
            var transport = new HttpClientTransport(new HttpClientTransportOptions
            {
                Endpoint = new Uri(_opt.Endpoint),
                Name = "personal-agent",
            });
            var client = await McpClient.CreateAsync(transport, cancellationToken: ct);
            _tools = await client.ListToolsAsync(cancellationToken: ct);
            _client = client;
            log.LogInformation("Connected to MCP server {Endpoint} with {Count} tools", _opt.Endpoint, _tools.Count);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new McpUnavailableException($"Cannot reach MCP server at {_opt.Endpoint}.", ex);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task ResetAsync()
    {
        await _gate.WaitAsync();
        try
        {
            if (_client is not null) await _client.DisposeAsync();
            _client = null;
        }
        catch { /* best effort */ }
        finally { _gate.Release(); }
    }

    public async ValueTask DisposeAsync()
    {
        if (_client is not null) await _client.DisposeAsync();
    }
}
