# Initial validation record

Date: 2026-09-27. Platform: native Windows x64, .NET SDK 10.0.100.
Native Claude Code: 2.1.283. Authentication: existing Claude Pro subscription,
through Claude Code's own login. No credential extraction or API billing fallback.

## Live budget: 5 of 5 generations used

| Generation | Exercise | Observed result |
| --- | --- | --- |
| 1 | Minimal official-MCP continuation feasibility proof | Claude invoked read_fixture; actual invocation remained open with native child alive. |
| 2 | Deliver freshly generated caller nonce to that waiting invocation | Same native process produced an answer containing that nonce, emitted terminal output and exited. |
| 3 | Full public `/v1/chat/completions` request with read_fixture | Received a proxy-issued tool ID; authenticated status showed one continuation and no occupied generation slot. |
| 4 | Standard appended assistant + role:tool messages, containing a new caller nonce | Received final text with the actual result; continuation count returned to zero. |
| 5 | Inline base64 PNG of a solid red 128×128 square | Claude identified the dominant color as red. |

The first proof preceded API packaging work. The temporary proof entry point was
removed once the implementation used the production bridge. The reproducible
public API exercises are in `scripts/Invoke-LiveValidation.ps1`, explicitly opt-in.
Its temporary application key was revoked. All proxy hosts were stopped after
validation and the owned runs directory was empty. Readiness/auth-status and
management checks do not generate model responses.

No additional live generations are authorized by this record. Final refinements
to cancellation, resource bounds, exact-ID validation and recovery were checked
with deterministic tests, without exceeding the live budget.

## Deterministic automated validation

The Release suite currently contains **37 passing test cases**, using fake CLI
processes and protocol fixtures. It covers:

- Incremental UTF-8 across single-byte boundaries (including emoji), malformed
  UTF-8, malformed/truncated JSON, output bounds, unexpected exits, missing terminal
  output, provider errors, unexpected tools, and partial SSE failure without DONE.
- Identical tool names and arguments producing independent IDs/results in one
  native process; duplicate submissions, cross-client rejection, expired IDs, and
  actual held invocations that release inference capacity.
- Changed instructions, model and catalogs, imported roles/order/tool associations,
  caller-driven compaction, historical/native image blocks, and explicit required
  tool-choice failure.
- Round-robin scheduling, bounded queues, cancellation, generation deadlines,
  abandoned continuation cleanup, shutdown, and Windows descendant-process cleanup.
- A 100,000-character Unicode request through stdin, inline image validation,
  unsupported parameters, authentication, browser-origin denial, and absence of
  prompt/key canaries from retained local files after cancellation.
- OpenAI .NET 2.8.0 ordinary text, streaming and standard tool continuation;
  Microsoft.Extensions.AI 10.4.1 with the OpenAI adapter 10.3.0. The resolved
  Microsoft.Extensions.AI.Abstractions version is 10.8.3 due to the MCP SDK 2.2.0
  dependency. No Lorekeeper project reference is used.

Release build and tests succeeded; the win-x64 framework-dependent publish check
succeeded. The published executable's start, authenticated ready/status, and stop
commands were exercised without inference. NuGet's vulnerability query reported
no known vulnerable dependencies from the configured advisory sources at this
validation time. Source/diff and sensitive-file review completed before initial
publication; runtime files and credentials are excluded from the source tree.

## Limits of the evidence

This does not verify every Lorekeeper UI workflow, any other OS, start-at-login on
an actual login cycle, all Claude models or exact dated model IDs, every client
library, every image type, or managed enterprise configurations. Tool duplicate
handling and adverse runtime behavior use deterministic fixtures; the live tool
proof uses a single harmless tool invocation. Live public-API checks were
non-streaming; streaming is covered by deterministic protocol/client tests.

Required/forced tool choice is validated after generation, not a native provider
forced-choice guarantee. Historical messages are imported context. No source,
credential, or native transcript manipulation establishes subscription permission.
Technical success is not a guarantee of Anthropic approval or account safety.

## Primary references consulted

- [Claude CLI reference](https://code.claude.com/docs/en/cli-reference)
- [Headless/programmatic operation](https://code.claude.com/docs/en/headless)
- [Claude MCP support](https://code.claude.com/docs/en/mcp)
- [Official C# MCP SDK](https://github.com/modelcontextprotocol/csharp-sdk)
- [Claude Code legal and compliance](https://code.claude.com/docs/en/legal-and-compliance)
- [Agent SDK with a Claude plan](https://support.claude.com/en/articles/15036540-use-the-claude-agent-sdk-with-your-claude-plan)
