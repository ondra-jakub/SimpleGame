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

    private const string TagsJson =
        "{\"models\":[{\"name\":\"llama3\",\"size\":123},{\"name\":\"llama3\",\"size\":999},{\"name\":\"alpha\",\"size\":5}]}";
    private const string TagsWithCapabilitiesJson =
        "{\"models\":[{\"name\":\"d1:latest\",\"size\":3730544597,\"capabilities\":[\"decision\",\"vision\"]}," +
        "{\"name\":\"qwen3.5:0.8b\",\"size\":520000000,\"capabilities\":[\"completion\",\"vision\",\"tools\",\"thinking\"]}]}";
    private const string EmptyTagsJson = "{\"models\":[]}";
    private static readonly OpponentContext FullContext = new(100, 90, 1, 0, ["attack", "special", "heal"], "attack");

    private static string ChatResponse(string contentJson) =>
        "{\"model\":\"llama3\",\"message\":{\"role\":\"assistant\",\"thinking\":\"skryte uvazovani, ktere se nesmi pouzit\",\"content\":" +
        JsonSerializer.Serialize(contentJson) + "},\"done\":true}";

    private static string DecisionResponse(
        string choice = "special",
        string probabilities = "{\"attack\":0.407,\"special\":0.358,\"heal\":0.235}",
        double confidence = 0.11,
        double noul = 0.72) =>
        "{\"model\":\"d1:latest\",\"answers\":{\"akce\":{\"type\":\"choice\",\"choice\":\"" + choice + "\"," +
        "\"probabilities\":" + probabilities + ",\"confidence\":" + confidence.ToString(System.Globalization.CultureInfo.InvariantCulture) +
        "},\"ohrozeni\":{\"type\":\"noul\",\"noul\":" + noul.ToString(System.Globalization.CultureInfo.InvariantCulture) + "}},\"usage\":{\"input_tokens\":464}}";

    private static PiskvorkyContext SquareContext(string? attemptFeedback = null, int? lastMoveIndex = null)
    {
        var markedRow = new string('.', 15).Remove(3, 1).Insert(3, "X");
        var board = string.Join('\n', Enumerable.Range(0, 15).Select(row => row == 0 ? markedRow : new string('.', 15)));
        return new PiskvorkyContext(15, "X", board, 1, lastMoveIndex, attemptFeedback);
    }

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
        Assert.Equal("alpha", view.Models[0].Name);
        Assert.Equal(5, view.Models[0].SizeBytes);
        Assert.Equal("llama3", view.Models[1].Name);
        Assert.Equal(123, view.Models[1].SizeBytes);
    }

    [Fact]
    public async Task Tags_MissingCapabilitiesField_YieldsEmptyCapabilityList()
    {
        var (client, handler, _, _) = Create();
        handler.RespondWithJson(TagsJson);
        var view = await client.GetModelsAsync(false, CancellationToken.None);
        Assert.All(view.Models, model => Assert.Empty(model.Capabilities));
        Assert.All(view.Models, model => Assert.False(model.Supports("decision")));
    }

    [Fact]
    public async Task Tags_ParsesCapabilities()
    {
        var (client, handler, _, _) = Create();
        handler.RespondWithJson(TagsWithCapabilitiesJson);
        var view = await client.GetModelsAsync(false, CancellationToken.None);
        Assert.Equal(2, view.Models.Length);
        var d1 = view.Models.Single(m => m.Name == "d1:latest");
        Assert.Equal(["decision", "vision"], d1.Capabilities);
        Assert.True(d1.Supports("decision"));
        Assert.True(d1.Supports("Decision"));
        var qwen = view.Models.Single(m => m.Name == "qwen3.5:0.8b");
        Assert.Equal(["completion", "thinking", "tools", "vision"], qwen.Capabilities);
        Assert.False(qwen.Supports("decision"));
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
    public async Task Decision_ValidAnswer_ReturnsActionProbabilitiesConfidenceAndMood()
    {
        var (client, handler, _, _) = Create();
        handler.RespondWithJson(DecisionResponse());
        var decision = await client.ChooseDecisionMoveAsync("d1:latest", FullContext, CancellationToken.None);

        Assert.Equal("special", decision.Action);
        Assert.Null(decision.FallbackReason);
        Assert.NotNull(decision.Decision);
        Assert.Equal(0.407, decision.Decision!.Probabilities["attack"], 3);
        Assert.Equal(0.358, decision.Decision.Probabilities["special"], 3);
        Assert.Equal(0.235, decision.Decision.Probabilities["heal"], 3);
        Assert.Equal(0.11, decision.Decision.Confidence, 3);
        Assert.Equal(0.72, decision.Decision.Danger!.Value, 3);
        Assert.Equal("Hráč je v ohrožení", decision.Decision.DangerLabel.@true);
        Assert.Equal("Hráč je v bezpečí", decision.Decision.DangerLabel.@false);
        Assert.False(string.IsNullOrWhiteSpace(decision.Taunt));
        Assert.Contains("útok 41 %", decision.Taunt);
        Assert.Contains("speciální 36 %", decision.Taunt);
    }

    [Fact]
    public async Task Decision_SendsNamedQuestionsObjectStateAndKeepAlive()
    {
        var (client, handler, _, _) = Create();
        handler.RespondWithJson(DecisionResponse());
        var context = FullContext with { PlayerCooldown = 2, RecentLog = ["Kolo 1", "Hrac: utok za 14 HP."] };
        await client.ChooseDecisionMoveAsync("d1:latest", context, CancellationToken.None);

        Assert.Equal(1, handler.Calls);
        Assert.Equal("POST http://ollama/v1/systemone", handler.Requests[0]);

        using var body = JsonDocument.Parse(handler.Bodies[0]!);
        var root = body.RootElement;
        Assert.Equal("d1:latest", root.GetProperty("model").GetString());
        Assert.False(root.GetProperty("stream").GetBoolean());
        Assert.Equal("10m", root.GetProperty("keep_alive").GetString());

        var state = root.GetProperty("state");
        Assert.Equal(1, state.GetProperty("round").GetInt32());
        Assert.Equal(100, state.GetProperty("playerHp").GetInt32());
        Assert.Equal(90, state.GetProperty("opponentHp").GetInt32());
        Assert.Equal(2, state.GetProperty("playerCooldown").GetInt32());
        Assert.Equal(0, state.GetProperty("opponentCooldown").GetInt32());
        Assert.Equal("attack", state.GetProperty("lastPlayerAction").GetString());
        Assert.Equal(new string?[] { "attack", "special", "heal" },
            state.GetProperty("allowedActions").EnumerateArray().Select(e => e.GetString()));
        Assert.Equal(new string?[] { "Kolo 1", "Hrac: utok za 14 HP." },
            state.GetProperty("recentLog").EnumerateArray().Select(e => e.GetString()));

        var questions = root.GetProperty("questions");
        Assert.Equal(JsonValueKind.Object, questions.ValueKind);
        var akce = questions.GetProperty("akce");
        Assert.Equal("choice", akce.GetProperty("type").GetString());
        Assert.False(string.IsNullOrWhiteSpace(akce.GetProperty("instructions").GetString()));
        var criteria = akce.GetProperty("criteria");
        Assert.Equal(JsonValueKind.Object, criteria.ValueKind);
        Assert.Equal(new string?[] { "attack", "special", "heal" }, criteria.EnumerateObject().Select(e => e.Name));
        Assert.All(criteria.EnumerateObject(), entry => Assert.False(string.IsNullOrWhiteSpace(entry.Value.GetString())));

        var ohrozeni = questions.GetProperty("ohrozeni");
        Assert.Equal("noul", ohrozeni.GetProperty("type").GetString());
        var dangerCriteria = ohrozeni.GetProperty("criteria");
        Assert.Equal(JsonValueKind.Object, dangerCriteria.ValueKind);
        Assert.Equal(new string?[] { "true", "false" }, dangerCriteria.EnumerateObject().Select(e => e.Name));
    }

    [Fact]
    public async Task Decision_CriteriaContainOnlyAllowedActions()
    {
        var (client, handler, _, _) = Create();
        handler.RespondWithJson(DecisionResponse("attack", "{\"attack\":0.6,\"heal\":0.4}", 0.4, 0.1));
        var context = FullContext with { AllowedActions = ["attack", "heal"] };
        var decision = await client.ChooseDecisionMoveAsync("d1:latest", context, CancellationToken.None);
        Assert.Equal("attack", decision.Action);

        using var body = JsonDocument.Parse(handler.Bodies[0]!);
        var criteria = body.RootElement.GetProperty("questions").GetProperty("akce").GetProperty("criteria");
        Assert.Equal(new string?[] { "attack", "heal" }, criteria.EnumerateObject().Select(e => e.Name));
    }

    [Fact]
    public async Task Decision_ChoiceOutsideAllowedActions_FallsBack()
    {
        var (client, handler, _, _) = Create();
        handler.RespondWithJson(DecisionResponse("special"));
        var context = FullContext with { AllowedActions = ["attack", "heal"] };
        var decision = await client.ChooseDecisionMoveAsync("d1:latest", context, CancellationToken.None);
        Assert.Null(decision.Action);
        Assert.Contains("neplatné rozhodnutí", decision.FallbackReason);
        Assert.Null(decision.Decision);
    }

    [Fact]
    public async Task Decision_UnknownAction_FallsBack()
    {
        var (client, handler, _, _) = Create();
        handler.RespondWithJson(DecisionResponse("flee"));
        var decision = await client.ChooseDecisionMoveAsync("d1:latest", FullContext, CancellationToken.None);
        Assert.Null(decision.Action);
        Assert.Contains("neplatné rozhodnutí", decision.FallbackReason);
    }

    [Fact]
    public async Task Decision_EmptyProbabilities_FallsBack()
    {
        var (client, handler, _, _) = Create();
        handler.RespondWithJson(DecisionResponse("attack", "{}", 0.5, 0.2));
        var decision = await client.ChooseDecisionMoveAsync("d1:latest", FullContext, CancellationToken.None);
        Assert.Null(decision.Action);
        Assert.Contains("neplatné rozhodnutí", decision.FallbackReason);
    }

    [Fact]
    public async Task Decision_MissingAnswers_FallsBack()
    {
        var (client, handler, _, _) = Create();
        handler.RespondWithJson("{\"model\":\"d1:latest\"}");
        var decision = await client.ChooseDecisionMoveAsync("d1:latest", FullContext, CancellationToken.None);
        Assert.Null(decision.Action);
        Assert.Contains("neplatné rozhodnutí", decision.FallbackReason);
    }

    [Fact]
    public async Task Decision_InvalidJson_FallsBack()
    {
        var (client, handler, _, _) = Create();
        handler.RespondWithJson("not-json{{");
        var decision = await client.ChooseDecisionMoveAsync("d1:latest", FullContext, CancellationToken.None);
        Assert.Null(decision.Action);
        Assert.Contains("neplatné rozhodnutí", decision.FallbackReason);
    }

    [Fact]
    public async Task Decision_BadRequest_ReportsMissingDecisionSupport()
    {
        var (client, handler, _, _) = Create();
        handler.RespondWithJson("{\"error\":\"model is not a decision model\"}", HttpStatusCode.BadRequest);
        var decision = await client.ChooseDecisionMoveAsync("qwen3.5:0.8b", FullContext, CancellationToken.None);
        Assert.Null(decision.Action);
        Assert.Contains("neumí decision režim", decision.FallbackReason);
        Assert.Null(decision.Decision);
    }

    [Fact]
    public async Task Decision_NotFound_ReportsMissingModel()
    {
        var (client, handler, _, _) = Create();
        handler.RespondWithJson("{\"error\":\"model not found\"}", HttpStatusCode.NotFound);
        var decision = await client.ChooseDecisionMoveAsync("invalid-model-zzz", FullContext, CancellationToken.None);
        Assert.Null(decision.Action);
        Assert.Contains("není nainstalovaný", decision.FallbackReason);
    }

    [Fact]
    public async Task Decision_OtherHttpError_ReportsStatusCode()
    {
        var (client, handler, _, _) = Create();
        handler.RespondWithJson("{\"error\":\"boom\"}", HttpStatusCode.InternalServerError);
        var decision = await client.ChooseDecisionMoveAsync("d1:latest", FullContext, CancellationToken.None);
        Assert.Null(decision.Action);
        Assert.Contains("HTTP 500", decision.FallbackReason);
    }

    [Fact]
    public async Task Decision_ConnectionFailure_FallsBackWithConnectionReason()
    {
        var (client, handler, _, _) = Create();
        handler.ThrowConnectionFailure = true;
        var decision = await client.ChooseDecisionMoveAsync("d1:latest", FullContext, CancellationToken.None);
        Assert.Null(decision.Action);
        Assert.Contains("Spojení s Ollamou selhalo", decision.FallbackReason);
    }

    [Fact]
    public async Task Decision_Timeout_FallsBackExactlyOnce()
    {
        var (client, handler, _, _) = Create(new OllamaOptions { DecisionTimeoutSeconds = 0.01 });
        handler.Blocker = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var decision = await client.ChooseDecisionMoveAsync("d1:latest", FullContext, CancellationToken.None);
        Assert.Null(decision.Action);
        Assert.Contains("neodpověděl včas", decision.FallbackReason);
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task Decision_CallerCancellation_Propagates()
    {
        var (client, handler, _, _) = Create();
        handler.Blocker = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cts = new CancellationTokenSource();
        var pending = client.ChooseDecisionMoveAsync("d1:latest", FullContext, cts.Token);
        Assert.False(pending.IsCompleted);
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
    }

    [Fact]
    public async Task Decision_MissingDanger_KeepsDecisionWithNullDanger()
    {
        var (client, handler, _, _) = Create();
        handler.RespondWithJson("{\"answers\":{\"akce\":{\"choice\":\"attack\",\"probabilities\":{\"attack\":0.9},\"confidence\":0.8}}}");
        var decision = await client.ChooseDecisionMoveAsync("d1:latest", FullContext, CancellationToken.None);
        Assert.Equal("attack", decision.Action);
        Assert.NotNull(decision.Decision);
        Assert.Null(decision.Decision!.Danger);
        Assert.Equal(0.8, decision.Decision.Confidence, 3);
    }

    [Fact]
    public async Task Square_ValidMove_ParsesContentOnlyAndSendsRequiredSchema()
    {
        var (client, handler, _, _) = Create();
        handler.RespondWithJson(ChatResponse("{\"row\":7,\"col\":8}"));
        var decision = await client.ChooseSquareAsync("llama3", SquareContext(), CancellationToken.None);
        Assert.Equal(7, decision.Row);
        Assert.Equal(8, decision.Col);
        Assert.Null(decision.FallbackReason);
        Assert.Equal(1, handler.Calls);
        Assert.Equal("POST http://ollama/api/chat", handler.Requests[0]);

        using var body = JsonDocument.Parse(handler.Bodies[0]!);
        var root = body.RootElement;
        Assert.Equal("llama3", root.GetProperty("model").GetString());
        Assert.False(root.GetProperty("stream").GetBoolean());
        Assert.False(root.GetProperty("think").GetBoolean());
        Assert.Equal("10m", root.GetProperty("keep_alive").GetString());
        Assert.Equal(64, root.GetProperty("options").GetProperty("num_predict").GetInt32());
        var format = root.GetProperty("format");
        Assert.Equal("object", format.GetProperty("type").GetString());
        foreach (var field in new[] { "row", "col" })
        {
            var property = format.GetProperty("properties").GetProperty(field);
            Assert.Equal("integer", property.GetProperty("type").GetString());
            Assert.Equal(0, property.GetProperty("minimum").GetInt32());
            Assert.Equal(14, property.GetProperty("maximum").GetInt32());
        }
        Assert.Equal(new string?[] { "row", "col" }, format.GetProperty("required").EnumerateArray().Select(e => e.GetString()));
        Assert.False(format.GetProperty("additionalProperties").GetBoolean());
        var messages = root.GetProperty("messages").EnumerateArray().ToArray();
        Assert.Equal(2, messages.Length);
        Assert.Equal("system", messages[0].GetProperty("role").GetString());
        var system = messages[0].GetProperty("content").GetString();
        Assert.Contains("piskvork", system);
        Assert.Contains("pet kamenu", system);
        Assert.Equal("user", messages[1].GetProperty("role").GetString());
        using var contextJson = JsonDocument.Parse(messages[1].GetProperty("content").GetString()!);
        var context = contextJson.RootElement;
        Assert.Equal(15, context.GetProperty("size").GetInt32());
        Assert.Equal("X", context.GetProperty("playerSymbol").GetString());
        var boardText = context.GetProperty("boardText").GetString()!;
        Assert.Equal(15, boardText.Split('\n').Length);
        Assert.All(boardText.Split('\n'), line => Assert.Equal(15, line.Length));
        Assert.Contains("X", boardText);
        Assert.Equal(1, context.GetProperty("moveNumber").GetInt32());
        Assert.Equal(JsonValueKind.Null, context.GetProperty("lastMoveIndex").ValueKind);
        Assert.Equal(JsonValueKind.Null, context.GetProperty("attemptFeedback").ValueKind);
    }

    [Fact]
    public async Task Square_MissingOrEmptyContent_FallsBack()
    {
        var (client, handler, _, _) = Create();
        handler.RespondWithJson(ChatResponse(""));
        var first = await client.ChooseSquareAsync("llama3", SquareContext(), CancellationToken.None);
        Assert.Null(first.Row);
        Assert.Null(first.Col);
        Assert.Contains("neplatne souradnice", first.FallbackReason);

        var (client2, handler2, _, _) = Create();
        handler2.RespondWithJson("{\"model\":\"llama3\",\"message\":{\"role\":\"assistant\",\"content\":null},\"done\":true}");
        var second = await client2.ChooseSquareAsync("llama3", SquareContext(), CancellationToken.None);
        Assert.Null(second.Row);
        Assert.Contains("neplatne souradnice", second.FallbackReason);

        var (client3, handler3, _, _) = Create();
        handler3.RespondWithJson("{\"model\":\"llama3\",\"message\":{\"role\":\"assistant\"},\"done\":true}");
        var third = await client3.ChooseSquareAsync("llama3", SquareContext(), CancellationToken.None);
        Assert.Null(third.Row);
        Assert.Contains("neplatne souradnice", third.FallbackReason);
    }

    [Fact]
    public async Task Square_InvalidJsonTypesOrRange_FallsBack()
    {
        var (client, handler, _, _) = Create();
        handler.RespondWithJson(ChatResponse("ahoj, jak je?"));
        var invalid = await client.ChooseSquareAsync("llama3", SquareContext(), CancellationToken.None);
        Assert.Null(invalid.Row);
        Assert.Contains("neplatne souradnice", invalid.FallbackReason);

        var (client2, handler2, _, _) = Create();
        handler2.RespondWithJson(ChatResponse("{\"row\":7}"));
        var missing = await client2.ChooseSquareAsync("llama3", SquareContext(), CancellationToken.None);
        Assert.Null(missing.Col);
        Assert.Contains("neplatne souradnice", missing.FallbackReason);

        var (client3, handler3, _, _) = Create();
        handler3.RespondWithJson(ChatResponse("{\"row\":\"7\",\"col\":\"8\"}"));
        var strings = await client3.ChooseSquareAsync("llama3", SquareContext(), CancellationToken.None);
        Assert.Null(strings.Row);
        Assert.Contains("neplatne souradnice", strings.FallbackReason);

        var (client4, handler4, _, _) = Create();
        handler4.RespondWithJson(ChatResponse("{\"row\":15,\"col\":0}"));
        var outOfRange = await client4.ChooseSquareAsync("llama3", SquareContext(), CancellationToken.None);
        Assert.Null(outOfRange.Row);
        Assert.Contains("neplatne souradnice", outOfRange.FallbackReason);

        var (client5, handler5, _, _) = Create();
        handler5.RespondWithJson(ChatResponse("{\"row\":0,\"col\":-1}"));
        var negative = await client5.ChooseSquareAsync("llama3", SquareContext(), CancellationToken.None);
        Assert.Null(negative.Col);
        Assert.Contains("neplatne souradnice", negative.FallbackReason);

        var (client6, handler6, _, _) = Create();
        handler6.RespondWithJson(ChatResponse("{\"row\":7.5,\"col\":8}"));
        var fraction = await client6.ChooseSquareAsync("llama3", SquareContext(), CancellationToken.None);
        Assert.Null(fraction.Row);
        Assert.Contains("neplatne souradnice", fraction.FallbackReason);
    }

    [Fact]
    public async Task Square_AttemptFeedbackAndLastMove_TravelInUserMessage()
    {
        var (client, handler, _, _) = Create();
        handler.RespondWithJson(ChatResponse("{\"row\":3,\"col\":4}"));
        var context = SquareContext("Model zvolil jiz obsazene pole (radek 0, sloupec 0).", lastMoveIndex: 3);
        var decision = await client.ChooseSquareAsync("llama3", context, CancellationToken.None);
        Assert.Equal(3, decision.Row);
        Assert.Equal(4, decision.Col);

        using var body = JsonDocument.Parse(handler.Bodies[0]!);
        using var user = JsonDocument.Parse(body.RootElement.GetProperty("messages")[1].GetProperty("content").GetString()!);
        Assert.Equal("Model zvolil jiz obsazene pole (radek 0, sloupec 0).", user.RootElement.GetProperty("attemptFeedback").GetString());
        Assert.Equal(3, user.RootElement.GetProperty("lastMoveIndex").GetInt32());
    }

    [Fact]
    public async Task Square_HttpErrorForRemovedOrEmbeddingOnlyModel_FallsBackWithStatusReason()
    {
        var (client, handler, _, _) = Create();
        handler.RespondWithJson("{\"error\":\"model not found\"}", HttpStatusCode.NotFound);
        var removed = await client.ChooseSquareAsync("llama3", SquareContext(), CancellationToken.None);
        Assert.Null(removed.Row);
        Assert.Contains("HTTP 404", removed.FallbackReason);

        handler.RespondWithJson("{\"error\":\"does not support chat\"}", HttpStatusCode.BadRequest);
        var embeddingOnly = await client.ChooseSquareAsync("llama3", SquareContext(), CancellationToken.None);
        Assert.Null(embeddingOnly.Row);
        Assert.Contains("HTTP 400", embeddingOnly.FallbackReason);
    }

    [Fact]
    public async Task Square_ConnectionFailure_FallsBackWithConnectionReason()
    {
        var (client, handler, _, _) = Create();
        handler.ThrowConnectionFailure = true;
        var decision = await client.ChooseSquareAsync("llama3", SquareContext(), CancellationToken.None);
        Assert.Null(decision.Row);
        Assert.Null(decision.Col);
        Assert.Contains("Spojeni s Ollamou selhalo", decision.FallbackReason);
    }

    [Fact]
    public async Task Square_Timeout_FallsBackExactlyOnce()
    {
        var (client, handler, _, _) = Create(new OllamaOptions { MoveTimeoutSeconds = 0.01 });
        handler.Blocker = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var decision = await client.ChooseSquareAsync("llama3", SquareContext(), CancellationToken.None);
        Assert.Null(decision.Row);
        Assert.Contains("neodpovedel vcas", decision.FallbackReason);
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task Square_CallerCancellation_Propagates()
    {
        var (client, handler, _, _) = Create();
        handler.Blocker = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cts = new CancellationTokenSource();
        var pending = client.ChooseSquareAsync("llama3", SquareContext(), cts.Token);
        Assert.False(pending.IsCompleted);
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
    }
}
