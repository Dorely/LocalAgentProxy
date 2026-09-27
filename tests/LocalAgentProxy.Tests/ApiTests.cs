using System.ClientModel;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using OpenAI;
using OpenAI.Chat;
using Microsoft.Extensions.AI;

[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace LocalAgentProxy.Tests;

public class ApiTests
{
    [Fact]
    public async Task OpenAiClientTextStreamingAndExtensionsAdapterWork()
    {
        await using var host = await TestHost.StartAsync();
        var client = new OpenAIClient(new ApiKeyCredential(host.Key), new OpenAIClientOptions { Endpoint = new Uri(host.Options.Url + "/v1") }).GetChatClient("claude-opus-5-5");
        var response = await client.CompleteChatAsync([new UserChatMessage("hello")]);
        Assert.Equal("Hello 雪 🌍", response.Value.Content[0].Text);
        Assert.Equal("claude-opus-5-5", response.Value.Model);
        var streamed = new StringBuilder();
        await foreach (var update in client.CompleteChatStreamingAsync([new UserChatMessage("hello")]))
            foreach (var part in update.ContentUpdate) streamed.Append(part.Text);
        Assert.Equal("Hello 雪 🌍", streamed.ToString());
        using var adapter = client.AsIChatClient();
        var adapted = await adapter.GetResponseAsync("hello");
        Assert.Equal("Hello 雪 🌍", adapted.Text);
    }
    [Fact]
    public async Task DuplicateIdenticalCallsHaveIndependentResultsAndClientIsolation()
    {
        await using var host = await TestHost.StartAsync();
        var request = ProtocolTests.Request("fixture:parallel"); ProtocolTests.Tools(request);
        var first = await host.Chat(request);
        var firstMessage = first["choices"]![0]!["message"]!;
        var firstId = firstMessage["tool_calls"]![0]!["id"]!.GetValue<string>();
        var originalPid = Assert.Single(host.Broker.ActiveConversations).ProcessId;
        Append(request, firstMessage, "A");
        var foreignKey = host.Store.AddClient("Other").Key;
        using (var foreign = new HttpClient { BaseAddress = host.Client.BaseAddress })
        {
            foreign.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", foreignKey);
            using var denied = await foreign.PostAsync("/v1/chat/completions", Body(request));
            Assert.Equal(HttpStatusCode.Conflict, denied.StatusCode);
        }
        // While A waits for a client tool, a separate application can generate.
        var independent = await host.Chat(ProtocolTests.Request("hello"));
        Assert.Equal("stop", independent["choices"]![0]!["finish_reason"]!.GetValue<string>());
        var second = await host.Chat(request);
        var secondMessage = second["choices"]![0]!["message"]!;
        var secondId = secondMessage["tool_calls"]![0]!["id"]!.GetValue<string>();
        Assert.NotEqual(firstId, secondId);
        Assert.Equal(originalPid, Assert.Single(host.Broker.ActiveConversations).ProcessId);
        using (var duplicate = await host.Client.PostAsync("/v1/chat/completions", Body(request))) Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
        Append(request, secondMessage, "B");
        var final = await host.Chat(request);
        var answer = final["choices"]![0]!["message"]!["content"]!.GetValue<string>();
        Assert.Contains("A", answer); Assert.Contains("B", answer);
        Assert.Equal(0, host.Broker.Count);
        using var expired = await host.Client.PostAsync("/v1/chat/completions", Body(request));
        Assert.Equal(HttpStatusCode.Gone, expired.StatusCode);
    }
    [Fact]
    public async Task StandardOpenAiToolContinuationWorks()
    {
        await using var host = await TestHost.StartAsync();
        var client = new OpenAIClient(new ApiKeyCredential(host.Key), new OpenAIClientOptions { Endpoint = new Uri(host.Options.Url + "/v1") }).GetChatClient("sonnet");
        var messages = new List<OpenAI.Chat.ChatMessage> { new UserChatMessage("fixture:tools") };
        var options = new ChatCompletionOptions();
        options.ToolChoice = ChatToolChoice.CreateRequiredChoice();
        options.Tools.Add(ChatTool.CreateFunctionTool("fixture", "Fixture", BinaryData.FromString("""{"type":"object","properties":{"value":{"type":"string"}}}""")));
        var first = (await client.CompleteChatAsync(messages, options)).Value;
        Assert.Equal(OpenAI.Chat.ChatFinishReason.ToolCalls, first.FinishReason);
        messages.Add(new AssistantChatMessage(first));
        messages.Add(new ToolChatMessage(first.ToolCalls.Single().Id, "client result"));
        options.ToolChoice = ChatToolChoice.CreateAutoChoice();
        var final = (await client.CompleteChatAsync(messages, options)).Value;
        Assert.Equal("client result", final.Content.Single().Text);
    }
    [Fact]
    public async Task ChangedInstructionsRebuildFromAuthoritativeHistory()
    {
        await using var host = await TestHost.StartAsync();
        var request = ProtocolTests.Request("fixture:tools"); ProtocolTests.Tools(request);
        var first = await host.Chat(request);
        var run = Assert.Single(host.Broker.ActiveConversations);
        var pid = run.ProcessId;
        Append(request, first["choices"]![0]!["message"]!, "prior evidence");
        ((JsonArray)request["messages"]!).Insert(0, new JsonObject { ["role"] = "system", ["content"] = "Changed instruction" });
        // Remove the fixture trigger from authoritative history. New CLI returns text.
        request["messages"]![1]!["content"] = "Earlier user message compacted.";
        var result = await host.Chat(request);
        Assert.Equal("stop", result["choices"]![0]!["finish_reason"]!.GetValue<string>());
        Assert.True(run.Stopped);
        Assert.False(IsAlive(pid!.Value));
    }
    [Theory]
    [InlineData("fixture:crash")][InlineData("fixture:malformed")][InlineData("fixture:truncated")]
    [InlineData("fixture:unexpected")][InlineData("fixture:missing-final")][InlineData("fixture:model-mismatch")]
    public async Task CliFailuresDoNotMasqueradeAsSuccess(string fixture)
    {
        await using var host = await TestHost.StartAsync();
        using var response = await host.Client.PostAsync("/v1/chat/completions", Body(ProtocolTests.Request(fixture)));
        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.Equal(0, host.Broker.Count);
    }
    [Fact]
    public async Task StreamedErrorPreservesPartialOutputWithoutSuccessfulFinish()
    {
        await using var host = await TestHost.StartAsync();
        var request = ProtocolTests.Request("fixture:partial-error"); request["stream"] = true;
        using var response = await host.Client.PostAsync("/v1/chat/completions", Body(request));
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("Hello", body); Assert.Contains("claude_failure", body);
        Assert.DoesNotContain("[DONE]", body); Assert.DoesNotContain("\"finish_reason\":\"stop\"", body);
    }
    [Fact]
    public async Task AuthenticationUnsupportedParametersAndLargePayloads()
    {
        await using var host = await TestHost.StartAsync();
        using var anonymous = new HttpClient { BaseAddress = host.Client.BaseAddress };
        using var denied = await anonymous.GetAsync("/v1/models"); Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
        using var alive = await anonymous.GetAsync("/health/live"); Assert.Equal(HttpStatusCode.OK, alive.StatusCode);
        var request = ProtocolTests.Request(new string('雪', 100_000));
        var result = await host.Chat(request); Assert.NotNull(result["usage"]);
        request["temperature"] = 0.5;
        using var rejected = await host.Client.PostAsync("/v1/chat/completions", Body(request)); Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
        host.Client.DefaultRequestHeaders.Add("Origin", "https://example.com");
        using var browser = await host.Client.GetAsync("/v1/models"); Assert.Equal(HttpStatusCode.Forbidden, browser.StatusCode);
    }
    [Fact]
    public async Task AbandonedCallsAndProcessTreesAreReclaimed()
    {
        await using var host = await TestHost.StartAsync(seconds: 1);
        var request = ProtocolTests.Request("fixture:tools"); ProtocolTests.Tools(request);
        await host.Chat(request);
        var run = Assert.Single(host.Broker.ActiveConversations);
        var pid = run.ProcessId!.Value;
        await Task.Delay(1100); await host.Broker.SweepAsync();
        Assert.False(IsAlive(pid)); Assert.Equal(0, host.Broker.Count);
        var file = Path.Combine(host.Store.Root, "child.pid");
        var pending = host.Client.PostAsync("/v1/chat/completions", Body(ProtocolTests.Request("fixture:orphan|" + file)));
        await WaitFor(() => File.Exists(file));
        var child = int.Parse(await File.ReadAllTextAsync(file));
        await host.Broker.DisposeAsync();
        await pending;
        await WaitFor(() => !IsAlive(child));
    }
    [Fact]
    public async Task RequiredToolFailureIsExplicit()
    {
        await using var host = await TestHost.StartAsync();
        var request = ProtocolTests.Request(); ProtocolTests.Tools(request); request["tool_choice"] = "required";
        using var response = await host.Client.PostAsync("/v1/chat/completions", Body(request));
        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
    }
    [Fact]
    public async Task CancellationKillsTheGenerationAndLogsNoRequestMaterial()
    {
        await using var host = await TestHost.StartAsync();
        using var cancel = new CancellationTokenSource();
        var pending = host.Client.PostAsync("/v1/chat/completions", Body(ProtocolTests.Request("fixture:hang PRIVATE_PROMPT_CANARY")), cancel.Token);
        await WaitFor(() => host.Broker.ActiveConversations.FirstOrDefault()?.ProcessId is not null);
        var pid = host.Broker.ActiveConversations[0].ProcessId!.Value;
        await cancel.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        await WaitFor(() => host.Broker.Count == 0 && !IsAlive(pid));
        foreach (var file in Directory.EnumerateFiles(host.Store.Root, "*", SearchOption.AllDirectories))
        {
            var text = await File.ReadAllTextAsync(file);
            Assert.DoesNotContain("PRIVATE_PROMPT_CANARY", text);
            Assert.DoesNotContain(host.Key, text);
        }
    }
    [Fact]
    public async Task CatalogAndModelChangesRebuildAndImportedImagesRemainNative()
    {
        await using var host = await TestHost.StartAsync();
        var request = ProtocolTests.Request("fixture:tools"); ProtocolTests.Tools(request);
        var first = await host.Chat(request);
        var old = Assert.Single(host.Broker.ActiveConversations);
        Append(request, first["choices"]![0]!["message"]!, "old result");
        request["model"] = "different-exact-id";
        request["tools"] = new JsonArray();
        var final = await host.Chat(request);
        Assert.True(old.Stopped);
        Assert.Equal("stop", final["choices"]![0]!["finish_reason"]!.GetValue<string>());
    }
    [Fact]
    public async Task DeadlineAndShutdownRejectFutureWork()
    {
        await using var host = await TestHost.StartAsync(generationSeconds: 1);
        var pending = host.Client.PostAsync("/v1/chat/completions", Body(ProtocolTests.Request("fixture:hang")));
        await WaitFor(() => host.Broker.ActiveConversations.FirstOrDefault()?.ProcessId is not null);
        var pid = host.Broker.ActiveConversations[0].ProcessId!.Value;
        await Task.Delay(1100); await host.Broker.SweepAsync();
        using var failed = await pending;
        Assert.False(failed.IsSuccessStatusCode); Assert.False(IsAlive(pid));
        await host.Broker.DisposeAsync();
        using var rejected = await host.Client.PostAsync("/v1/chat/completions", Body(ProtocolTests.Request()));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, rejected.StatusCode);
    }
    internal static void Append(JsonObject request, JsonNode assistant, string result)
    {
        var messages = (JsonArray)request["messages"]!;
        messages.Add(assistant.DeepClone());
        messages.Add(new JsonObject { ["role"] = "tool", ["tool_call_id"] = assistant["tool_calls"]![0]!["id"]!.GetValue<string>(), ["content"] = result });
    }
    internal static StringContent Body(JsonObject request) => new(request.ToJsonString(), Encoding.UTF8, "application/json");
    internal static bool IsAlive(int pid) { try { using var process = Process.GetProcessById(pid); return !process.HasExited; } catch (ArgumentException) { return false; } }
    internal static async Task WaitFor(Func<bool> predicate)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!predicate()) await Task.Delay(25, deadline.Token);
    }
}

internal sealed class TestHost : IAsyncDisposable
{
    public LocalStore Store { get; }
    public ProxyOptions Options { get; }
    public string Key { get; }
    public HttpClient Client { get; }
    public ConversationBroker Broker { get; }
    private readonly WebApplication _app;
    private TestHost(LocalStore store, ProxyOptions options, WebApplication app, string key)
    {
        Store = store; Options = options; _app = app; Key = key;
        Client = new HttpClient { BaseAddress = new Uri(options.Url), Timeout = TimeSpan.FromSeconds(15) };
        Client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key);
        Broker = app.Services.GetRequiredService<ConversationBroker>();
    }
    public static async Task<TestHost> StartAsync(int seconds = 20, int generationSeconds = 20)
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop();
        var store = new LocalStore(Path.Combine(Path.GetTempPath(), "LocalAgentProxy.Tests", Guid.NewGuid().ToString("N")));
        var options = new ProxyOptions { Url = "http://127.0.0.1:" + port, ClaudePath = Path.Combine(AppContext.BaseDirectory, "FakeCli.exe"), GenerationSeconds = generationSeconds, ContinuationSeconds = seconds };
        var key = store.AddClient("Test").Key;
        var app = Api.Build(store, options, checkLogin: false);
        await app.StartAsync();
        return new TestHost(store, options, app, key);
    }
    public async Task<JsonNode> Chat(JsonObject request)
    {
        using var response = await Client.PostAsync("/v1/chat/completions", ApiTests.Body(request));
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, text);
        return JsonNode.Parse(text)!;
    }
    public async ValueTask DisposeAsync()
    {
        Client.Dispose(); await Broker.DisposeAsync(); await _app.StopAsync(); await _app.DisposeAsync();
        Directory.Delete(Store.Root, recursive: true);
    }
}
