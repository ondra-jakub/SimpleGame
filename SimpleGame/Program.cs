using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Options;
using SimpleGame;
using SimpleGame.Games;

var builder = WebApplication.CreateBuilder(args);
builder.Services.Configure<RouteHandlerOptions>(options => options.ThrowOnBadRequest = true);
builder.Services.AddOptions<SimpleGame.SessionOptions>().BindConfiguration("Sessions")
    .Validate(o => o.MaxSessions > 0 && o.IdleTimeout > TimeSpan.Zero, "Invalid session limits.").ValidateOnStart();
builder.Services.AddOptions<OllamaOptions>().BindConfiguration("Ollama")
    .Validate(o => Uri.TryCreate(o.BaseUrl, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https", "Invalid Ollama BaseUrl.")
    .Validate(o => o.ModelsTimeoutSeconds is > 0 and <= 300 && o.MoveTimeoutSeconds is > 0 and <= 300, "Invalid Ollama timeout.")
    .ValidateOnStart();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<SessionStore>();
builder.Services.AddSingleton<IGameRandom, GameRandom>();
builder.Services.AddHostedService<SessionCleanupService>();
builder.Services.AddSingleton<TwentyTree>();
builder.Services.AddSingleton<ModelCatalogCache>();
builder.Services.AddHttpClient<IOllamaClient, OllamaClient>((services, http) =>
{
    var options = services.GetRequiredService<IOptions<OllamaOptions>>().Value;
    http.BaseAddress = new Uri(options.BaseUrl.TrimEnd('/') + "/");
    http.Timeout = Timeout.InfiniteTimeSpan;
    http.MaxResponseContentBufferSize = 4 * 1024 * 1024;
});
builder.Services.AddTransient<RpgEngine>();

var app = builder.Build();
app.Use(async (context, next) =>
{
    try { await next(context); }
    catch (GameException ex)
    {
        context.Response.StatusCode = ex.StatusCode;
        await context.Response.WriteAsJsonAsync(new ApiError(ex.Code, ex.Message), context.RequestAborted);
    }
    catch (BadHttpRequestException)
    {
        context.Response.StatusCode = StatusCodes.Status400BadRequest;
        await context.Response.WriteAsJsonAsync(new ApiError("invalid_input", "Pozadavek musi obsahovat platny JSON a vsechna povinna pole."), context.RequestAborted);
    }
    catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
    {
        context.Response.StatusCode = 499;
    }
    catch (OperationCanceledException) when (app.Lifetime.ApplicationStopping.IsCancellationRequested)
    {
        context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
        await context.Response.WriteAsJsonAsync(new ApiError("server_stopping", "Server se zastavuje. Tah nebyl potvrzen."));
    }
    catch (Exception ex)
    {
        app.Logger.LogError(ex, "Unhandled API failure.");
        context.Response.StatusCode = StatusCodes.Status500InternalServerError;
        await context.Response.WriteAsJsonAsync(new ApiError("server_error", "Nastala chyba serveru. Obnovte stav hry."), context.RequestAborted);
    }
});

app.MapGet("/api/health", () => new { status = "ok" });
app.MapGet("/api/llm/models", (IOllamaClient ollama, CancellationToken ct) => ollama.GetModelsAsync(true, ct));
var games = app.MapGroup("/api/games");
games.MapNumberGame();
games.MapHangmanGame();
games.MapWordGame();
games.MapTwentyGame();
games.MapRpgGame();
app.MapFallback(() => Results.NotFound(new ApiError("not_found", "Pozadovana cesta neexistuje.")));
app.Run();

public partial class Program;
