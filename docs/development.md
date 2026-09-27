# Development and handoff

Open this repository directly in your editor/agent. It has its own Git history,
solution, instructions, and dependencies; work does not depend on Lorekeeper.

```powershell
dotnet restore --locked-mode
dotnet build -c Release --no-restore
dotnet test -c Release --no-build
dotnet publish src/LocalAgentProxy -c Release -r win-x64 --self-contained false -o artifacts/win-x64
dotnet run --project src/LocalAgentProxy -- serve
```

Use an installed native Windows Claude CLI signed in through its own `auth login`.
Never inspect or copy native credential files. Application keys are created with
`clients add NAME`; each application should have a separate key. Configuration is
created at `%LOCALAPPDATA%\LocalAgentProxy\config.json`. Stop before editing it.
Only the current Windows user has an ACL grant on the local state directory.

The deterministic tests launch `tests/FakeCli` in place of Claude. They do not run
inference. The fake executable accepts production CLI arguments and emits protocol
fixtures; its private invocation requests exercise the real HTTP broker. Tests
allocate temporary user-only directories and loopback ports, and reclaim their
processes. Windows job-object checks require Windows.

The OpenAI package versions match the Lorekeeper client versions at initial
implementation without referencing that repository. The MCP SDK brings a newer
Microsoft.Extensions.AI.Abstractions transitive version; package lock files record
the resolved graph. Keep dependency updates explicit and review changed locks.

## Live validation

Never run live validation automatically. Obtain a fresh explicit user budget first.
The initial implementation used all five authorized generations; none remain from
that authorization. See the validation record.

For a separately authorized three-generation check, start an idle proxy yourself,
then run `scripts/Invoke-LiveValidation.ps1 -AuthorizedGenerations 3`. Requests use
the exact `claude-opus-5-5` ID by default (`-Model` can override it). The script
creates a temporary application key, exercises a real tool/result round trip and
an inline image, then revokes its key. It does not start or stop a potentially user-owned
host. Stop the host you started when finished. Calls are intentionally short and
tools are harmless. Record actual failures and usage; do not retry outside the
budget. A successful tool round trip requires two model generations.

## Publishing source

Run the Release build/tests and Windows publish check on the final source. Inspect
all tracked and staged files, dependency notices, and diff whitespace. Search for
credentials, private host paths, account IDs, prompt/transcript captures, and
runtime state. `bin`, `obj`, and `artifacts` are ignored. Only public fixture text
belongs in tests. Commit the verified source before pushing. The GitHub workflow
only builds, tests fake processes, and checks publish; it does not sign in or spend
subscription quota.
