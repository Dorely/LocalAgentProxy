using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

Console.OutputEncoding = new UTF8Encoding(false);
if (args.FirstOrDefault() == "child") { await Task.Delay(Timeout.Infinite); return; }
var input = await Console.In.ReadLineAsync() ?? "";
if (input.Contains("fixture:crash")) Environment.Exit(7);
if (input.Contains("fixture:malformed")) { Console.WriteLine("not json"); return; }
if (input.Contains("fixture:truncated")) { Console.Write("{\"type\":"); return; }
if (input.Contains("fixture:orphan"))
{
    var pidFile = JsonNode.Parse(input)!["message"]!["content"]![0]!["text"]!.GetValue<string>().Split('|')[1];
    using var child = Process.Start(new ProcessStartInfo(Environment.ProcessPath!) { ArgumentList = { "child" }, UseShellExecute = false, CreateNoWindow = true });
    await File.WriteAllTextAsync(pidFile + ".new", child!.Id.ToString());
    File.Move(pidFile + ".new", pidFile);
    await Task.Delay(Timeout.Infinite); return;
}
if (input.Contains("fixture:hang")) { await Task.Delay(Timeout.Infinite); return; }
void Emit(object value) => Console.WriteLine(JsonSerializer.Serialize(value));
Emit(new { type = "system", subtype = "init", session_id = Guid.NewGuid().ToString(), model = input.Contains("fixture:model-mismatch") ? "wrong-model" : args[Array.IndexOf(args, "--model") + 1] });
Emit(new { type = "stream_event", @event = new { type = "message_start", message = new { id = "m1", usage = new { input_tokens = 12, output_tokens = 1 } } } });
var answer = "Hello 雪 🌍";
if (input.Contains("fixture:tools") || input.Contains("fixture:parallel"))
{
    var config = JsonNode.Parse(await File.ReadAllTextAsync(args[Array.IndexOf(args, "--mcp-config") + 1]))!;
    var env = config["mcpServers"]!["bridge"]!["env"]!;
    using var client = new HttpClient { BaseAddress = new Uri(env["LOCAL_AGENT_BRIDGE_URL"]!.GetValue<string>()), Timeout = TimeSpan.FromMinutes(2) };
    client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", env["LOCAL_AGENT_BRIDGE_TOKEN"]!.GetValue<string>());
    var catalog = JsonNode.Parse(await client.GetStringAsync("catalog"))!.AsArray();
    if (catalog.Count == 0) answer = "No tools available.";
    else
    {
        var toolName = catalog[0]!["name"]!.GetValue<string>();
        var count = input.Contains("fixture:parallel") ? 2 : 1;
        var pending = Enumerable.Range(0, count).Select(async i => {
            // Intentionally identical names AND arguments: only invocation identity differs.
            using var body = new StringContent(JsonSerializer.Serialize(new { name = toolName, arguments = new { value = "same" } }), Encoding.UTF8, "application/json");
            using var response = await client.PostAsync("invoke", body);
            response.EnsureSuccessStatusCode();
            return JsonNode.Parse(await response.Content.ReadAsStringAsync())!["content"]![0]!["text"]!.GetValue<string>();
        }).ToArray();
        answer = string.Join(" / ", await Task.WhenAll(pending));
    }
}
if (input.Contains("fixture:unexpected")) Emit(new { type = "stream_event", @event = new { type = "content_block_start", content_block = new { type = "tool_use", name = "Bash" } } });
Emit(new { type = "stream_event", @event = new { type = "content_block_delta", delta = new { type = "text_delta", text = answer } } });
if (input.Contains("fixture:partial-error")) { Emit(new { type = "result", subtype = "error_during_execution", is_error = true }); return; }
if (input.Contains("fixture:missing-final")) return;
Emit(new { type = "result", subtype = "success", is_error = false, result = answer, usage = new { input_tokens = 12, output_tokens = 8 } });
