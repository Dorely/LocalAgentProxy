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

The initial Release suite contained **37 passing test cases**, using fake CLI
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
proof used a single harmless tool invocation. The initial live public-API checks
were non-streaming. The subsequent Lorekeeper exercise below additionally used
the application's streaming tool loop.

Required/forced tool choice is validated after generation, not a native provider
forced-choice guarantee. Historical messages are imported context. No source,
credential, or native transcript manipulation establishes subscription permission.
Technical success is not a guarantee of Anthropic approval or account safety.

## Lorekeeper integration: Opus 5.5

Separately authorized on 2026-09-27: configure the provider in the app, create a
small outline, and perform limited chapter writing. The user then selected Opus
5.5 for the integration. The exact `claude-opus-5-5` ID was used, with provider
default thinking and no output-token cap. The proxy discovery list, setup example
and opt-in live script now prefer that exact ID; other explicitly requested models
remain supported without automatic substitution.

Lorekeeper 43.6.0, source commit `38d49d5961bc3078108999774687dbaf2725c649`, ran
with its development Electron launch profile. Native window capture/input failed
in the automation helper, so the actual app workflows were exercised in a browser
against the same running host. This establishes browser integration, not successful
native Electron interaction or packaged-app verification. No Lorekeeper source
change was needed.

| Exercise | Observed result |
| --- | --- |
| Dedicated application key and custom provider setup | Connection saved through Lorekeeper's UI; exact Opus 5.5 chat readiness passed. Existing global default was preserved. |
| Vision readiness probe | Rejected with HTTP 400 before inference. Lorekeeper supplies `temperature: 0` and `max_tokens`; the proxy correctly rejects these unsupported controls. Saved connection is chat-ready, not vision-ready. |
| Outline chat | Created one act, The Lost Letter, and two ordered chapters, The Bottle and The Delivery, each with a synopsis. Four real tool calls completed: update_book_brief, create_act, create_chapter twice, followed by a final answer. |
| Initial Editor request | Rejected before inference or manuscript mutation because its catalog exceeded the original 64-definition limit. |
| Editor retry after catalog fix | apply_manuscript_operations saved three paragraphs; read_manuscript returned the actual saved result; final answer followed. UI showed 156 words, revision 1, saved. |
| Persistence and scope | Navigated to The Delivery and confirmed zero words, then reopened The Bottle and confirmed the saved three paragraphs. UI showed no uncheckpointed changes. |

The successful workflow comprised one readiness completion, five Outline API
completion round trips (four tool deliveries plus final text), and three Editor
round trips (apply, readback, final text). These are nine public API completion
round trips, including tool continuations; parallel native tool batches are not
counted as independently observed internal model turns. The two rejected request
types above did not reach inference. No additional image generation, research,
contest, revision-worker, or other surface exercise was performed.

Catalog admission now accepts up to 128 definitions; the separate per-run limit
of 64 actual invocations remains unchanged. A deterministic boundary regression
preserves all 128 schemas and rejects a 129th definition. Final Release build,
**38 tests**, and win-x64 publish passed. Operational status returned to zero
conversations and no active generation after each successful assistant turn.
The test browser, Lorekeeper host/Electron tree, and proxy were stopped afterward;
ports 1455 and 17432 had no listeners and the proxy's owned run directory was empty.
The dedicated test project and provider remain available in the development app.
Screenshots and host diagnostics remain local ignored artifacts; no key or
transcript was added to the public repository.

## Primary references consulted

- [Claude CLI reference](https://code.claude.com/docs/en/cli-reference)
- [Opus 5.5 model ID](https://platform.claude.com/docs/en/models/opus-5-5/overview)
- [Headless/programmatic operation](https://code.claude.com/docs/en/headless)
- [Claude MCP support](https://code.claude.com/docs/en/mcp)
- [Official C# MCP SDK](https://github.com/modelcontextprotocol/csharp-sdk)
- [Claude Code legal and compliance](https://code.claude.com/docs/en/legal-and-compliance)
- [Agent SDK with a Claude plan](https://support.claude.com/en/articles/15036540-use-the-claude-agent-sdk-with-your-claude-plan)
