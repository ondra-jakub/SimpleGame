namespace SimpleGame.Games;

public static class OpponentModes
{
    public const string Classic = "classic";
    public const string Decision = "decision";
}

public static class OpponentSources
{
    public const string Classic = "classic";
    public const string Decision = "decision";
    public const string Heuristic = "heuristic";
}

public sealed record OpponentMove(string Source, string? Model, string Action, string? Taunt, string? FallbackReason,
    DecisionDetail? Decision = null);
public sealed record RpgState(int PlayerHp = 100, int OpponentHp = 100, int PlayerCooldown = 0, int OpponentCooldown = 0,
    int Round = 1, int Revision = 0, string Status = "playing", string[]? Log = null, OpponentMove? LastOpponentMove = null);
public sealed record RpgView(Guid Id, string? Model, string OpponentMode, int PlayerHp, int OpponentHp, int PlayerCooldown, int OpponentCooldown,
    int Round, int Revision, bool InProgress, string Status, string[] Log, OpponentMove? LastOpponentMove);
public sealed record RpgStartRequest(string? Model);
public sealed record RpgActionRequest(string Action, int? ExpectedRevision);

public sealed class RpgSession(string? model, string opponentMode) : GameSession
{
    private RpgState state = new();
    private int inProgress;
    public string? Model { get; } = model;
    public string OpponentMode { get; } = opponentMode;
    public RpgState State { get => Volatile.Read(ref state); set => Volatile.Write(ref state, value); }
    public bool InProgress { get => Volatile.Read(ref inProgress) == 1; set => Volatile.Write(ref inProgress, value ? 1 : 0); }
    public RpgView View()
    {
        var snapshot = State;
        return new RpgView(Id, Model, OpponentMode, snapshot.PlayerHp, snapshot.OpponentHp, snapshot.PlayerCooldown, snapshot.OpponentCooldown,
            snapshot.Round, snapshot.Revision, InProgress, snapshot.Status, snapshot.Log ?? [], snapshot.LastOpponentMove);
    }
}

public sealed class RpgEngine(SessionStore sessions, IGameRandom random, IOllamaClient ollama, IHostApplicationLifetime lifetime)
{
    public async Task<RpgView> StartAsync(RpgStartRequest request, CancellationToken cancellationToken)
    {
        var opponentMode = OpponentModes.Classic;
        if (request.Model is not null)
        {
            if (request.Model.Length is 0 or > 256) throw GameException.Invalid("Zvolte platny model nebo klasickou AI.");
            var models = await ollama.GetModelsAsync(false, cancellationToken);
            if (!models.Available)
                throw new GameException(503, "ollama_unavailable", "Ollama neni dostupna. Zvolte klasickou AI nebo zkuste obnovit modely.");
            var selected = models.Models.FirstOrDefault(m => m.Name == request.Model);
            if (selected is null)
                throw GameException.Invalid("Tento model neni nainstalovany v Ollame. Obnovte seznam modelu.");
            if (!selected.Supports("decision"))
                throw GameException.Invalid("Tento model neumi decision rezim a nemuze byt protivnikem. Zvolte decision model nebo klasickou AI.");
            opponentMode = OpponentModes.Decision;
        }
        cancellationToken.ThrowIfCancellationRequested();
        return sessions.Add(new RpgSession(request.Model, opponentMode)).View();
    }

    public RpgView Get(Guid id)
    {
        using var lease = sessions.Get<RpgSession>(id);
        return lease.Value.View();
    }

    public async Task<RpgView> ActAsync(Guid id, RpgActionRequest request, CancellationToken cancellationToken)
    {
        using var lease = sessions.Get<RpgSession>(id);
        var session = lease.Value;
        if (!session.Gate.Wait(0)) throw GameException.Conflict("Protivnik jeste premysli. Obnovte stav souboje.");
        try
        {
            var current = session.State;
            if (request.ExpectedRevision is null || request.ExpectedRevision < 0)
                throw GameException.Invalid("Chybi platna revision souboje.");
            if (current.Revision != request.ExpectedRevision)
                throw GameException.Conflict("Stav souboje se zmenil. Obnovte jej pred dalsim tahem.");
            if (current.Status != "playing") throw GameException.Conflict("Souboj uz skoncil. Zacnete novy.");
            if (request.Action is not ("attack" or "special" or "heal")) throw GameException.Invalid("Neznama akce.");
            if (request.Action == "special" && current.PlayerCooldown > 0) throw GameException.Invalid("Special se jeste obnovuje.");
            session.InProgress = true;
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime.ApplicationStopping);
            var token = cancellation.Token;
            token.ThrowIfCancellationRequested();
            var turnLog = new List<string> { $"Kolo {current.Round}" };
            var working = Apply(current, request.Action, true, turnLog);
            OpponentMove? move = null;
            if (working.OpponentHp > 0)
            {
                var allowed = working.OpponentCooldown == 0 ? new[] { "attack", "special", "heal" } : ["attack", "heal"];
                OpponentDecision? decision = null;
                if (session.OpponentMode == OpponentModes.Decision)
                {
                    var recentLog = (current.Log ?? []).Concat(turnLog).TakeLast(3).ToArray();
                    decision = await ollama.ChooseDecisionMoveAsync(session.Model!,
                        new OpponentContext(working.PlayerHp, working.OpponentHp, working.Round, working.OpponentCooldown,
                            allowed, request.Action, working.PlayerCooldown) { RecentLog = recentLog }, token);
                }
                token.ThrowIfCancellationRequested();
                var action = decision?.Action ?? Heuristic(working);
                var source = session.OpponentMode == OpponentModes.Decision
                    ? decision?.Action is not null ? OpponentSources.Decision : OpponentSources.Heuristic
                    : OpponentSources.Classic;
                move = new OpponentMove(source, session.Model, action, decision?.Taunt, decision?.FallbackReason,
                    source == OpponentSources.Decision ? decision?.Decision : null);
                if (move.FallbackReason is not null) turnLog.Add($"Nahradni AI: {move.FallbackReason}");
                if (!string.IsNullOrEmpty(move.Taunt)) turnLog.Add($"Protivnik: {move.Taunt}");
                working = Apply(working, action, false, turnLog);
            }
            working = working with
            {
                PlayerCooldown = request.Action == "special" ? 3 : Math.Max(0, current.PlayerCooldown - 1),
                OpponentCooldown = move?.Action == "special" ? 3 : Math.Max(0, current.OpponentCooldown - 1),
                Status = working.OpponentHp == 0 ? "won" : working.PlayerHp == 0 ? "lost" : "playing",
                Revision = current.Revision + 1,
                Round = current.Round + 1,
                LastOpponentMove = move,
                Log = [.. current.Log ?? [], .. turnLog]
            };
            token.ThrowIfCancellationRequested();
            // Only publish a complete round. GET readers never observe either half-turn.
            session.State = working;
            session.InProgress = false;
            return session.View();
        }
        finally
        {
            session.InProgress = false;
            session.Gate.Release();
        }
    }

    private string Heuristic(RpgState state)
    {
        if (state.OpponentHp < 30 && random.NextDouble() < .4) return "heal";
        if (state.OpponentCooldown == 0 && random.NextDouble() < .5) return "special";
        return "attack";
    }

    private RpgState Apply(RpgState state, string action, bool player, List<string> log)
    {
        var actor = player ? "Hrac" : "Protivnik";
        if (action == "heal")
        {
            var hp = player ? state.PlayerHp : state.OpponentHp;
            var healed = Math.Min(100 - hp, player ? random.Next(15, 26) : random.Next(12, 21));
            log.Add($"{actor} se leci: +{healed} HP.");
            return player ? state with { PlayerHp = hp + healed } : state with { OpponentHp = hp + healed };
        }
        if (action == "special" && random.NextDouble() < .3)
        {
            log.Add($"{actor}: special minul.");
            return state;
        }
        var damage = (player, action) switch
        {
            (true, "attack") => random.Next(10, 19),
            (true, "special") => random.Next(20, 31),
            (false, "special") => random.Next(18, 27),
            _ => random.Next(8, 16)
        };
        log.Add($"{actor}: {(action == "special" ? "special" : "utok")} za {damage} HP.");
        return player ? state with { OpponentHp = Math.Max(0, state.OpponentHp - damage) }
            : state with { PlayerHp = Math.Max(0, state.PlayerHp - damage) };
    }
}

public static class RpgGame
{
    public static void MapRpgGame(this RouteGroupBuilder games)
    {
        var group = games.MapGroup("/rpg");
        group.MapPost("/start", (RpgStartRequest request, RpgEngine engine, CancellationToken ct) => engine.StartAsync(request, ct));
        group.MapGet("/{id:guid}", (Guid id, RpgEngine engine) => engine.Get(id));
        group.MapPost("/{id:guid}/action", (Guid id, RpgActionRequest request, RpgEngine engine, CancellationToken ct) => engine.ActAsync(id, request, ct));
    }
}
