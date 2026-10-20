using System.Globalization;
using System.Text;

namespace SimpleGame.Games;

public sealed record HangmanGuessRequest(string? Letter);
public sealed record HangmanView(Guid Id, string MaskedWord, string Category, string[] Guessed, int Wrong, int MaxWrong,
    string Status, string? Word);

internal sealed class HangmanSession(string word, string category) : GameSession
{
    public const int MaxWrongGuesses = 8;
    private readonly string word = word;
    private readonly string category = category;
    private readonly string letters = CzechText.Letters(word);
    private readonly List<char> guessed = [];
    private int wrong;
    private string status = "playing";

    public HangmanView View()
    {
        var masked = new StringBuilder();
        foreach (var character in word)
        {
            var normalized = NormalizeLetter(character);
            masked.Append(normalized is { } letter && guessed.Contains(letter) ? character : '_');
        }
        return new HangmanView(Id, masked.ToString(), category, [.. guessed.Select(c => c.ToString())], wrong,
            MaxWrongGuesses, status, status == "playing" ? null : word);
    }

    public HangmanView Guess(string? letter)
    {
        if (status != "playing")
            throw GameException.Conflict("Hra už skončila. Začněte novou hru.");
        var normalizedInput = CzechText.Letters(letter);
        if (normalizedInput.Length != 1)
            throw GameException.Invalid("Zadejte právě jedno písmeno.");
        if (guessed.Contains(normalizedInput[0]))
            return View();
        var guess = normalizedInput[0];
        guessed.Add(guess);
        if (!letters.Contains(guess))
        {
            wrong++;
            if (wrong >= MaxWrongGuesses)
                status = "lost";
        }
        else if (letters.All(c => guessed.Contains(c)))
            status = "won";
        return View();
    }

    private static char? NormalizeLetter(char character)
    {
        var decomposed = new StringBuilder();
        foreach (var part in character.ToString().Normalize(NormalizationForm.FormD))
            if (CharUnicodeInfo.GetUnicodeCategory(part) != UnicodeCategory.NonSpacingMark)
                decomposed.Append(part);
        var normalized = decomposed.ToString().ToUpperInvariant();
        return normalized.Length == 1 && normalized[0] is >= 'A' and <= 'Z' ? normalized[0] : null;
    }
}

public static class HangmanGame
{
    public static readonly (string Word, string Category)[] Words =
    [
        ("KOČKA", "Zvířata"), ("ŽIRAFA", "Zvířata"), ("KRÁVA", "Zvířata"), ("VEVERKA", "Zvířata"),
        ("JELEN", "Zvířata"), ("MEDVĚD", "Zvířata"), ("LIŠKA", "Zvířata"), ("SLON", "Zvířata"),
        ("ZAJÍC", "Zvířata"), ("KRÁLÍK", "Zvířata"), ("SOVA", "Zvířata"), ("VRABEC", "Zvířata"),
        ("LABUŤ", "Zvířata"), ("KACHNA", "Zvířata"), ("ŽELVA", "Zvířata"), ("VYDRA", "Zvířata"),
        ("SUMEC", "Zvířata"), ("PSTRUH", "Zvířata"),
        ("KNEDLÍK", "Jídlo"), ("KOLÁČ", "Jídlo"), ("BRAMBORA", "Jídlo"), ("MRKEV", "Jídlo"),
        ("ŘÍZEK", "Jídlo"), ("GULÁŠ", "Jídlo"), ("SALÁM", "Jídlo"), ("ŠUNKA", "Jídlo"),
        ("VAJCE", "Jídlo"), ("MLÉKO", "Jídlo"), ("MÁSLO", "Jídlo"), ("CHLÉB", "Jídlo"),
        ("SÝR", "Jídlo"), ("BANÁN", "Jídlo"), ("JABLKO", "Jídlo"), ("RYBÍZ", "Jídlo"), ("OŘECH", "Jídlo"),
        ("HOKEJ", "Sport"), ("TENIS", "Sport"), ("MARATON", "Sport"), ("FOTBAL", "Sport"),
        ("PEKAŘ", "Povolání"), ("LÉKAŘ", "Povolání"), ("HASIČ", "Povolání"), ("UČITEL", "Povolání"),
        ("ZAHRADNÍK", "Povolání"),
        ("HORA", "Příroda"), ("ŘEKA", "Příroda"), ("MRAK", "Příroda"), ("DUHA", "Příroda"),
        ("SMRK", "Příroda"), ("BŘEZA", "Příroda"),
        ("KNIHA", "Předměty"), ("HRAD", "Předměty"), ("ZÁMEK", "Předměty"), ("KLÍČ", "Předměty"),
        ("LAMPA", "Předměty"), ("ŽIDLE", "Předměty"), ("OKNO", "Předměty"), ("KOTVA", "Předměty"),
        ("VESLO", "Předměty"), ("MINCE", "Předměty"),
    ];

    public static void MapHangmanGame(this RouteGroupBuilder games)
    {
        var group = games.MapGroup("/hangman");

        group.MapPost("/start", (SessionStore store, IGameRandom random) =>
        {
            var (word, category) = Words[random.Next(0, Words.Length)];
            return store.Add(new HangmanSession(word, category)).View();
        });

        group.MapPost("/{id:guid}/guess", (Guid id, HangmanGuessRequest request, SessionStore store) =>
            store.Mutate<HangmanSession, HangmanView>(id, session => session.Guess(request.Letter)));
    }
}