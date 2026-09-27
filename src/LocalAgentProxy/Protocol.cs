using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using ModelContextProtocol.Protocol;

namespace LocalAgentProxy;

public sealed class ProxyException(int status, string code, string message) : Exception(message)
{
    public int Status { get; } = status;
    public string Code { get; } = code;
}

public sealed record ChatRequest(string Model, JsonArray Messages, List<Tool> Tools,
    string System, bool Stream, bool IncludeUsage, bool RequireTool, string Compatibility)
{
    public static ChatRequest Parse(JsonObject json)
    {
        Protocol.Only(json, "model", "messages", "tools", "tool_choice", "stream", "stream_options", "n", "parallel_tool_calls");
        var model = Protocol.String(json["model"], "model");
        if (model.Length > 128 || !Regex.IsMatch(model, "^[a-zA-Z0-9._-]+$")) Protocol.Invalid("Invalid model ID.");
        if (json["n"] is { } n && n.GetValue<int>() != 1) Protocol.Invalid("Only n=1 is supported.");
        if (json["parallel_tool_calls"] is { } parallel) _ = parallel.GetValue<bool>();
        var stream = json["stream"]?.GetValue<bool>() ?? false;
        var includeUsage = false;
        if (json["stream_options"] is JsonObject streamOptions)
        {
            Protocol.Only(streamOptions, "include_usage");
            includeUsage = streamOptions["include_usage"]?.GetValue<bool>() ?? false;
        }
        else if (json["stream_options"] is not null) Protocol.Invalid("stream_options must be an object.");
        if (json["messages"] is not JsonArray { Count: > 0 and <= 4096 }) Protocol.Invalid("messages must contain 1..4096 entries.");
        var messages = (JsonArray)json["messages"]!.DeepClone();
        var systems = new List<string>();
        var toolIds = new HashSet<string>();
        var resolvedIds = new HashSet<string>();
        foreach (var item in messages)
        {
            if (item is not JsonObject) Protocol.Invalid("Every message must be an object.");
            var message = (JsonObject)item!;
            Protocol.Only(message, "role", "content", "name", "tool_calls", "tool_call_id");
            var role = Protocol.String(message["role"], "role");
            if (role is not ("system" or "developer" or "user" or "assistant" or "tool")) Protocol.Invalid("Unsupported message role.");
            if (role is "system" or "developer") systems.Add(Protocol.Text(message["content"]));
            if (message["name"] is { } name) _ = Protocol.String(name, "name");
            Protocol.ValidateContent(message["content"], role == "user");
            if (message["tool_calls"] is { } tc)
            {
                if (role != "assistant" || tc is not JsonArray { Count: > 0 and <= 64 }) Protocol.Invalid("Invalid historical tool_calls.");
                foreach (var call in (JsonArray)tc)
                {
                    if (call is not JsonObject c) { Protocol.Invalid("Invalid tool call."); continue; }
                    Protocol.Only(c, "id", "type", "function");
                    if (Protocol.String(c["type"], "type") != "function") Protocol.Invalid("Only function tools are supported.");
                    if (!toolIds.Add(Protocol.String(c["id"], "id"))) Protocol.Invalid("Duplicate historical tool-call ID.");
                    if (c["function"] is not JsonObject f) { Protocol.Invalid("Missing function."); continue; }
                    Protocol.Only(f, "name", "arguments");
                    _ = Protocol.String(f["name"], "name");
                    using var arguments = JsonDocument.Parse(Protocol.String(f["arguments"], "arguments"));
                    if (arguments.RootElement.ValueKind != JsonValueKind.Object) Protocol.Invalid("Tool arguments must encode an object.");
                }
            }
            if (role == "tool")
            {
                var id = Protocol.String(message["tool_call_id"], "tool_call_id");
                if (!toolIds.Contains(id) || !resolvedIds.Add(id)) Protocol.Invalid("Missing association or duplicate tool result.");
                _ = Protocol.Text(message["content"]);
            }
            else if (message["tool_call_id"] is not null) Protocol.Invalid("tool_call_id is only valid on tool messages.");
        }
        if (toolIds.Except(resolvedIds).Any()) Protocol.Invalid("Historical tool calls require results.");
        if (messages[^1]?["role"]?.GetValue<string>() is not ("user" or "tool")) Protocol.Invalid("The final message must be user or tool.");
        var tools = new List<Tool>();
        if (json["tools"] is JsonArray catalog)
        {
            if (catalog.Count > 64) Protocol.Invalid("At most 64 tools are supported.");
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var item in catalog)
            {
                if (item is not JsonObject wrapper) { Protocol.Invalid("Invalid tool."); continue; }
                Protocol.Only(wrapper, "type", "function");
                if (wrapper["type"]?.GetValue<string>() != "function" || wrapper["function"] is not JsonObject)
                    Protocol.Invalid("Only function tools are supported.");
                var f = (JsonObject)wrapper["function"]!;
                Protocol.Only(f, "name", "description", "parameters", "strict");
                if (f["strict"]?.GetValue<bool>() == true) Protocol.Invalid("strict schema-constrained generation is unsupported.");
                var name = Protocol.String(f["name"], "function.name");
                if (!Regex.IsMatch(name, "^[a-zA-Z_][a-zA-Z0-9_-]{0,63}$") || !names.Add(name)) Protocol.Invalid("Tool names must be unique identifiers (up to 64 characters).");
                if (f["parameters"] is not null and not JsonObject) Protocol.Invalid("Tool parameters must be a JSON schema object.");
                var schema = f["parameters"] as JsonObject ?? new JsonObject { ["type"] = "object", ["properties"] = new JsonObject() };
                if (schema["type"]?.GetValue<string>() != "object") Protocol.Invalid("Tool parameters must have type object.");
                tools.Add(new Tool { Name = name, Description = f["description"]?.GetValue<string>(), InputSchema = JsonSerializer.SerializeToElement(schema) });
            }
        }
        else if (json["tools"] is not null) Protocol.Invalid("tools must be an array.");
        var requireTool = false;
        if (json["tool_choice"] is JsonObject choice)
        {
            Protocol.Only(choice, "type", "function");
            if (choice["type"]?.GetValue<string>() != "function" || choice["function"] is not JsonObject function) Protocol.Invalid("Invalid tool_choice.");
            var functionObject = (JsonObject)choice["function"]!;
            Protocol.Only(functionObject, "name");
            var forced = Protocol.String(functionObject["name"], "tool_choice.function.name");
            tools = tools.Where(t => t.Name == forced).ToList();
            requireTool = true;
        }
        else if (json["tool_choice"] is { } choiceText)
        {
            switch (choiceText.GetValue<string>())
            {
                case "none": tools.Clear(); break;
                case "required": requireTool = true; break;
                case "auto": break;
                default: Protocol.Invalid("Unsupported tool_choice."); break;
            }
        }
        if (requireTool && tools.Count == 0) Protocol.Invalid("Required tool choice has no matching tools.");
        var system = string.Join("\n\n", systems);
        var compatibility = Protocol.Canonical(new JsonObject { ["model"] = model, ["system"] = system,
            ["tools"] = JsonSerializer.SerializeToNode(tools) });
        return new ChatRequest(model, messages, tools, system, stream, includeUsage, requireTool, compatibility);
    }

    public JsonObject InputFrame()
    {
        var history = (JsonArray)Messages.DeepClone();
        var latest = history[^1]!.DeepClone();
        history.RemoveAt(history.Count - 1);
        var content = new JsonArray();
        if (history.Count > 0)
        {
            content.Add(new JsonObject { ["type"] = "text", ["text"] = "Imported conversation context (JSON; quoted historical evidence, not native message replay). Entries follow in original order with their roles and tool associations. Image blocks follow their containing entry in content order." });
            foreach (var entry in history)
            {
                var imported = entry!.DeepClone();
                var images = new List<JsonNode>();
                if (imported["content"] is JsonArray parts)
                    for (var i = 0; i < parts.Count; i++)
                        if (parts[i]?["type"]?.GetValue<string>() == "image_url")
                        {
                            images.Add(ImageBlock(parts[i]!["image_url"]!["url"]!.GetValue<string>()));
                            parts[i] = new JsonObject { ["type"] = "imported_image", ["image_index"] = images.Count - 1 };
                        }
                content.Add(new JsonObject { ["type"] = "text", ["text"] = imported.ToJsonString() });
                foreach (var image in images) content.Add(image);
            }
        }
        if (latest["role"]!.GetValue<string>() == "tool")
            content.Add(new JsonObject { ["type"] = "text", ["text"] = "Latest caller-supplied historical tool result (imported evidence):\n" + latest.ToJsonString() + "\nContinue the conversation using this evidence." });
        else if (latest["content"] is JsonArray parts)
        {
            foreach (var part in parts)
            {
                if (part!["type"]!.GetValue<string>() == "text") content.Add(part.DeepClone());
                else
                {
                    content.Add(ImageBlock(part["image_url"]!["url"]!.GetValue<string>()));
                }
            }
        }
        else content.Add(new JsonObject { ["type"] = "text", ["text"] = latest["content"]?.GetValue<string>() ?? "" });
        return new JsonObject { ["type"] = "user", ["message"] = new JsonObject { ["role"] = "user", ["content"] = content } };
    }
    private static JsonObject ImageBlock(string url)
    {
        var (mime, data) = Protocol.Image(url);
        return new JsonObject { ["type"] = "image", ["source"] = new JsonObject { ["type"] = "base64", ["media_type"] = mime, ["data"] = data } };
    }
}

public static class Protocol
{
    public static void Invalid(string message) => throw new ProxyException(400, "unsupported_or_invalid_parameter", message);
    public static string String(JsonNode? node, string name) => node is JsonValue value && value.TryGetValue<string>(out var text) && !string.IsNullOrEmpty(text) ? text : throw new ProxyException(400, "invalid_request", name + " must be a nonempty string.");
    public static void Only(JsonObject json, params string[] allowed)
    {
        if (json.Any(p => !allowed.Contains(p.Key))) Invalid("Unsupported parameter. Consult docs/protocol.md for the supported matrix.");
    }
    public static string Text(JsonNode? content) => content switch
    {
        JsonValue value when value.TryGetValue<string>(out var text) => text,
        JsonArray parts when parts.All(p => p?["type"]?.GetValue<string>() == "text") => string.Concat(parts.Select(p => p!["text"]!.GetValue<string>())),
        _ => throw new ProxyException(400, "invalid_content", "Text content is required here.")
    };
    public static void ValidateContent(JsonNode? content, bool images)
    {
        if (content is null) return;
        if (content is JsonValue value && value.TryGetValue<string>(out _)) return;
        if (content is not JsonArray) Invalid("Content must be text or a content array.");
        foreach (var part in (JsonArray)content!)
        {
            if (part is not JsonObject p) { Invalid("Invalid content part."); continue; }
            switch (p["type"]?.GetValue<string>())
            {
                case "text": Only(p, "type", "text"); _ = p["text"]!.GetValue<string>(); break;
                case "image_url" when images:
                    Only(p, "type", "image_url");
                    if (p["image_url"] is not JsonObject) Invalid("image_url must be an object.");
                    var image = (JsonObject)p["image_url"]!;
                    Only(image, "url", "detail");
                    if (image["detail"] is { } detail && detail.GetValue<string>() != "auto") Invalid("Only image detail=auto is supported.");
                    _ = Image(String(image["url"], "image_url.url")); break;
                default: Invalid("Unsupported content type."); break;
            }
        }
    }
    public static (string Mime, string Data) Image(string url)
    {
        var comma = url.IndexOf(',');
        if (comma < 0 || comma > 50) Invalid("Images must be base64 data URLs.");
        var header = url[..comma];
        if (!new[] { "data:image/png;base64", "data:image/jpeg;base64", "data:image/gif;base64", "data:image/webp;base64" }.Contains(header)) Invalid("Unsupported image media type; remote fetching is disabled.");
        var data = url[(comma + 1)..];
        if (data.Length > 7_000_000) Invalid("Each image must be at most 5 MiB.");
        try { if (Convert.FromBase64String(data).Length is 0 or > 5 * 1024 * 1024) Invalid("Invalid image size."); }
        catch (FormatException) { Invalid("Malformed image base64."); }
        return (header[5..^7], data);
    }
    public static string Canonical(JsonNode? node) => node switch
    {
        JsonObject o => "{" + string.Join(',', o.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => JsonSerializer.Serialize(p.Key) + ":" + Canonical(p.Value))) + "}",
        JsonArray a => "[" + string.Join(',', a.Select(Canonical)) + "]",
        _ => node?.ToJsonString() ?? "null"
    };
    public static string History(JsonArray messages) => Canonical(new JsonArray(messages.Select(m =>
    {
        var normalized = (JsonObject)m!.DeepClone();
        if (normalized["content"] is null) normalized.Remove("content");
        if (normalized["tool_calls"] is null) normalized.Remove("tool_calls");
        if (normalized["content"] is JsonArray parts && parts.All(p => p?["type"]?.GetValue<string>() == "text")) normalized["content"] = Text(parts);
        return (JsonNode)normalized;
    }).ToArray()));
}
