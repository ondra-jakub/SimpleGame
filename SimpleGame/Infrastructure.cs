using System.Globalization;
using System.Text;
using Microsoft.Extensions.Options;

namespace SimpleGame;

public sealed record ApiError(string Code, string Message);

public sealed class GameException(int statusCode, string code, string message) : Exception(message)
{
    public int StatusCode { get; } = statusCode;
    public string Code { get; } = code;
    public static GameException Invalid(string message) => new(400, "invalid_input", message);
    public static GameException Conflict(string message = "Tento tah nelze provest. Obnovte stav hry.") => new(409, "conflict", message);
    public static GameException Missing() => new(404, "session_not_found", "Hra neexistuje nebo vyprsela. Zacnete novou hru.");
}

public abstract class GameSession
{
    public Guid Id { get; } = Guid.NewGuid();
    public SemaphoreSlim Gate { get; } = new(1, 1);
    internal DateTimeOffset LastAccess { get; set; }
    internal int ActiveOperations { get; set; }
}

public sealed class SessionOptions
{
    public int MaxSessions { get; set; } = 1000;
    public TimeSpan IdleTimeout { get; set; } = TimeSpan.FromHours(2);
}

public sealed class SessionStore(IOptions<SessionOptions> options, TimeProvider clock)
{
    private readonly object sync = new();
    private readonly Dictionary<Guid, GameSession> sessions = [];

    public T Add<T>(T session) where T : GameSession
    {
        lock (sync)
        {
            CleanupLocked();
            if (sessions.Count >= options.Value.MaxSessions)
                throw new GameException(429, "session_limit", "Server ma prilis mnoho her. Zkuste to pozdeji.");
            session.LastAccess = clock.GetUtcNow();
            sessions.Add(session.Id, session);
            return session;
        }
    }

    public SessionLease<T> Get<T>(Guid id) where T : GameSession
    {
        lock (sync)
        {
            if (!sessions.TryGetValue(id, out var session) || session is not T typed)
                throw GameException.Missing();
            if (session.ActiveOperations == 0 && clock.GetUtcNow() - session.LastAccess >= options.Value.IdleTimeout)
            {
                sessions.Remove(id);
                throw GameException.Missing();
            }
            session.ActiveOperations++;
            session.LastAccess = clock.GetUtcNow();
            return new SessionLease<T>(typed, () =>
            {
                lock (sync)
                {
                    session.ActiveOperations--;
                    session.LastAccess = clock.GetUtcNow();
                }
            });
        }
    }

    public TResult Mutate<T, TResult>(Guid id, Func<T, TResult> mutation) where T : GameSession
    {
        using var lease = Get<T>(id);
        if (!lease.Value.Gate.Wait(0)) throw GameException.Conflict("Predchozi tah se jeste zpracovava.");
        try { return mutation(lease.Value); }
        finally { lease.Value.Gate.Release(); }
    }

    public void Cleanup()
    {
        lock (sync) CleanupLocked();
    }

    private void CleanupLocked()
    {
        var now = clock.GetUtcNow();
        foreach (var id in sessions.Where(p => p.Value.ActiveOperations == 0 && now - p.Value.LastAccess >= options.Value.IdleTimeout).Select(p => p.Key).ToArray())
            sessions.Remove(id);
    }
}

public sealed class SessionLease<T>(T value, Action release) : IDisposable where T : GameSession
{
    public T Value { get; } = value;
    private Action? onDispose = release;
    public void Dispose() => Interlocked.Exchange(ref onDispose, null)?.Invoke();
}

public sealed class SessionCleanupService(SessionStore store) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1));
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken)) store.Cleanup();
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }
}

public interface IGameRandom
{
    int Next(int minInclusive, int maxExclusive);
    long NextInt64(long minInclusive, long maxExclusive);
    double NextDouble();
}

public sealed class GameRandom : IGameRandom
{
    public int Next(int minInclusive, int maxExclusive) => Random.Shared.Next(minInclusive, maxExclusive);
    public long NextInt64(long minInclusive, long maxExclusive) => Random.Shared.NextInt64(minInclusive, maxExclusive);
    public double NextDouble() => Random.Shared.NextDouble();
}

public static class CzechText
{
    public static string Letters(string? input, int maxInputLength = 64)
    {
        if (string.IsNullOrWhiteSpace(input) || input.Length > maxInputLength)
            throw GameException.Invalid("Zadejte platna pismena.");
        string normalized;
        try
        {
            normalized = new string(input.Normalize(NormalizationForm.FormD)
                .Where(c => CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
                .ToArray()).ToUpperInvariant();
        }
        catch (ArgumentException) { throw GameException.Invalid("Text obsahuje neplatne znaky."); }
        if (normalized.Length == 0 || normalized.Any(c => c is < 'A' or > 'Z'))
            throw GameException.Invalid("Pouzijte pouze pismena A-Z, pripadne s ceskou diakritikou.");
        return normalized;
    }
}
