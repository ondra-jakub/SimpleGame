using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using SimpleGame.Games;

namespace SimpleGame.Tests;

/// <summary>
/// Deterministic IGameRandom used to script secret numbers and word picks.
/// </summary>
public sealed class ScriptedRandom : IGameRandom
{
    private readonly Queue<long> longs = new();
    private readonly Queue<int> ints = new();

    public ScriptedRandom WithLongs(params long[] values)
    {
        foreach (var value in values) longs.Enqueue(value);
        return this;
    }

    public ScriptedRandom WithInts(params int[] values)
    {
        foreach (var value in values) ints.Enqueue(value);
        return this;
    }

    public int Next(int minInclusive, int maxExclusive) =>
        ints.TryDequeue(out var value) ? value : throw new InvalidOperationException("Chybí scripted hodnota pro Next.");
    public long NextInt64(long minInclusive, long maxExclusive) =>
        longs.TryDequeue(out var value) ? value : throw new InvalidOperationException("Chybí scripted hodnota pro NextInt64.");
    public double NextDouble() => throw new InvalidOperationException("Random double není v testu očekáván.");
}

/// <summary>
/// In-memory test host that maps only the four basic games, mirroring Program.cs error
/// handling so the tests are independent of Program, Ollama and RPG wiring.
/// </summary>
public sealed class TestApi : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly WebApplication app;

    private TestApi(WebApplication app, HttpClient client, ScriptedRandom random)
    {
        this.app = app;
        Client = client;
        Random = random;
    }

    public HttpClient Client { get; }
    public ScriptedRandom Random { get; }

    public static async Task<TestApi> CreateAsync(ScriptedRandom? random = null)
    {
        random ??= new ScriptedRandom();
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.Configure<RouteHandlerOptions>(options => options.ThrowOnBadRequest = true);
        builder.Services.Configure<SessionOptions>(_ => { });
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddSingleton<SessionStore>();
        builder.Services.AddSingleton<IGameRandom>(random);
        builder.Services.AddSingleton<TwentyTree>();
        var app = builder.Build();
        var games = app.MapGroup("/api/games");
        games.MapNumberGame();
        games.MapHangmanGame();
        games.MapWordGame();
        games.MapTwentyGame();
        app.Use(async (context, next) =>
        {
            try
            {
                await next(context);
            }
            catch (GameException ex)
            {
                if (context.Response.HasStarted) throw;
                context.Response.StatusCode = ex.StatusCode;
                await context.Response.WriteAsJsonAsync(new ApiError(ex.Code, ex.Message));
            }
            catch (BadHttpRequestException)
            {
                if (context.Response.HasStarted) throw;
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                await context.Response.WriteAsJsonAsync(new ApiError("invalid_input",
                    "Požadavek musí obsahovat platný JSON a všechna povinná pole."));
            }
        });
        await app.StartAsync();
        var server = (TestServer)app.Services.GetRequiredService<IServer>();
        return new TestApi(app, server.CreateClient(), random);
    }

    public void Dispose()
    {
        Client.Dispose();
        app.StopAsync().GetAwaiter().GetResult();
        app.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }

    public async Task<JsonElement> PostOkAsync(string url, object? body = null)
    {
        using var response = await Client.PostAsync(url, Content(body));
        Assert.True(response.IsSuccessStatusCode, $"{url} vrátila {response.StatusCode}: {await ReadStringAsync(response)}");
        return await ReadJsonAsync(response);
    }

    public async Task<JsonElement> PostErrorAsync(string url, object? body, HttpStatusCode status, string code)
    {
        using var response = await Client.PostAsync(url, Content(body));
        Assert.Equal(status, response.StatusCode);
        var json = await ReadJsonAsync(response);
        Assert.Equal(code, json.GetProperty("code").GetString());
        Assert.False(string.IsNullOrWhiteSpace(json.GetProperty("message").GetString()));
        return json;
    }

    public Task<HttpResponseMessage> PostRawAsync(string url, string json) =>
        Client.PostAsync(url, new StringContent(json, Encoding.UTF8, "application/json"));

    private static HttpContent? Content(object? body) =>
        body is null ? null : new StringContent(JsonSerializer.Serialize(body, JsonOptions), Encoding.UTF8, "application/json");

    private static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await ReadStringAsync(response));
        return document.RootElement.Clone();
    }

    private static async Task<string> ReadStringAsync(HttpResponseMessage response) =>
        await response.Content.ReadAsStringAsync();
}

public sealed class NumberGameTests
{
    [Fact]
    public async Task StartUsesDefaultsAndFlattenedView()
    {
        using var api = await TestApi.CreateAsync(new ScriptedRandom().WithLongs(42));
        var view = await api.PostOkAsync("/api/games/number/start", new { });
        Assert.True(Guid.TryParse(view.GetProperty("id").GetString(), out _));
        Assert.Equal(1, view.GetProperty("min").GetInt64());
        Assert.Equal(100, view.GetProperty("max").GetInt64());
        Assert.Equal(0, view.GetProperty("attempts").GetInt32());
        Assert.Equal("playing", view.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, view.GetProperty("result").ValueKind);
        Assert.Empty(view.GetProperty("history").EnumerateArray());
        Assert.False(view.TryGetProperty("state", out _));
    }

    [Fact]
    public async Task GuessFeedbackHigherLowerCorrectHistoryAndConflictAfterEnd()
    {
        using var api = await TestApi.CreateAsync(new ScriptedRandom().WithLongs(9));
        var id = (await api.PostOkAsync("/api/games/number/start", new { })).GetProperty("id").GetString();

        var low = await api.PostOkAsync($"/api/games/number/{id}/guess", new { value = 5 });
        Assert.Equal("higher", low.GetProperty("result").GetString());
        Assert.Equal("playing", low.GetProperty("status").GetString());
        Assert.Equal(1, low.GetProperty("attempts").GetInt32());

        var high = await api.PostOkAsync($"/api/games/number/{id}/guess", new { value = 15 });
        Assert.Equal("lower", high.GetProperty("result").GetString());
        Assert.Equal(2, high.GetProperty("attempts").GetInt32());
        Assert.Equal("playing", high.GetProperty("status").GetString());

        var correct = await api.PostOkAsync($"/api/games/number/{id}/guess", new { value = 10 });
        Assert.Equal("correct", correct.GetProperty("result").GetString());
        Assert.Equal("won", correct.GetProperty("status").GetString());
        var history = correct.GetProperty("history").EnumerateArray().ToArray();
        Assert.Equal(3, history.Length);
        Assert.Equal(5, history[0].GetProperty("value").GetInt64());
        Assert.Equal("higher", history[0].GetProperty("result").GetString());
        Assert.Equal(15, history[1].GetProperty("value").GetInt64());
        Assert.Equal("lower", history[1].GetProperty("result").GetString());
        Assert.Equal(10, history[2].GetProperty("value").GetInt64());
        Assert.Equal("correct", history[2].GetProperty("result").GetString());

        await api.PostErrorAsync($"/api/games/number/{id}/guess", new { value = 10 }, HttpStatusCode.Conflict, "conflict");
    }

    [Fact]
    public async Task OutOfRangeGuessIsRejectedWithoutCounting()
    {
        using var api = await TestApi.CreateAsync(new ScriptedRandom().WithLongs(49));
        var id = (await api.PostOkAsync("/api/games/number/start", new { })).GetProperty("id").GetString();
        await api.PostErrorAsync($"/api/games/number/{id}/guess", new { value = 0 }, HttpStatusCode.BadRequest, "invalid_input");
        await api.PostErrorAsync($"/api/games/number/{id}/guess", new { value = 101 }, HttpStatusCode.BadRequest, "invalid_input");
        var ok = await api.PostOkAsync($"/api/games/number/{id}/guess", new { value = 50 });
        Assert.Equal(1, ok.GetProperty("attempts").GetInt32());
        Assert.Equal("won", ok.GetProperty("status").GetString());
    }

    [Fact]
    public async Task RangeValidationHandlesExtremeLongBoundaries()
    {
        using var api = await TestApi.CreateAsync(new ScriptedRandom().WithLongs(0, 0, 0));
        await api.PostErrorAsync("/api/games/number/start", new { min = 10, max = 1 }, HttpStatusCode.BadRequest, "invalid_input");
        await api.PostErrorAsync("/api/games/number/start", new { min = 5, max = 5 }, HttpStatusCode.BadRequest, "invalid_input");
        await api.PostErrorAsync("/api/games/number/start", new { min = 0, max = 1_000_001 }, HttpStatusCode.BadRequest, "invalid_input");
        // Span of exactly 1 000 000 numbers is allowed.
        var boundary = await api.PostOkAsync("/api/games/number/start", new { min = 0, max = 1_000_000 });
        Assert.Equal(0, boundary.GetProperty("min").GetInt64());
        Assert.Equal(1_000_000, boundary.GetProperty("max").GetInt64());
        // Extreme long values must not overflow into an accepted range.
        await api.PostErrorAsync("/api/games/number/start",
            new { min = long.MinValue, max = long.MaxValue }, HttpStatusCode.BadRequest, "invalid_input");
        var top = await api.PostOkAsync("/api/games/number/start", new { min = long.MaxValue - 1, max = long.MaxValue });
        Assert.Equal(long.MaxValue, top.GetProperty("max").GetInt64());
        var bottom = await api.PostOkAsync("/api/games/number/start", new { min = long.MinValue, max = long.MinValue + 10 });
        Assert.Equal(long.MinValue, bottom.GetProperty("min").GetInt64());
    }

    [Fact]
    public async Task NegativeRangeUsesLongArithmetic()
    {
        using var api = await TestApi.CreateAsync(new ScriptedRandom().WithLongs(2));
        var id = (await api.PostOkAsync("/api/games/number/start", new { min = -1000, max = -998 })).GetProperty("id").GetString();
        var low = await api.PostOkAsync($"/api/games/number/{id}/guess", new { value = -1000 });
        Assert.Equal("higher", low.GetProperty("result").GetString());
        var won = await api.PostOkAsync($"/api/games/number/{id}/guess", new { value = -998 });
        Assert.Equal("won", won.GetProperty("status").GetString());
    }

    [Fact]
    public async Task GuessRequiresValueAndMissingSessionIs404()
    {
        using var api = await TestApi.CreateAsync(new ScriptedRandom().WithLongs(1));
        await api.PostErrorAsync($"/api/games/number/{Guid.NewGuid()}/guess", new { value = 1 }, HttpStatusCode.NotFound, "session_not_found");
        var id = (await api.PostOkAsync("/api/games/number/start", new { })).GetProperty("id").GetString();
        await api.PostErrorAsync($"/api/games/number/{id}/guess", new { }, HttpStatusCode.BadRequest, "invalid_input");
        using (var missingBody = await api.PostRawAsync($"/api/games/number/{id}/guess", "{}"))
        {
            Assert.Equal(HttpStatusCode.BadRequest, missingBody.StatusCode);
        }
        using (var nullBody = await api.PostRawAsync($"/api/games/number/{id}/guess", "null"))
        {
            Assert.Equal(HttpStatusCode.BadRequest, nullBody.StatusCode);
        }
        using (var missing = await api.PostRawAsync($"/api/games/number/{id}/guess", ""))
        {
            Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode);
        }
    }
}

public sealed class HangmanGameTests
{
    private static int WordIndex(string word)
    {
        var index = Array.FindIndex(HangmanGame.Words, pair => pair.Word == word);
        Assert.True(index >= 0, $"Slovník neobsahuje {word}.");
        return index;
    }

    private static async Task<string> StartAsync(TestApi api, string word) =>
        (await api.PostOkAsync("/api/games/hangman/start", new { })).GetProperty("id").GetString()
            ?? throw new InvalidOperationException("Start nevrátil id.");

    [Fact]
    public async Task StartMasksWordAndKeepsItSecret()
    {
        using var api = await TestApi.CreateAsync(new ScriptedRandom().WithInts(WordIndex("KOČKA")));
        var view = await api.PostOkAsync("/api/games/hangman/start", new { });
        Assert.True(Guid.TryParse(view.GetProperty("id").GetString(), out _));
        Assert.Equal("_____", view.GetProperty("maskedWord").GetString());
        Assert.Equal("Zvířata", view.GetProperty("category").GetString());
        Assert.Empty(view.GetProperty("guessed").EnumerateArray());
        Assert.Equal(0, view.GetProperty("wrong").GetInt32());
        Assert.Equal(8, view.GetProperty("maxWrong").GetInt32());
        Assert.Equal("playing", view.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, view.GetProperty("word").ValueKind);
    }

    [Fact]
    public async Task PlainAndDiacriticGuessesRevealOriginalAndRepeatIsIgnored()
    {
        using var api = await TestApi.CreateAsync(new ScriptedRandom().WithInts(WordIndex("KOČKA")));
        var id = await StartAsync(api, "KOČKA");

        var withC = await api.PostOkAsync($"/api/games/hangman/{id}/guess", new { letter = "c" });
        Assert.Equal("__Č__", withC.GetProperty("maskedWord").GetString());
        Assert.Equal(0, withC.GetProperty("wrong").GetInt32());
        Assert.Equal(new[] { "C" }, withC.GetProperty("guessed").EnumerateArray().Select(g => g.GetString()).ToArray());

        // Diacritic input normalizes to the same letter and does not change state twice.
        var repeated = await api.PostOkAsync($"/api/games/hangman/{id}/guess", new { letter = "č" });
        Assert.Equal("__Č__", repeated.GetProperty("maskedWord").GetString());
        Assert.Equal(0, repeated.GetProperty("wrong").GetInt32());
        Assert.Single(repeated.GetProperty("guessed").EnumerateArray());

        var withK = await api.PostOkAsync($"/api/games/hangman/{id}/guess", new { letter = "K" });
        Assert.Equal("K_ČK_", withK.GetProperty("maskedWord").GetString());

        await api.PostOkAsync($"/api/games/hangman/{id}/guess", new { letter = "o" });
        var won = await api.PostOkAsync($"/api/games/hangman/{id}/guess", new { letter = "a" });
        Assert.Equal("KOČKA", won.GetProperty("maskedWord").GetString());
        Assert.Equal("won", won.GetProperty("status").GetString());
        Assert.Equal("KOČKA", won.GetProperty("word").GetString());
    }

    [Fact]
    public async Task EightWrongGuessesLoseAndRevealWord()
    {
        using var api = await TestApi.CreateAsync(new ScriptedRandom().WithInts(WordIndex("KOČKA")));
        var id = await StartAsync(api, "KOČKA");
        foreach (var letter in new[] { "B", "D", "E", "F", "G", "H", "I" })
        {
            var wrong = await api.PostOkAsync($"/api/games/hangman/{id}/guess", new { letter });
            Assert.Equal("playing", wrong.GetProperty("status").GetString());
        }
        var lost = await api.PostOkAsync($"/api/games/hangman/{id}/guess", new { letter = "J" });
        Assert.Equal("lost", lost.GetProperty("status").GetString());
        Assert.Equal(8, lost.GetProperty("wrong").GetInt32());
        Assert.Equal("KOČKA", lost.GetProperty("word").GetString());
        await api.PostErrorAsync($"/api/games/hangman/{id}/guess", new { letter = "K" }, HttpStatusCode.Conflict, "conflict");
    }

    [Fact]
    public async Task GuessRequiresExactlyOneLetter()
    {
        using var api = await TestApi.CreateAsync(new ScriptedRandom().WithInts(WordIndex("KOČKA")));
        await api.PostErrorAsync($"/api/games/hangman/{Guid.NewGuid()}/guess", new { letter = "A" }, HttpStatusCode.NotFound, "session_not_found");
        var id = await StartAsync(api, "KOČKA");
        await api.PostErrorAsync($"/api/games/hangman/{id}/guess", new { letter = "1" }, HttpStatusCode.BadRequest, "invalid_input");
        await api.PostErrorAsync($"/api/games/hangman/{id}/guess", new { letter = "CH" }, HttpStatusCode.BadRequest, "invalid_input");
        await api.PostErrorAsync($"/api/games/hangman/{id}/guess", new { letter = "" }, HttpStatusCode.BadRequest, "invalid_input");
        await api.PostErrorAsync($"/api/games/hangman/{id}/guess", new { }, HttpStatusCode.BadRequest, "invalid_input");
        using var missing = await api.PostRawAsync($"/api/games/hangman/{id}/guess", "");
        Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode);
    }
}

public sealed class WordGameTests
{
    private static int SecretIndex(string secret)
    {
        var index = Array.IndexOf(WordGame.Dictionary, secret);
        Assert.True(index >= 0, $"Slovník neobsahuje {secret}.");
        return index;
    }

    private static string[] Feedback(JsonElement view, int index) =>
        view.GetProperty("guesses")[index].GetProperty("feedback").EnumerateArray()
            .Select(f => f.GetString()).ToArray()!;

    [Fact]
    public async Task StartReturnsGridDefaultsWithoutSecret()
    {
        using var api = await TestApi.CreateAsync(new ScriptedRandom().WithInts(SecretIndex("MESTO")));
        var view = await api.PostOkAsync("/api/games/word/start", new { });
        Assert.True(Guid.TryParse(view.GetProperty("id").GetString(), out _));
        Assert.Equal(5, view.GetProperty("length").GetInt32());
        Assert.Equal(6, view.GetProperty("maxAttempts").GetInt32());
        Assert.Equal("playing", view.GetProperty("status").GetString());
        Assert.Empty(view.GetProperty("guesses").EnumerateArray());
        Assert.Equal(JsonValueKind.Null, view.GetProperty("secret").ValueKind);
    }

    [Fact]
    public async Task FeedbackCountsEachSecretLetterOccurrenceOnlyOnce()
    {
        using var api = await TestApi.CreateAsync(new ScriptedRandom().WithInts(SecretIndex("BANAN")));
        var id = (await api.PostOkAsync("/api/games/word/start", new { })).GetProperty("id").GetString();

        var guess = await api.PostOkAsync($"/api/games/word/{id}/guess", new { word = "NNNNN" });
        Assert.Equal(new[] { "absent", "absent", "correct", "absent", "correct" }, Feedback(guess, 0));

        var ananb = await api.PostOkAsync($"/api/games/word/{id}/guess", new { word = "ANANB" });
        Assert.Equal(new[] { "present", "present", "present", "present", "present" }, Feedback(ananb, 1));

        var nanaa = await api.PostOkAsync($"/api/games/word/{id}/guess", new { word = "NANAA" });
        Assert.Equal(new[] { "present", "correct", "correct", "correct", "absent" }, Feedback(nanaa, 2));

        Assert.Equal(3, nanaa.GetProperty("guesses").EnumerateArray().Count());
        Assert.Equal("playing", nanaa.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, nanaa.GetProperty("secret").ValueKind);
    }

    [Fact]
    public async Task DiacriticGuessNormalizesAndWins()
    {
        using var api = await TestApi.CreateAsync(new ScriptedRandom().WithInts(SecretIndex("BANAN")));
        var id = (await api.PostOkAsync("/api/games/word/start", new { })).GetProperty("id").GetString();
        var won = await api.PostOkAsync($"/api/games/word/{id}/guess", new { word = "banán" });
        Assert.Equal("won", won.GetProperty("status").GetString());
        Assert.Equal("BANAN", won.GetProperty("guesses")[0].GetProperty("word").GetString());
        Assert.Equal(new[] { "correct", "correct", "correct", "correct", "correct" }, Feedback(won, 0));
        Assert.Equal("BANAN", won.GetProperty("secret").GetString());
        await api.PostErrorAsync($"/api/games/word/{id}/guess", new { word = "MESTO" }, HttpStatusCode.Conflict, "conflict");
    }

    [Fact]
    public async Task SixGuessesLoseAndRevealSecret()
    {
        using var api = await TestApi.CreateAsync(new ScriptedRandom().WithInts(SecretIndex("MESTO")));
        var id = (await api.PostOkAsync("/api/games/word/start", new { })).GetProperty("id").GetString();
        foreach (var word in new[] { "AAAAA", "BBBBB", "CCCCC", "DDDDD", "EEEEE" })
        {
            var playing = await api.PostOkAsync($"/api/games/word/{id}/guess", new { word });
            Assert.Equal("playing", playing.GetProperty("status").GetString());
        }
        var lost = await api.PostOkAsync($"/api/games/word/{id}/guess", new { word = "FFFFF" });
        Assert.Equal("lost", lost.GetProperty("status").GetString());
        Assert.Equal(6, lost.GetProperty("guesses").EnumerateArray().Count());
        Assert.Equal("MESTO", lost.GetProperty("secret").GetString());
        await api.PostErrorAsync($"/api/games/word/{id}/guess", new { word = "GGGGG" }, HttpStatusCode.Conflict, "conflict");
    }

    [Fact]
    public async Task GuessRequiresFiveLettersButNotDictionaryWord()
    {
        using var api = await TestApi.CreateAsync(new ScriptedRandom().WithInts(SecretIndex("MESTO")));
        var id = (await api.PostOkAsync("/api/games/word/start", new { })).GetProperty("id").GetString();
        await api.PostErrorAsync($"/api/games/word/{id}/guess", new { word = "ABCD" }, HttpStatusCode.BadRequest, "invalid_input");
        await api.PostErrorAsync($"/api/games/word/{id}/guess", new { word = "ABCDEFG" }, HttpStatusCode.BadRequest, "invalid_input");
        await api.PostErrorAsync($"/api/games/word/{id}/guess", new { word = "12345" }, HttpStatusCode.BadRequest, "invalid_input");
        await api.PostErrorAsync($"/api/games/word/{id}/guess", new { word = "A-B-C" }, HttpStatusCode.BadRequest, "invalid_input");
        await api.PostErrorAsync($"/api/games/word/{id}/guess", new { }, HttpStatusCode.BadRequest, "invalid_input");
        // A non-dictionary five-letter sequence is a valid guess.
        var valid = await api.PostOkAsync($"/api/games/word/{id}/guess", new { word = "QQQQQ" });
        Assert.Equal(new[] { "absent", "absent", "absent", "absent", "absent" }, Feedback(valid, 0));
    }
}