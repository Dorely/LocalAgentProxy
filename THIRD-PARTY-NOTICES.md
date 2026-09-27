# Third-party components

LocalAgentProxy source is MIT licensed. Dependencies retain their own licenses.
The installed Claude Code CLI is neither bundled nor relicensed by this project.

| Component | License | Source |
| --- | --- | --- |
| ModelContextProtocol / ModelContextProtocol.Core 2.2.0 | Apache-2.0 | https://github.com/modelcontextprotocol/csharp-sdk |
| .NET / ASP.NET Core and Microsoft.Extensions packages | MIT | https://github.com/dotnet |
| OpenAI .NET (test dependency) | MIT | https://github.com/openai/openai-dotnet |
| xUnit.net (test dependency) | Apache-2.0 | https://github.com/xunit/xunit |
| Microsoft.NET.Test.Sdk (test dependency) | MIT | https://github.com/microsoft/vstest |
| coverlet (test dependency) | MIT | https://github.com/coverlet-coverage/coverlet |

Exact direct and transitive versions are recorded in `packages.lock.json` files.
When redistributing a binary publish directory, include applicable dependency
license texts/notices from the NuGet packages and the runtime distribution if
publishing self-contained. The source repository does not distribute Claude or
credential-bearing native CLI state.
