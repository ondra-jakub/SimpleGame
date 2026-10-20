using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using SimpleGame;
using SimpleGame.Games;

namespace SimpleGame.Tests;

public sealed class RpgTests
{
    private sealed class FakeClock : TimeProvider
    {
        private DateTimeOffset now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class FakeHostLifetime : IHostApplicationLifetime
    {
        private readonly CancellationTokenSource started = new();
        private readonly CancellationTokenSource stopping = new();
        private readonly CancellationTokenSource stopped = new();
        public CancellationToken Token => started.Token;
        public CancellationToken ApplicationStarted => started.Token;
        public CancellationToken ApplicationStopping => stopping.Token;
        public CancellationToken ApplicationStopped => stopped.Token;
        public void StopApplication() => stopping.Cancel();
    }

    private sealed class ScriptedRandom : IGameRandom
    {
        private readonly Queue<double> doubles = new();
        private readonly Dictionary<(int Min, int MaxExclusive), Queue<int>> rolls = new();
        public List<(int Min, int MaxExclusive)> IntCalls { get; } = [];

        public ScriptedRandom Doubles(params double[] values)
        {
            foreach (var value in values) doubles.Enqueue(value);
            return this;
        }

        public ScriptedRandom Ints(int min, int maxExclusive, params int[] values)
        {
            if (!rolls.TryGetValue((min, maxExclusive), out var queue)) rolls[(min, maxExclusive)] = queue = new();
            foreach (var value in values) queue.Enqueue(value);
            return this;
        }

        public int Next(int minInclusive, int maxExclusive)
        {
            IntCalls.Add((minInclusive, maxExclusive));
            return rolls.TryGetValue((minInclusive, maxExclusive), out var queue) && queue.Count > 0 ? queue.Dequeue() : minInclusive;
        }

        public long NextInt64(long minInclusive, long maxExclusive) => minInclusive;
        public double NextDouble() => doubles.Count > 0 ? doubles.Dequeue() : 1.0;
    }

    private sealed class FakeOllama : IOllamaClient
    {
        public ModelsView Models { get; set; } = new(true, [new ModelInfo("d1:latest", 42, ["decision", "vision"])]);
        public OpponentDecision Decision { get; set; } = new("attack", "Jsem tu!");
        public TaskCompletionSource<OpponentDecision>? Blocker { get; set; }
        public int ModelsCalls { get; private set; }
        public bool? LastRefresh { get; private set; }
        public int ChooseCalls { get; private set; }
        public List<OpponentContext> Contexts { get; } = [];
        public List<CancellationToken> Tokens { get; } = [];

        public Task<ModelsView> GetModelsAsync(bool refresh, CancellationToken cancellationToken)
        {
            ModelsCalls++;
            LastRefresh = refresh;
            return Task.FromResult(Models);
        }

        public Task<OpponentDecision> ChooseDecisionMoveAsync(string model, OpponentContext context, CancellationToken cancellationToken)
        {
            ChooseCalls++;
            Contexts.Add(context);
            Tokens.Add(cancellationToken);
            return Blocker is null ? Task.FromResult(Decision) : Blocker.Task.WaitAsync(cancellationToken);
        }
    }

    private sealed class OtherSession : GameSession { }

    private static (RpgEngine Engine, ScriptedRandom Random, FakeOllama Ollama, FakeHostLifetime Lifetime, SessionStore Store) Create()
    {
        var random = new ScriptedRandom();
        var ollama = new FakeOllama();
        var lifetime = new FakeHostLifetime();
        var store = new SessionStore(Options.Create(new SessionOptions()), new FakeClock());
        var engine = new RpgEngine(store, random, ollama, lifetime);
        return (engine, random, ollama, lifetime, store);
    }

    private static Task<RpgView> Start(RpgEngine engine, string? model = null) =>
        engine.StartAsync(new RpgStartRequest(model), CancellationToken.None);

    private static Task<RpgView> Act(RpgEngine engine, Guid id, string action, int revision) =>
        engine.ActAsync(id, new RpgActionRequest(action, revision), CancellationToken.None);

    [Fact]
    public async Task Start_WithoutModel_CreatesClassicalSessionWithoutTouchingOllama()
    {
        var (engine, _, ollama, _, _) = Create();
        var view = await Start(engine);
        Assert.NotEqual(Guid.Empty, view.Id);
        Assert.Null(view.Model);
        Assert.Equal("classic", view.OpponentMode);
        Assert.Equal(100, view.PlayerHp);
        Assert.Equal(100, view.OpponentHp);
        Assert.Equal(0, view.PlayerCooldown);
        Assert.Equal(0, view.OpponentCooldown);
        Assert.Equal(1, view.Round);
        Assert.Equal(0, view.Revision);
        Assert.False(view.InProgress);
        Assert.Equal("playing", view.Status);
        Assert.Empty(view.Log);
        Assert.Null(view.LastOpponentMove);
        Assert.Equal(0, ollama.ModelsCalls);
        Assert.Equal(0, ollama.ChooseCalls);
    }

    [Fact]
    public async Task Start_WithDecisionModel_SwitchesSessionToDecisionMode()
    {
        var (engine, _, ollama, _, _) = Create();
        var view = await Start(engine, "d1:latest");
        Assert.Equal("d1:latest", view.Model);
        Assert.Equal("decision", view.OpponentMode);
        Assert.Equal(1, ollama.ModelsCalls);
        Assert.False(ollama.LastRefresh);
    }

    [Fact]
    public async Task Start_WithModelWithoutDecisionCapability_Rejected400()
    {
        var (engine, _, ollama, _, _) = Create();
        ollama.Models = new ModelsView(true, [new ModelInfo("qwen3.5:0.8b", 42, ["completion", "vision"])]);
        var ex = await Assert.ThrowsAsync<GameException>(() => Start(engine, "qwen3.5:0.8b"));
        Assert.Equal(400, ex.StatusCode);
        Assert.Equal("invalid_input", ex.Code);
        Assert.Equal(0, ollama.ChooseCalls);
    }

    [Fact]
    public async Task Start_WithModelWithoutCapabilityInformation_Rejected400()
    {
        var (engine, _, ollama, _, _) = Create();
        ollama.Models = new ModelsView(true, [new ModelInfo("legacy:model", 42)]);
        var ex = await Assert.ThrowsAsync<GameException>(() => Start(engine, "legacy:model"));
        Assert.Equal(400, ex.StatusCode);
        Assert.Equal("invalid_input", ex.Code);
    }

    [Fact]
    public async Task Start_WithUnknownModel_Rejected400()
    {
        var (engine, _, ollama, _, _) = Create();
        var ex = await Assert.ThrowsAsync<GameException>(() => Start(engine, "gpt4"));
        Assert.Equal(400, ex.StatusCode);
        Assert.Equal("invalid_input", ex.Code);
        Assert.Equal(0, ollama.ChooseCalls);
    }

    [Fact]
    public async Task Start_WithEmptyOrOverlongModelName_Rejected400WithoutCatalogCall()
    {
        var (engine, _, ollama, _, _) = Create();
        var empty = await Assert.ThrowsAsync<GameException>(() => Start(engine, ""));
        Assert.Equal(400, empty.StatusCode);
        var overlong = await Assert.ThrowsAsync<GameException>(() => Start(engine, new string('m', 257)));
        Assert.Equal(400, overlong.StatusCode);
        Assert.Equal(0, ollama.ModelsCalls);
    }

    [Fact]
    public async Task Start_WhenOllamaUnavailable_Returns503()
    {
        var (engine, _, ollama, _, _) = Create();
        ollama.Models = new ModelsView(false, [], "Ollama neni dostupna.");
        var ex = await Assert.ThrowsAsync<GameException>(() => Start(engine, "d1:latest"));
        Assert.Equal(503, ex.StatusCode);
        Assert.Equal("ollama_unavailable", ex.Code);
    }

    [Fact]
    public void Get_UnknownSession_Returns404()
    {
        var (engine, _, _, _, _) = Create();
        var ex = Assert.Throws<GameException>(() => engine.Get(Guid.NewGuid()));
        Assert.Equal(404, ex.StatusCode);
        Assert.Equal("session_not_found", ex.Code);
    }

    [Fact]
    public async Task Act_UnknownSession_Returns404()
    {
        var (engine, _, _, _, _) = Create();
        var ex = await Assert.ThrowsAsync<GameException>(() => Act(engine, Guid.NewGuid(), "attack", 0));
        Assert.Equal(404, ex.StatusCode);
    }

    [Fact]
    public void Get_SessionOfDifferentGameType_Returns404()
    {
        var (engine, _, _, _, store) = Create();
        var other = store.Add(new OtherSession());
        var ex = Assert.Throws<GameException>(() => engine.Get(other.Id));
        Assert.Equal(404, ex.StatusCode);
    }

    [Fact]
    public async Task Act_UnknownAction_RejectedWithoutMutation()
    {
        var (engine, _, _, _, _) = Create();
        var start = await Start(engine);
        foreach (var action in new[] { "flee", "ATTACK", "" })
        {
            var ex = await Assert.ThrowsAsync<GameException>(() => Act(engine, start.Id, action, 0));
            Assert.Equal(400, ex.StatusCode);
            Assert.Equal("invalid_input", ex.Code);
        }
        var view = engine.Get(start.Id);
        Assert.Equal(0, view.Revision);
        Assert.Equal(100, view.PlayerHp);
        Assert.Equal(100, view.OpponentHp);
        Assert.Empty(view.Log);
    }

    [Fact]
    public async Task Act_MissingOrNegativeRevision_Rejected400()
    {
        var (engine, _, _, _, _) = Create();
        var start = await Start(engine);
        var missing = await Assert.ThrowsAsync<GameException>(
            () => engine.ActAsync(start.Id, new RpgActionRequest("attack", null), CancellationToken.None));
        Assert.Equal(400, missing.StatusCode);
        var negative = await Assert.ThrowsAsync<GameException>(
            () => engine.ActAsync(start.Id, new RpgActionRequest("attack", -1), CancellationToken.None));
        Assert.Equal(400, negative.StatusCode);
        Assert.Equal(0, engine.Get(start.Id).Revision);
    }

    [Fact]
    public async Task Act_AttackTurn_UpdatesStateAtomically()
    {
        var (engine, _, _, _, _) = Create();
        var start = await Start(engine);
        var view = await Act(engine, start.Id, "attack", 0);
        Assert.Equal(90, view.OpponentHp);
        Assert.Equal(92, view.PlayerHp);
        Assert.Equal(2, view.Round);
        Assert.Equal(1, view.Revision);
        Assert.Equal(0, view.PlayerCooldown);
        Assert.Equal(0, view.OpponentCooldown);
        Assert.False(view.InProgress);
        Assert.Equal("playing", view.Status);
        Assert.Equal((IEnumerable<string>)["Kolo 1", "Hrac: utok za 10 HP.", "Protivnik: utok za 8 HP."], view.Log);
        Assert.Equal("classic", view.LastOpponentMove!.Source);
        Assert.Null(view.LastOpponentMove.Decision);
    }

    [Fact]
    public async Task Act_Heal_ClampsTo100Hp()
    {
        var (engine, random, _, _, _) = Create();
        var start = await Start(engine);
        await Act(engine, start.Id, "attack", 0);
        random.Ints(15, 26, 20);
        var view = await Act(engine, start.Id, "heal", 1);
        Assert.Contains("Hrac se leci: +8 HP.", view.Log);
        Assert.True(view.PlayerHp <= 100);
        Assert.Equal(92, view.PlayerHp);
    }

    [Fact]
    public async Task Act_HealAtFullHp_ConsumesTurnWithZeroEffect()
    {
        var (engine, random, _, _, _) = Create();
        var start = await Start(engine);
        random.Ints(15, 26, 20);
        var view = await Act(engine, start.Id, "heal", 0);
        Assert.Contains("Hrac se leci: +0 HP.", view.Log);
        Assert.Equal(100, view.OpponentHp);
        Assert.Equal(92, view.PlayerHp);
        Assert.Equal(1, view.Revision);
        Assert.Equal(2, view.Round);
    }

    [Fact]
    public async Task Act_OpponentHeal_ClampsTo100Hp()
    {
        var (engine, random, ollama, _, _) = Create();
        ollama.Decision = new OpponentDecision("heal");
        random.Ints(12, 21, 20);
        var start = await Start(engine, "d1:latest");
        var view = await Act(engine, start.Id, "attack", 0);
        Assert.Equal(100, view.OpponentHp);
        Assert.Contains("Protivnik se leci: +10 HP.", view.Log);
        Assert.Equal("heal", view.LastOpponentMove!.Action);
    }

    [Fact]
    public async Task Act_SpecialMiss_StillStartsCooldown()
    {
        var (engine, random, _, _, _) = Create();
        var start = await Start(engine);
        random.Doubles(0.0);
        var view = await Act(engine, start.Id, "special", 0);
        Assert.Contains("Hrac: special minul.", view.Log);
        Assert.Equal(100, view.OpponentHp);
        Assert.Equal(3, view.PlayerCooldown);
    }

    [Fact]
    public async Task Act_SpecialCooldown_ProgressesThreeTwoOneZero()
    {
        var (engine, _, ollama, _, _) = Create();
        var start = await Start(engine, "d1:latest");

        var view = await Act(engine, start.Id, "special", 0);
        Assert.Equal(3, view.PlayerCooldown);
        Assert.Equal(80, view.OpponentHp);

        for (var (revision, cooldown) = (1, 2); revision < 4; revision++, cooldown--)
        {
            var rejected = await Assert.ThrowsAsync<GameException>(() => Act(engine, start.Id, "special", revision));
            Assert.Equal(400, rejected.StatusCode);
            Assert.Equal("invalid_input", rejected.Code);
            view = await Act(engine, start.Id, "attack", revision);
            Assert.Equal(cooldown, view.PlayerCooldown);
        }
        Assert.Equal(0, view.PlayerCooldown);

        view = await Act(engine, start.Id, "special", 4);
        Assert.Equal(3, view.PlayerCooldown);
        Assert.Equal(30, view.OpponentHp);
        Assert.Equal(5, ollama.ChooseCalls);
    }

    [Fact]
    public async Task Act_OpponentSpecialCooldown_ProgressesAndRestrictsAllowedActions()
    {
        var (engine, _, ollama, _, _) = Create();
        var start = await Start(engine, "d1:latest");
        ollama.Decision = new OpponentDecision("special", "Priprav se!");

        var view = await Act(engine, start.Id, "attack", 0);
        Assert.Equal(3, view.OpponentCooldown);
        Assert.Equal(82, view.PlayerHp);
        Assert.Equal((IEnumerable<string>)["attack", "special", "heal"], ollama.Contexts[0].AllowedActions);
        ollama.Decision = new OpponentDecision("attack");

        for (var (revision, cooldown) = (1, 2); revision < 4; revision++, cooldown--)
        {
            view = await Act(engine, start.Id, "attack", revision);
            Assert.Equal(cooldown, view.OpponentCooldown);
            Assert.Equal((IEnumerable<string>)["attack", "heal"], ollama.Contexts[revision].AllowedActions);
            Assert.Equal(4 - revision, ollama.Contexts[revision].OpponentCooldown);
        }
        view = await Act(engine, start.Id, "attack", 4);
        Assert.Equal(0, view.OpponentCooldown);
        Assert.Contains("special", ollama.Contexts[4].AllowedActions);
    }

    [Fact]
    public async Task Act_BothSidesSpecial_SameRound_BothCooldownsTickTogether()
    {
        var (engine, random, ollama, _, _) = Create();
        var start = await Start(engine, "d1:latest");
        ollama.Decision = new OpponentDecision("special");
        random.Doubles(0.0, 0.9);

        var view = await Act(engine, start.Id, "special", 0);
        Assert.Equal(3, view.PlayerCooldown);
        Assert.Equal(3, view.OpponentCooldown);
        Assert.Contains("Hrac: special minul.", view.Log);

        ollama.Decision = new OpponentDecision("attack");
        view = await Act(engine, start.Id, "attack", 1);
        Assert.Equal(2, view.PlayerCooldown);
        Assert.Equal(2, view.OpponentCooldown);
    }

    [Fact]
    public async Task Act_SpecialWhileOnCooldown_RejectedWithoutAnyMutation()
    {
        var (engine, _, ollama, _, _) = Create();
        var start = await Start(engine, "d1:latest");
        await Act(engine, start.Id, "special", 0);
        var before = engine.Get(start.Id);
        var calls = ollama.ChooseCalls;

        var ex = await Assert.ThrowsAsync<GameException>(() => Act(engine, start.Id, "special", before.Revision));
        Assert.Equal(400, ex.StatusCode);

        var after = engine.Get(start.Id);
        Assert.Equal(before.PlayerHp, after.PlayerHp);
        Assert.Equal(before.OpponentHp, after.OpponentHp);
        Assert.Equal(before.PlayerCooldown, after.PlayerCooldown);
        Assert.Equal(before.OpponentCooldown, after.OpponentCooldown);
        Assert.Equal(before.Revision, after.Revision);
        Assert.Equal(before.Round, after.Round);
        Assert.Equal(before.Status, after.Status);
        Assert.False(after.InProgress);
        Assert.Equal(before.LastOpponentMove, after.LastOpponentMove);
        Assert.Equal((IEnumerable<string>)before.Log, after.Log);
        Assert.Equal(calls, ollama.ChooseCalls);
    }

    [Fact]
    public async Task Act_DecisionMove_UsesServerTauntAndContext()
    {
        var (engine, _, ollama, _, _) = Create();
        var detail = new DecisionDetail(
            new Dictionary<string, double> { ["attack"] = 0.4, ["special"] = 0.35, ["heal"] = 0.25 },
            0.11, 0.72, "Hráč je v ohrožení", "Hráč je v bezpečí");
        ollama.Decision = new OpponentDecision("special", "Protivnik vaha: special 35 %.", Decision: detail);
        var start = await Start(engine, "d1:latest");
        var view = await Act(engine, start.Id, "attack", 0);

        Assert.Equal(1, ollama.ChooseCalls);
        var context = ollama.Contexts[0];
        Assert.Equal(100, context.PlayerHp);
        Assert.Equal(90, context.OpponentHp);
        Assert.Equal(1, context.Round);
        Assert.Equal(0, context.OpponentCooldown);
        Assert.Equal(0, context.PlayerCooldown);
        Assert.Equal("attack", context.LastPlayerAction);
        Assert.Equal((IEnumerable<string>)["attack", "special", "heal"], context.AllowedActions);
        Assert.NotEmpty(context.RecentLog);

        Assert.Equal("decision", view.LastOpponentMove!.Source);
        Assert.Equal("d1:latest", view.LastOpponentMove.Model);
        Assert.Equal("special", view.LastOpponentMove.Action);
        Assert.Equal("Protivnik vaha: special 35 %.", view.LastOpponentMove.Taunt);
        Assert.Null(view.LastOpponentMove.FallbackReason);
        Assert.NotNull(view.LastOpponentMove.Decision);
        Assert.Equal(0.11, view.LastOpponentMove.Decision!.Confidence, 3);
        Assert.Equal(0.72, view.LastOpponentMove.Decision.Danger!.Value, 3);
        Assert.Equal("Hráč je v ohrožení", view.LastOpponentMove.Decision.DangerLabel.@true);
        Assert.Equal(82, view.PlayerHp);
        Assert.Equal(3, view.OpponentCooldown);
        Assert.Contains("Protivnik: special za 18 HP.", view.Log);
        Assert.Contains("Protivnik: Protivnik vaha: special 35 %.", view.Log);
    }

    [Fact]
    public async Task Act_DecisionFallback_CompletesExactlyOneHeuristicTurn()
    {
        var (engine, _, ollama, _, _) = Create();
        var reason = "Model neodpověděl včas; tah převzala klasická AI.";
        ollama.Decision = new OpponentDecision(null, FallbackReason: reason);
        var start = await Start(engine, "d1:latest");
        var view = await Act(engine, start.Id, "attack", 0);

        Assert.Equal(1, ollama.ChooseCalls);
        Assert.Equal("heuristic", view.LastOpponentMove!.Source);
        Assert.Equal("d1:latest", view.LastOpponentMove.Model);
        Assert.Equal("attack", view.LastOpponentMove.Action);
        Assert.Equal(reason, view.LastOpponentMove.FallbackReason);
        Assert.Null(view.LastOpponentMove.Decision);
        Assert.Equal(1, view.Log.Count(entry => entry.StartsWith("Nahradni AI:")));
        Assert.Contains("Nahradni AI: " + reason, view.Log);
        Assert.Equal(92, view.PlayerHp);
        Assert.Equal(90, view.OpponentHp);
        Assert.Equal(1, view.Revision);
    }

    [Fact]
    public async Task Act_ClassicalAi_NeverCallsDecisionModel()
    {
        var (engine, _, ollama, _, _) = Create();
        var start = await Start(engine);
        var view = await Act(engine, start.Id, "attack", 0);
        Assert.Equal(0, ollama.ChooseCalls);
        Assert.Equal(0, ollama.ModelsCalls);
        Assert.Equal("classic", view.LastOpponentMove!.Source);
        Assert.Null(view.LastOpponentMove.Model);
        Assert.Null(view.LastOpponentMove.Taunt);
    }

    [Fact]
    public async Task Act_ClassicalAi_HealsAtLowHp()
    {
        var (engine, random, _, _, _) = Create();
        var start = await Start(engine);
        random.Ints(10, 19, 18, 18, 18, 18, 18);
        RpgView view = start;
        for (var revision = 0; revision < 4; revision++)
            view = await Act(engine, start.Id, "attack", revision);
        Assert.Equal(28, view.OpponentHp);
        random.Doubles(0.0);
        view = await Act(engine, start.Id, "attack", 4);
        Assert.Equal("heal", view.LastOpponentMove!.Action);
        Assert.Equal("classic", view.LastOpponentMove.Source);
        Assert.Equal(22, view.OpponentHp);
    }

    [Fact]
    public async Task Act_LethalPlayerAttack_SkipsDecisionModelAndWinsGame()
    {
        var (engine, random, ollama, _, _) = Create();
        random.Ints(10, 19, 18, 18, 18, 18, 18, 18);
        var start = await Start(engine, "d1:latest");
        RpgView view = start;
        for (var revision = 0; revision < 5; revision++)
            view = await Act(engine, start.Id, "attack", revision);
        Assert.Equal(10, view.OpponentHp);
        Assert.Equal(5, ollama.ChooseCalls);

        view = await Act(engine, start.Id, "attack", 5);
        Assert.Equal(5, ollama.ChooseCalls);
        Assert.Equal("won", view.Status);
        Assert.Equal(0, view.OpponentHp);
        Assert.Null(view.LastOpponentMove);
        Assert.Equal(60, view.PlayerHp);
        Assert.Equal(6, view.Revision);
        Assert.Contains("Kolo 6", view.Log);
        var finalRound = view.Log.SkipWhile(entry => entry != "Kolo 6");
        Assert.Equal((IEnumerable<string>)["Kolo 6", "Hrac: utok za 18 HP."], finalRound);

        var finished = await Assert.ThrowsAsync<GameException>(() => Act(engine, start.Id, "attack", 6));
        Assert.Equal(409, finished.StatusCode);
        Assert.Equal("conflict", finished.Code);
    }

    [Fact]
    public async Task Act_ConcurrentSameRevision_SecondGets409AndExactlyOneDecisionCallCommits()
    {
        var (engine, _, ollama, _, _) = Create();
        var start = await Start(engine, "d1:latest");
        ollama.Blocker = new TaskCompletionSource<OpponentDecision>(TaskCreationOptions.RunContinuationsAsynchronously);

        var first = engine.ActAsync(start.Id, new RpgActionRequest("attack", 0), CancellationToken.None);
        Assert.False(first.IsCompleted);

        var second = await Assert.ThrowsAsync<GameException>(() => engine.ActAsync(start.Id, new RpgActionRequest("attack", 0), CancellationToken.None));
        Assert.Equal(409, second.StatusCode);
        Assert.Equal("conflict", second.Code);
        Assert.Equal(1, ollama.ChooseCalls);

        ollama.Blocker.SetResult(new OpponentDecision("attack", "Ahoj"));
        var committed = await first;
        Assert.Equal(1, committed.Revision);
        Assert.Equal(90, committed.OpponentHp);
        Assert.Equal(1, ollama.ChooseCalls);

        var view = engine.Get(start.Id);
        Assert.Equal(1, view.Revision);
        Assert.False(view.InProgress);
    }

    [Fact]
    public async Task Act_StaleRevision_Rejected409()
    {
        var (engine, _, _, _, _) = Create();
        var start = await Start(engine);
        await Act(engine, start.Id, "attack", 0);
        var ex = await Assert.ThrowsAsync<GameException>(() => Act(engine, start.Id, "attack", 0));
        Assert.Equal(409, ex.StatusCode);
        Assert.Equal("conflict", ex.Code);
    }

    [Fact]
    public async Task Get_WhileTurnInProgress_ShowsOnlyConfirmedPreTurnState()
    {
        var (engine, _, ollama, _, _) = Create();
        var start = await Start(engine, "d1:latest");
        ollama.Blocker = new TaskCompletionSource<OpponentDecision>(TaskCreationOptions.RunContinuationsAsynchronously);

        var pending = engine.ActAsync(start.Id, new RpgActionRequest("attack", 0), CancellationToken.None);
        Assert.False(pending.IsCompleted);

        var view = engine.Get(start.Id);
        Assert.True(view.InProgress);
        Assert.Equal(0, view.Revision);
        Assert.Equal(100, view.PlayerHp);
        Assert.Equal(100, view.OpponentHp);
        Assert.Empty(view.Log);

        ollama.Blocker.SetResult(new OpponentDecision("attack", "Hotovo"));
        var done = await pending;
        Assert.Equal(1, done.Revision);
        var after = engine.Get(start.Id);
        Assert.False(after.InProgress);
        Assert.Equal(1, after.Revision);
        Assert.Equal(90, after.OpponentHp);
    }

    [Fact]
    public async Task Act_CallerCancellation_DiscardsPartialTurn()
    {
        var (engine, _, ollama, _, _) = Create();
        var start = await Start(engine, "d1:latest");
        ollama.Blocker = new TaskCompletionSource<OpponentDecision>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cts = new CancellationTokenSource();

        var pending = engine.ActAsync(start.Id, new RpgActionRequest("attack", 0), cts.Token);
        Assert.False(pending.IsCompleted);
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);

        var view = engine.Get(start.Id);
        Assert.Equal(0, view.Revision);
        Assert.Equal(100, view.PlayerHp);
        Assert.Equal(100, view.OpponentHp);
        Assert.Empty(view.Log);
        Assert.False(view.InProgress);
        Assert.Equal(1, ollama.ChooseCalls);

        ollama.Blocker = null;
        var retry = await Act(engine, start.Id, "attack", 0);
        Assert.Equal(1, retry.Revision);
        Assert.Equal(2, ollama.ChooseCalls);
    }

    [Fact]
    public async Task Act_ApplicationStopping_DiscardsPartialTurn()
    {
        var (engine, _, ollama, lifetime, _) = Create();
        var start = await Start(engine, "d1:latest");
        ollama.Blocker = new TaskCompletionSource<OpponentDecision>(TaskCreationOptions.RunContinuationsAsynchronously);

        var pending = engine.ActAsync(start.Id, new RpgActionRequest("attack", 0), CancellationToken.None);
        Assert.False(pending.IsCompleted);
        lifetime.StopApplication();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.True(ollama.Tokens[0].IsCancellationRequested);

        var view = engine.Get(start.Id);
        Assert.Equal(0, view.Revision);
        Assert.Equal(100, view.PlayerHp);
        Assert.Equal(100, view.OpponentHp);
        Assert.Empty(view.Log);
        Assert.False(view.InProgress);
    }
}
