# Architecture

The ASP.NET Core host authenticates application keys and accepts chat requests.
Protocol translation validates supported inputs and imports history as labeled
context. The conversation broker owns opaque invocation identities and continuation
compatibility. A fair scheduler owns generation capacity. A process supervisor owns
each native CLI and its Windows job object.

The same executable has a private `bridge` mode using the official MCP SDK over
stdio. Claude starts that child from a request-specific MCP configuration. The
bridge retrieves its catalog and forwards actual invocations over authenticated
loopback HTTP to the broker. An invocation stays open until the application sends
its actual result through a standard OpenAI tool message. No tool operation runs
inside the proxy. Only admitted MCP callbacks create outward tool calls; raw model
tool-call deltas are not used for correlation.

All runtime state is local to the current user, outside this repository. Processes
are disposable; pending continuations do not survive proxy restart. Historical
messages are imported evidence, not native CLI replay. Technical interoperability
does not establish vendor permission for a particular subscription use.

## Ownership and data flow

| Area | Owning source |
| --- | --- |
| HTTP authentication, OpenAI responses/SSE, readiness and management routes | `Api.cs` |
| Request parameter validation, history representation, model/tool choices, images | `Protocol.cs` |
| Native stream decoding, terminal validation and reported token accounting | `CliProtocol.cs` |
| Process arguments, stdin, output bounds, isolated files, environment and cleanup | `CliRunner.cs` |
| Windows kill-on-close process containment | `WindowsJob.cs` |
| Pending invocation IDs, context compatibility, deadlines, result delivery and retirement | `Conversation.cs` |
| Single active generation and per-application round-robin queue | `FairScheduler.cs` |
| Hashed application keys, configuration, state ACL and native login readiness | `LocalStore.cs` |
| Foreground/background commands and optional current-user startup | `Commands.cs` |
| Official MCP SDK stdio transport and private HTTP forwarding | `Bridge.cs` |

The HTTP client supplies the complete authoritative request. The broker validates
the exact requested model without fallback. New configurations advertise
`claude-opus-5-5` first, followed by the optional moving CLI aliases; discovery is
not a model override or an account entitlement check. The broker validates
any pending continuation against its original application identity and expected
history. New runs acquire a fair generation lease before launching. On a real MCP
callback, the broker assigns its identity and releases the lease; the CLI is
awaiting the application's result. A submission reacquires capacity before
delivering that result. If other actual parallel calls are still unresolved, the
lease is released again. No inference capacity is reserved while a client thinks
or performs its own operation.

An actual tool callback, rather than a predicted tool name/argument pair in the
model stream, is the authority for issuing a tool call. Identical native calls
therefore remain independent. The public stream suppresses native tool deltas and
emits the broker's complete function arguments. Text can be delivered as available;
native-output reading and MCP callbacks are separate asynchronous streams, so a
near-simultaneous text fragment may appear in the following completion.

The channel between each CLI and HTTP response is bounded to 128 events; CLI lines
are bounded to 8 MiB, aggregate output to 16 MiB characters by default. Input and
tool results are bounded by HTTP request size. Each catalog admits up to 128
definitions, independently of the maximum of 64 actual invocations per run.
Admission bounds conversations and reserves space for invocation tombstones;
retirement drops all transcript/argument references and retains only ownership
metadata for one continuation timeout. A one-second sweeper enforces generation
and continuation deadlines; exact failure timing can differ by that interval.

## Native process boundary

Each CLI starts in a generated user-only `runs/<opaque-id>` directory. System text
and MCP configuration use protected files; large user input goes through stdin.
The native command uses restricted mode, no built-in tools, a strict MCP config,
disabled slash commands/Chrome/hooks, empty setting sources, and no transcript
persistence. Only that request's bridge tools are allowed without permission
prompts. Unexpected tool events fail. Background MCP execution is disabled so a
tool handler cannot return an invented background/placeholder result.

The native CLI owns subscription credentials. The proxy only invokes `auth status`
and retains its boolean readiness outcome. Child environment preparation removes
alternative Anthropic credentials, billing selectors and inherited Claude/MCP
overrides. There is no API key backend or fallback implementation. Managed vendor
policies still apply; this is not a sandbox for a malicious installed CLI or other
software already running as the same Windows user.

Before it spawns children, the production host joins a kill-on-close Windows job;
descendants inherit containment immediately. Each conversation also has its own job
for targeted cleanup. Cancellation kills the process tree, drains/ends stdout, and
awaits termination before releasing inference capacity. Native stderr is consumed
without retaining or logging it. Prompt/config files are deleted after cleanup.
An abrupt host kill is still contained by the host job, but can leave run files;
see the recovery policy below. A graceful stop closes active/pending conversations.

## Storage and operational boundaries

Configuration, key hashes and a separate local management key are under
`%LOCALAPPDATA%/LocalAgentProxy`, restricted by ACL to the current user. Writes to
the client registry use a lock and atomic replacement. Key revocation blocks new
HTTP submissions immediately; already running generations complete or expire.
Logs contain no request payloads, native diagnostics or account identifiers. The
native CLI is a separate product and may maintain its own diagnostic data under
its vendor-defined policies, despite session persistence being disabled.

Restart discards continuation state. Run files from an abrupt prior termination
are reclaimed at the next serve startup while holding the exclusive host lock.
No private transcript is read, modified, or used for recovery. The management
credential is not an application key, and per-run bridge credentials cannot access
other runs or application routes. The server is not designed for remote binding,
multi-user hosting, or browser access.

## Verification references

The Windows test suite covers real fake-process supervision, HTTP translation,
official OpenAI clients, continuation isolation, cleanup, and protocol fixtures.
Live MCP interoperability and base64 images were additionally exercised within an
explicit five-generation subscription budget. See `docs/validation.md` for the
precise tested scope, including the separately authorized Opus 5.5 Outline and
Editor browser exercises. `docs/protocol.md` is the supported client contract;
broader UI compatibility is not implied by those focused exercises.
