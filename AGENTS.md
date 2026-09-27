# LocalAgentProxy agent instructions

Read README.md and docs/architecture.md before changing this repository. This is
an independent project; no Lorekeeper checkout is required. Use .NET 10 and the
official MCP C# SDK. Keep translation, scheduling, continuation ownership, and
child-process supervision separate.

Never extract Claude credentials, use private provider endpoints, fabricate tool
results, or silently fall back to API billing. Logs must exclude prompts, results,
credentials and raw CLI diagnostics. Native CLI diagnostics have their own policy.

Deterministic automated tests are authorized and required for behavior changes.
Live Claude generations need an explicit user budget; record each generation,
including continuations. Do not turn live validation into an automatic test.

Before publishing, run Release build/tests and the Windows publish check; inspect
the entire diff and staged files for sensitive material. Commit completed changes
with focused messages. Keep protocol and validation documents accurate; never
claim technical success establishes vendor approval.
