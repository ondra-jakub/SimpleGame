using System.Globalization;
using System.Text;

namespace SimpleGame.Games;

public abstract record TwentyNode;
public sealed record TwentyQuestion(string Question, TwentyNode Yes, TwentyNode No) : TwentyNode;
public sealed record TwentyTerm(string Term) : TwentyNode;

public sealed record TwentyAnswerRequest(string? Answer);
public sealed record TwentyVerdictRequest(bool? Correct);
public sealed record TwentyTeachRequest(string? RealAnswer, string? Question);
public sealed record TwentyTranscriptEntry(string Question, string Answer);
public sealed record TwentyView(Guid Id, string Phase, string? Question, string? Guess, int QuestionsAsked,
    IReadOnlyList<TwentyTranscriptEntry> Transcript, string? Message);

/// <summary>
/// Shared knowledge tree. Sessions capture the current immutable root at start and keep
/// playing on that snapshot; teaching publishes a new root under a short lock. If another
/// session taught the same leaf in the meantime, the optimistic identity/path compare
/// fails with a conflict instead of overwriting the foreign lesson.
/// </summary>
public sealed class TwentyTree
{
    private readonly object sync = new();
    private TwentyNode root = BuildSeed();

    public TwentyNode Snapshot()
    {
        lock (sync)
            return root;
    }

    public void Teach(IReadOnlyList<bool> answers, TwentyNode expectedLeaf, string question, string term)
    {
        lock (sync)
        {
            var current = Walk(root, answers);
            if (!ReferenceEquals(current, expectedLeaf))
                throw GameException.Conflict("Tento pojem už mezitím naučila jiná hra. Začněte novou hru.");
            root = Replace(root, answers, 0, new TwentyQuestion(question, new TwentyTerm(term), expectedLeaf));
        }
    }

    private static TwentyNode Walk(TwentyNode node, IReadOnlyList<bool> answers)
    {
        var current = node;
        foreach (var yes in answers)
        {
            if (current is not TwentyQuestion question)
                throw GameException.Conflict("Strom znalostí se mezitím změnil. Začněte novou hru.");
            current = yes ? question.Yes : question.No;
        }
        return current;
    }

    private static TwentyNode Replace(TwentyNode node, IReadOnlyList<bool> answers, int index, TwentyNode replacement)
    {
        if (index == answers.Count)
            return replacement;
        if (node is not TwentyQuestion question)
            throw GameException.Conflict("Strom znalostí se mezitím změnil. Začněte novou hru.");
        return answers[index]
            ? question with { Yes = Replace(question.Yes, answers, index + 1, replacement) }
            : question with { No = Replace(question.No, answers, index + 1, replacement) };
    }

    // Seed of 16 concepts; all text is stored normalized (A-Z, uppercase) exactly like
    // player-taught content, and questions already include '?'.
    public static TwentyNode BuildSeed() =>
        new TwentyQuestion("JE TO SAVEC?",
            new TwentyQuestion("JE TO DOMACI ZVIRE?",
                new TwentyQuestion("DAJE MLEKO?",
                    new TwentyTerm("KRAVA"),
                    new TwentyQuestion("MUZES NA NEM JEZDIT?", new TwentyTerm("KUN"),
                        new TwentyQuestion("UMI HLIDAT DOMOV?", new TwentyTerm("PES"), new TwentyTerm("KOCKA")))),
                new TwentyQuestion("ZIJE V MORI?",
                    new TwentyTerm("VELRYBA"),
                    new TwentyQuestion("JE VETSI NEZ CLOVEK?",
                        new TwentyQuestion("MA CHOBOT?", new TwentyTerm("SLON"), new TwentyTerm("ZIRAFA")),
                        new TwentyTerm("KRALIK")))),
            new TwentyQuestion("JE TO ROSTLINA?",
                new TwentyQuestion("JE TO OVOCE?",
                    new TwentyQuestion("JE TO ZLUTE?", new TwentyTerm("BANAN"), new TwentyTerm("JABLKO")),
                    new TwentyTerm("MRKEV")),
                new TwentyQuestion("JE TO ZVIRE?",
                    new TwentyQuestion("MA KRIDLA?", new TwentyTerm("PTAK"),
                        new TwentyQuestion("MA NOHY?", new TwentyTerm("ZELVA"),
                            new TwentyQuestion("JE TO HAD?", new TwentyTerm("HAD"), new TwentyTerm("RYBA")))),
                    new TwentyTerm("AUTO"))));
}

internal sealed class TwentySession : GameSession
{
    public const int QuestionLimit = 20;
    private readonly TwentyNode root;
    private readonly List<bool> answers = [];
    private readonly List<TwentyTranscriptEntry> transcript = [];
    private TwentyNode current;
    private string phase = "question";
    private string? question;
    private string? guess;
    private string? message;
    private int questionsAsked;

    public TwentySession(TwentyTree tree)
    {
        root = tree.Snapshot();
        current = root;
        Present();
    }

    public TwentyView View() => new(Id, phase, question, guess, questionsAsked, [.. transcript], message);

    private void Present()
    {
        // The final guess counts as a question; once 20 items were shown the game gives up.
        if (questionsAsked >= QuestionLimit)
        {
            phase = "giveup";
            question = null;
            guess = null;
            message = "Dosáhl jsem limitu 20 otázek. Tentokrát jste vyhráli, vzdávám to.";
            return;
        }
        questionsAsked++;
        switch (current)
        {
            case TwentyQuestion asked:
                phase = "question";
                question = asked.Question;
                guess = null;
                break;
            case TwentyTerm term:
                phase = "guess";
                question = null;
                guess = term.Term;
                break;
        }
    }

    public TwentyView Answer(string answer)
    {
        if (phase != "question")
            throw GameException.Conflict("Právě nečekám odpověď na otázku. Obnovte stav hry.");
        var yes = answer == "yes";
        transcript.Add(new TwentyTranscriptEntry(question!, answer));
        answers.Add(yes);
        current = yes ? ((TwentyQuestion)current).Yes : ((TwentyQuestion)current).No;
        question = null;
        Present();
        return View();
    }

    public TwentyView Verdict(bool correct)
    {
        if (phase != "guess")
            throw GameException.Conflict("Právě nečekám hodnocení mého tipu.");
        if (correct)
        {
            phase = "finished";
            message = "Uhodl jsem! Děkuji za hru.";
        }
        else
        {
            phase = "teach";
            message = "Tentokrát jsem neuhodl. Prozraďte, co to bylo, a jakou otázkou se to liší od mého tipu.";
        }
        return View();
    }

    public TwentyView Teach(TwentyTree tree, string normalizedQuestion, string normalizedTerm)
    {
        if (phase != "teach")
            throw GameException.Conflict("Učení teď není možné.");
        if (current is not TwentyTerm wrongGuess ||
            string.Equals(wrongGuess.Term, normalizedTerm, StringComparison.OrdinalIgnoreCase))
            throw GameException.Invalid("Nový pojem se musí lišit od mého tipu.");
        tree.Teach(answers, current, normalizedQuestion, normalizedTerm);
        phase = "finished";
        message = $"Děkuji, naučil jsem se pojem {normalizedTerm}. Příště ho určitě uhodnu.";
        return View();
    }
}

public static class TwentyGame
{
    public static void MapTwentyGame(this RouteGroupBuilder games)
    {
        var group = games.MapGroup("/twenty");

        group.MapPost("/start", (SessionStore store, TwentyTree tree) =>
            store.Add(new TwentySession(tree)).View());

        group.MapPost("/{id:guid}/answer", (Guid id, TwentyAnswerRequest request, SessionStore store) =>
        {
            var answer = request.Answer?.Trim().ToLowerInvariant();
            if (answer is not ("yes" or "no"))
                throw GameException.Invalid("Odpověď musí být 'yes' nebo 'no'.");
            return store.Mutate<TwentySession, TwentyView>(id, session => session.Answer(answer!));
        });

        group.MapPost("/{id:guid}/verdict", (Guid id, TwentyVerdictRequest request, SessionStore store) =>
        {
            if (request.Correct is not { } correct)
                throw GameException.Invalid("Zadejte true nebo false.");
            return store.Mutate<TwentySession, TwentyView>(id, session => session.Verdict(correct));
        });

        group.MapPost("/{id:guid}/teach", (Guid id, TwentyTeachRequest request, TwentyTree tree, SessionStore store) =>
        {
            var term = NormalizeText(request.RealAnswer, 64, "Zadejte slovo, které jste měli na mysli.");
            var question = NormalizeText(request.Question, 200, "Zadejte otázku, která rozliší vaše slovo od mého tipu.");
            return store.Mutate<TwentySession, TwentyView>(id, session => session.Teach(tree, question + "?", term));
        });
    }

    // Czech text normalization: FormD + diacritics removal + uppercase, letters and spaces
    // kept, anything else (digits, punctuation) stripped. Raw input length is checked
    // before normalization so overlong payloads cannot be hidden by stripping.
    internal static string NormalizeText(string? input, int maxLength, string error)
    {
        if (input is null || input.Length > maxLength || string.IsNullOrWhiteSpace(input))
            throw GameException.Invalid(error);
        string formD;
        try { formD = input.Normalize(NormalizationForm.FormD); }
        catch (ArgumentException) { throw GameException.Invalid(error); }
        var decomposed = new StringBuilder();
        foreach (var character in formD)
            if (CharUnicodeInfo.GetUnicodeCategory(character) != UnicodeCategory.NonSpacingMark)
                decomposed.Append(character);
        var normalized = new StringBuilder();
        foreach (var character in decomposed.ToString())
        {
            var upper = char.ToUpperInvariant(character);
            if (upper is >= 'A' and <= 'Z')
                normalized.Append(upper);
            else if (char.IsLetter(character))
                throw GameException.Invalid(error);
            else if (char.IsWhiteSpace(character) || character == '-')
                normalized.Append(' ');
        }
        var collapsed = string.Join(' ', normalized.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
        if (collapsed.Length == 0)
            throw GameException.Invalid(error);
        return collapsed;
    }
}
