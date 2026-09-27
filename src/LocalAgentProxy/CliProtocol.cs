using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json.Nodes;

namespace LocalAgentProxy;

public sealed record TokenUsage(long Input = 0, long Output = 0)
{
    public object ToWire() => new { prompt_tokens = Input, completion_tokens = Output, total_tokens = Input + Output };
}
public sealed record RunEvent(string? Text = null, Invocation? Tool = null, bool Finished = false, TokenUsage? Usage = null);

public sealed class CliProtocol(IEnumerable<string> allowedTools)
{
    private readonly HashSet<string> _allowedTools = allowedTools.Select(t => "mcp__bridge__" + t).ToHashSet(StringComparer.Ordinal);
    private readonly Dictionary<string, TokenUsage> _usage = [];
    private string _messageId = "";
    public bool HasResult { get; private set; }
    public TokenUsage Usage { get; private set; } = new();
    public string? SessionId { get; private set; }
    public string? ResolvedModel { get; private set; }
    public RunEvent? Accept(string line)
    {
        if (HasResult) throw Failure("Unexpected output after terminal result.");
        var root = JsonNode.Parse(line) as JsonObject ?? throw Failure("Malformed CLI event.");
        switch (root["type"]?.GetValue<string>())
        {
            case "system":
                if (root["subtype"]?.GetValue<string>() == "init")
                {
                    SessionId = root["session_id"]?.GetValue<string>();
                    ResolvedModel = root["model"]?.GetValue<string>();
                }
                break;
            case "stream_event":
                var evt = root["event"] ?? throw Failure("Missing streaming event.");
                switch (evt["type"]?.GetValue<string>())
                {
                    case "message_start":
                        _messageId = evt["message"]?["id"]?.GetValue<string>() ?? throw Failure("Missing message identity.");
                        UpdateUsage(_messageId, evt["message"]?["usage"]); break;
                    case "message_delta": UpdateUsage(_messageId, evt["usage"]); break;
                    case "content_block_start": ValidateTool(evt["content_block"]); break;
                    case "content_block_delta":
                        if (evt["delta"]?["type"]?.GetValue<string>() == "text_delta")
                            return new RunEvent(Text: evt["delta"]!["text"]!.GetValue<string>());
                        break;
                    case "error": throw Failure("CLI returned a streaming provider error.");
                }
                break;
            case "assistant":
                var message = root["message"];
                if (root["error"] is not null) throw Failure("CLI returned an assistant error.");
                if (message?["content"] is JsonArray parts) foreach (var part in parts) ValidateTool(part);
                UpdateUsage(message?["id"]?.GetValue<string>() ?? "", message?["usage"]);
                break;
            case "result":
                if (root["is_error"]?.GetValue<bool>() != false || root["subtype"]?.GetValue<string>() != "success")
                    throw Failure("Claude did not complete successfully (quota, permission, turn limit, or provider failure).");
                if (root["result"] is not JsonValue final || !final.TryGetValue<string>(out _)) throw Failure("Missing final text result.");
                HasResult = true;
                if (root["usage"] is { } usage) Usage = ReadUsage(usage);
                break;
            case "user": case "rate_limit_event": case "tool_progress": case "tool_use_summary": break;
            default: throw Failure("Unknown CLI event type; CLI compatibility must be checked.");
        }
        return null;
    }
    private void ValidateTool(JsonNode? block)
    {
        if (block?["type"]?.GetValue<string>() == "tool_use" && !_allowedTools.Contains(block["name"]!.GetValue<string>()))
            throw Failure("Claude attempted an unexpected tool.");
    }
    private void UpdateUsage(string id, JsonNode? usage)
    {
        if (id.Length == 0 || usage is null) return;
        var value = ReadUsage(usage);
        _usage.TryGetValue(id, out var old);
        _usage[id] = new TokenUsage(Math.Max(old?.Input ?? 0, value.Input), Math.Max(old?.Output ?? 0, value.Output));
        Usage = new TokenUsage(_usage.Values.Sum(u => u.Input), _usage.Values.Sum(u => u.Output));
    }
    private static TokenUsage ReadUsage(JsonNode usage) => new(
        (usage["input_tokens"]?.GetValue<long>() ?? 0) + (usage["cache_read_input_tokens"]?.GetValue<long>() ?? 0) + (usage["cache_creation_input_tokens"]?.GetValue<long>() ?? 0),
        usage["output_tokens"]?.GetValue<long>() ?? 0);
    public static ProxyException Failure(string message) => new(502, "claude_failure", message);

    public static async IAsyncEnumerable<string> LinesAsync(Stream stream, int limit = 8 * 1024 * 1024, [EnumeratorCancellation] CancellationToken ct = default)
    {
        using var reader = new StreamReader(stream, new UTF8Encoding(false, true), false, 4096, leaveOpen: true);
        var buffer = new char[4096];
        var line = new StringBuilder();
        int read;
        while ((read = await reader.ReadAsync(buffer, ct)) != 0)
        {
            for (var i = 0; i < read; i++)
            {
                if (buffer[i] == '\n')
                {
                    var result = line.ToString().TrimEnd('\r');
                    line.Clear();
                    if (result.Length > 0) yield return result;
                }
                else
                {
                    if (line.Length >= limit) throw Failure("CLI line exceeded the output limit.");
                    line.Append(buffer[i]);
                }
            }
        }
        if (line.Length > 0) throw Failure("Truncated CLI output.");
    }
}
