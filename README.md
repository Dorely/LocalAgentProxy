# LocalAgentProxy

A local OpenAI-compatible chat API backed by your installed, unmodified Claude
Code CLI. Windows-first, C#/.NET 10, MIT licensed. Each application supplies its
own prompts, images, and function tools. It executes its own tools through normal
OpenAI `tool_calls` and `role: tool` messages; the proxy holds Claude's actual MCP
invocation open until the real result arrives.

This is an independent project. Lorekeeper, or another OpenAI chat client, connects
using a base URL, application key, and model. No Lorekeeper checkout, MCP server,
custom session header, or surface-specific integration is required.

## Requirements and setup

- Native Windows and the .NET 10 SDK for building (ASP.NET Core 10 runtime for a
  framework-dependent published build).
- Native `claude.exe`, tested with **Claude Code 2.1.283**. It must support
  `--restricted` and `--permission-prompts none`; older releases are unsupported.
- Sign in directly with `claude auth login` using your Claude plan. The proxy never
  performs login, extracts credentials, or falls back to paid API billing.

```powershell
git clone https://github.com/Dorely/LocalAgentProxy.git
cd LocalAgentProxy
dotnet build -c Release
dotnet test -c Release
dotnet publish src/LocalAgentProxy -c Release -r win-x64 --self-contained false -o artifacts/win-x64
./artifacts/win-x64/LocalAgentProxy.exe clients add Lorekeeper
./artifacts/win-x64/LocalAgentProxy.exe start
```

Save the key shown by `clients add`; only its hash is stored. Configure the client:

| Setting | Value |
| --- | --- |
| Base URL | `http://127.0.0.1:17432/v1` |
| API key | The application's generated key |
| Model | `claude-opus-5-5` (Opus 5.5) |

Disable client sampling/token-limit options that the CLI cannot honor. Requests
containing unsupported parameters fail explicitly. See the
[parameter matrix](docs/protocol.md) before configuring a client.

The discovery list includes Opus 5.5 first. Other supported exact model IDs and
the moving `sonnet`, `opus`, and `haiku` aliases remain available when explicitly
requested. The proxy always uses the request's model; it has no implicit model
fallback. Existing installations can update `Models` in their local `config.json`
while stopped to change the discovery list.

## Management

```powershell
LocalAgentProxy.exe serve                 # foreground; Ctrl+C stops it
LocalAgentProxy.exe start                 # hidden background host
LocalAgentProxy.exe status                # authenticated readiness and queue state
LocalAgentProxy.exe stop
LocalAgentProxy.exe clients add AnotherApp
LocalAgentProxy.exe clients list
LocalAgentProxy.exe clients remove CLIENT_ID
LocalAgentProxy.exe login-start enable    # optional; use from a stable installed path
LocalAgentProxy.exe login-start disable
```

The server runs under your Windows account, independently of client applications.
Install a published directory at a stable location before enabling login startup.
Do not delete or overwrite a running installation. Configuration and hashed keys
live in `%LOCALAPPDATA%\LocalAgentProxy`; the directory's ACL is restricted to the
current user. `config.json` is created on first use. Change it while stopped and
restart. Defaults include a 32-entry queue, 64 live/queued conversations, a
120-second generation deadline, and a 600-second continuation deadline. Set
`ClaudePath` if your native CLI is installed elsewhere. Only `127.0.0.1` HTTP binds
are accepted. Browser-origin requests are rejected.

`GET /health/live` is public. `/health/ready`, `/v1/status`, `/v1/models`, and chat
requests require an application bearer key. Management uses a separate private
credential automatically read by the management commands.

## Compatibility and limits

- Streaming and non-streaming text, inline base64 images, caller-defined function
  tools, real tool-result continuation, and available CLI token usage.
- One tool call per API completion; each invocation has an independent opaque ID.
  Concurrent native calls are queued. Applications must return the original
  assistant message and a matching tool result in their next request.
- Arbitrary earlier messages are **imported as labeled context**, not native
  historical-message replay. Changing model, instructions, tools, or preceding
  context ends the old continuation and starts from supplied history. If compaction
  drops every proxy-issued ID, the requests cannot be linked; the old run expires.
- Exact model IDs are passed unchanged and a different resolved ID fails. `sonnet`,
  `opus`, and `haiku` are moving CLI aliases. `/v1/models` is a configurable discovery
  list, not proof your subscription grants access to every model.
- Sampling controls, output-token caps, constrained JSON generation, remote image
  fetching, Responses, Anthropic Messages, embeddings, and image generation are
  unsupported. No application operation executes inside the proxy.
- The native CLI, account quota, managed enterprise policy, and model availability
  remain external dependencies. Pending continuations are in memory and do not
  survive restart. Lost responses are not automatically replayed.

The OpenAI .NET **2.8.0**, Microsoft.Extensions.AI **10.4.1**, and
Microsoft.Extensions.AI.OpenAI **10.3.0** client packages are exercised by the
deterministic compatibility suite. See [validation](docs/validation.md) for exact
evidence and transitive dependency details. A limited Lorekeeper browser workflow
was exercised with Opus 5.5: a two-chapter outline and a saved 156-word opening.
The Editor's larger catalog is supported up to 128 tool definitions. Lorekeeper's
vision readiness probe currently fails because it supplies unsupported sampling
and token-limit parameters; this connection is verified for chat only.

## Subscription policy

Technical success is **not a guarantee of Anthropic approval or account safety**.
This project does not impersonate Claude Code, collect login credentials, modify
the installed CLI, or call private provider endpoints. Subscription terms and
vendor enforcement can change. Consult Anthropic's current
[Claude Code legal guidance](https://code.claude.com/docs/en/legal-and-compliance)
and [plan/SDK guidance](https://support.claude.com/en/articles/15036540-use-the-claude-agent-sdk-with-your-claude-plan)
for your use. This project is not affiliated with Anthropic.

## Development

Read [AGENTS.md](AGENTS.md), [architecture](docs/architecture.md),
[protocol](docs/protocol.md), and [development](docs/development.md). Automated tests
use fake CLI executables and never access a Claude account. Live checks require an
explicit generation budget. Dependency licensing is listed in
[THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).
