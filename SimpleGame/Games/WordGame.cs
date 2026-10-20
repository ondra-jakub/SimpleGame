namespace SimpleGame.Games;

public sealed record WordGuessRequest(string? Word);
public sealed record WordGuessEntry(string Word, string[] Feedback);
public sealed record WordView(Guid Id, int Length, int MaxAttempts, string Status,
    IReadOnlyList<WordGuessEntry> Guesses, string? Secret);

internal sealed class WordSession(string secret) : GameSession
{
    public const int WordLength = 5;
    public const int MaxAttempts = 6;
    private readonly string secret = secret;
    private readonly List<WordGuessEntry> guesses = [];
    private string status = "playing";

    public WordView View() => new(Id, WordLength, MaxAttempts, status, [.. guesses],
        status == "playing" ? null : secret);

    public WordView Guess(string? word)
    {
        if (status != "playing")
            throw GameException.Conflict("Hra už skončila. Začněte novou hru.");
        var candidate = CzechText.Letters(word);
        if (candidate.Length != WordLength)
            throw GameException.Invalid($"Tip musí mít právě {WordLength} písmen.");
        guesses.Add(new WordGuessEntry(candidate, Evaluate(candidate, secret)));
        if (candidate == secret)
            status = "won";
        else if (guesses.Count >= MaxAttempts)
            status = "lost";
        return View();
    }

    // Two passes: exact positions consume secret letters first, remaining occurrences are
    // marked present at most once per secret occurrence, everything else is absent.
    internal static string[] Evaluate(string guess, string secret)
    {
        var feedback = new string[guess.Length];
        Array.Fill(feedback, "absent");
        var remaining = new List<char>(secret);
        for (var i = 0; i < guess.Length; i++)
            if (guess[i] == secret[i])
            {
                feedback[i] = "correct";
                remaining.Remove(secret[i]);
            }
        for (var i = 0; i < guess.Length; i++)
        {
            if (feedback[i] == "correct")
                continue;
            if (remaining.Remove(guess[i]))
                feedback[i] = "present";
        }
        return feedback;
    }
}

public static class WordGame
{
    // Five-letter Czech words stored normalized (without diacritics, uppercase).
    public static readonly string[] Dictionary =
    [
        "MESTO", "KRAVA", "STENA", "LODKA", "KOCKA", "MYSKA", "ZAJIC", "LISKA", "VOZIK", "SEDLO",
        "KOKOS", "BANAN", "SALAM", "TRAVA", "SNEZI", "OBLAK", "ZVONY", "LZICE", "VIDLE", "KABAT",
        "JESLE", "ZIDLE", "LAMPA", "VECER", "KOTVA", "VETEV", "MINCE", "LEKAR", "PEKAR", "SKOLA",
        "HOKEJ", "TENIS", "KAMNA", "SALAT", "RIZEK", "GULAS", "KOLAC", "MLEKO", "MASLO", "CHLEB",
        "SUNKA", "VAJCE", "RYBIZ", "ORECH", "ZEBRA", "PANDA", "DANEK", "SRNKA", "VYDRA", "LABUT",
        "CAJKA", "VRANA", "ZELVA", "RYBKA", "SUMEC", "VESLA", "POHAR", "SKORE", "VYHRA", "BRANA",
    ];

    public static void MapWordGame(this RouteGroupBuilder games)
    {
        var group = games.MapGroup("/word");

        group.MapPost("/start", (SessionStore store, IGameRandom random) =>
            store.Add(new WordSession(Dictionary[random.Next(0, Dictionary.Length)])).View());

        group.MapPost("/{id:guid}/guess", (Guid id, WordGuessRequest request, SessionStore store) =>
            store.Mutate<WordSession, WordView>(id, session => session.Guess(request.Word)));
    }
}
