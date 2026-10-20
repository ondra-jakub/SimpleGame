using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using SimpleGame;
using SimpleGame.Games;

namespace SimpleGame.Tests;

public sealed class PiskvorkyTests
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
        public CancellationToken ApplicationStarted => started.Token;
        public CancellationToken ApplicationStopping => stopping.Token;
        public CancellationToken ApplicationStopped => stopped.Token;
        public void StopApplication() => stopping.Cancel();
    }

    private sealed class ScriptedRandom : IGameRandom
    {
        private readonly Dictionary<(int Min, int MaxExclusive), Queue<int>> rolls = new();

        public ScriptedRandom Ints(int min, int maxExclusive, params int[] values)
        {
            if (!rolls.TryGetValue((min, maxExclusive), out var queue)) rolls[(min, maxExclusive)] = queue = new();
            foreach (var value in values) queue.Enqueue(value);
            return this;
        }

        public int Next(int minInclusive, int maxExclusive) =>
            rolls.TryGetValue((minInclusive, maxExclusive), out var queue) && queue.Count > 0 ? queue.Dequeue() : minInclusive;
        public long NextInt64(long minInclusive, long maxExclusive) => minInclusive;
        public double NextDouble() => 1.0;
    }

    private sealed class FakeOllama : IOllamaClient
    {
        public ModelsView Models { get; set; } = new(true, [new ModelInfo("llama3", 42)]);
        private readonly Queue<PiskvorkyDecision> decisions = new();
        private readonly Queue<PiskvorkyDecision> squareDecisions = new();
        public TaskCompletionSource<PiskvorkyDecision>? Blocker { get; set; }
        public int ModelsCalls { get; private set; }
        public int ChooseCalls { get; private set; }
        public int DecisionSquareCalls { get; private set; }
        public List<PiskvorkyContext> Contexts { get; } = [];
        public List<IReadOnlyList<(int Row, int Col, string Description)>> CandidateSquares { get; } = [];
        public List<CancellationToken> Tokens { get; } = [];

        public FakeOllama Decisions(params PiskvorkyDecision[] values)
        {
            foreach (var value in values) decisions.Enqueue(value);
            return this;
        }

        public FakeOllama SquareDecisions(params PiskvorkyDecision[] values)
        {
            foreach (var value in values) squareDecisions.Enqueue(value);
            return this;
        }

        public Task<ModelsView> GetModelsAsync(bool refresh, CancellationToken cancellationToken)
        {
            ModelsCalls++;
            return Task.FromResult(Models);
        }

        public Task<PiskvorkyDecision> ChooseSquareAsync(string model, PiskvorkyContext context, CancellationToken cancellationToken)
        {
            ChooseCalls++;
            Contexts.Add(context);
            Tokens.Add(cancellationToken);
            return Blocker is null
                ? Task.FromResult(decisions.Count > 0 ? decisions.Dequeue() : new PiskvorkyDecision(7, 7))
                : Blocker.Task.WaitAsync(cancellationToken);
        }

        public Task<PiskvorkyDecision> ChooseSquareDecisionAsync(string model, PiskvorkyContext context,
            IReadOnlyList<(int Row, int Col, string Description)> candidates, CancellationToken cancellationToken)
        {
            DecisionSquareCalls++;
            Contexts.Add(context);
            CandidateSquares.Add(candidates);
            Tokens.Add(cancellationToken);
            return Blocker is null
                ? Task.FromResult(squareDecisions.Count > 0 ? squareDecisions.Dequeue() : new PiskvorkyDecision(7, 7))
                : Blocker.Task.WaitAsync(cancellationToken);
        }

        public Task<OpponentDecision> ChooseDecisionMoveAsync(string model, OpponentContext context, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Piskvorky tests must not play RPG.");
    }

    private sealed class OtherSession : GameSession { }

    private static (PiskvorkyEngine Engine, ScriptedRandom Random, FakeOllama Ollama, FakeHostLifetime Lifetime, SessionStore Store) Create()
    {
        var random = new ScriptedRandom();
        var ollama = new FakeOllama();
        var lifetime = new FakeHostLifetime();
        var store = new SessionStore(Options.Create(new SessionOptions()), new FakeClock());
        var engine = new PiskvorkyEngine(store, random, ollama, lifetime);
        return (engine, random, ollama, lifetime, store);
    }

    private static Task<PiskvorkyView> Start(PiskvorkyEngine engine, string? xModel = null, string? oModel = null) =>
        engine.StartAsync(new PiskvorkyStartRequest(xModel, oModel), CancellationToken.None);

    private static Task<PiskvorkyView> Move(PiskvorkyEngine engine, Guid id, int? position, int revision) =>
        engine.MoveAsync(id, new PiskvorkyMoveRequest(position, revision), CancellationToken.None);

    // A null entry lets the model player decide through the fake Ollama queue.
    private static async Task<PiskvorkyView> PlayAll(PiskvorkyEngine engine, Guid id, params int?[] positions)
    {
        var view = engine.Get(id);
        var revision = view.Revision;
        foreach (var position in positions) view = await Move(engine, id, position, revision++);
        return view;
    }

    private static string EmptyBoard => new('.', 225);

    private static string BoardWith((int Index, char Mark)[] marks)
    {
        var board = EmptyBoard.ToCharArray();
        foreach (var (index, mark) in marks) board[index] = mark;
        return new string(board);
    }

    [Fact]
    public async Task Start_BothHumans_CreatesSessionWithoutTouchingOllama()
    {
        var (engine, _, ollama, _, _) = Create();
        var view = await Start(engine);
        Assert.NotEqual(Guid.Empty, view.Id);
        Assert.Equal("human", view.XMode);
        Assert.Equal("human", view.OMode);
        Assert.Null(view.XModel);
        Assert.Null(view.OModel);
        Assert.Equal(15, view.Size);
        Assert.Equal(EmptyBoard, view.Board);
        Assert.Equal("X", view.Current);
        Assert.Equal("playing", view.Status);
        Assert.Equal(0, view.Revision);
        Assert.Equal(0, view.MoveNumber);
        Assert.False(view.InProgress);
        Assert.Null(view.WinningLine);
        Assert.Null(view.LastMove);
        Assert.Equal(0, ollama.ModelsCalls);
        Assert.Equal(0, ollama.ChooseCalls);
    }

    [Fact]
    public async Task Start_WithModelsFromCatalog_AssignsBothPlayersAndQueriesCatalogOnce()
    {
        var (engine, _, ollama, _, _) = Create();
        var view = await Start(engine, "llama3", "llama3");
        Assert.Equal("model", view.XMode);
        Assert.Equal("model", view.OMode);
        Assert.Equal("llama3", view.XModel);
        Assert.Equal("llama3", view.OModel);
        Assert.Equal(1, ollama.ModelsCalls);

        var oneSide = await Start(engine, oModel: "llama3");
        Assert.Equal("human", oneSide.XMode);
        Assert.Equal("model", oneSide.OMode);
        Assert.Equal(2, ollama.ModelsCalls);
    }

    [Fact]
    public async Task Start_WithUnknownModel_Rejected400()
    {
        var (engine, _, ollama, _, _) = Create();
        var asX = await Assert.ThrowsAsync<GameException>(() => Start(engine, "gpt4"));
        Assert.Equal(400, asX.StatusCode);
        Assert.Equal("invalid_input", asX.Code);
        var asO = await Assert.ThrowsAsync<GameException>(() => Start(engine, null, "gpt4"));
        Assert.Equal(400, asO.StatusCode);
        Assert.Equal(0, ollama.ChooseCalls);
    }

    [Fact]
    public async Task Start_WithEmptyOrOverlongModelName_Rejected400WithoutCatalogCall()
    {
        var (engine, _, ollama, _, _) = Create();
        var empty = await Assert.ThrowsAsync<GameException>(() => Start(engine, ""));
        Assert.Equal(400, empty.StatusCode);
        var overlong = await Assert.ThrowsAsync<GameException>(() => Start(engine, null, new string('m', 257)));
        Assert.Equal(400, overlong.StatusCode);
        Assert.Equal(0, ollama.ModelsCalls);
    }

    [Fact]
    public async Task Start_WhenOllamaUnavailable_Returns503()
    {
        var (engine, _, ollama, _, _) = Create();
        ollama.Models = new ModelsView(false, [], "Ollama neni dostupna.");
        var ex = await Assert.ThrowsAsync<GameException>(() => Start(engine, "llama3"));
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
    public async Task Move_UnknownSession_Returns404()
    {
        var (engine, _, _, _, _) = Create();
        var ex = await Assert.ThrowsAsync<GameException>(() => Move(engine, Guid.NewGuid(), 0, 0));
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
    public async Task Move_MissingPosition_Rejected400()
    {
        var (engine, _, _, _, _) = Create();
        var start = await Start(engine);
        var ex = await Assert.ThrowsAsync<GameException>(() => Move(engine, start.Id, null, 0));
        Assert.Equal(400, ex.StatusCode);
        Assert.Equal("invalid_input", ex.Code);
        Assert.Equal(0, engine.Get(start.Id).Revision);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(225)]
    [InlineData(int.MaxValue)]
    public async Task Move_OutOfRangePosition_Rejected400(int position)
    {
        var (engine, _, _, _, _) = Create();
        var start = await Start(engine);
        var ex = await Assert.ThrowsAsync<GameException>(() => Move(engine, start.Id, position, 0));
        Assert.Equal(400, ex.StatusCode);
        Assert.Equal(EmptyBoard, engine.Get(start.Id).Board);
    }

    [Fact]
    public async Task Move_OccupiedSquare_Rejected400WithoutModelCall()
    {
        var (engine, _, ollama, _, _) = Create();
        var start = await Start(engine, oModel: "llama3");
        var first = await Move(engine, start.Id, 0, 0);
        Assert.Equal(0, ollama.ChooseCalls);
        var ex = await Assert.ThrowsAsync<GameException>(() => Move(engine, start.Id, 0, 1));
        Assert.Equal(400, ex.StatusCode);
        var view = engine.Get(start.Id);
        Assert.Equal(1, view.Revision);
        Assert.Equal(0, ollama.ChooseCalls);
    }

    [Fact]
    public async Task Move_MissingOrNegativeRevision_Rejected400()
    {
        var (engine, _, _, _, _) = Create();
        var start = await Start(engine);
        var missing = await Assert.ThrowsAsync<GameException>(
            () => engine.MoveAsync(start.Id, new PiskvorkyMoveRequest(0, null), CancellationToken.None));
        Assert.Equal(400, missing.StatusCode);
        var negative = await Assert.ThrowsAsync<GameException>(() => Move(engine, start.Id, 0, -1));
        Assert.Equal(400, negative.StatusCode);
        Assert.Equal(0, engine.Get(start.Id).Revision);
    }

    [Fact]
    public async Task Move_ModelMoveWithPosition_Rejected400()
    {
        var (engine, _, ollama, _, _) = Create();
        var start = await Start(engine, xModel: "llama3");
        var ex = await Assert.ThrowsAsync<GameException>(() => Move(engine, start.Id, 5, 0));
        Assert.Equal(400, ex.StatusCode);
        Assert.Equal("invalid_input", ex.Code);
        Assert.Equal(0, ollama.ChooseCalls);
        Assert.Equal(0, engine.Get(start.Id).Revision);
    }

    [Fact]
    public async Task Move_StaleRevision_Rejected409()
    {
        var (engine, _, _, _, _) = Create();
        var start = await Start(engine);
        await Move(engine, start.Id, 0, 0);
        var ex = await Assert.ThrowsAsync<GameException>(() => Move(engine, start.Id, 1, 0));
        Assert.Equal(409, ex.StatusCode);
        Assert.Equal("conflict", ex.Code);
    }

    [Fact]
    public async Task Move_AfterGameEnd_Rejected409()
    {
        var (engine, _, _, _, _) = Create();
        var start = await Start(engine);
        var view = await PlayAll(engine, start.Id, 0, 210, 1, 211, 2, 212, 3, 213, 4);
        Assert.Equal("x_won", view.Status);
        var finished = await Assert.ThrowsAsync<GameException>(() => Move(engine, start.Id, 5, view.Revision));
        Assert.Equal(409, finished.StatusCode);
        Assert.Equal("conflict", finished.Code);
    }

    [Fact]
    public async Task Move_HumanMove_AppliesMarkAndSwitchesPlayer()
    {
        var (engine, _, _, _, _) = Create();
        var start = await Start(engine);
        var view = await Move(engine, start.Id, 112, 0);
        Assert.Equal(BoardWith([(112, 'X')]), view.Board);
        Assert.Equal("O", view.Current);
        Assert.Equal("playing", view.Status);
        Assert.Equal(1, view.Revision);
        Assert.Equal(1, view.MoveNumber);
        Assert.Equal(new PiskvorkyMoveInfo("X", 112, "human", null, null), view.LastMove);
    }

    [Fact]
    public async Task Move_RowWins_ReportsWinningLine()
    {
        var (engine, _, _, _, _) = Create();
        var start = await Start(engine);
        var view = await PlayAll(engine, start.Id, 0, 210, 1, 211, 2, 212, 3, 213, 4);
        Assert.Equal("x_won", view.Status);
        Assert.Equal(new[] { 0, 1, 2, 3, 4 }, view.WinningLine);
        Assert.Equal("X", view.Current);
        Assert.Equal(9, view.Revision);
        Assert.Equal(9, view.MoveNumber);
    }

    [Fact]
    public async Task Move_ColumnWins_ReportsWinningLine()
    {
        var (engine, _, _, _, _) = Create();
        var start = await Start(engine);
        var view = await PlayAll(engine, start.Id, 0, 210, 15, 211, 30, 212, 45, 213, 60);
        Assert.Equal("x_won", view.Status);
        Assert.Equal(new[] { 0, 15, 30, 45, 60 }, view.WinningLine);
    }

    [Fact]
    public async Task Move_MainDiagonalWins_ReportsWinningLine()
    {
        var (engine, _, _, _, _) = Create();
        var start = await Start(engine);
        var view = await PlayAll(engine, start.Id, 0, 210, 16, 211, 32, 212, 48, 213, 64);
        Assert.Equal("x_won", view.Status);
        Assert.Equal(new[] { 0, 16, 32, 48, 64 }, view.WinningLine);
    }

    [Fact]
    public async Task Move_AntiDiagonalWins_ReportsWinningLine()
    {
        var (engine, _, _, _, _) = Create();
        var start = await Start(engine);
        var view = await PlayAll(engine, start.Id, 4, 210, 18, 211, 32, 212, 46, 213, 60);
        Assert.Equal("x_won", view.Status);
        Assert.Equal(new[] { 4, 18, 32, 46, 60 }, view.WinningLine);
    }

    [Fact]
    public async Task Move_SecondPlayerWins()
    {
        var (engine, _, _, _, _) = Create();
        var start = await Start(engine);
        var view = await PlayAll(engine, start.Id, 0, 210, 1, 211, 2, 212, 3, 213, 29, 214);
        Assert.Equal("o_won", view.Status);
        Assert.Equal(new[] { 210, 211, 212, 213, 214 }, view.WinningLine);
        Assert.Equal("O", view.Current);
        Assert.Equal(10, view.Revision);
    }

    [Fact]
    public async Task Move_SixInARow_ReportsWholeLine()
    {
        var (engine, _, _, _, _) = Create();
        var start = await Start(engine);
        var view = await PlayAll(engine, start.Id, 0, 210, 1, 211, 2, 212, 3, 213, 5, 180, 4);
        Assert.Equal("x_won", view.Status);
        Assert.Equal(new[] { 0, 1, 2, 3, 4, 5 }, view.WinningLine);
        Assert.Equal(6, view.WinningLine!.Length);
    }

    [Fact]
    public async Task Move_FullBoardWithoutFive_Draws()
    {
        var (engine, _, _, _, _) = Create();
        var start = await Start(engine);
        var xCells = new Queue<int>();
        var oCells = new Queue<int>();
        for (var row = 0; row < 15; row++)
            for (var col = 0; col < 15; col++)
                ((col + 2 * row) % 4 < 2 ? xCells : oCells).Enqueue(row * 15 + col);
        Assert.Equal(113, xCells.Count);
        Assert.Equal(112, oCells.Count);
        var revision = 0;
        PiskvorkyView view = start;
        while (xCells.Count > 0)
        {
            view = await Move(engine, start.Id, xCells.Dequeue(), revision++);
            if (oCells.Count > 0) view = await Move(engine, start.Id, oCells.Dequeue(), revision++);
        }
        Assert.Equal("draw", view.Status);
        Assert.Null(view.WinningLine);
        Assert.Equal(225, view.MoveNumber);
        Assert.Equal(225, view.Revision);
        Assert.DoesNotContain('.', view.Board);
    }

    [Fact]
    public async Task Move_ModelFirstMove_SendsEmptyBoardContext()
    {
        var (engine, _, ollama, _, _) = Create();
        var start = await Start(engine, xModel: "llama3");
        var view = await Move(engine, start.Id, null, 0);

        Assert.Equal(1, ollama.ChooseCalls);
        var context = ollama.Contexts[0];
        Assert.Equal(15, context.Size);
        Assert.Equal("X", context.PlayerSymbol);
        Assert.Equal(string.Join('\n', Enumerable.Repeat(new string('.', 15), 15)), context.BoardText);
        Assert.Equal(1, context.MoveNumber);
        Assert.Null(context.LastMoveIndex);
        Assert.Null(context.AttemptFeedback);

        Assert.Equal(new PiskvorkyMoveInfo("X", 112, "llm", "llama3", null), view.LastMove);
        Assert.Equal('X', view.Board[112]);
        Assert.Equal("O", view.Current);
        Assert.Equal(1, view.Revision);
    }

    [Fact]
    public async Task Move_LlmDecision_UsesServerContextWithLastMove()
    {
        var (engine, _, ollama, _, _) = Create();
        var start = await Start(engine, oModel: "llama3");
        await Move(engine, start.Id, 0, 0);
        Assert.Equal(0, ollama.ChooseCalls);
        ollama.Decisions(new PiskvorkyDecision(7, 8));
        var view = await Move(engine, start.Id, null, 1);

        Assert.Equal(1, ollama.ChooseCalls);
        var context = ollama.Contexts[0];
        Assert.Equal("O", context.PlayerSymbol);
        Assert.Equal(2, context.MoveNumber);
        Assert.Equal(0, context.LastMoveIndex);
        Assert.Contains("X", context.BoardText);

        Assert.Equal(new PiskvorkyMoveInfo("O", 113, "llm", "llama3", null), view.LastMove);
        Assert.Equal('O', view.Board[113]);
        Assert.Equal("X", view.Current);
        Assert.Equal(2, view.Revision);
        Assert.Equal(2, view.MoveNumber);
    }

    [Fact]
    public async Task Move_ModelAgainstModel_SameOrDifferentModelsBothPlay()
    {
        var (engine, _, ollama, _, _) = Create();
        var start = await Start(engine, "llama3", "llama3");
        ollama.Decisions(new PiskvorkyDecision(0, 0), new PiskvorkyDecision(1, 1));
        var view = await Move(engine, start.Id, null, 0);
        Assert.Equal(new PiskvorkyMoveInfo("X", 0, "llm", "llama3", null), view.LastMove);
        view = await Move(engine, start.Id, null, 1);
        Assert.Equal(new PiskvorkyMoveInfo("O", 16, "llm", "llama3", null), view.LastMove);
        Assert.Equal(2, ollama.ChooseCalls);
        Assert.Equal("X", view.Current);
    }

    private static ModelsView DecisionModels => new(true, [new ModelInfo("d1:latest", 3730544597, ["decision", "vision"])]);

    [Fact]
    public async Task Move_DecisionCapableModel_RoutesToDecisionProtocol()
    {
        var (engine, _, ollama, _, _) = Create();
        ollama.Models = DecisionModels;
        var start = await Start(engine, xModel: "d1:latest");
        ollama.SquareDecisions(new PiskvorkyDecision(7, 8));
        var view = await Move(engine, start.Id, null, 0);

        Assert.Equal(1, ollama.DecisionSquareCalls);
        Assert.Equal(0, ollama.ChooseCalls);
        Assert.Equal(new PiskvorkyMoveInfo("X", 7 * 15 + 8, "llm", "d1:latest", null), view.LastMove);
        Assert.Equal('X', view.Board[113]);
        Assert.Equal("O", view.Current);
        // Empty board offers only the center square.
        var (row, col, description) = Assert.Single(ollama.CandidateSquares[0]);
        Assert.Equal(7, row);
        Assert.Equal(7, col);
        Assert.Contains("střed desky", description);
    }

    [Fact]
    public async Task Move_DecisionModelOccupiedSquare_RetriesWithAttemptFeedback()
    {
        var (engine, _, ollama, _, _) = Create();
        ollama.Models = DecisionModels;
        var start = await Start(engine, oModel: "d1:latest");
        await Move(engine, start.Id, 112, 0);
        ollama.SquareDecisions(new PiskvorkyDecision(7, 7), new PiskvorkyDecision(8, 8));

        var view = await Move(engine, start.Id, null, 1);

        Assert.Equal(2, ollama.DecisionSquareCalls);
        Assert.Equal(0, ollama.ChooseCalls);
        Assert.Null(ollama.Contexts[0].AttemptFeedback);
        Assert.Contains("obsazene", ollama.Contexts[1].AttemptFeedback);
        Assert.Equal(new PiskvorkyMoveInfo("O", 8 * 15 + 8, "llm", "d1:latest", null), view.LastMove);
        // Candidates are free neighbours of the stone, so the occupied centre is never offered.
        var candidates = ollama.CandidateSquares[0];
        Assert.DoesNotContain(candidates, c => c.Row == 7 && c.Col == 7);
        Assert.All(candidates, c => Assert.Contains("soused", c.Description));
    }

    [Fact]
    public async Task Move_DecisionCandidates_DescribeWinningAndBlockingSquares()
    {
        var (engine, _, ollama, _, _) = Create();
        ollama.Models = DecisionModels;
        var start = await Start(engine, oModel: "d1:latest");
        // X builds four in row 0 (threat at (0,4)); O plays its model moves far away.
        ollama.SquareDecisions(new PiskvorkyDecision(10, 10), new PiskvorkyDecision(11, 11), new PiskvorkyDecision(12, 12));

        var view = await PlayAll(engine, start.Id, 0, null, 1, null, 2, null, 3, null);

        Assert.Equal("playing", view.Status);
        var candidates = ollama.CandidateSquares[^1];
        Assert.Contains(candidates, c => c.Row == 0 && c.Col == 4 && c.Description.Contains("zablokuje"));
        Assert.DoesNotContain(candidates, c => c.Row == 0 && c.Col == 8);
        // O's own stones offered neither a win nor a block.
        Assert.DoesNotContain(candidates, c => c.Description.Contains("dokončí"));
    }

    [Fact]
    public async Task Move_ThreeFailedDecisionAttempts_FallsBackToHeuristicWithReason()
    {
        var (engine, _, ollama, _, _) = Create();
        ollama.Models = DecisionModels;
        var start = await Start(engine, xModel: "d1:latest");
        var reason = "Model neumi decision rezim; tah prevzala zalozni heuristika.";
        ollama.SquareDecisions(
            new PiskvorkyDecision(null, null, reason),
            new PiskvorkyDecision(null, null, reason),
            new PiskvorkyDecision(null, null, reason));

        var view = await Move(engine, start.Id, null, 0);

        Assert.Equal(3, ollama.DecisionSquareCalls);
        Assert.Equal(0, ollama.ChooseCalls);
        Assert.Equal(reason, ollama.Contexts[1].AttemptFeedback);
        Assert.Equal(new PiskvorkyMoveInfo("X", 112, "heuristic", "d1:latest", reason), view.LastMove);
    }

    [Fact]
    public async Task Move_CatalogUnavailableAtMove_FallsBackToChatProtocol()
    {
        var (engine, _, ollama, _, _) = Create();
        ollama.Models = DecisionModels;
        var start = await Start(engine, xModel: "d1:latest");
        ollama.Models = new ModelsView(false, [], "Ollama neni dostupna.");
        ollama.Decisions(new PiskvorkyDecision(7, 7));

        var view = await Move(engine, start.Id, null, 0);

        Assert.Equal(1, ollama.ChooseCalls);
        Assert.Equal(0, ollama.DecisionSquareCalls);
        Assert.Equal("llm", view.LastMove!.Source);
    }

    [Fact]
    public async Task Move_ModelWithChatAndDecisionCapabilities_PlaysOverChat()
    {
        var (engine, _, ollama, _, _) = Create();
        ollama.Models = new ModelsView(true, [new ModelInfo("hybrid", 37, ["completion", "decision"])]);
        var start = await Start(engine, xModel: "hybrid");
        ollama.Decisions(new PiskvorkyDecision(7, 7));

        var view = await Move(engine, start.Id, null, 0);

        Assert.Equal(1, ollama.ChooseCalls);
        Assert.Equal(0, ollama.DecisionSquareCalls);
        Assert.Equal("llm", view.LastMove!.Source);
    }

    [Fact]
    public async Task Move_LlmOccupiedSquare_RetriesWithAttemptFeedback()
    {
        var (engine, _, ollama, _, _) = Create();
        var start = await Start(engine, oModel: "llama3");
        await Move(engine, start.Id, 0, 0);
        ollama.Decisions(new PiskvorkyDecision(0, 0), new PiskvorkyDecision(0, 1));

        var view = await Move(engine, start.Id, null, 1);

        Assert.Equal(2, ollama.ChooseCalls);
        Assert.Null(ollama.Contexts[0].AttemptFeedback);
        Assert.Contains("obsazene", ollama.Contexts[1].AttemptFeedback);
        Assert.Contains("radek 0, sloupec 0", ollama.Contexts[1].AttemptFeedback);
        Assert.Equal(new PiskvorkyMoveInfo("O", 1, "llm", "llama3", null), view.LastMove);
        Assert.Equal(2, view.Revision);
    }

    [Fact]
    public async Task Move_ThreeFailedAttempts_FallsBackToHeuristicWithReason()
    {
        var (engine, _, ollama, _, _) = Create();
        var start = await Start(engine, oModel: "llama3");
        await Move(engine, start.Id, 0, 0);
        var reason = "Model neodpovedel vcas; tah prevzala zalozni heuristika.";
        ollama.Decisions(
            new PiskvorkyDecision(null, null, reason),
            new PiskvorkyDecision(null, null, reason),
            new PiskvorkyDecision(null, null, reason));

        var view = await Move(engine, start.Id, null, 1);

        Assert.Equal(3, ollama.ChooseCalls);
        Assert.Equal(reason, ollama.Contexts[1].AttemptFeedback);
        Assert.Equal(reason, ollama.Contexts[2].AttemptFeedback);
        Assert.Equal(new PiskvorkyMoveInfo("O", 1, "heuristic", "llama3", reason), view.LastMove);
        Assert.Equal('O', view.Board[1]);
        Assert.Equal(2, view.Revision);
    }

    [Fact]
    public async Task Move_MixedFailures_CountIntoThreeAttempts()
    {
        var (engine, _, ollama, _, _) = Create();
        var start = await Start(engine, oModel: "llama3");
        await Move(engine, start.Id, 0, 0);
        ollama.Decisions(
            new PiskvorkyDecision(null, null, "Model vratil neplatne souradnice; tah prevzala zalozni heuristika."),
            new PiskvorkyDecision(0, 0),
            new PiskvorkyDecision(15, 15, "Model vratil neplatne souradnice; tah prevzala zalozni heuristika."));

        var view = await Move(engine, start.Id, null, 1);

        Assert.Equal(3, ollama.ChooseCalls);
        Assert.Contains("obsazene", ollama.Contexts[2].AttemptFeedback);
        Assert.Equal("heuristic", view.LastMove!.Source);
        Assert.Contains("mimo desku", view.LastMove.FallbackReason);
        Assert.Equal(2, view.Revision);
    }

    [Fact]
    public async Task Get_WhileModelMoveInProgress_ShowsOnlyConfirmedState()
    {
        var (engine, _, ollama, _, _) = Create();
        var start = await Start(engine, oModel: "llama3");
        await Move(engine, start.Id, 0, 0);
        ollama.Blocker = new TaskCompletionSource<PiskvorkyDecision>(TaskCreationOptions.RunContinuationsAsynchronously);

        var pending = engine.MoveAsync(start.Id, new PiskvorkyMoveRequest(null, 1), CancellationToken.None);
        Assert.False(pending.IsCompleted);

        var view = engine.Get(start.Id);
        Assert.True(view.InProgress);
        Assert.Equal(1, view.Revision);
        Assert.Equal("O", view.Current);
        Assert.Equal(BoardWith([(0, 'X')]), view.Board);

        ollama.Blocker.SetResult(new PiskvorkyDecision(7, 7));
        var done = await pending;
        Assert.Equal(2, done.Revision);
        var after = engine.Get(start.Id);
        Assert.False(after.InProgress);
        Assert.Equal('O', after.Board[112]);
    }

    [Fact]
    public async Task Move_ConcurrentMove_SecondGets409()
    {
        var (engine, _, ollama, _, _) = Create();
        var start = await Start(engine, xModel: "llama3");
        ollama.Blocker = new TaskCompletionSource<PiskvorkyDecision>(TaskCreationOptions.RunContinuationsAsynchronously);

        var first = engine.MoveAsync(start.Id, new PiskvorkyMoveRequest(null, 0), CancellationToken.None);
        Assert.False(first.IsCompleted);

        var second = await Assert.ThrowsAsync<GameException>(
            () => engine.MoveAsync(start.Id, new PiskvorkyMoveRequest(null, 0), CancellationToken.None));
        Assert.Equal(409, second.StatusCode);
        Assert.Equal("conflict", second.Code);
        Assert.Equal(1, ollama.ChooseCalls);

        ollama.Blocker.SetResult(new PiskvorkyDecision(7, 7));
        var committed = await first;
        Assert.Equal(1, committed.Revision);
        Assert.False(engine.Get(start.Id).InProgress);
    }

    [Fact]
    public async Task Move_CallerCancellation_DiscardsWholeMove()
    {
        var (engine, _, ollama, _, _) = Create();
        var start = await Start(engine, oModel: "llama3");
        await Move(engine, start.Id, 0, 0);
        ollama.Blocker = new TaskCompletionSource<PiskvorkyDecision>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cts = new CancellationTokenSource();

        var pending = engine.MoveAsync(start.Id, new PiskvorkyMoveRequest(null, 1), cts.Token);
        Assert.False(pending.IsCompleted);
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);

        var view = engine.Get(start.Id);
        Assert.Equal(1, view.Revision);
        Assert.Equal(BoardWith([(0, 'X')]), view.Board);
        Assert.False(view.InProgress);
        Assert.Equal(1, ollama.ChooseCalls);

        ollama.Blocker = null;
        ollama.Decisions(new PiskvorkyDecision(7, 7));
        var retry = await Move(engine, start.Id, null, 1);
        Assert.Equal(2, retry.Revision);
        Assert.Equal(2, ollama.ChooseCalls);
    }

    [Fact]
    public async Task Move_ApplicationStopping_DiscardsWholeMove()
    {
        var (engine, _, ollama, lifetime, _) = Create();
        var start = await Start(engine, xModel: "llama3");
        ollama.Blocker = new TaskCompletionSource<PiskvorkyDecision>(TaskCreationOptions.RunContinuationsAsynchronously);

        var pending = engine.MoveAsync(start.Id, new PiskvorkyMoveRequest(null, 0), CancellationToken.None);
        Assert.False(pending.IsCompleted);
        lifetime.StopApplication();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.True(ollama.Tokens[0].IsCancellationRequested);

        var view = engine.Get(start.Id);
        Assert.Equal(0, view.Revision);
        Assert.Equal(EmptyBoard, view.Board);
        Assert.False(view.InProgress);
    }

    [Fact]
    public async Task Heuristic_CompletesOwnFiveBeforeBlocking()
    {
        var (engine, _, ollama, _, _) = Create();
        var start = await Start(engine, oModel: "llama3");
        ollama.Decisions(
            new PiskvorkyDecision(14, 0), new PiskvorkyDecision(14, 1), new PiskvorkyDecision(14, 2),
            new PiskvorkyDecision(14, 3),
            new PiskvorkyDecision(null, null, "selhani"), new PiskvorkyDecision(null, null, "selhani"),
            new PiskvorkyDecision(null, null, "selhani"));
        // X threatens (0,9) for six in a row; O already owns four stones in row 14, so the
        // heuristic must complete its own five at (14,4) instead of blocking.
        var view = await PlayAll(engine, start.Id,
            10, null, 11, null, 12, null, 13, null, 8, null);

        Assert.Equal("heuristic", view.LastMove!.Source);
        Assert.Equal(214, view.LastMove.Position);
        Assert.Equal("O", view.LastMove.Player);
        Assert.Equal(7, ollama.ChooseCalls);
        // Completing the own five ends the game even though X threatened a win too.
        Assert.Equal("o_won", view.Status);
        Assert.Equal(new[] { 210, 211, 212, 213, 214 }, view.WinningLine);
        Assert.Equal('O', view.Board[214]);
    }

    [Fact]
    public async Task Heuristic_BlocksOpponentFive()
    {
        var (engine, _, ollama, _, _) = Create();
        var start = await Start(engine, oModel: "llama3");
        ollama.Decisions(
            new PiskvorkyDecision(14, 14), new PiskvorkyDecision(14, 13), new PiskvorkyDecision(14, 12),
            new PiskvorkyDecision(null, null, "selhani"), new PiskvorkyDecision(null, null, "selhani"),
            new PiskvorkyDecision(null, null, "selhani"));
        // X has four in row 0 and threatens (0,4); O owns only three stones.
        var view = await PlayAll(engine, start.Id,
            0, null, 1, null, 2, null, 3, null);

        Assert.Equal("heuristic", view.LastMove!.Source);
        Assert.Equal(4, view.LastMove.Position);
        Assert.Equal(6, ollama.ChooseCalls);
        Assert.Equal("playing", view.Status);
    }

    [Fact]
    public async Task Heuristic_PicksAdjacentFreeSquareViaRandom()
    {
        var (engine, random, ollama, _, _) = Create();
        var start = await Start(engine, oModel: "llama3");
        ollama.Decisions(
            new PiskvorkyDecision(null, null, "selhani"), new PiskvorkyDecision(null, null, "selhani"),
            new PiskvorkyDecision(null, null, "selhani"));
        await Move(engine, start.Id, 112, 0);
        random.Ints(0, 8, 4);

        var view = await Move(engine, start.Id, null, 1);

        Assert.Equal("heuristic", view.LastMove!.Source);
        Assert.Equal(113, view.LastMove.Position);
    }

    [Fact]
    public async Task Heuristic_EmptyBoard_PlaysCenter()
    {
        var (engine, _, ollama, _, _) = Create();
        var start = await Start(engine, xModel: "llama3");
        ollama.Decisions(
            new PiskvorkyDecision(null, null, "selhani"), new PiskvorkyDecision(null, null, "selhani"),
            new PiskvorkyDecision(null, null, "selhani"));

        var view = await Move(engine, start.Id, null, 0);

        Assert.Equal(new PiskvorkyMoveInfo("X", 112, "heuristic", "llama3", "selhani"), view.LastMove);
        Assert.Equal('X', view.Board[112]);
    }
}