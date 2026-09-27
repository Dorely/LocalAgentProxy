using System.Diagnostics;
using System.Text.Json;
using System.Text;

namespace LocalAgentProxy;

public interface ICliRunner
{
    Task RunAsync(Conversation conversation, CancellationToken ct);
}

public sealed class CliRunner(ProxyOptions options, LocalStore store) : ICliRunner
{
    public async Task RunAsync(Conversation conversation, CancellationToken ct)
    {
        var dir = Path.Combine(store.Root, "runs", conversation.Id);
        Directory.CreateDirectory(dir);
        using var job = OperatingSystem.IsWindows() ? new WindowsJob() : null;
        using var process = new Process();
        try
        {
            var config = Path.Combine(dir, "mcp.json");
            var system = Path.Combine(dir, "system.txt");
            var settings = Path.Combine(dir, "settings.json");
            await File.WriteAllTextAsync(system, conversation.Request.System, new UTF8Encoding(false), ct);
            await File.WriteAllTextAsync(settings, "{\"disableAllHooks\":true,\"enabledPlugins\":{},\"autoMemoryEnabled\":false}", ct);
            await File.WriteAllTextAsync(config, JsonSerializer.Serialize(new { mcpServers = new { bridge = new {
                command = Path.Combine(AppContext.BaseDirectory, "LocalAgentProxy.exe"), args = new[] { "bridge" }, env = new Dictionary<string,string> {
                    ["LOCAL_AGENT_BRIDGE_URL"] = options.Url + "/internal/" + conversation.Id + "/",
                    ["LOCAL_AGENT_BRIDGE_TOKEN"] = conversation.BridgeToken }
            }}}), ct);
            var start = new ProcessStartInfo(options.ClaudePath)
            {
                WorkingDirectory = dir, UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
                StandardInputEncoding = new UTF8Encoding(false), StandardOutputEncoding = new UTF8Encoding(false, true)
            };
            foreach (var arg in new[] { "-p", "--model", conversation.Request.Model, "--input-format", "stream-json", "--output-format", "stream-json", "--verbose", "--include-partial-messages", "--no-session-persistence", "--restricted", "--setting-sources", "", "--settings", settings, "--disable-slash-commands", "--no-chrome", "--tools", "", "--disallowedTools", "EndConversation", "--permission-mode", "dontAsk", "--permission-prompts", "none", "--strict-mcp-config", "--mcp-config", config, "--system-prompt-file", system }) start.ArgumentList.Add(arg);
            if (conversation.Request.Tools.Count > 0)
            {
                start.ArgumentList.Add("--allowedTools");
                start.ArgumentList.Add(string.Join(',', conversation.Request.Tools.Select(t => "mcp__bridge__" + t.Name)));
            }
            PrepareEnvironment(start);
            process.StartInfo = start;
            if (!process.Start()) throw CliProtocol.Failure("Unable to start Claude.");
            conversation.ProcessId = process.Id;
            job?.Assign(process);
            using var registration = ct.Register(() => { try { if (!process.HasExited) process.Kill(true); } catch (InvalidOperationException) { } });
            var drain = DrainAsync(process.StandardError, ct);
            await process.StandardInput.WriteLineAsync(conversation.Request.InputFrame().ToJsonString().AsMemory(), ct);
            process.StandardInput.Close();
            var decoder = new CliProtocol(conversation.Request.Tools.Select(t => t.Name));
            var count = 0L;
            await foreach (var line in CliProtocol.LinesAsync(process.StandardOutput.BaseStream, ct: ct))
            {
                count += line.Length;
                if (count > options.MaxOutputCharacters) throw CliProtocol.Failure("CLI output exceeded configured bounds.");
                var evt = decoder.Accept(line);
                if (decoder.ResolvedModel is { } resolved && conversation.Request.Model is not ("sonnet" or "opus" or "haiku")
                    && resolved != conversation.Request.Model)
                    throw CliProtocol.Failure("Claude resolved a different model than the requested exact ID.");
                conversation.Usage = decoder.Usage;
                if (evt is not null) await conversation.EmitAsync(evt, ct);
            }
            await process.WaitForExitAsync(ct);
            await drain;
            if (process.ExitCode != 0 || !decoder.HasResult) throw CliProtocol.Failure("Claude exited without a successful terminal result.");
            if (conversation.Request.RequireTool && !conversation.ToolWasCalled) throw CliProtocol.Failure("Required tool choice was not satisfied.");
            await conversation.EmitAsync(new RunEvent(Finished: true, Usage: decoder.Usage), ct);
        }
        finally
        {
            if (conversation.ProcessId is not null && !process.HasExited)
            {
                process.Kill(true);
                await process.WaitForExitAsync(CancellationToken.None);
            }
            job?.Dispose();
            // This path is generated under our private runs directory, never caller supplied.
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    public static void PrepareEnvironment(ProcessStartInfo start)
    {
        // Native login remains with Claude. Remove alternative auth/billing and inherited overrides.
        foreach (var key in start.Environment.Keys.Where(k => k.StartsWith("ANTHROPIC_", StringComparison.OrdinalIgnoreCase)
            || k.StartsWith("CLAUDE_", StringComparison.OrdinalIgnoreCase) || k.StartsWith("MCP_", StringComparison.OrdinalIgnoreCase)).ToArray()) start.Environment.Remove(key);
        start.Environment["CLAUDE_CODE_DISABLE_BACKGROUND_TASKS"] = "1";
        start.Environment["CLAUDE_CODE_MCP_AUTO_BACKGROUND_MS"] = "0";
        start.Environment["CLAUDE_CODE_DISABLE_AUTO_MEMORY"] = "1";
        start.Environment["CLAUDE_CODE_DISABLE_NONESSENTIAL_TRAFFIC"] = "1";
        start.Environment["MCP_TOOL_TIMEOUT"] = "1800000";
        start.Environment["CLAUDE_CODE_MCP_TOOL_IDLE_TIMEOUT"] = "1800000";
    }
    private static async Task DrainAsync(StreamReader reader, CancellationToken ct)
    {
        var buffer = new char[4096];
        while (await reader.ReadAsync(buffer, ct) != 0) { } // Never persist raw stderr.
    }
}
