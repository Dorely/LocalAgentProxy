# HTTP and continuation protocol

All endpoints bind to `http://127.0.0.1:17432` by default. Use a bearer application
key. Keys are application identities, not Claude credentials. Requests with an
Origin header are rejected. Request bodies are bounded (16 MiB by default).

## Supported parameter matrix

| Parameter | Support |
| --- | --- |
| `model` | Required nonempty identifier; passed unchanged. Only sonnet/opus/haiku treated as moving aliases. |
| `messages` | Required, 1..4096, ending in user or tool. Roles: system, developer, user, assistant, tool. |
| `messages[].content` | Text; arrays of text; user image_url parts containing PNG/JPEG/GIF/WebP base64 data URLs. Null allowed for assistant tool messages. |
| `messages[].name` | Preserved as imported historical metadata. |
| `tool_calls`, `tool_call_id` | Function calls with unique IDs, JSON-object arguments, and matching results. Historical calls must have results. |
| `tools` | Up to 128 function definitions, unique names matching `[a-zA-Z_][a-zA-Z0-9_-]{0,63}`. Object schemas forwarded to MCP, including `$defs`. |
| `tools[].function.strict` | false or omitted; true rejected. Caller validates arguments against its schema. No schema-constrained generation guarantee. |
| `tool_choice` | auto/default, none, required, or named function. Required/named with no matching tool is rejected. |
| `stream` | false/default or true, SSE chat-completion chunks. |
| `stream_options.include_usage` | Boolean; final usage chunk when streaming is enabled. |
| `parallel_tool_calls` | Boolean accepted. Public responses always serialize invocation delivery to one call per completion, even if native Claude issues a parallel batch. |
| `n` | Omitted or 1. |
| Image `detail` | Omitted or auto. Explicit low/high rejected. |
| All other parameters | Rejected, including temperature, top_p, seed, stop, max_tokens, max_completion_tokens, response_format, logprobs, modalities, audio, service_tier, store, user, metadata, reasoning_effort. |

Remote images are never fetched. Each image is limited to 5 MiB decoded and the
whole request must fit the body limit. Image bytes are sent through stdin, including
historical images in their original message order. Image validity beyond base64
and media-type checks is handled by Claude. No token cap is promised; generation
and output-memory deadlines are separate from output-token limits.

System/developer text is passed to a protected system-prompt file without changing
its contents. Multiple instruction messages are concatenated in order with two
newlines between them. Historical JSON includes their original roles. Earlier
conversation entries are explicitly quoted as imported evidence; images appear as
native image blocks associated with their historical entry. This is not native
assistant-history replay. Native Claude built-in identity/managed policies may
still apply; this proxy does not claim to remove vendor policy.

## Tool round trip

1. POST a normal chat request containing function definitions. The native CLI gets
   only that run's private MCP configuration and catalog.
2. An actual MCP `tools/call` creates a random `call_lap_...` identity. The MCP
   handler remains awaiting its result. The HTTP response has finish_reason
   `tool_calls`, with one function call. Raw CLI tool deltas never create IDs.
3. Execute the function in the application. Append the returned assistant message,
   then `{ "role": "tool", "tool_call_id": "...", "content": "actual result" }`.
   Repeat the model, instructions, and catalog and POST the full history normally.
4. The application key and ID locate the waiting invocation. The prior messages
   must match the expected history semantically (object key order and text-only
   content-array representation are normalized). A compatible result completes the
   actual MCP invocation in the same Claude process.
5. The next completion contains more text, another independently identified pending
   invocation, or the final answer. The process ends after final output.

At most one request owns a continuation. A repeated pending submission receives
409; ended/expired continuations receive 410 while their IDs are retained, and 409
after tombstone expiry or restart. Unknown/foreign IDs return 409 without revealing
another client's run. Duplicate history results and missing associations return
400. No retry automatically executes application code. Preserve the tool result in
the application if a network error makes delivery uncertain.

Changing compatible context/model/catalog/instructions retires the identified old
process and imports the authoritative request, including the supplied real result.
This may produce a newly requested tool operation; it is not automatic replay of
an earlier operation. Do not drop tool associations while returning a pending
result. When compaction drops every ID and ends with a user message, there is no
standard OpenAI field identifying the abandoned run; it expires by timeout.

`none` exposes no client tools. Required/forced choices restrict the catalog and
the completion is rejected if it returns without an admitted tool call. The CLI
has no equivalent hard forced-choice parameter; validation is explicit, rather
than claiming a requirement was met. For a final tool-result continuation normally
use auto; required means this next completion must call a tool again.

## Responses and failure behavior

Text responses use `chat.completion`; streams use `chat.completion.chunk` with
stable per-completion IDs, requested model IDs, and finish_reason stop/tool_calls.
Only validated successful CLI terminal output and a successful process exit permit
a final stop. Malformed/truncated output, unexpected tools, bad exits, quota errors,
missing results and timeouts fail. A stream already in progress emits an OpenAI
error envelope as SSE and closes without a successful finish or `[DONE]`.
Partial text already received remains valid partial output.

Usage reflects CLI-reported input/output token counts, with cache read/creation
included in prompt_tokens. Native message IDs prevent duplicate stream/assistant
accounting; final CLI usage is authoritative. Continuations report newly observed
usage since the preceding completion, not the whole session again. Interim tool
usage depends on what the CLI has reported by the callback and may be zero or
partial. There is no invented billable-token estimate.

`GET /v1/models` lists configured IDs and moving_alias metadata. Exact IDs are
accepted even if absent from that discovery list. `GET /health/live` is public;
`GET /health/ready` and `/v1/status` authenticate and check native subscription
login without a model generation. Status exposes operational counts and host PID,
not prompts, transcripts or account details. `/management/*` and `/internal/*` have
separate credentials and are not client API extensions.
