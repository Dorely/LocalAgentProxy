using System.Diagnostics;
using System.Net.Http.Headers;
using Microsoft.Win32;

namespace LocalAgentProxy;

public static class Commands
{
    // The host belongs to a kill-on-close job before spawning anything. Children
    // inherit containment at creation, including during per-conversation assignment.
    private static WindowsJob? _hostJob;
    public static async Task<int> RunAsync(string[] args)
    {
        if (!OperatingSystem.IsWindows()) throw new InvalidOperationException("This release supports native Windows only.");
        var store = new LocalStore();
        var options = store.Options();
        switch (args.FirstOrDefault())
        {
            case "serve":
                using (store.Lock("host.lock"))
                {
                    store.CleanAbandonedRuns();
                    _hostJob = new WindowsJob();
                    _hostJob.Assign(Process.GetCurrentProcess());
                    await using var app = Api.Build(store, options);
                    var broker = app.Services.GetRequiredService<ConversationBroker>();
                    using var stopping = app.Lifetime.ApplicationStopping.Register(() => _ = broker.DisposeAsync().AsTask());
                    using var stop = new CancellationTokenSource();
                    var sweep = Task.Run(async () =>
                    {
                        try { using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1)); while (await timer.WaitForNextTickAsync(stop.Token)) await broker.SweepAsync(); }
                        catch (OperationCanceledException) { }
                    });
                    Console.WriteLine("LocalAgentProxy listening on " + options.Url + "/v1");
                    try { await app.RunAsync(); }
                    finally { await stop.CancelAsync(); await sweep; await broker.DisposeAsync(); }
                }
                return 0;
            case "start":
                if (await IsRunning(store, options)) { Console.WriteLine("Already running."); return 0; }
                var start = new ProcessStartInfo(Executable()) { WorkingDirectory = store.Root, UseShellExecute = false, CreateNoWindow = true };
                start.ArgumentList.Add("serve");
                using (var child = Process.Start(start))
                {
                    for (var i = 0; i < 50; i++)
                    {
                        if (await IsRunning(store, options)) { Console.WriteLine("Started."); return 0; }
                        if (child is null || child.HasExited) throw new InvalidOperationException("Host failed to start; run serve for a diagnostic.");
                        await Task.Delay(200);
                    }
                    if (child is not null && !child.HasExited) { child.Kill(true); await child.WaitForExitAsync(); }
                }
                throw new InvalidOperationException("Host startup timed out.");
            case "stop":
                using (var client = ManagementClient(store, options))
                using (var response = await client.PostAsync("/management/stop", null)) response.EnsureSuccessStatusCode();
                Console.WriteLine("Stop requested."); return 0;
            case "status":
                using (var client = ManagementClient(store, options))
                using (var response = await client.GetAsync("/management/status")) Console.WriteLine(await response.Content.ReadAsStringAsync());
                return 0;
            case "clients" when args.Length >= 2:
                switch (args[1])
                {
                    case "add" when args.Length == 3:
                        var (id, key) = store.AddClient(args[2]);
                        Console.WriteLine("Client ID: " + id + "\nAPI key (shown once): " + key); break;
                    case "list": foreach (var entry in store.Clients()) Console.WriteLine(entry.Id + "  " + entry.Name); break;
                    case "remove" when args.Length == 3: store.RemoveClient(args[2]); break;
                    default: throw new InvalidOperationException("Use clients add NAME, list, or remove ID.");
                }
                return 0;
            case "login-start" when args.Length == 2:
                using (var runKey = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run"))
                {
                    if (args[1] == "enable") runKey.SetValue("LocalAgentProxy", "\"" + Executable() + "\" start");
                    else if (args[1] == "disable") runKey.DeleteValue("LocalAgentProxy", false);
                    else throw new InvalidOperationException("Use login-start enable or disable.");
                }
                return 0;
            default:
                Console.WriteLine("LocalAgentProxy\n  serve | start | stop | status\n  clients add NAME | list | remove ID\n  login-start enable | disable\nConfiguration: " + Path.Combine(store.Root, "config.json")); return 0;
        }
    }
    private static string Executable() => Path.Combine(AppContext.BaseDirectory, "LocalAgentProxy.exe");
    private static HttpClient ManagementClient(LocalStore store, ProxyOptions options)
    {
        var client = new HttpClient { BaseAddress = new Uri(options.Url), Timeout = TimeSpan.FromSeconds(12) };
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", store.ManagementKey());
        return client;
    }
    private static async Task<bool> IsRunning(LocalStore store, ProxyOptions options)
    {
        try
        {
            using var client = ManagementClient(store, options);
            using var response = await client.GetAsync("/management/status");
            return response.IsSuccessStatusCode || response.StatusCode == System.Net.HttpStatusCode.ServiceUnavailable;
        }
        catch (HttpRequestException) { return false; }
        catch (TaskCanceledException) { return false; }
    }
}
