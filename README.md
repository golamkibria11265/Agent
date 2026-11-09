# PersonalAgent (.NET)

```
Chat window (browser)  ->  Agent API  ->  LLM (Claude)
                                      \->  MCP client  ->  MCP server (http://127.0.0.1:5080/mcp)
```

| Project | Role | Dev URL |
|---|---|---|
| `src/PersonalAgent.Chat` | Chat window: static page, calls the agent over HTTP | http://localhost:5100 |
| `src/PersonalAgent.Agent` | Agent: ASP.NET Core API, runs the loop, talks to Claude and MCP | http://localhost:5200 |

Agent internals: `AgentService` (loop) -> `LlmClient` (Anthropic SDK) and `McpToolClient` (MCP over HTTP); `SessionStore` keeps chat history in memory.

## Run (Visual Studio)
1. Put your key in `src/PersonalAgent.Agent/appsettings.Development.json` (`Anthropic:ApiKey`; this file is git-ignored).
2. Open `PersonalAgent.slnx`. Right-click the solution > Configure Startup Projects > Multiple startup projects: set `PersonalAgent.Agent` and `PersonalAgent.Chat` to Start.
3. Start your MCP server on port 5080, then press F5. The chat page opens.

## Run (command line)
```bash
# key: src/PersonalAgent.Agent/appsettings.Development.json -> Anthropic:ApiKey
dotnet run --project src/PersonalAgent.Agent
dotnet run --project src/PersonalAgent.Chat      # second terminal
```

## Choosing the LLM provider
Set `"Llm": { "Provider": "Anthropic" }` or `"OpenRouter"` in `appsettings.Development.json` / `appsettings.Production.json`, fill that provider's `ApiKey` and `Model`, and restart. Code: `Llm/ILlmClient.cs` (contract), `Llm/AnthropicLlmClient.cs`, `Llm/OpenRouterLlmClient.cs`; the agent loop is provider-neutral.
- If the chosen provider has no key or model, the app still starts and `/api/chat` returns 503 until it is set.
- Unknown provider names stop the app at startup with a clear message.
- OpenRouter free models (ids ending `:free`) are for learning with dummy data only: tool calling is often unreliable, limits are low, and providers may log prompts. Check openrouter.ai/models for a free model that lists tool support. Never send customer or payment data.

## Environments (which appsettings file is used)
ASP.NET Core picks the file from `ASPNETCORE_ENVIRONMENT`. Later sources win: `appsettings.json` -> `appsettings.{Environment}.json` -> environment variables.

| Environment | How it is selected | File | Typical contents |
|---|---|---|---|
| Development | Visual Studio / `dotnet run` launch profile | `appsettings.Development.json` (Agent one is git-ignored) | dev key, local MCP `http://127.0.0.1:5080/mcp`, local CORS |
| Production | Default when unset; the Dockerfile sets it explicitly | `appsettings.Production.json` | prod MCP URL, prod chat origin, quieter logging |

- Fill the `[PLACEHOLDER]` values in `appsettings.Production.json` on the server. The key placeholder is rejected at startup, so a missing key fails fast.
- Do not commit a real production key or bake it into an image. Prefer the `Anthropic__ApiKey` environment variable or a secret store on the server; it overrides the file.
- To test Production locally: `ASPNETCORE_ENVIRONMENT=Production dotnet run --no-launch-profile --project src/PersonalAgent.Agent`

## Agent API
- `POST /api/chat` body `{ "sessionId": null | "<id>", "message": "..." }` -> `{ sessionId, reply, toolsUsed }`
- `GET /health`

## Configuration (appsettings.json or environment variables, `__` as separator)
| Setting | Default |
|---|---|
| `Llm__Provider` | `Anthropic` or `OpenRouter`: chooses which LLM client is used |
| `Anthropic__ApiKey`, `Anthropic__Model` | used when Provider is `Anthropic` (model default `claude-opus-5-5`) |
| `OpenRouter__ApiKey`, `OpenRouter__Model` | used when Provider is `OpenRouter` (model must support tool calling) |
| `OpenRouter__BaseUrl`, `OpenRouter__MaxTokens` | default `https://openrouter.ai/api/v1`, `4096` |
| `Agent__AllowWriteTools` | `false` (only `_list_`, `_get_`, `_search_` tools are exposed) |
| `Agent__MaxTurns` | `10` |
| `Mcp__Endpoint` | `http://127.0.0.1:5080/mcp` |
| `Cors__AllowedOrigins__0` | `http://localhost:5100` (set to the chat site URL in prod) |
| `Agent__BaseUrl` (Chat project) | `http://localhost:5200` (set to the agent URL in prod) |

## Already built in for production
Fail-fast key check, read-only tool policy, per-IP rate limit, 64 KB request cap and message length cap, CORS allow-list, health endpoint, per-call MCP timeout and auto-reconnect, history rollback on failure, error mapping (400/429/502/503), no user text or tool arguments in logs, chat output rendered as text (no HTML injection), Dockerfile (non-root).

## Before real production use
1. Authentication: the agent API is currently open to anyone who can reach it. Put it behind your identity provider or an API gateway.
2. HTTPS everywhere (reverse proxy or Kestrel certificate).
3. Replace the in-memory `SessionStore` with Redis or a database (needed for more than one instance and restarts), and set retention/deletion rules for chat history.
4. Secrets from a secret manager, not files. Never send card numbers, tokens or merchant credentials to the model; add redaction before enabling any payment-related MCP tool.
5. Enabling write tools (`AllowWriteTools=true`) needs a human approval step first.
6. Compliance review (Bangladesh Bank, PCI-DSS scope, data residency, vendor risk for the external LLM API) before real customer data is used.
7. Add tests, structured tracing (OpenTelemetry), and streaming responses.

Docker (agent): `docker build -f src/PersonalAgent.Agent/Dockerfile -t personal-agent .`
