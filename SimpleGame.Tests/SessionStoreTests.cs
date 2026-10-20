using Microsoft.Extensions.Options;
using SimpleGame;

namespace SimpleGame.Tests;

public sealed class SessionStoreTests
{
    private sealed class FakeClock : TimeProvider
    {
        private DateTimeOffset now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => now;
        public void Advance(TimeSpan time) => now += time;
    }

    private sealed class DummySession : GameSession { }
    private sealed class OtherSession : GameSession { }

    private static SessionStore CreateStore(FakeClock clock, int maxSessions = 1000, TimeSpan? idleTimeout = null) =>
        new(Options.Create(new SessionOptions
        {
            MaxSessions = maxSessions,
            IdleTimeout = idleTimeout ?? TimeSpan.FromMinutes(10)
        }), clock);

    [Fact]
    public void Get_UnknownSession_ThrowsSessionNotFound()
    {
        var store = CreateStore(new FakeClock());
        var ex = Assert.Throws<GameException>(() => store.Get<DummySession>(Guid.NewGuid()));
        Assert.Equal(404, ex.StatusCode);
        Assert.Equal("session_not_found", ex.Code);
    }

    [Fact]
    public void Get_SessionOfWrongType_Throws404()
    {
        var store = CreateStore(new FakeClock());
        var session = store.Add(new OtherSession());
        var ex = Assert.Throws<GameException>(() => store.Get<DummySession>(session.Id));
        Assert.Equal(404, ex.StatusCode);
        Assert.Equal("session_not_found", ex.Code);
    }

    [Fact]
    public void Get_AfterIdleTimeout_Throws404()
    {
        var clock = new FakeClock();
        var store = CreateStore(clock);
        var session = store.Add(new DummySession());
        clock.Advance(TimeSpan.FromMinutes(9));
        store.Get<DummySession>(session.Id).Dispose();
        clock.Advance(TimeSpan.FromMinutes(10));
        var ex = Assert.Throws<GameException>(() => store.Get<DummySession>(session.Id));
        Assert.Equal(404, ex.StatusCode);
    }

    [Fact]
    public void Get_RefreshesIdleWindowOnEachAccess()
    {
        var clock = new FakeClock();
        var store = CreateStore(clock);
        var session = store.Add(new DummySession());
        for (var i = 0; i < 3; i++)
        {
            clock.Advance(TimeSpan.FromMinutes(9));
            using var lease = store.Get<DummySession>(session.Id);
        }
        clock.Advance(TimeSpan.FromMinutes(10));
        Assert.Throws<GameException>(() => store.Get<DummySession>(session.Id));
    }

    [Fact]
    public void Get_ActiveLease_PreventsExpiry()
    {
        var clock = new FakeClock();
        var store = CreateStore(clock);
        var session = store.Add(new DummySession());
        var lease = store.Get<DummySession>(session.Id);
        clock.Advance(TimeSpan.FromHours(3));
        using var second = store.Get<DummySession>(session.Id);
        lease.Dispose();
        second.Dispose();
        clock.Advance(TimeSpan.FromHours(3));
        Assert.Throws<GameException>(() => store.Get<DummySession>(session.Id));
    }

    [Fact]
    public void Dispose_LeaseTwice_SessionStillExpires()
    {
        var clock = new FakeClock();
        var store = CreateStore(clock);
        var session = store.Add(new DummySession());
        var lease = store.Get<DummySession>(session.Id);
        lease.Dispose();
        lease.Dispose();
        clock.Advance(TimeSpan.FromMinutes(10));
        Assert.Throws<GameException>(() => store.Get<DummySession>(session.Id));
    }

    [Fact]
    public void Add_OverCapacity_Throws429()
    {
        var store = CreateStore(new FakeClock(), maxSessions: 2);
        var first = store.Add(new DummySession());
        var second = store.Add(new DummySession());
        var ex = Assert.Throws<GameException>(() => store.Add(new DummySession()));
        Assert.Equal(429, ex.StatusCode);
        Assert.Equal("session_limit", ex.Code);
        using var lease = store.Get<DummySession>(first.Id);
        using var lease2 = store.Get<DummySession>(second.Id);
    }

    [Fact]
    public void Add_AfterExpiry_ReusesCapacity()
    {
        var clock = new FakeClock();
        var store = CreateStore(clock, maxSessions: 1);
        var expired = store.Add(new DummySession());
        clock.Advance(TimeSpan.FromMinutes(10));
        var fresh = store.Add(new DummySession());
        Assert.NotEqual(expired.Id, fresh.Id);
        Assert.Throws<GameException>(() => store.Get<DummySession>(expired.Id));
        using var lease = store.Get<DummySession>(fresh.Id);
    }

    [Fact]
    public void Cleanup_RemovesIdleSessions()
    {
        var clock = new FakeClock();
        var store = CreateStore(clock, maxSessions: 1);
        var idle = store.Add(new DummySession());
        clock.Advance(TimeSpan.FromMinutes(10));
        store.Cleanup();
        var fresh = store.Add(new DummySession());
        Assert.Throws<GameException>(() => store.Get<DummySession>(idle.Id));
        using var lease = store.Get<DummySession>(fresh.Id);
    }

    [Fact]
    public void Mutate_ReturnsValueAndAllowsFollowUpMutations()
    {
        var store = CreateStore(new FakeClock());
        var session = store.Add(new DummySession());
        Assert.Equal(42, store.Mutate<DummySession, int>(session.Id, s => 42));
        Assert.Equal(43, store.Mutate<DummySession, int>(session.Id, s => 43));
    }

    [Fact]
    public void Mutate_WhileGateHeld_Throws409()
    {
        var store = CreateStore(new FakeClock());
        var session = store.Add(new DummySession());
        Assert.True(session.Gate.Wait(0));
        var ex = Assert.Throws<GameException>(() => store.Mutate<DummySession, int>(session.Id, s => 42));
        Assert.Equal(409, ex.StatusCode);
        Assert.Equal("conflict", ex.Code);
        session.Gate.Release();
        Assert.Equal(42, store.Mutate<DummySession, int>(session.Id, s => 42));
    }

    [Fact]
    public void Mutate_MissingSession_Throws404()
    {
        var store = CreateStore(new FakeClock());
        var ex = Assert.Throws<GameException>(() => store.Mutate<DummySession, int>(Guid.NewGuid(), s => 42));
        Assert.Equal(404, ex.StatusCode);
    }

    [Fact]
    public void Mutate_SessionOfWrongType_Throws404()
    {
        var store = CreateStore(new FakeClock());
        var session = store.Add(new OtherSession());
        var ex = Assert.Throws<GameException>(() => store.Mutate<DummySession, int>(session.Id, s => 42));
        Assert.Equal(404, ex.StatusCode);
    }

    [Fact]
    public void Mutate_ExpiredSession_Throws404()
    {
        var clock = new FakeClock();
        var store = CreateStore(clock);
        var session = store.Add(new DummySession());
        clock.Advance(TimeSpan.FromMinutes(10));
        var ex = Assert.Throws<GameException>(() => store.Mutate<DummySession, int>(session.Id, s => 42));
        Assert.Equal(404, ex.StatusCode);
    }
}