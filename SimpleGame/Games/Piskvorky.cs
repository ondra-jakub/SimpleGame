namespace SimpleGame.Games;

public sealed record PiskvorkyStartRequest(string? XModel, string? OModel);
public sealed record PiskvorkyMoveRequest(int? Position, int? ExpectedRevision);
public sealed record PiskvorkyMoveInfo(string Player, int Position, string Source, string? Model, string? FallbackReason);
public sealed record PiskvorkyState(string Board, string Current, string Status, int Revision, int[]? WinningLine,
    PiskvorkyMoveInfo? LastMoveInfo, int MoveNumber);
public sealed record PiskvorkyView(Guid Id, string XMode, string? XModel, string OMode, string? OModel, int Size, string Board,
    string Current, string Status, int[]? WinningLine, PiskvorkyMoveInfo? LastMove, int Revision, bool InProgress, int MoveNumber);

public sealed class PiskvorkySession(string? xModel, string? oModel) : GameSession
{
    private PiskvorkyState state = new(new string('.', PiskvorkyEngine.Size * PiskvorkyEngine.Size), "X", "playing", 0, null, null, 0);
    private int inProgress;
    public string? XModel { get; } = xModel;
    public string? OModel { get; } = oModel;
    public PiskvorkyState State { get => Volatile.Read(ref state); set => Volatile.Write(ref state, value); }
    public bool InProgress { get => Volatile.Read(ref inProgress) == 1; set => Volatile.Write(ref inProgress, value ? 1 : 0); }
    public PiskvorkyView View()
    {
        var snapshot = State;
        return new PiskvorkyView(Id, XModel is null ? "human" : "model", XModel, OModel is null ? "human" : "model", OModel,
            PiskvorkyEngine.Size, snapshot.Board, snapshot.Current, snapshot.Status, snapshot.WinningLine,
            snapshot.LastMoveInfo, snapshot.Revision, InProgress, snapshot.MoveNumber);
    }
}

public sealed class PiskvorkyEngine(SessionStore sessions, IGameRandom random, IOllamaClient ollama, IHostApplicationLifetime lifetime)
{
    public const int Size = 15;
    public const int Squares = Size * Size;
    private const int WinLength = 5;
    private const int MaxAttempts = 3;
    private static readonly (int Row, int Col)[] Directions = [(0, 1), (1, 0), (1, 1), (1, -1)];

    public async Task<PiskvorkyView> StartAsync(PiskvorkyStartRequest request, CancellationToken cancellationToken)
    {
        if (request.XModel is not null && request.XModel.Length is 0 or > 256)
            throw GameException.Invalid("Zvolte platny model nebo lidskeho hrace.");
        if (request.OModel is not null && request.OModel.Length is 0 or > 256)
            throw GameException.Invalid("Zvolte platny model nebo lidskeho hrace.");
        if (request.XModel is not null || request.OModel is not null)
        {
            var models = await ollama.GetModelsAsync(false, cancellationToken);
            if (!models.Available)
                throw new GameException(503, "ollama_unavailable", "Ollama neni dostupna. Zvolte lidskeho hrace nebo zkuste obnovit modely.");
            if (request.XModel is not null && !models.Models.Any(m => m.Name == request.XModel))
                throw GameException.Invalid("Tento model neni nainstalovany v Ollame. Obnovte seznam modelu.");
            if (request.OModel is not null && !models.Models.Any(m => m.Name == request.OModel))
                throw GameException.Invalid("Tento model neni nainstalovany v Ollame. Obnovte seznam modelu.");
        }
        cancellationToken.ThrowIfCancellationRequested();
        return sessions.Add(new PiskvorkySession(request.XModel, request.OModel)).View();
    }

    public PiskvorkyView Get(Guid id)
    {
        using var lease = sessions.Get<PiskvorkySession>(id);
        return lease.Value.View();
    }

    public async Task<PiskvorkyView> MoveAsync(Guid id, PiskvorkyMoveRequest request, CancellationToken cancellationToken)
    {
        using var lease = sessions.Get<PiskvorkySession>(id);
        var session = lease.Value;
        if (!session.Gate.Wait(0)) throw GameException.Conflict("Model jeste premysli. Obnovte stav hry.");
        try
        {
            var current = session.State;
            if (request.ExpectedRevision is null || request.ExpectedRevision < 0)
                throw GameException.Invalid("Chybi platna revision hry.");
            if (current.Revision != request.ExpectedRevision)
                throw GameException.Conflict("Stav hry se zmenil. Obnovte jej pred dalsim tahem.");
            if (current.Status != "playing") throw GameException.Conflict("Hra uz skoncila. Zacnete novou.");
            var model = current.Current == "X" ? session.XModel : session.OModel;
            // Human moves must be fully valid before any state change or model call; model moves carry no position.
            var position = -1;
            if (model is null)
            {
                if (request.Position is not { } humanPosition)
                    throw GameException.Invalid("Zadejte pozici 0-224.");
                if (humanPosition < 0 || humanPosition >= Squares)
                    throw GameException.Invalid("Pozice musi byt cele cislo 0-224.");
                if (current.Board[humanPosition] != '.')
                    throw GameException.Invalid("Pole je jiz obsazene.");
                position = humanPosition;
            }
            else if (request.Position is not null)
            {
                throw GameException.Invalid("Tah modelu se zadava bez pozice.");
            }
            session.InProgress = true;
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime.ApplicationStopping);
            var token = cancellation.Token;
            token.ThrowIfCancellationRequested();
            PiskvorkyMoveInfo move;
            if (model is not null)
            {
                move = await ModelMoveAsync(current, model!, token);
                token.ThrowIfCancellationRequested();
            }
            else
            {
                move = new PiskvorkyMoveInfo(current.Current, position, "human", null, null);
            }
            // Only publish a complete move. GET readers never observe half of it.
            session.State = Apply(current, move);
            session.InProgress = false;
            return session.View();
        }
        finally
        {
            session.InProgress = false;
            session.Gate.Release();
        }
    }

    private async Task<PiskvorkyMoveInfo> ModelMoveAsync(PiskvorkyState current, string model, CancellationToken token)
    {
        // Decision-only models (no chat capability) answer through the decision protocol.
        var useDecision = await UsesDecisionProtocolAsync(model, token);
        var candidates = useDecision ? DecisionCandidates(current.Board, current.Current) : [];
        var feedback = (string?)null;
        var lastReason = (string?)null;
        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            var context = new PiskvorkyContext(Size, current.Current, BoardText(current.Board), current.MoveNumber + 1,
                current.LastMoveInfo?.Position, feedback);
            var decision = useDecision
                ? await ollama.ChooseSquareDecisionAsync(model, context, candidates, token)
                : await ollama.ChooseSquareAsync(model, context, token);
            token.ThrowIfCancellationRequested();
            if (decision.Row is int row && decision.Col is int col)
            {
                if (row >= 0 && row < Size && col >= 0 && col < Size && current.Board[row * Size + col] == '.')
                    return new PiskvorkyMoveInfo(current.Current, row * Size + col, "llm", model, null);
                if (row >= 0 && row < Size && col >= 0 && col < Size)
                    lastReason = feedback = $"Model zvolil jiz obsazene pole (radek {row}, sloupec {col}).";
                else
                    lastReason = feedback = $"Model zvolil pole mimo desku (radek {row}, sloupec {col}).";
            }
            else
            {
                lastReason = feedback = decision.FallbackReason ?? "Model nedokazal zvolit pole.";
            }
        }
        return new PiskvorkyMoveInfo(current.Current, Heuristic(current.Board, current.Current), "heuristic", model, lastReason);
    }

    private async Task<bool> UsesDecisionProtocolAsync(string model, CancellationToken token)
    {
        // Unavailable catalog falls back to the chat protocol, which reports its own failure reason.
        var models = await ollama.GetModelsAsync(false, token);
        token.ThrowIfCancellationRequested();
        var info = models.Models.FirstOrDefault(m => m.Name == model);
        return info is not null && info.Supports("decision") && !info.Supports("completion");
    }

    // Candidate squares offered to decision models: any answer is a legal move.
    private List<(int Row, int Col, string Description)> DecisionCandidates(string board, string player)
    {
        var symbol = player[0];
        var opponent = symbol == 'X' ? 'O' : 'X';
        var result = new List<(int Row, int Col, string Description)>();
        var hasStones = false;
        for (var i = 0; i < Squares; i++)
        {
            if (board[i] != '.')
            {
                hasStones = true;
                continue;
            }
            if (!HasNeighbor(board, i)) continue;
            var row = i / Size;
            var col = i % Size;
            var description = CompletesFive(board, row, col, symbol) ? "volné pole, které dokončí tvou pětku a vyhraješ"
                : CompletesFive(board, row, col, opponent) ? "volné pole, které zablokuje soupeřovu pětku"
                : "volné pole sousedící s kameny";
            result.Add((row, col, $"řádek {row}, sloupec {col}: {description}"));
        }
        if (result.Count > 0) return result;
        // Degenerate boards: offer the center on an empty board, otherwise the first free square.
        var square = hasStones ? board.IndexOf('.') : Size / 2 * Size + Size / 2;
        var fallbackRow = square / Size;
        var fallbackCol = square % Size;
        result.Add((fallbackRow, fallbackCol,
            $"řádek {fallbackRow}, sloupec {fallbackCol}: {(hasStones ? "volné pole" : "střed desky (první tah)")}"));
        return result;
    }

    private static PiskvorkyState Apply(PiskvorkyState current, PiskvorkyMoveInfo move)
    {
        var board = current.Board.ToCharArray();
        board[move.Position] = move.Player[0];
        var updated = new string(board);
        var winningLine = WinningLine(updated, move.Position / Size, move.Position % Size, move.Player[0]);
        var status = winningLine is not null ? (move.Player == "X" ? "x_won" : "o_won") : updated.Contains('.') ? "playing" : "draw";
        return current with
        {
            Board = updated,
            Current = status == "playing" ? (move.Player == "X" ? "O" : "X") : current.Current,
            Status = status,
            Revision = current.Revision + 1,
            WinningLine = winningLine,
            LastMoveInfo = move,
            MoveNumber = current.MoveNumber + 1
        };
    }

    private static string BoardText(string board)
    {
        var lines = new string[Size];
        for (var row = 0; row < Size; row++) lines[row] = board.Substring(row * Size, Size);
        return string.Join('\n', lines);
    }

    private static int CountLine(string board, int row, int col, int dRow, int dCol, char symbol)
    {
        var count = 0;
        var r = row + dRow;
        var c = col + dCol;
        while (r >= 0 && r < Size && c >= 0 && c < Size && board[r * Size + c] == symbol)
        {
            count++;
            r += dRow;
            c += dCol;
        }
        return count;
    }

    private static int[]? WinningLine(string board, int row, int col, char symbol)
    {
        foreach (var (dRow, dCol) in Directions)
        {
            if (1 + CountLine(board, row, col, dRow, dCol, symbol) + CountLine(board, row, col, -dRow, -dCol, symbol) < WinLength) continue;
            var line = new List<int>();
            var r = row;
            var c = col;
            while (r >= 0 && r < Size && c >= 0 && c < Size && board[r * Size + c] == symbol)
            {
                r -= dRow;
                c -= dCol;
            }
            r += dRow;
            c += dCol;
            while (r >= 0 && r < Size && c >= 0 && c < Size && board[r * Size + c] == symbol)
            {
                line.Add(r * Size + c);
                r += dRow;
                c += dCol;
            }
            return [.. line];
        }
        return null;
    }

    private int Heuristic(string board, string player)
    {
        var symbol = player[0];
        var opponent = symbol == 'X' ? 'O' : 'X';
        // 1) Complete own five, 2) block the opponent's five.
        if (FindCompleting(board, symbol, out var winning)) return winning;
        if (FindCompleting(board, opponent, out var blocking)) return blocking;
        // 3) A free square adjacent to a stone, picked at random; empty board starts in the center.
        var candidates = new List<int>();
        var hasStones = false;
        for (var i = 0; i < Squares; i++)
        {
            if (board[i] == '.')
            {
                if (HasNeighbor(board, i)) candidates.Add(i);
            }
            else hasStones = true;
        }
        if (!hasStones) return 7 * Size + 7;
        if (candidates.Count == 0) return board.IndexOf('.');
        return candidates[random.Next(0, candidates.Count)];
    }

    private static bool CompletesFive(string board, int row, int col, char symbol) =>
        Directions.Any(direction =>
            1 + CountLine(board, row, col, direction.Row, direction.Col, symbol)
              + CountLine(board, row, col, -direction.Row, -direction.Col, symbol) >= WinLength);

    private static bool FindCompleting(string board, char symbol, out int square)
    {
        for (var i = 0; i < Squares; i++)
        {
            if (board[i] != '.') continue;
            var row = i / Size;
            var col = i % Size;
            if (CompletesFive(board, row, col, symbol))
            {
                square = i;
                return true;
            }
        }
        square = -1;
        return false;
    }

    private static bool HasNeighbor(string board, int index)
    {
        var row = index / Size;
        var col = index % Size;
        for (var dRow = -1; dRow <= 1; dRow++)
            for (var dCol = -1; dCol <= 1; dCol++)
            {
                if (dRow == 0 && dCol == 0) continue;
                var r = row + dRow;
                var c = col + dCol;
                if (r >= 0 && r < Size && c >= 0 && c < Size && board[r * Size + c] != '.') return true;
            }
        return false;
    }
}

public static class PiskvorkyGame
{
    public static void MapPiskvorkyGame(this RouteGroupBuilder games)
    {
        var group = games.MapGroup("/piskvorky");
        group.MapPost("/start", (PiskvorkyStartRequest request, PiskvorkyEngine engine, CancellationToken ct) => engine.StartAsync(request, ct));
        group.MapGet("/{id:guid}", (Guid id, PiskvorkyEngine engine) => engine.Get(id));
        group.MapPost("/{id:guid}/move", (Guid id, PiskvorkyMoveRequest request, PiskvorkyEngine engine, CancellationToken ct) => engine.MoveAsync(id, request, ct));
    }
}