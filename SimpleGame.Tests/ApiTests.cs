using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace SimpleGame.Tests;

public sealed class ApiTests
{
    private static WebApplicationFactory<Program> Factory(int maxSessions = 1000) => new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<IOllamaClient>();
            services.AddSingleton<IOllamaClient>(new OfflineOllama());
            services.PostConfigure<SessionOptions>(options => options.MaxSessions = maxSessions);
        }));

    [Fact]
    public async Task HealthAndModelListRemainAvailableWithoutOllama()
    {
        await using var factory = Factory();
        using var client = factory.CreateClient();
        var health = await client.GetFromJsonAsync<JsonElement>("/api/health");
        Assert.Equal("ok", health.GetProperty("status").GetString());
        using var response = await client.GetAsync("/api/llm/models");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var models = await response.Content.ReadFromJsonAsync<ModelsView>();
        Assert.False(models!.Available);
        Assert.Empty(models.Models);
        var start = await client.PostAsJsonAsync("/api/games/rpg/start", new { model = "unavailable:model" });
        Assert.Equal(HttpStatusCode.ServiceUnavailable, start.StatusCode);
        Assert.Equal("ollama_unavailable", (await start.Content.ReadFromJsonAsync<ApiError>())!.Code);
    }

    [Theory]
    [InlineData("{broken")]
    [InlineData("{\"min\":\"not-a-number\"}")]
    [InlineData("null")]
    public async Task MalformedRequestsUseStableErrorContract(string json)
    {
        await using var factory = Factory();
        using var client = factory.CreateClient();
        using var response = await client.PostAsync("/api/games/number/start", new StringContent(json, Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var error = await response.Content.ReadFromJsonAsync<ApiError>();
        Assert.Equal("invalid_input", error!.Code);
        Assert.False(string.IsNullOrWhiteSpace(error.Message));
    }

    [Fact]
    public async Task StartViewsDoNotExposeSecretsAndMissingGameIs404()
    {
        await using var factory = Factory();
        using var client = factory.CreateClient();
        foreach (var game in new[] { "number", "hangman", "word" })
        {
            using var response = await client.PostAsJsonAsync($"/api/games/{game}/start", new { });
            response.EnsureSuccessStatusCode();
            var view = await response.Content.ReadFromJsonAsync<JsonElement>();
            Assert.True(Guid.TryParse(view.GetProperty("id").GetString(), out _));
            Assert.False(view.TryGetProperty("answer", out _));
            foreach (var field in new[] { "secret", "word" })
                if (view.TryGetProperty(field, out var value)) Assert.Equal(JsonValueKind.Null, value.ValueKind);
        }
        using var missing = await client.PostAsJsonAsync($"/api/games/number/{Guid.NewGuid()}/guess", new { value = 1 });
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        Assert.Equal("session_not_found", (await missing.Content.ReadFromJsonAsync<ApiError>())!.Code);
    }

    [Fact]
    public async Task SessionCapacityReturns429()
    {
        await using var factory = Factory(1);
        using var client = factory.CreateClient();
        (await client.PostAsJsonAsync("/api/games/number/start", new { })).EnsureSuccessStatusCode();
        using var response = await client.PostAsJsonAsync("/api/games/word/start", new { });
        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
        Assert.Equal("session_limit", (await response.Content.ReadFromJsonAsync<ApiError>())!.Code);
    }

    [Fact]
    public async Task WrongGameTypeAndUnknownRoutesReturnStable404()
    {
        await using var factory = Factory();
        using var client = factory.CreateClient();
        var started = await client.PostAsJsonAsync("/api/games/rpg/start", new { model = (string?)null });
        var battle = await started.Content.ReadFromJsonAsync<Games.RpgView>();
        using var wrongType = await client.PostAsJsonAsync($"/api/games/number/{battle!.Id}/guess", new { value = 50 });
        Assert.Equal(HttpStatusCode.NotFound, wrongType.StatusCode);
        Assert.Equal("session_not_found", (await wrongType.Content.ReadFromJsonAsync<ApiError>())!.Code);
        using var unknown = await client.GetAsync("/api/does-not-exist");
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        Assert.Equal("not_found", (await unknown.Content.ReadFromJsonAsync<ApiError>())!.Code);
    }

    [Fact]
    public async Task NumberRejectsOverflowRangeAndOutOfRangeGuessWithoutAttempt()
    {
        await using var factory = Factory();
        using var client = factory.CreateClient();
        using var invalid = await client.PostAsJsonAsync("/api/games/number/start", new { min = int.MinValue, max = int.MaxValue });
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        var started = await client.PostAsJsonAsync("/api/games/number/start", new { min = int.MaxValue - 1, max = int.MaxValue });
        started.EnsureSuccessStatusCode();
        var game = await started.Content.ReadFromJsonAsync<JsonElement>();
        var route = $"/api/games/number/{game.GetProperty("id").GetString()}/guess";
        using var badGuess = await client.PostAsJsonAsync(route, new { value = int.MinValue });
        Assert.Equal(HttpStatusCode.BadRequest, badGuess.StatusCode);
        var valid = await client.PostAsJsonAsync(route, new { value = int.MaxValue });
        valid.EnsureSuccessStatusCode();
        var view = await valid.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(1, view.GetProperty("attempts").GetInt32());
    }

    [Fact]
    public async Task RpgGetRecoversConfirmedTurnAndStaleRevisionConflicts()
    {
        await using var factory = Factory();
        using var client = factory.CreateClient();
        var start = await client.PostAsJsonAsync("/api/games/rpg/start", new { model = (string?)null });
        var view = await start.Content.ReadFromJsonAsync<Games.RpgView>();
        Assert.Equal(0, view!.Revision);
        Assert.Equal("classic", view.OpponentMode);
        var route = $"/api/games/rpg/{view.Id}";
        using var missingRevision = await client.PostAsJsonAsync(route + "/action", new { action = "heal" });
        Assert.Equal(HttpStatusCode.BadRequest, missingRevision.StatusCode);
        var move = await client.PostAsJsonAsync(route + "/action", new { action = "heal", expectedRevision = 0 });
        move.EnsureSuccessStatusCode();
        var confirmed = await client.GetFromJsonAsync<Games.RpgView>(route);
        Assert.Equal(1, confirmed!.Revision);
        Assert.False(confirmed.InProgress);
        Assert.Equal("classic", confirmed.LastOpponentMove!.Source);
        using var duplicate = await client.PostAsJsonAsync(route + "/action", new { action = "heal", expectedRevision = 0 });
        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
        Assert.Equal("conflict", (await duplicate.Content.ReadFromJsonAsync<ApiError>())!.Code);
    }

    private sealed class OfflineOllama : IOllamaClient
    {
        public Task<ModelsView> GetModelsAsync(bool refresh, CancellationToken cancellationToken) =>
            Task.FromResult(new ModelsView(false, [], "Offline pro test."));
        public Task<OpponentDecision> ChooseDecisionMoveAsync(string model, OpponentContext context, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("A classic game must not call Ollama.");
    }
}
