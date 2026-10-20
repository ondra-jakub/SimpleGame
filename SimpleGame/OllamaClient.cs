using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;

namespace SimpleGame;

public sealed class OllamaOptions
{
    public string BaseUrl { get; set; } = "http://localhost:11434";
    public double ModelsTimeoutSeconds { get; set; } = 3;
    public double DecisionTimeoutSeconds { get; set; } = 10;
    public string KeepAlive { get; set; } = "10m";
}

public sealed record ModelInfo
{
    public ModelInfo(string name, long sizeBytes, string[]? capabilities = null)
    {
        Name = name;
        SizeBytes = sizeBytes;
        Capabilities = capabilities ?? [];
    }

    public string Name { get; init; }
    public long SizeBytes { get; init; }
    public string[] Capabilities { get; init; }

    public bool Supports(string capability) =>
        Capabilities.Contains(capability, StringComparer.OrdinalIgnoreCase);
}

public sealed record ModelsView(bool Available, ModelInfo[] Models, string? Message = null);

public sealed record OpponentContext(
    int PlayerHp,
    int OpponentHp,
    int Round,
    int OpponentCooldown,
    string[] AllowedActions,
    string LastPlayerAction,
    int PlayerCooldown = 0)
{
    public string[] RecentLog { get; init; } = [];
}

public sealed record DangerLabels(string? @true, string? @false);

public sealed record DecisionDetail(
    IReadOnlyDictionary<string, double> Probabilities,
    double Confidence,
    double? Danger,
    [property: JsonIgnore] string? DangerTrue,
    [property: JsonIgnore] string? DangerFalse)
{
    public DangerLabels DangerLabel => new(DangerTrue, DangerFalse);
}

public sealed record OpponentDecision(string? Action, string? Taunt = null, string? FallbackReason = null, DecisionDetail? Decision = null);

public interface IOllamaClient
{
    Task<ModelsView> GetModelsAsync(bool refresh, CancellationToken cancellationToken);
    Task<OpponentDecision> ChooseDecisionMoveAsync(string model, OpponentContext context, CancellationToken cancellationToken);
}

public sealed class ModelCatalogCache
{
    internal SemaphoreSlim Gate { get; } = new(1, 1);
    internal ModelsView? Value { get; set; }
    internal DateTimeOffset Expires { get; set; }
}

public sealed class OllamaClient(HttpClient http, IOptions<OllamaOptions> options, ModelCatalogCache cache,
    TimeProvider clock, ILogger<OllamaClient> logger) : IOllamaClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private static readonly Dictionary<string, string> ActionCriteria = new(StringComparer.Ordinal)
    {
        ["attack"] = "Protivník uteče hráči do zad a udeří ho. Způsobí 8 až 16 poškození.",
        ["special"] = "Protivník použije silový speciální útok, který má 30% šanci minout. Způsobí 18 až 27 poškození a trvá tři kola.",
        ["heal"] = "Protivník se léčí a obnoví 12 až 20 HP, nejvýš do 100."
    };

    private const string DangerTrueLabel = "Hráč je v ohrožení";
    private const string DangerFalseLabel = "Hráč je v bezpečí";

    public async Task<ModelsView> GetModelsAsync(bool refresh, CancellationToken cancellationToken)
    {
        await cache.Gate.WaitAsync(cancellationToken);
        try
        {
            if (!refresh && cache.Value is not null && clock.GetUtcNow() < cache.Expires) return cache.Value;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(options.Value.ModelsTimeoutSeconds));
            ModelsView result;
            try
            {
                using var response = await http.GetAsync("api/tags", timeout.Token);
                response.EnsureSuccessStatusCode();
                using var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(timeout.Token), cancellationToken: timeout.Token);
                var models = json.RootElement.GetProperty("models").EnumerateArray().Select(m =>
                {
                    var name = m.GetProperty("name").GetString();
                    if (string.IsNullOrWhiteSpace(name) || name.Length > 256) throw new JsonException("Invalid model name.");
                    var size = m.GetProperty("size").GetInt64();
                    if (size < 0) throw new JsonException("Invalid model size.");
                    return new ModelInfo(name, size, ReadCapabilities(m));
                }).DistinctBy(m => m.Name, StringComparer.Ordinal).OrderBy(m => m.Name, StringComparer.Ordinal).ToArray();
                result = new ModelsView(true, models);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                logger.LogWarning("Ollama model listing timed out.");
                result = new ModelsView(false, [], "Ollama neodpovedela vcas. Klasicka AI je dostupna.");
            }
            catch (Exception ex) when (ex is HttpRequestException or JsonException or InvalidOperationException or KeyNotFoundException or IOException or FormatException)
            {
                logger.LogWarning(ex, "Ollama model listing failed.");
                result = new ModelsView(false, [], "Ollama neni dostupna. Klasicka AI je dostupna.");
            }
            cancellationToken.ThrowIfCancellationRequested();
            cache.Value = result;
            cache.Expires = clock.GetUtcNow().AddSeconds(result.Available ? 30 : 3);
            return result;
        }
        finally { cache.Gate.Release(); }
    }

    public async Task<OpponentDecision> ChooseDecisionMoveAsync(string model, OpponentContext context, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(options.Value.DecisionTimeoutSeconds));
        var body = BuildDecisionRequest(model, context);
        try
        {
            using var response = await http.PostAsJsonAsync("v1/systemone", body, JsonOptions, timeout.Token);
            if (!response.IsSuccessStatusCode)
            {
                var status = response.StatusCode;
                var reason = status switch
                {
                    HttpStatusCode.BadRequest => "Model neumí decision režim; tah převzala klasická AI.",
                    HttpStatusCode.NotFound => "Model není nainstalovaný v Ollamě; tah převzala klasická AI.",
                    _ => $"Ollama odmítla požadavek (HTTP {(int)status}); tah převzala klasická AI."
                };
                logger.LogWarning("Ollama decision request failed for {Model} with status {Status}.", model, (int)status);
                return new OpponentDecision(null, FallbackReason: reason);
            }
            using var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(timeout.Token), cancellationToken: timeout.Token);
            var decision = ParseDecision(json.RootElement, context);
            cancellationToken.ThrowIfCancellationRequested();
            return decision;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning("Ollama decision timed out for {Model}.", model);
            return new OpponentDecision(null, FallbackReason: "Model neodpověděl včas; tah převzala klasická AI.");
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or InvalidOperationException or KeyNotFoundException or IOException or FormatException)
        {
            logger.LogWarning(ex, "Ollama decision failed for {Model}.", model);
            var reason = ex is HttpRequestException or IOException
                ? "Spojení s Ollamou selhalo; tah převzala klasická AI."
                : "Model vrátil neplatné rozhodnutí; tah převzala klasická AI.";
            return new OpponentDecision(null, FallbackReason: reason);
        }
    }

    private object BuildDecisionRequest(string model, OpponentContext context)
    {
        var allowed = context.AllowedActions.Where(ActionCriteria.ContainsKey).ToArray();
        var criteria = allowed.ToDictionary(a => a, a => ActionCriteria[a], StringComparer.Ordinal);
        return new
        {
            model,
            stream = false,
            keep_alive = options.Value.KeepAlive,
            state = new
            {
                round = context.Round,
                playerHp = context.PlayerHp,
                opponentHp = context.OpponentHp,
                playerCooldown = context.PlayerCooldown,
                opponentCooldown = context.OpponentCooldown,
                lastPlayerAction = context.LastPlayerAction,
                allowedActions = allowed,
                recentLog = context.RecentLog
            },
            questions = new
            {
                akce = new
                {
                    type = "choice",
                    instructions = "Vyber jednu akci, kterou protivník v tomto kole použije.",
                    criteria
                },
                ohrozeni = new
                {
                    type = "noul",
                    instructions = "Posuď, zda je hráč v ohrožení.",
                    criteria = new
                    {
                        @true = "Hráč je v ohrožení: jeho zdraví je nízké nebo má připravený speciální útok.",
                        @false = "Hráč je v bezpečí: jeho zdraví je plné a speciální útok není připravený."
                    }
                }
            }
        };
    }

    private static OpponentDecision ParseDecision(JsonElement root, OpponentContext context)
    {
        if (!root.TryGetProperty("answers", out var answers) || answers.ValueKind != JsonValueKind.Object)
            throw new JsonException("Missing answers.");
        if (!answers.TryGetProperty("akce", out var akce) || akce.ValueKind != JsonValueKind.Object)
            throw new JsonException("Missing answers.akce.");
        var action = akce.GetProperty("choice").GetString();
        if (action is null || !context.AllowedActions.Contains(action, StringComparer.Ordinal))
            throw new JsonException("Chosen action is not allowed.");

        var probabilities = new Dictionary<string, double>(StringComparer.Ordinal);
        if (akce.TryGetProperty("probabilities", out var probabilityElement) && probabilityElement.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in probabilityElement.EnumerateObject())
            {
                if (property.Value.ValueKind != JsonValueKind.Number) continue;
                var value = property.Value.GetDouble();
                if (double.IsNaN(value) || double.IsInfinity(value)) continue;
                probabilities[property.Name] = value;
            }
        }
        if (probabilities.Count == 0) throw new JsonException("Missing probabilities.");

        var confidence = 0d;
        if (akce.TryGetProperty("confidence", out var confidenceElement) && confidenceElement.ValueKind == JsonValueKind.Number)
        {
            var value = confidenceElement.GetDouble();
            if (!double.IsNaN(value) && !double.IsInfinity(value)) confidence = value;
        }

        double? danger = null;
        if (answers.TryGetProperty("ohrozeni", out var ohrozeni) && ohrozeni.ValueKind == JsonValueKind.Object &&
            ohrozeni.TryGetProperty("noul", out var noulElement) && noulElement.ValueKind == JsonValueKind.Number)
        {
            var value = noulElement.GetDouble();
            if (!double.IsNaN(value) && !double.IsInfinity(value)) danger = value;
        }

        var detail = new DecisionDetail(probabilities, confidence, danger, DangerTrueLabel, DangerFalseLabel);
        return new OpponentDecision(action, BuildTaunt(detail), Decision: detail);
    }

    private static string BuildTaunt(DecisionDetail detail)
    {
        var ranked = detail.Probabilities.OrderByDescending(p => p.Value).ToArray();
        var lead = ranked.Length == 0
            ? "Protivník váhá nad svým tahem."
            : ranked.Length == 1
                ? $"Protivník váhá: {ActionLabel(ranked[0].Key)} {Percent(ranked[0].Value)}."
                : $"Protivník váhá: {ActionLabel(ranked[0].Key)} {Percent(ranked[0].Value)}, {ActionLabel(ranked[1].Key)} {Percent(ranked[1].Value)}.";
        var mood = detail.Danger is null
            ? ""
            : detail.Danger >= 0.5
                ? " Cítí, že jste v ohrožení."
                : " Spíš si věří, že jste v bezpečí.";
        var certainty = detail.Confidence >= 0.6
            ? " Je v tom jednoznačný."
            : detail.Confidence < 0.25
                ? " Rozhodnutí je spíš hádanka."
                : "";
        return $"{lead}{mood}{certainty}";
    }

    private static string ActionLabel(string action) => action switch
    {
        "attack" => "útok",
        "special" => "speciální",
        "heal" => "léčení",
        _ => action
    };

    private static string Percent(double value) => $"{Math.Round(value * 100)} %";

    private static string[] ReadCapabilities(JsonElement model)
    {
        if (!model.TryGetProperty("capabilities", out var element) || element.ValueKind != JsonValueKind.Array) return [];
        return element.EnumerateArray()
            .Where(c => c.ValueKind == JsonValueKind.String)
            .Select(c => c.GetString())
            .Where(c => !string.IsNullOrWhiteSpace(c))
            .Select(c => c!.Trim().ToLowerInvariant())
            .Distinct(StringComparer.Ordinal)
            .OrderBy(c => c, StringComparer.Ordinal)
            .ToArray();
    }
}
