using System.Text;
using System.Text.Json.Nodes;

namespace LocalAgentProxy.Tests;

public class ProtocolTests
{
    public static JsonObject Request(string prompt = "hello") => new() { ["model"] = "claude-exact-20260901", ["messages"] = new JsonArray(new JsonObject { ["role"] = "user", ["content"] = prompt }) };
    internal static void Tools(JsonObject request) => request["tools"] = JsonNode.Parse("""[{"type":"function","function":{"name":"fixture","description":"Fixture","parameters":{"type":"object","properties":{"value":{"$ref":"#/$defs/value"}},"$defs":{"value":{"type":"string"}}}}}]""");

    [Theory]
    [InlineData("temperature")][InlineData("max_tokens")][InlineData("max_completion_tokens")][InlineData("top_p")][InlineData("seed")][InlineData("response_format")]
    public void RejectsUnsupportedParameters(string parameter)
    {
        var request = Request(); request[parameter] = 1;
        Assert.Throws<ProxyException>(() => ChatRequest.Parse(request));
    }
    [Fact]
    public void PreservesExactModelSystemAndSchema()
    {
        var request = Request(); Tools(request);
        ((JsonArray)request["messages"]!).Insert(0, new JsonObject { ["role"] = "system", ["content"] = "  Keep <tags>\n雪 exactly.  " });
        var parsed = ChatRequest.Parse(request);
        Assert.Equal("claude-exact-20260901", parsed.Model);
        Assert.Equal("  Keep <tags>\n雪 exactly.  ", parsed.System);
        Assert.Contains("$defs", parsed.Tools.Single().InputSchema.GetRawText());
        request["tool_choice"] = "none";
        Assert.Empty(ChatRequest.Parse(request).Tools);
        request["tool_choice"] = JsonNode.Parse("""{"type":"function","function":{"name":"missing"}}""");
        Assert.Throws<ProxyException>(() => ChatRequest.Parse(request));
    }
    [Fact]
    public void LargeCatalogPreservesEverySchemaAndRejectsOverflow()
    {
        var request = Request(); Tools(request);
        var template = request["tools"]![0]!;
        var catalog = new JsonArray();
        for (var i = 0; i < 128; i++)
        {
            var tool = template.DeepClone();
            tool["function"]!["name"] = $"fixture_{i}";
            catalog.Add(tool);
        }
        request["tools"] = catalog;
        var parsed = ChatRequest.Parse(request);
        Assert.Equal(128, parsed.Tools.Count);
        Assert.Equal("fixture_127", parsed.Tools[^1].Name);
        Assert.All(parsed.Tools, tool => Assert.Contains("$defs", tool.InputSchema.GetRawText()));
        var overflow = template.DeepClone();
        overflow["function"]!["name"] = "fixture_128";
        catalog.Add(overflow);
        Assert.Equal(400, Assert.Throws<ProxyException>(() => ChatRequest.Parse(request)).Status);
    }
    [Fact]
    public void ImportsRolesOrderingAndToolAssociations()
    {
        var request = Request();
        request["messages"] = JsonNode.Parse("""[{"role":"user","content":"first"},{"role":"assistant","content":null,"tool_calls":[{"id":"external","type":"function","function":{"name":"fixture","arguments":"{}"}}]},{"role":"tool","tool_call_id":"external","content":"evidence"},{"role":"user","content":"latest"}]""");
        var input = ChatRequest.Parse(request).InputFrame().ToJsonString();
        Assert.Contains("Imported conversation context", input);
        Assert.Contains("tool_call_id", input);
        Assert.True(input.IndexOf("first", StringComparison.Ordinal) < input.LastIndexOf("evidence", StringComparison.Ordinal));
    }
    [Theory]
    [InlineData("https://example.com/a.png")][InlineData("data:image/png;base64,broken")][InlineData("data:text/plain;base64,YQ==")]
    public void RejectsBadImages(string image) => Assert.Throws<ProxyException>(() => Protocol.Image(image));
    [Fact]
    public void ImagesBecomeNativeImageBlocks()
    {
        var request = Request();
        request["messages"]![0]!["content"] = JsonNode.Parse("""[{"type":"text","text":"Describe"},{"type":"image_url","image_url":{"url":"data:image/png;base64,YQ=="}}]""");
        var block = ChatRequest.Parse(request).InputFrame()["message"]!["content"]![1]!;
        Assert.Equal("image", block["type"]!.GetValue<string>());
        Assert.Equal("image/png", block["source"]!["media_type"]!.GetValue<string>());
    }
    [Fact]
    public async Task IncrementalUtf8SurvivesEveryByteBoundary()
    {
        var data = Encoding.UTF8.GetBytes("{\"text\":\"雪 🌍\"}\r\nsecond\n");
        using var stream = new ByteStream(data);
        var lines = new List<string>();
        await foreach (var line in CliProtocol.LinesAsync(stream)) lines.Add(line);
        Assert.Equal(["{\"text\":\"雪 🌍\"}", "second"], lines);
    }
    [Fact]
    public async Task RejectsTruncationAndBoundedLineOverflow()
    {
        await Assert.ThrowsAsync<ProxyException>(async () => { await foreach (var _ in CliProtocol.LinesAsync(new MemoryStream(Encoding.UTF8.GetBytes("{}")))) { } });
        await Assert.ThrowsAsync<ProxyException>(async () => { await foreach (var _ in CliProtocol.LinesAsync(new MemoryStream(Encoding.UTF8.GetBytes("12345\n")), 4)) { } });
    }
    [Fact]
    public void RejectsErrorFinalAndUnexpectedTools()
    {
        Assert.Throws<ProxyException>(() => new CliProtocol([]).Accept("""{"type":"result","subtype":"error_max_turns","is_error":true}"""));
        Assert.Throws<ProxyException>(() => new CliProtocol([]).Accept("""{"type":"stream_event","event":{"type":"content_block_start","content_block":{"type":"tool_use","name":"Bash"}}}"""));
    }
    [Fact]
    public async Task InvalidUtf8IsRejected()
    {
        await Assert.ThrowsAsync<DecoderFallbackException>(async () => { await foreach (var _ in CliProtocol.LinesAsync(new MemoryStream([0xff, 0xfe, 0x0a]))) { } });
    }
    [Fact]
    public void HistoricalImagesArePreservedAsImageBlocks()
    {
        var request = Request();
        request["messages"] = JsonNode.Parse("""[{"role":"user","content":[{"type":"image_url","image_url":{"url":"data:image/png;base64,YQ=="}}]},{"role":"assistant","content":"previous"},{"role":"user","content":"recall image"}]""");
        var blocks = ChatRequest.Parse(request).InputFrame()["message"]!["content"]!.AsArray();
        Assert.Single(blocks, b => b!["type"]!.GetValue<string>() == "image");
    }
    [Fact]
    public void MalformedSchemaAndNumericTerminalAreNotSilentlyAccepted()
    {
        var request = Request(); Tools(request);
        request["tools"]![0]!["function"]!["parameters"] = new JsonArray();
        Assert.Throws<ProxyException>(() => ChatRequest.Parse(request));
        Assert.Throws<ProxyException>(() => new CliProtocol([]).Accept("""{"type":"result","subtype":"success","is_error":false,"result":42}"""));
    }
    private sealed class ByteStream(byte[] data) : MemoryStream(data)
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => base.ReadAsync(buffer[..Math.Min(buffer.Length, 1)], cancellationToken);
    }
}
