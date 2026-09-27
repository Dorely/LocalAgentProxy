using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;

namespace LocalAgentProxy;

public static class Api
{
    public static WebApplication Build(LocalStore store, ProxyOptions options, ICliRunner? runner = null, bool checkLogin = true)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = [], ContentRootPath = store.Root });
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls(options.Url);
        builder.WebHost.ConfigureKestrel(k => k.Limits.MaxRequestBodySize = options.MaxRequestBytes);
        var scheduler = new FairScheduler(options.QueueCapacity);
        var broker = new ConversationBroker(scheduler, runner ?? new CliRunner(options, store), options);
        builder.Services.AddSingleton(broker);
        var app = builder.Build();
        var management = store.ManagementKey();
        app.Use(async (ctx, next) =>
        {
            try
            {
                if (ctx.Request.Headers.ContainsKey("Origin") || ctx.Request.Host.Host != "127.0.0.1"
                    || ctx.Connection.RemoteIpAddress is { } remote && !System.Net.IPAddress.IsLoopback(remote))
                    throw new ProxyException(403, "local_clients_only", "Only local non-browser clients are supported.");
                if (ctx.Request.Path == "/health/live") { await next(ctx); return; }
                if (ctx.Request.Path.StartsWithSegments("/internal")) { await next(ctx); return; }
                var authorization = ctx.Request.Headers.Authorization.ToString();
                if (ctx.Request.Path.StartsWithSegments("/management"))
                {
                    if (!System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(authorization), Encoding.UTF8.GetBytes("Bearer " + management)))
                        throw new ProxyException(401, "unauthorized", "A management credential is required.");
                }
                else ctx.Items["client"] = store.Authenticate(authorization) ?? throw new ProxyException(401, "unauthorized", "A valid application key is required.");
                await next(ctx);
            }
            catch (OperationCanceledException) when (ctx.RequestAborted.IsCancellationRequested) { }
            catch (Exception ex)
            {
                var error = ex as ProxyException ?? (ex is OperationCanceledException
                    ? new ProxyException(504, "generation_timeout", "The generation or queue deadline expired.")
                    : ex is BadHttpRequestException badRequest
                    ? new ProxyException(badRequest.StatusCode, "invalid_body", "The HTTP request body is invalid or exceeds configured limits.")
                    : ex is JsonException or InvalidOperationException or FormatException or NullReferenceException or ArgumentException
                    ? new ProxyException(400, "invalid_request", "The request has an invalid shape or value.")
                    : new ProxyException(500, "proxy_failure", "The proxy could not complete this request."));
                var payload = new { error = new { message = error.Message, type = error.Status >= 500 ? "server_error" : "invalid_request_error", code = error.Code } };
                if (ctx.Response.HasStarted)
                {
                    await ctx.Response.WriteAsync("data: " + JsonSerializer.Serialize(payload) + "\n\n", ctx.RequestAborted);
                    await ctx.Response.Body.FlushAsync(ctx.RequestAborted);
                }
                else { ctx.Response.StatusCode = error.Status; await ctx.Response.WriteAsJsonAsync(payload, ctx.RequestAborted); }
            }
        });
        app.MapGet("/health/live", () => Results.Json(new { status = "alive" }));
        async Task<IResult> Status(CancellationToken ct)
        {
            var ready = !checkLogin || await store.SubscriptionReadyAsync(options, ct);
            return Results.Json(new { status = ready ? "ready" : "not_ready", process_id = Environment.ProcessId,
                active_generation = scheduler.Active, queued = scheduler.Waiting, conversations = broker.Count,
                subscription_login = ready }, statusCode: ready ? 200 : 503);
        }
        app.MapGet("/health/ready", Status);
        app.MapGet("/v1/status", Status);
        app.MapGet("/management/status", Status);
        app.MapPost("/management/stop", (IHostApplicationLifetime lifetime) => { lifetime.StopApplication(); return Results.Ok(); });
        app.MapGet("/v1/models", () => new { @object = "list", data = options.Models.Select(id => new {
            id, @object = "model", created = 0, owned_by = "claude-cli", moving_alias = id is "sonnet" or "opus" or "haiku"
        }) });
        app.MapGet("/internal/{id}/catalog", (HttpContext ctx, string id) =>
        {
            var run = broker.Private(id, ctx.Request.Headers.Authorization.ToString()) ?? throw new ProxyException(401, "unauthorized", "Invalid private bridge credential.");
            return Results.Json(run.Request.Tools);
        });
        app.MapPost("/internal/{id}/invoke", async (HttpContext ctx, string id, CallToolRequestParams request) =>
        {
            var run = broker.Private(id, ctx.Request.Headers.Authorization.ToString()) ?? throw new ProxyException(401, "unauthorized", "Invalid private bridge credential.");
            return Results.Json(await broker.InvokeAsync(run, request, ctx.RequestAborted));
        });
        app.MapPost("/v1/chat/completions", async (HttpContext ctx) =>
        {
            if (!ctx.Request.HasJsonContentType()) throw new ProxyException(415, "content_type", "application/json is required.");
            var json = await JsonNode.ParseAsync(ctx.Request.Body, cancellationToken: ctx.RequestAborted) as JsonObject
                ?? throw new ProxyException(400, "invalid_request", "A JSON object is required.");
            var request = ChatRequest.Parse(json);
            if (checkLogin && !await store.SubscriptionReadyAsync(options, ctx.RequestAborted))
                throw new ProxyException(503, "subscription_not_ready", "Sign in using the native Claude CLI with your Claude plan. No API billing fallback is used.");
            var run = await broker.BeginAsync((string)ctx.Items["client"]!, request, ctx.RequestAborted);
            var keep = false;
            try
            {
                var id = "chatcmpl_" + Guid.NewGuid().ToString("N");
                var created = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                var text = new StringBuilder();
                Invocation? call = null;
                var finished = false;
                if (request.Stream)
                {
                    ctx.Response.ContentType = "text/event-stream";
                    ctx.Response.Headers.CacheControl = "no-cache";
                    await Chunk(new { role = "assistant" });
                }
                await foreach (var evt in run.Events(ctx.RequestAborted))
                {
                    if (evt.Text is { } delta)
                    {
                        text.Append(delta);
                        if (text.Length > options.MaxOutputCharacters) throw CliProtocol.Failure("Response exceeded configured bounds.");
                        if (request.Stream) await Chunk(new { content = delta });
                    }
                    if (evt.Tool is { } tool) { call = tool; break; }
                    if (evt.Finished) { finished = true; break; }
                }
                if (!finished && call is null) throw CliProtocol.Failure("Missing completion event.");
                if (call is null && request.RequireTool) throw CliProtocol.Failure("Required tool choice was not satisfied in this completion.");
                var reason = call is not null ? "tool_calls" : "stop";
                var assistant = new JsonObject { ["role"] = "assistant", ["content"] = text.Length > 0 ? text.ToString() : null };
                if (call is not null) assistant["tool_calls"] = new JsonArray(call.Wire());
                if (call is not null)
                {
                    call.Exposed = true;
                    run.ExpectedHistory.Add(assistant.DeepClone());
                }
                var usage = new TokenUsage(Math.Max(0, run.Usage.Input - run.ReportedUsage.Input), Math.Max(0, run.Usage.Output - run.ReportedUsage.Output));
                run.ReportedUsage = run.Usage;
                if (request.Stream)
                {
                    if (call is not null) await Chunk(new { tool_calls = new[] { new { index = 0, id = call.Id, type = "function", function = new { name = call.Name, arguments = call.Arguments.ToJsonString() } } } });
                    await Chunk(new { }, reason);
                    if (request.IncludeUsage) await Data(new { id, @object = "chat.completion.chunk", created, model = request.Model, choices = Array.Empty<object>(), usage = usage.ToWire() });
                    await ctx.Response.WriteAsync("data: [DONE]\n\n", ctx.RequestAborted);
                    await ctx.Response.Body.FlushAsync(ctx.RequestAborted);
                }
                else await ctx.Response.WriteAsJsonAsync(new { id, @object = "chat.completion", created, model = request.Model,
                    choices = new[] { new { index = 0, message = assistant, finish_reason = reason } }, usage = usage.ToWire() }, ctx.RequestAborted);
                if (call is not null)
                {
                    keep = true;
                }
                async Task Data(object value)
                {
                    await ctx.Response.WriteAsync("data: " + JsonSerializer.Serialize(value) + "\n\n", ctx.RequestAborted);
                    await ctx.Response.Body.FlushAsync(ctx.RequestAborted);
                }
                Task Chunk(object delta, string? finish = null) => Data(new { id, @object = "chat.completion.chunk", created,
                    model = request.Model, choices = new[] { new { index = 0, delta, finish_reason = finish } } });
            }
            finally { run.Leave(); if (!keep) await broker.RemoveAsync(run); }
        });
        app.MapFallback(() => Results.Json(new { error = new { code = "unsupported_endpoint", message = "Only OpenAI chat completions and models are supported." } }, statusCode: 404));
        return app;
    }
}
