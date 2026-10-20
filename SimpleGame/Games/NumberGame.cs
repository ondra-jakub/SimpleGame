namespace SimpleGame.Games;

public sealed record NumberStartRequest(long? Min, long? Max);
public sealed record NumberGuessRequest(long? Value);
public sealed record NumberHistoryEntry(long Value, string Result);
public sealed record NumberView(Guid Id, long Min, long Max, int Attempts, string Status, string? Result,
    IReadOnlyList<NumberHistoryEntry> History);

internal sealed class NumberSession(long min, long max, long secret) : GameSession
{
    private readonly long min = min;
    private readonly long max = max;
    private readonly long secret = secret;
    private readonly List<NumberHistoryEntry> history = [];
    private int attempts;
    private string status = "playing";
    private string? lastResult;

    public NumberView View() => new(Id, min, max, attempts, status, lastResult, [.. history]);

    public NumberView Guess(long value)
    {
        if (status != "playing")
            throw GameException.Conflict("Hra už skončila. Začněte novou hru.");
        if (value < min || value > max)
            throw GameException.Invalid($"Tip musí být celé číslo v rozsahu {min} až {max}.");
        attempts++;
        var result = value == secret ? "correct" : value < secret ? "higher" : "lower";
        history.Add(new NumberHistoryEntry(value, result));
        lastResult = result;
        if (result == "correct")
            status = "won";
        return View();
    }
}

public static class NumberGame
{
    public const long MaxRangeSize = 1_000_000;

    public static void MapNumberGame(this RouteGroupBuilder games)
    {
        var group = games.MapGroup("/number");

        group.MapPost("/start", (NumberStartRequest request, SessionStore store, IGameRandom random) =>
        {
            var min = request.Min ?? 1;
            var max = request.Max ?? 100;
            if (min >= max)
                throw GameException.Invalid("Spodní mez musí být menší než horní mez.");
            // Int128 comparison cannot overflow even for extreme long boundaries.
            if ((Int128)max - min > MaxRangeSize)
                throw GameException.Invalid($"Rozsah může obsahovat nejvýše {MaxRangeSize} čísel.");
            // Span is at most MaxRangeSize here, so the arithmetic below cannot overflow.
            var secret = min + random.NextInt64(0, max - min + 1);
            return store.Add(new NumberSession(min, max, secret)).View();
        });

        group.MapPost("/{id:guid}/guess", (Guid id, NumberGuessRequest request, SessionStore store) =>
        {
            if (request.Value is not { } value)
                throw GameException.Invalid("Zadejte celé číslo.");
            return store.Mutate<NumberSession, NumberView>(id, session => session.Guess(value));
        });
    }
}
