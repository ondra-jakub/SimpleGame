using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SimpleGame;

namespace SimpleGame.Tests;

public sealed class OllamaTests
{
    private sealed class FakeClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class ScriptedHandler : HttpMessageHandler
    {
        private readonly Queue<HttpResponseMessage> responses = new();
        public TaskCompletionSource? Blocker { get; set; }
        public bool ThrowConnectionFailure { get; set; }
        public List<string> Requests { get; } = [];
        public List<string?> Bodies { get; } = [];
        public int Calls { get; private set; }

        public ScriptedHandler RespondWithJson(string json, HttpStatusCode status = HttpStatusCode.OK)
        {
            responses.Enqueue(new HttpResponseMessage(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") });
            return this;
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            Requests.Add($"{request.Method} {request.RequestUri}");
            Bodies.Add(request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken));
            if (Blocker is not null) await Blocker.Task.WaitAsync(cancellationToken);
            if (ThrowConnectionFailure) throw new HttpRequestException("Connection refused");
            if (responses.Count == 0) throw new InvalidOperationException("No scripted response");
            return responses.Dequeue();
        }
    }

    private const string TagsJson = "{\"models\":[{\"name\":\"llama3\",\"size\":123},{\"name\":\"llama3\",\"size\":999},{\"name\":\"alpha\",\"size\":5}]}";
    private const string EmptyTagsJson = "{\"models\":[]}";
    private static readonly OpponentContext FullContext = new(100, 90, 1, 0, ["attack", "special", "heal"], "attack");

    private static string ChatResponse(string contentJson) =>
        "{\"model\":\"llama3\",\"message\":{\"role\":\"assistant\",\"thinking\":\"skryte uvazovani, ktere se nesmi pouzit\",\"content\":" +
        JsonSerializer.Serialize(contentJson) + "},\"done\":true}";

    private static (OllamaClient Client, ScriptedHandler Handler, FakeClock Clock, ModelCatalogCache Cache) Create(OllamaOptions? options = null)
    {
        var handler = new ScriptedHandler();
        var clock = new FakeClock();
        var cache = new ModelCatalogCache();
        var client = new OllamaClient(
            new HttpClient(handler) { BaseAddress = new Uri("http://ollama/") },
            Options.Create(options ?? new OllamaOptions()),
            cache,
            clock,
            NullLogger<OllamaClient>.Instance);
        return (client, handler, clock, cache);
    }

    [Fact]
    public async Task Tags_EmptyCatalog_ReportsAvailableWithNoModels()
    {
        var (client, handler, _, _) = Create();
        handler.RespondWithJson(EmptyTagsJson);
        var view = await client.GetModelsAsync(false, CancellationToken.None);
        Assert.True(view.Available);
        Assert.Empty(view.Models);
        Assert.Null(view.Message);
        Assert.Equal("GET http://ollama/api/tags", handler.Requests[0]);
        Assert.Null(handler.Bodies[0]);
    }

    [Fact]
    public async Task Tags_SortsDeduplicatesAndMapsModels()
    {
        var (client, handler, _, _) = Create();
        handler.RespondWithJson(TagsJson);
        var view = await client.GetModelsAsync(false, CancellationToken.None);
        Assert.True(view.Available);
        Assert.Equal(2, view.Models.Length);
        Assert.Equal(new ModelInfo("alpha", 5), view.Models[0]);
        Assert.Equal(new ModelInfo("llama3", 123), view.Models[1]);
    }

    [Fact]
    public async Task Tags_HttpError_Unavailable()
    {
        var (client, handler, _, _) = Create();
        handler.RespondWithJson("{}", HttpStatusCode.InternalServerError);
        var view = await client.GetModelsAsync(false, CancellationToken.None);
        Assert.False(view.Available);
        Assert.Empty(view.Models);
        Assert.False(string.IsNullOrWhiteSpace(view.Message));
    }

    [Fact]
    public async Task Tags_ConnectionFailure_Unavailable()
    {
        var (client, handler, _, _) = Create();
        handler.ThrowConnectionFailure = true;
        var view = await client.GetModelsAsync(false, CancellationToken.None);
        Assert.False(view.Available);
        Assert.False(string.IsNullOrWhiteSpace(view.Message));
    }

    [Fact]
    public async Task Tags_InvalidJson_Unavailable()
    {
        var (client, handler, _, _) = Create();
        handler.RespondWithJson("not-json{{");
        var view = await client.GetModelsAsync(false, CancellationToken.None);
        Assert.False(view.Available);
    }

    [Fact]
    public async Task Tags_MalformedModelEntries_Unavailable()
    {
        var (client, handler, _, _) = Create();
        handler.RespondWithJson("{\"models\":[{\"name\":\"x\"}]}");
        Assert.False((await client.GetModelsAsync(true, CancellationToken.None)).Available);

        var (client2, handler2, _, _) = Create();
        handler2.RespondWithJson("{\"models\":[{\"name\":\"x\",\"size\":\"big\"}]}");
        Assert.False((await client2.GetModelsAsync(false, CancellationToken.None)).Available);

        var (client3, handler3, _, _) = Create();
        handler3.RespondWithJson("{\"models\":[{\"name\":\"x\",\"size\":-5}]}");
        Assert.False((await client3.GetModelsAsync(false, CancellationToken.None)).Available);
    }

    [Fact]
    public async Task Tags_Timeout_UnavailableWithoutRetry()
    {
        var (client, handler, _, _) = Create(new OllamaOptions { ModelsTimeoutSeconds = 0.01 });
        handler.Blocker = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var view = await client.GetModelsAsync(false, CancellationToken.None);
        Assert.False(view.Available);
        Assert.Contains("neodpovedela vcas", view.Message);
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task Tags_CachedFor30Seconds()
    {
        var (client, handler, clock, _) = Create();
        handler.RespondWithJson(TagsJson);
        var first = await client.GetModelsAsync(false, CancellationToken.None);
        Assert.True(first.Available);
        Assert.Equal(1, handler.Calls);

        var cached = await client.GetModelsAsync(false, CancellationToken.None);
        Assert.Same(first, cached);
        Assert.Equal(1, handler.Calls);

        clock.Now += TimeSpan.FromSeconds(29);
        await client.GetModelsAsync(false, CancellationToken.None);
        Assert.Equal(1, handler.Calls);

        clock.Now += TimeSpan.FromSeconds(1);
        handler.RespondWithJson(TagsJson);
        await client.GetModelsAsync(false, CancellationToken.None);
        Assert.Equal(2, handler.Calls);
    }

    [Fact]
    public async Task Tags_RefreshBypassesCache()
    {
        var (client, handler, _, _) = Create();
        handler.RespondWithJson(EmptyTagsJson);
        await client.GetModelsAsync(false, CancellationToken.None);
        handler.RespondWithJson(TagsJson);
        var refreshed = await client.GetModelsAsync(true, CancellationToken.None);
        Assert.Equal(2, handler.Calls);
        Assert.Equal(2, refreshed.Models.Length);
    }

    [Fact]
    public async Task Tags_UnavailableResult_CachedFor3Seconds()
    {
        var (client, handler, clock, _) = Create();
        handler.RespondWithJson("{}", HttpStatusCode.ServiceUnavailable);
        Assert.False((await client.GetModelsAsync(false, CancellationToken.None)).Available);
        Assert.False((await client.GetModelsAsync(false, CancellationToken.None)).Available);
        Assert.Equal(1, handler.Calls);

        clock.Now += TimeSpan.FromSeconds(3);
        handler.RespondWithJson(EmptyTagsJson);
        Assert.True((await client.GetModelsAsync(false, CancellationToken.None)).Available);
        Assert.Equal(2, handler.Calls);
    }

    [Fact]
    public async Task Tags_CallerCancellation_PropagatesWithoutCachingResult()
    {
        var (client, handler, _, _) = Create();
        handler.Blocker = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        handler.RespondWithJson(TagsJson);
        using var cts = new CancellationTokenSource();
        var pending = client.GetModelsAsync(false, cts.Token);
        Assert.False(pending.IsCompleted);
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);

        handler.Blocker = null;
        var view = await client.GetModelsAsync(false, CancellationToken.None);
        Assert.True(view.Available);
        Assert.Equal(2, handler.Calls);
    }

    [Fact]
    public async Task Chat_ValidMove_ParsesContentOnlyAndSendsRequiredSchema()
    {
        var (client, handler, _, _) = Create();
        handler.RespondWithJson(ChatResponse("{\"action\":\"special\",\"taunt\":\"Nazdar!\"}"));
        var decision = await client.ChooseMoveAsync("llama3", FullContext, CancellationToken.None);
        Assert.Equal("special", decision.Action);
        Assert.Equal("Nazdar!", decision.Taunt);
        Assert.Null(decision.FallbackReason);
        Assert.Equal(1, handler.Calls);
        Assert.Equal("POST http://ollama/api/chat", handler.Requests[0]);

        using var body = JsonDocument.Parse(handler.Bodies[0]!);
        var root = body.RootElement;
        Assert.Equal("llama3", root.GetProperty("model").GetString());
        Assert.False(root.GetProperty("stream").GetBoolean());
        Assert.False(root.GetProperty("think").GetBoolean());
        Assert.Equal("10m", root.GetProperty("keep_alive").GetString());
        Assert.Equal(256, root.GetProperty("options").GetProperty("num_predict").GetInt32());
        var format = root.GetProperty("format");
        Assert.Equal("object", format.GetProperty("type").GetString());
        Assert.Equal(new string?[] { "attack", "special", "heal" },
            format.GetProperty("properties").GetProperty("action").GetProperty("enum").EnumerateArray().Select(e => e.GetString()));
        Assert.Equal(240, format.GetProperty("properties").GetProperty("taunt").GetProperty("maxLength").GetInt32());
        Assert.Equal(new string?[] { "action", "taunt" }, format.GetProperty("required").EnumerateArray().Select(e => e.GetString()));
        Assert.False(format.GetProperty("additionalProperties").GetBoolean());
        var messages = root.GetProperty("messages").EnumerateArray().ToArray();
        Assert.Equal(2, messages.Length);
        Assert.Equal("system", messages[0].GetProperty("role").GetString());
        Assert.Contains("protivnik", messages[0].GetProperty("content").GetString());
        Assert.Equal("user", messages[1].GetProperty("role").GetString());
        using var contextJson = JsonDocument.Parse(messages[1].GetProperty("content").GetString()!);
        var context = contextJson.RootElement;
        Assert.Equal(100, context.GetProperty("playerHp").GetInt32());
        Assert.Equal(90, context.GetProperty("opponentHp").GetInt32());
        Assert.Equal(1, context.GetProperty("round").GetInt32());
        Assert.Equal(0, context.GetProperty("opponentCooldown").GetInt32());
        Assert.Equal(new string?[] { "attack", "special", "heal" }, context.GetProperty("allowedActions").EnumerateArray().Select(e => e.GetString()));
        Assert.Equal("attack", context.GetProperty("lastPlayerAction").GetString());
    }

    [Fact]
    public async Task Chat_MissingOrEmptyContent_FallsBack()
    {
        var (client, handler, _, _) = Create();
        handler.RespondWithJson(ChatResponse(""));
        var first = await client.ChooseMoveAsync("llama3", FullContext, CancellationToken.None);
        Assert.Null(first.Action);
        Assert.Contains("neplatny tah", first.FallbackReason);

        var (client2, handler2, _, _) = Create();
        handler2.RespondWithJson("{\"model\":\"llama3\",\"message\":{\"role\":\"assistant\",\"content\":null},\"done\":true}");
        var second = await client2.ChooseMoveAsync("llama3", FullContext, CancellationToken.None);
        Assert.Null(second.Action);
        Assert.Contains("neplatny tah", second.FallbackReason);

        var (client3, handler3, _, _) = Create();
        handler3.RespondWithJson("{\"model\":\"llama3\",\"message\":{\"role\":\"assistant\"},\"done\":true}");
        var third = await client3.ChooseMoveAsync("llama3", FullContext, CancellationToken.None);
        Assert.Null(third.Action);
        Assert.Contains("neplatny tah", third.FallbackReason);
    }

    [Fact]
    public async Task Chat_InvalidJsonContent_FallsBack()
    {
        var (client, handler, _, _) = Create();
        handler.RespondWithJson(ChatResponse("ahoj, jak je?"));
        var decision = await client.ChooseMoveAsync("llama3", FullContext, CancellationToken.None);
        Assert.Null(decision.Action);
        Assert.Contains("neplatny tah", decision.FallbackReason);
    }

    [Fact]
    public async Task Chat_UnknownAction_FallsBack()
    {
        var (client, handler, _, _) = Create();
        handler.RespondWithJson(ChatResponse("{\"action\":\"flee\",\"taunt\":\"utek\"}"));
        var decision = await client.ChooseMoveAsync("llama3", FullContext, CancellationToken.None);
        Assert.Null(decision.Action);
        Assert.Contains("neplatny tah", decision.FallbackReason);
    }

    [Fact]
    public async Task Chat_MissingOrNullTaunt_FallsBack()
    {
        var (client, handler, _, _) = Create();
        handler.RespondWithJson(ChatResponse("{\"action\":\"attack\"}"));
        var missing = await client.ChooseMoveAsync("llama3", FullContext, CancellationToken.None);
        Assert.Null(missing.Action);
        Assert.Contains("neplatny tah", missing.FallbackReason);

        var (client2, handler2, _, _) = Create();
        handler2.RespondWithJson(ChatResponse("{\"action\":\"attack\",\"taunt\":null}"));
        var nullTaunt = await client2.ChooseMoveAsync("llama3", FullContext, CancellationToken.None);
        Assert.Null(nullTaunt.Action);
        Assert.Contains("neplatny tah", nullTaunt.FallbackReason);
    }

    [Fact]
    public async Task Chat_SpecialBlockedByCooldown_FallsBack()
    {
        var (client, handler, _, _) = Create();
        handler.RespondWithJson(ChatResponse("{\"action\":\"special\",\"taunt\":\"bouchnu\"}"));
        var context = FullContext with { OpponentCooldown = 2, AllowedActions = ["attack", "heal"] };
        var decision = await client.ChooseMoveAsync("llama3", context, CancellationToken.None);
        Assert.Null(decision.Action);
        Assert.Contains("neplatny tah", decision.FallbackReason);
    }

    [Fact]
    public async Task Chat_HttpErrorForRemovedOrEmbeddingOnlyModel_FallsBackWithStatusReason()
    {
        var (client, handler, _, _) = Create();
        handler.RespondWithJson("{\"error\":\"model not found\"}", HttpStatusCode.NotFound);
        var removed = await client.ChooseMoveAsync("llama3", FullContext, CancellationToken.None);
        Assert.Null(removed.Action);
        Assert.Contains("HTTP 404", removed.FallbackReason);

        handler.RespondWithJson("{\"error\":\"does not support chat\"}", HttpStatusCode.BadRequest);
        var embeddingOnly = await client.ChooseMoveAsync("llama3", FullContext, CancellationToken.None);
        Assert.Null(embeddingOnly.Action);
        Assert.Contains("HTTP 400", embeddingOnly.FallbackReason);
    }

    [Fact]
    public async Task Chat_ConnectionFailure_FallsBackWithConnectionReason()
    {
        var (client, handler, _, _) = Create();
        handler.ThrowConnectionFailure = true;
        var decision = await client.ChooseMoveAsync("llama3", FullContext, CancellationToken.None);
        Assert.Null(decision.Action);
        Assert.Contains("Spojeni s Ollamou selhalo", decision.FallbackReason);
    }

    [Fact]
    public async Task Chat_Timeout_FallsBackExactlyOnce()
    {
        var (client, handler, _, _) = Create(new OllamaOptions { MoveTimeoutSeconds = 0.01 });
        handler.Blocker = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var decision = await client.ChooseMoveAsync("llama3", FullContext, CancellationToken.None);
        Assert.Null(decision.Action);
        Assert.Contains("neodpovedel vcas", decision.FallbackReason);
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task Chat_CallerCancellation_Propagates()
    {
        var (client, handler, _, _) = Create();
        handler.Blocker = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cts = new CancellationTokenSource();
        var pending = client.ChooseMoveAsync("llama3", FullContext, cts.Token);
        Assert.False(pending.IsCompleted);
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
    }

    [Fact]
    public async Task Chat_Taunt_SanitizedAndTruncatedTo240()
    {
        var (client, handler, _, _) = Create();
        var raw = "\n  " + new string('a', 250) + "\t" + new string('b', 100) + "  ";
        handler.RespondWithJson(ChatResponse(JsonSerializer.Serialize(new { action = "attack", taunt = raw })));
        var decision = await client.ChooseMoveAsync("llama3", FullContext, CancellationToken.None);
        Assert.Equal("attack", decision.Action);
        Assert.Equal(238, decision.Taunt!.Length);
        Assert.All(decision.Taunt, c => Assert.Equal('a', c));
    }
}