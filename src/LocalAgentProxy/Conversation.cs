using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using ModelContextProtocol.Protocol;

namespace LocalAgentProxy;

public sealed class Invocation(string name, JsonNode arguments, Conversation conversation)
{
    public string Id { get; } = "call_lap_" + Guid.NewGuid().ToString("N");
    public string Name { get; } = name;
    public JsonNode Arguments { get; } = arguments;
    public Conversation Conversation { get; } = conversation;
    public TaskCompletionSource<string> Result { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public bool Exposed { get; set; }
    public bool Submitted { get; set; }
    public JsonObject Wire() => new() { ["id"] = Id, ["type"] = "function", ["function"] = new JsonObject { ["name"] = Name, ["arguments"] = Arguments.ToJsonString() } };
}

public sealed class Conversation : IAsyncDisposable
{
    private readonly object _sync = new();
    private readonly FairScheduler _scheduler;
    private readonly ProxyOptions _options;
    private readonly Channel<RunEvent> _events = Channel.CreateBounded<RunEvent>(128);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly List<Invocation> _calls = [];
    private IDisposable? _lease;
    private Task? _runner;
    private Task? _stopping;
    private int _busy;
    private bool _stopped;
    public string Id { get; } = Guid.NewGuid().ToString("N");
    public string BridgeToken { get; } = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
    public string Client { get; }
    public ChatRequest Request { get; }
    public JsonArray ExpectedHistory { get; set; }
    public TokenUsage Usage { get; set; } = new();
    public TokenUsage ReportedUsage { get; set; } = new();
    public int? ProcessId { get; set; }
    public bool ToolWasCalled { get; private set; }
    public DateTimeOffset Expires { get; private set; }
    public CancellationToken Token => _lifetime.Token;
    public bool Stopped { get { lock (_sync) return _stopped; } }
    public Conversation(string client, ChatRequest request, FairScheduler scheduler, ProxyOptions options)
    {
        Client = client; Request = request; _scheduler = scheduler; _options = options;
        ExpectedHistory = (JsonArray)request.Messages.DeepClone();
        Expires = DateTimeOffset.UtcNow.AddSeconds(options.GenerationSeconds);
    }
    public bool TryEnter() => Interlocked.CompareExchange(ref _busy, 1, 0) == 0;
    public void Leave() => Interlocked.Exchange(ref _busy, 0);
    public async Task StartAsync(ICliRunner runner, CancellationToken ct)
    {
        await AcquireAsync(ct);
        _runner = ExecuteAsync(runner);
    }
    private async Task ExecuteAsync(ICliRunner runner)
    {
        try { await runner.RunAsync(this, Token); _events.Writer.TryComplete(); }
        catch (Exception ex)
        {
            _events.Writer.TryComplete(ex is ProxyException ? ex : CliProtocol.Failure("Claude process failed or was cancelled."));
        }
        finally { lock (_sync) { _lease?.Dispose(); _lease = null; } }
    }
    private async Task AcquireAsync(CancellationToken ct)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, Token);
        linked.CancelAfter(TimeSpan.FromSeconds(_options.GenerationSeconds));
        var lease = await _scheduler.AcquireAsync(Client, linked.Token);
        lock (_sync)
        {
            if (_stopped) { lease.Dispose(); throw new ProxyException(410, "expired_continuation", "The continuation has ended."); }
            _lease = lease;
            Expires = DateTimeOffset.UtcNow.AddSeconds(_options.GenerationSeconds);
        }
    }
    public ValueTask EmitAsync(RunEvent value, CancellationToken ct) => _events.Writer.WriteAsync(value, ct);
    public IAsyncEnumerable<RunEvent> Events(CancellationToken ct) => _events.Reader.ReadAllAsync(ct);
    public async Task<CallToolResult> InvokeAsync(CallToolRequestParams request, Action<Invocation> register, CancellationToken ct)
    {
        Invocation invocation;
        lock (_sync)
        {
            if (_stopped || !Request.Tools.Any(t => t.Name == request.Name)) throw CliProtocol.Failure("Unexpected private tool invocation.");
            if (_calls.Count >= 64) throw CliProtocol.Failure("Too many tool invocations in one run.");
            invocation = new Invocation(request.Name!, System.Text.Json.JsonSerializer.SerializeToNode(request.Arguments ?? new Dictionary<string, System.Text.Json.JsonElement>())!, this);
            register(invocation);
            _calls.Add(invocation);
            ToolWasCalled = true;
            _lease?.Dispose(); _lease = null;
            Expires = DateTimeOffset.UtcNow.AddSeconds(_options.ContinuationSeconds);
        }
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, Token);
        await EmitAsync(new RunEvent(Tool: invocation), linked.Token);
        var result = await invocation.Result.Task.WaitAsync(linked.Token);
        return new CallToolResult { Content = [new TextContentBlock { Text = result }] };
    }
    public async Task SubmitAsync(Invocation invocation, string result, CancellationToken ct)
    {
        lock (_sync)
        {
            if (_stopped || invocation.Submitted || !invocation.Exposed) throw new ProxyException(409, "conflicting_continuation", "This tool result is not awaiting submission.");
            invocation.Submitted = true;
        }
        await AcquireAsync(ct);
        lock (_sync)
        {
            // A native parallel batch cannot continue until every actual call has a result.
            if (_calls.Any(c => !c.Submitted))
            {
                _lease?.Dispose(); _lease = null;
                Expires = DateTimeOffset.UtcNow.AddSeconds(_options.ContinuationSeconds);
            }
            invocation.Result.TrySetResult(result);
        }
    }
    public ValueTask DisposeAsync()
    {
        lock (_sync) return new ValueTask(_stopping ??= StopAsync());
    }
    private async Task StopAsync()
    {
        _stopped = true;
        await _lifetime.CancelAsync();
        if (_runner is not null) await _runner;
        lock (_sync) { _lease?.Dispose(); _lease = null; }
        _events.Writer.TryComplete(new ProxyException(410, "expired_continuation", "The continuation ended."));
    }
}

public sealed class ConversationBroker(FairScheduler scheduler, ICliRunner runner, ProxyOptions options) : IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, Conversation> _runs = new();
    private readonly ConcurrentDictionary<string, CallRecord> _calls = new();
    private readonly object _admission = new();
    private bool _shutdown;
    private sealed record CallRecord(string Client, Invocation? Invocation, DateTimeOffset RetainUntil);
    public int Count => _runs.Count;
    internal Conversation[] ActiveConversations => _runs.Values.ToArray();
    public Conversation? Private(string id, string authorization) => _runs.TryGetValue(id, out var run)
        && CryptographicOperations.FixedTimeEquals(System.Text.Encoding.UTF8.GetBytes("Bearer " + run.BridgeToken), System.Text.Encoding.UTF8.GetBytes(authorization)) ? run : null;
    public async Task<Conversation> BeginAsync(string client, ChatRequest request, CancellationToken ct)
    {
        Conversation? old = null;
        Invocation? invocation = null;
        if (request.Messages[^1]?["role"]?.GetValue<string>() == "tool")
        {
            var id = request.Messages[^1]!["tool_call_id"]!.GetValue<string>();
            if (!_calls.TryGetValue(id, out var record) || record.Client != client)
                throw new ProxyException(409, "unknown_continuation", "Unknown or foreign tool-call ID.");
            invocation = record.Invocation ?? throw new ProxyException(410, "expired_continuation", "The continuation expired or ended.");
            old = invocation.Conversation;
            if (old.Stopped) throw new ProxyException(410, "expired_continuation", "The continuation expired or ended.");
            if (!old.TryEnter()) throw new ProxyException(409, "continuation_busy", "A request already owns this continuation.");
            try
            {
                if (invocation.Submitted || !invocation.Exposed) throw new ProxyException(409, "duplicate_result", "The invocation is not awaiting this result.");
                var prefix = (JsonArray)request.Messages.DeepClone(); prefix.RemoveAt(prefix.Count - 1);
                if (request.Compatibility == old.Request.Compatibility && Protocol.History(prefix) == Protocol.History(old.ExpectedHistory))
                {
                    old.ExpectedHistory = (JsonArray)request.Messages.DeepClone();
                    try { await old.SubmitAsync(invocation, Protocol.Text(request.Messages[^1]!["content"]), ct); }
                    catch { await RemoveAsync(old); throw; }
                    return old;
                }
                await RemoveAsync(old);
            }
            catch { old.Leave(); throw; }
            old.Leave();
        }
        // A compacted request that still identifies our prior run invalidates it even
        // when its latest message is a new user turn. Completely disjoint histories
        // cannot be associated without inventing a session extension.
        foreach (var message in request.Messages)
            if (message?["tool_calls"] is JsonArray historicalCalls)
                foreach (var call in historicalCalls)
                    if (_calls.TryGetValue(call!["id"]!.GetValue<string>(), out var previous))
                    {
                        if (previous.Client != client) throw new ProxyException(409, "foreign_history", "History contains another application's tool-call ID.");
                        if (previous.Invocation is { } prior && !prior.Conversation.Stopped)
                        {
                            if (!prior.Conversation.TryEnter()) throw new ProxyException(409, "continuation_busy", "A request already owns this continuation.");
                            try { await RemoveAsync(prior.Conversation); }
                            finally { prior.Conversation.Leave(); }
                        }
                    }
        var run = new Conversation(client, request, scheduler, options);
        lock (_admission)
        {
            if (_shutdown) throw new ProxyException(503, "shutting_down", "The proxy is stopping.");
            if (_runs.Count >= options.MaxContinuations || _calls.Count + (_runs.Count + 1) * 64 > 16384)
                throw new ProxyException(429, "continuation_capacity", "Continuation capacity reached; wait for expiry.");
            run.TryEnter();
            _runs[run.Id] = run;
        }
        try { await run.StartAsync(runner, ct); return run; }
        catch { await RemoveAsync(run); run.Leave(); throw; }
    }
    public async Task<CallToolResult> InvokeAsync(Conversation run, CallToolRequestParams request, CancellationToken ct)
    {
        try { return await run.InvokeAsync(request, invocation => _calls[invocation.Id] = new CallRecord(run.Client, invocation, DateTimeOffset.MaxValue), ct); }
        catch { await RemoveAsync(run); throw; }
    }
    public async Task RemoveAsync(Conversation run)
    {
        _runs.TryRemove(run.Id, out _);
        await run.DisposeAsync();
        foreach (var call in _calls.Where(p => p.Value.Invocation?.Conversation == run))
            _calls[call.Key] = new CallRecord(run.Client, null, DateTimeOffset.UtcNow.AddSeconds(options.ContinuationSeconds));
    }
    public async Task SweepAsync()
    {
        foreach (var run in _runs.Values.Where(r => r.Expires <= DateTimeOffset.UtcNow)) await RemoveAsync(run);
        foreach (var call in _calls.Where(p => p.Value.RetainUntil < DateTimeOffset.UtcNow)) _calls.TryRemove(call.Key, out _);
    }
    public async ValueTask DisposeAsync()
    {
        lock (_admission) _shutdown = true;
        foreach (var run in _runs.Values) await RemoveAsync(run);
    }
}
