using System.Net.Http.Headers;
using System.Net.Http.Json;
using ModelContextProtocol.Protocol;

namespace LocalAgentProxy;

// This process is an MCP transport only. The application executes every tool.
public static class Bridge
{
    public static async Task RunAsync()
    {
        var uri = new Uri(Environment.GetEnvironmentVariable("LOCAL_AGENT_BRIDGE_URL")
            ?? throw new InvalidOperationException("Missing private bridge URL."));
        if (uri.Scheme != "http" || uri.Host != "127.0.0.1")
            throw new InvalidOperationException("Bridge must be loopback.");
        using var client = new HttpClient { BaseAddress = uri, Timeout = Timeout.InfiniteTimeSpan };
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer",
            Environment.GetEnvironmentVariable("LOCAL_AGENT_BRIDGE_TOKEN"));
        var builder = Host.CreateApplicationBuilder();
        builder.Logging.ClearProviders();
        builder.Services.AddMcpServer().WithStdioServerTransport()
            .WithListToolsHandler(async (_, ct) => new ListToolsResult
            {
                Tools = await client.GetFromJsonAsync<List<Tool>>("catalog", ct) ?? []
            })
            .WithCallToolHandler(async (context, ct) =>
            {
                using var response = await client.PostAsJsonAsync("invoke", context.Params, ct);
                response.EnsureSuccessStatusCode();
                return await response.Content.ReadFromJsonAsync<CallToolResult>(ct)
                    ?? throw new InvalidOperationException("Missing bridge result.");
            });
        await builder.Build().RunAsync();
    }
}
