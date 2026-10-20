using System.Net;
using System.Text.Json;
using SimpleGame.Games;

namespace SimpleGame.Tests;

public sealed class TwentyGameTests
{
    private static async Task<string> StartAsync(TestApi api)
    {
        var view = await api.PostOkAsync("/api/games/twenty/start", new { });
        var id = view.GetProperty("id").GetString();
        Assert.False(string.IsNullOrWhiteSpace(id));
        return id!;
    }

    private static async Task<JsonElement> WalkAsync(TestApi api, string id, IEnumerable<bool> answers)
    {
        JsonElement view = default;
        foreach (var yes in answers)
            view = await api.PostOkAsync($"/api/games/twenty/{id}/answer", new { answer = yes ? "yes" : "no" });
        return view;
    }

    private static Task<JsonElement> VerdictAsync(TestApi api, string id, bool correct) =>
        api.PostOkAsync($"/api/games/twenty/{id}/verdict", new { correct });

    private static Task<JsonElement> TeachAsync(TestApi api, string id, string realAnswer, string question) =>
        api.PostOkAsync($"/api/games/twenty/{id}/teach", new { realAnswer, question });

    private static List<bool> SeedPathTo(string term)
    {
        var path = new List<bool>();
        Assert.True(TryFind(TwentyTree.BuildSeed(), term, path), $"Seed neobsahuje pojem {term}.");
        return path;
    }

    private static bool TryFind(TwentyNode node, string term, List<bool> path)
    {
        switch (node)
        {
            case TwentyTerm candidate:
                return candidate.Term == term;
            case TwentyQuestion question:
                path.Add(true);
                if (TryFind(question.Yes, term, path))
                    return true;
                path[^1] = false;
                if (TryFind(question.No, term, path))
                    return true;
                path.RemoveAt(path.Count - 1);
                return false;
            default:
                return false;
        }
    }

    [Fact]
    public async Task StartAsksFirstQuestion()
    {
        using var api = await TestApi.CreateAsync();
        var view = await api.PostOkAsync("/api/games/twenty/start", new { });
        Assert.True(Guid.TryParse(view.GetProperty("id").GetString(), out _));
        Assert.Equal("question", view.GetProperty("phase").GetString());
        Assert.EndsWith("?", view.GetProperty("question").GetString());
        Assert.Equal(1, view.GetProperty("questionsAsked").GetInt32());
        Assert.Empty(view.GetProperty("transcript").EnumerateArray());
        Assert.Equal(JsonValueKind.Null, view.GetProperty("guess").ValueKind);
        Assert.Equal(JsonValueKind.Null, view.GetProperty("message").ValueKind);
    }

    [Theory]
    [InlineData("KRAVA", "yyy")]
    [InlineData("KUN", "yyny")]
    [InlineData("PES", "yynny")]
    [InlineData("KOCKA", "yynnn")]
    public async Task DomesticAnimalSeedUsesConsistentQuestions(string term, string answers)
    {
        using var api = await TestApi.CreateAsync();
        var id = await StartAsync(api);
        var view = await WalkAsync(api, id, answers.Select(answer => answer == 'y'));
        Assert.Equal("guess", view.GetProperty("phase").GetString());
        Assert.Equal(term, view.GetProperty("guess").GetString());
    }

    [Fact]
    public async Task CorrectVerdictFinishesAndBlocksFurtherMoves()
    {
        using var api = await TestApi.CreateAsync();
        var path = SeedPathTo("PES");
        var id = await StartAsync(api);
        var guess = await WalkAsync(api, id, path);
        Assert.Equal("guess", guess.GetProperty("phase").GetString());
        Assert.Equal("PES", guess.GetProperty("guess").GetString());
        // The final guess counts as one of the questions.
        Assert.Equal(path.Count + 1, guess.GetProperty("questionsAsked").GetInt32());
        var transcript = guess.GetProperty("transcript").EnumerateArray().ToArray();
        Assert.Equal(path.Count, transcript.Length);
        Assert.EndsWith("?", transcript[0].GetProperty("question").GetString());
        Assert.Equal(path[0] ? "yes" : "no", transcript[0].GetProperty("answer").GetString());

        var finished = await VerdictAsync(api, id, true);
        Assert.Equal("finished", finished.GetProperty("phase").GetString());
        Assert.Equal("PES", finished.GetProperty("guess").GetString());
        Assert.False(string.IsNullOrWhiteSpace(finished.GetProperty("message").GetString()));

        await api.PostErrorAsync($"/api/games/twenty/{id}/answer", new { answer = "yes" }, HttpStatusCode.Conflict, "conflict");
        await api.PostErrorAsync($"/api/games/twenty/{id}/verdict", new { correct = false }, HttpStatusCode.Conflict, "conflict");
        await api.PostErrorAsync($"/api/games/twenty/{id}/teach", new { realAnswer = "NIC", question = "NIC" }, HttpStatusCode.Conflict, "conflict");
    }

    [Fact]
    public async Task TeachingDistinguishesNewTermYesFromOldTipNo()
    {
        using var api = await TestApi.CreateAsync();
        var path = SeedPathTo("PES");
        var id = await StartAsync(api);
        await WalkAsync(api, id, path);
        await VerdictAsync(api, id, false);
        var taught = await TeachAsync(api, id, "AUTOMOBIL", "MA MOTOR");
        Assert.Equal("finished", taught.GetProperty("phase").GetString());

        // The same route now passes the new question; YES leads to the taught term.
        var second = await StartAsync(api);
        var question = await WalkAsync(api, second, path);
        Assert.Equal("question", question.GetProperty("phase").GetString());
        Assert.Equal("MA MOTOR?", question.GetProperty("question").GetString());
        Assert.Equal(path.Count + 1, question.GetProperty("questionsAsked").GetInt32());
        var yes = await api.PostOkAsync($"/api/games/twenty/{second}/answer", new { answer = "yes" });
        Assert.Equal("guess", yes.GetProperty("phase").GetString());
        Assert.Equal("AUTOMOBIL", yes.GetProperty("guess").GetString());

        // NO still yields the original tip.
        var third = await StartAsync(api);
        await WalkAsync(api, third, path);
        var no = await api.PostOkAsync($"/api/games/twenty/{third}/answer", new { answer = "no" });
        Assert.Equal("guess", no.GetProperty("phase").GetString());
        Assert.Equal("PES", no.GetProperty("guess").GetString());
    }

    [Fact]
    public async Task TeachingValidatesInputAndNormalizesText()
    {
        using var api = await TestApi.CreateAsync();
        var path = SeedPathTo("PES");
        var id = await StartAsync(api);
        // Teaching is rejected outside the teach phase.
        await api.PostErrorAsync($"/api/games/twenty/{id}/teach", new { realAnswer = "BALON", question = "UMI LITAT" },
            HttpStatusCode.Conflict, "conflict");
        await WalkAsync(api, id, path);
        await api.PostErrorAsync($"/api/games/twenty/{id}/teach", new { realAnswer = "BALON", question = "UMI LITAT" },
            HttpStatusCode.Conflict, "conflict");
        await VerdictAsync(api, id, false);

        await api.PostErrorAsync($"/api/games/twenty/{id}/teach", new { realAnswer = "", question = "UMI LITAT" },
            HttpStatusCode.BadRequest, "invalid_input");
        await api.PostErrorAsync($"/api/games/twenty/{id}/teach", new { realAnswer = "BALON", question = "" },
            HttpStatusCode.BadRequest, "invalid_input");
        await api.PostErrorAsync($"/api/games/twenty/{id}/teach", new { realAnswer = (string?)null, question = "UMI LITAT" },
            HttpStatusCode.BadRequest, "invalid_input");
        await api.PostErrorAsync($"/api/games/twenty/{id}/teach", new { realAnswer = "BALON", question = (string?)null },
            HttpStatusCode.BadRequest, "invalid_input");
        await api.PostErrorAsync($"/api/games/twenty/{id}/teach", null, HttpStatusCode.BadRequest, "invalid_input");
        // The new term must differ from the server tip.
        await api.PostErrorAsync($"/api/games/twenty/{id}/teach", new { realAnswer = "PES", question = "UMI LITAT" },
            HttpStatusCode.BadRequest, "invalid_input");
        await api.PostErrorAsync($"/api/games/twenty/{id}/teach", new { realAnswer = new string('A', 65), question = "UMI LITAT" },
            HttpStatusCode.BadRequest, "invalid_input");
        await api.PostErrorAsync($"/api/games/twenty/{id}/teach", new { realAnswer = "BALON", question = new string('A', 201) },
            HttpStatusCode.BadRequest, "invalid_input");
        await api.PostErrorAsync($"/api/games/twenty/{id}/teach", new { realAnswer = "Кошка", question = "UMI LITAT" },
            HttpStatusCode.BadRequest, "invalid_input");

        var taught = await TeachAsync(api, id, "Kočka", "Umí vrnět?");
        Assert.Equal("finished", taught.GetProperty("phase").GetString());

        var second = await StartAsync(api);
        var question = await WalkAsync(api, second, path);
        Assert.Equal("UMI VRNET?", question.GetProperty("question").GetString());
        var yes = await api.PostOkAsync($"/api/games/twenty/{second}/answer", new { answer = "yes" });
        Assert.Equal("KOCKA", yes.GetProperty("guess").GetString());
    }

    [Fact]
    public async Task ConcurrentTeachingOnSameLeafConflictsWithoutClobbering()
    {
        using var api = await TestApi.CreateAsync();
        var path = SeedPathTo("PES");
        var first = await StartAsync(api);
        var second = await StartAsync(api);

        await WalkAsync(api, first, path);
        await VerdictAsync(api, first, false);
        await TeachAsync(api, first, "BALON", "UMI LITAT");

        // The older session still guesses the original tip on its snapshot...
        var guess = await WalkAsync(api, second, path);
        Assert.Equal("guess", guess.GetProperty("phase").GetString());
        Assert.Equal("PES", guess.GetProperty("guess").GetString());
        await VerdictAsync(api, second, false);
        // ...but its leaf has been replaced, so its lesson must not overwrite the other one.
        await api.PostErrorAsync($"/api/games/twenty/{second}/teach", new { realAnswer = "BLUD", question = "JE TO ZVIRE" },
            HttpStatusCode.Conflict, "conflict");
        await api.PostErrorAsync($"/api/games/twenty/{second}/teach", new { realAnswer = "JINY", question = "JINA OTAZKA" },
            HttpStatusCode.Conflict, "conflict");

        // The first lesson survived untouched.
        var third = await StartAsync(api);
        await WalkAsync(api, third, path);
        var yes = await api.PostOkAsync($"/api/games/twenty/{third}/answer", new { answer = "yes" });
        Assert.Equal("BALON", yes.GetProperty("guess").GetString());
    }

    [Fact]
    public async Task TeachingOnDifferentLeavesCoexistOptimistically()
    {
        using var api = await TestApi.CreateAsync();
        var pesPath = SeedPathTo("PES");
        var velrybaPath = SeedPathTo("VELRYBA");
        var first = await StartAsync(api);
        var second = await StartAsync(api);

        await WalkAsync(api, first, pesPath);
        await VerdictAsync(api, first, false);
        await TeachAsync(api, first, "AUTOMOBIL", "MA MOTOR");

        await WalkAsync(api, second, velrybaPath);
        await VerdictAsync(api, second, false);
        var taught = await TeachAsync(api, second, "LOD", "PLAVE PO VODE");
        Assert.Equal("finished", taught.GetProperty("phase").GetString());

        var third = await StartAsync(api);
        await WalkAsync(api, third, pesPath);
        var car = await api.PostOkAsync($"/api/games/twenty/{third}/answer", new { answer = "yes" });
        Assert.Equal("AUTOMOBIL", car.GetProperty("guess").GetString());

        var fourth = await StartAsync(api);
        await WalkAsync(api, fourth, velrybaPath);
        var boat = await api.PostOkAsync($"/api/games/twenty/{fourth}/answer", new { answer = "yes" });
        Assert.Equal("LOD", boat.GetProperty("guess").GetString());
    }

    [Fact]
    public async Task InProgressSessionUsesItsSnapshotAndLaterTeachConflicts()
    {
        using var api = await TestApi.CreateAsync();
        var path = SeedPathTo("PES");
        var playing = await StartAsync(api);
        await api.PostOkAsync($"/api/games/twenty/{playing}/answer", new { answer = path[0] ? "yes" : "no" });

        var teacher = await StartAsync(api);
        await WalkAsync(api, teacher, path);
        await VerdictAsync(api, teacher, false);
        await TeachAsync(api, teacher, "TRAKTOR", "MA KOLA");

        // The interrupted session continues on its snapshot and reaches the original tip.
        var guess = await WalkAsync(api, playing, path.Skip(1));
        Assert.Equal("guess", guess.GetProperty("phase").GetString());
        Assert.Equal("PES", guess.GetProperty("guess").GetString());
        await VerdictAsync(api, playing, false);
        await api.PostErrorAsync($"/api/games/twenty/{playing}/teach", new { realAnswer = "NIC", question = "NIC" },
            HttpStatusCode.Conflict, "conflict");
    }

    [Fact]
    public async Task AnswerAndVerdictValidation()
    {
        using var api = await TestApi.CreateAsync();
        var id = await StartAsync(api);
        await api.PostErrorAsync($"/api/games/twenty/{id}/answer", new { answer = "maybe" }, HttpStatusCode.BadRequest, "invalid_input");
        await api.PostErrorAsync($"/api/games/twenty/{id}/answer", new { }, HttpStatusCode.BadRequest, "invalid_input");
        await api.PostErrorAsync($"/api/games/twenty/{id}/answer", null, HttpStatusCode.BadRequest, "invalid_input");
        await api.PostErrorAsync($"/api/games/twenty/{Guid.NewGuid()}/answer", new { answer = "yes" },
            HttpStatusCode.NotFound, "session_not_found");
        await api.PostErrorAsync($"/api/games/twenty/{id}/verdict", new { correct = true }, HttpStatusCode.Conflict, "conflict");
        await api.PostErrorAsync($"/api/games/twenty/{id}/verdict", new { }, HttpStatusCode.BadRequest, "invalid_input");
        await api.PostErrorAsync($"/api/games/twenty/{id}/verdict", null, HttpStatusCode.BadRequest, "invalid_input");

        var view = await api.PostOkAsync($"/api/games/twenty/{id}/answer", new { answer = "YES" });
        Assert.Equal("question", view.GetProperty("phase").GetString());
        Assert.Equal(2, view.GetProperty("questionsAsked").GetInt32());
    }

    [Fact]
    public async Task TwentyQuestionsIncludingFinalGuessLimitGivesUp()
    {
        using var api = await TestApi.CreateAsync();
        var path = SeedPathTo("PES");
        var suffixes = "ABCDEFGHIJKLMNO";

        // Each lesson deepens the route by one question; iteration k sees 5+k questions
        // and the final guess is the (6+k)-th presented question.
        for (var depth = 0; depth < suffixes.Length; depth++)
        {
            var id = await StartAsync(api);
            var guess = await WalkAsync(api, id, path.Concat(Enumerable.Repeat(true, depth)));
            Assert.Equal("guess", guess.GetProperty("phase").GetString());
            Assert.Equal(path.Count + depth + 1, guess.GetProperty("questionsAsked").GetInt32());
            await VerdictAsync(api, id, false);
            var taught = await TeachAsync(api, id, $"POJEM{suffixes[depth]}", $"ROZLISUJE {suffixes[depth]}");
            Assert.Equal("finished", taught.GetProperty("phase").GetString());
        }

        // Now the route holds 20 questions, so no final guess can be presented anymore.
        var finalId = await StartAsync(api);
        JsonElement last = default;
        for (var i = 0; i < path.Count + suffixes.Length; i++)
        {
            var yes = i < path.Count ? path[i] : true;
            last = await api.PostOkAsync($"/api/games/twenty/{finalId}/answer", new { answer = yes ? "yes" : "no" });
            var expectedPhase = i == path.Count + suffixes.Length - 1 ? "giveup" : "question";
            Assert.Equal(expectedPhase, last.GetProperty("phase").GetString());
        }
        Assert.Equal(20, last.GetProperty("questionsAsked").GetInt32());
        Assert.Equal(JsonValueKind.Null, last.GetProperty("guess").ValueKind);
        Assert.Equal(JsonValueKind.Null, last.GetProperty("question").ValueKind);
        Assert.False(string.IsNullOrWhiteSpace(last.GetProperty("message").GetString()));

        await api.PostErrorAsync($"/api/games/twenty/{finalId}/answer", new { answer = "yes" }, HttpStatusCode.Conflict, "conflict");
        await api.PostErrorAsync($"/api/games/twenty/{finalId}/verdict", new { correct = false }, HttpStatusCode.Conflict, "conflict");
        await api.PostErrorAsync($"/api/games/twenty/{finalId}/teach", new { realAnswer = "NIC", question = "NIC" },
            HttpStatusCode.Conflict, "conflict");
    }
}
