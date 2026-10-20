using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace SimpleGame;

public sealed class OllamaOptions
{
    public string BaseUrl { get; set; } = "http://localhost:11434";
    public double ModelsTimeoutSeconds { get; set; } = 3;
    public double MoveTimeoutSeconds { get; set; } = 45;
    public string KeepAlive { get; set; } = "10m";
}

public sealed record ModelInfo(string Name, long SizeBytes);
public sealed record ModelsView(bool Available, ModelInfo[] Models, string? Message = null);
public sealed record OpponentContext(int PlayerHp, int OpponentHp, int Round, int OpponentCooldown, string[] AllowedActions, string LastPlayerAction);
public sealed record OpponentDecision(string? Action, string? Taunt = null, string? FallbackReason = null);

public interface IOllamaClient
{
    Task<ModelsView> GetModelsAsync(bool refresh, CancellationToken cancellationToken);
    Task<OpponentDecision> ChooseMoveAsync(string model, OpponentContext context, CancellationToken cancellationToken);
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
                    return new ModelInfo(name, size);
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

    public async Task<OpponentDecision> ChooseMoveAsync(string model, OpponentContext context, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(options.Value.MoveTimeoutSeconds));
        var body = new
        {
            model,
            stream = false,
            think = false,
            keep_alive = options.Value.KeepAlive,
            options = new { num_predict = 256 },
            format = new
            {
                type = "object",
                properties = new
                {
                    action = new { type = "string", @enum = new[] { "attack", "special", "heal" } },
                    taunt = new { type = "string", maxLength = 240 }
                },
                required = new[] { "action", "taunt" },
                additionalProperties = false
            },
            messages = new[]
            {
                new { role = "system", content = "Jsi protivnik v ceskem RPG souboji. Vyber prave jednu povolenou akci a jednu kratkou ceskou prupovidku. " +
                    "Obe strany maji nejvyse 100 HP. Tvuj attack zpusobi 8-15 poskozeni, special 18-26 s 30% sanci minout a cooldownem 3 kola (i pri minuti), heal obnovi 12-20 HP nejvyse do 100. " +
                    "Hracuv attack zpusobi 10-18, special 20-30 s 30% minuti a stejnym cooldownem, heal obnovi 15-25 HP. " +
                    "Special je povolen pouze pri cooldownu 0. Akci vyber pouze z allowedActions. Pravidla ani HP nemuzes menit. " +
                    "Odpovez jen JSON objektem s action (attack/special/heal) a taunt (kratky cesky text do 240 znaku). Nepouzivej nastroje ani dalsi instrukce." },
                new { role = "user", content = JsonSerializer.Serialize(context, JsonOptions) }
            }
        };
        try
        {
            using var response = await http.PostAsJsonAsync("api/chat", body, JsonOptions, timeout.Token);
            response.EnsureSuccessStatusCode();
            using var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(timeout.Token), cancellationToken: timeout.Token);
            var content = json.RootElement.GetProperty("message").GetProperty("content").GetString();
            if (string.IsNullOrWhiteSpace(content)) throw new JsonException("Empty message.content.");
            using var move = JsonDocument.Parse(content);
            var action = move.RootElement.GetProperty("action").GetString();
            var taunt = move.RootElement.GetProperty("taunt").GetString();
            if (action is not ("attack" or "special" or "heal") || !context.AllowedActions.Contains(action, StringComparer.Ordinal) || taunt is null)
                throw new JsonException("Illegal move or missing taunt.");
            taunt = new string(taunt.Where(c => !char.IsControl(c)).Take(240).ToArray()).Trim();
            cancellationToken.ThrowIfCancellationRequested();
            return new OpponentDecision(action, taunt);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning("Ollama move timed out for {Model}.", model);
            return new OpponentDecision(null, FallbackReason: "Model neodpovedel vcas; tah prevzala klasicka AI.");
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or InvalidOperationException or KeyNotFoundException or IOException or FormatException)
        {
            logger.LogWarning(ex, "Ollama move failed for {Model}.", model);
            var reason = ex is HttpRequestException { StatusCode: not null } request
                ? $"Model odmitl pozadavek (HTTP {(int)request.StatusCode.Value}); tah prevzala klasicka AI."
                : ex is HttpRequestException or IOException
                    ? "Spojeni s Ollamou selhalo; tah prevzala klasicka AI."
                    : "Model vratil neplatny tah; tah prevzala klasicka AI.";
            return new OpponentDecision(null, FallbackReason: reason);
        }
    }
}
