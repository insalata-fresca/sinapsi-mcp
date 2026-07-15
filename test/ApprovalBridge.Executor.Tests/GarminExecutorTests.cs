using System.Text.Json.Nodes;
using ApprovalBridge.Executor.Garmin;
using ApprovalBridge.Executor.Sdk;
using Xunit;

namespace ApprovalBridge.Executor.Tests;

/// <summary>
/// The <c>garmin.oauth.exchange</c> demo handler in isolation (home-server <c>docs/66 §6</c>): it reads the
/// client secret target-side, exchanges against the MOCK endpoint (no live Garmin, no network), stores the
/// token server-side, and returns only <c>{status, stored, expires_at}</c> — never the secret or token.
/// </summary>
public sealed class GarminExecutorTests
{
    private static ExecutorRequest Request(string paramsJson) => new("garmin.oauth.exchange", paramsJson, "garmin-connector");

    [Fact]
    public async Task HappyPath_ReturnsNonSecretConfirmation_AndStoresTokenServerSide()
    {
        var token = new GarminToken(Sentinels.AccessToken, Sentinels.RefreshToken, DateTimeOffset.Parse("2026-09-01T12:00:00Z"));
        var endpoint = new MockGarminEndpoint(token);
        var store = new MockGarminTokenStore();
        var handler = new GarminOAuthExchangeExecutor(endpoint, store);

        var result = await handler.ExecuteAsync(Request(Fixtures.ValidParams), new RecordingSecretSource(Sentinels.ClientSecret));

        Assert.True(result.IsOk);
        var obj = JsonNode.Parse(result.ResultJson)!.AsObject();
        Assert.Equal("ok", obj["status"]!.GetValue<string>());
        Assert.True(obj["stored"]!.GetValue<bool>());
        Assert.Equal("2026-09-01T12:00:00Z", obj["expires_at"]!.GetValue<string>());
        // The token/secret are nowhere in the returned result.
        Assert.DoesNotContain(Sentinels.AccessToken, result.ResultJson);
        Assert.DoesNotContain(Sentinels.ClientSecret, result.ResultJson);
        // The token was stored server-side.
        Assert.Single(store.Stored);
    }

    [Fact]
    public async Task MissingAuthCode_Throws_ExecutorException()
    {
        var handler = new GarminOAuthExchangeExecutor(
            new MockGarminEndpoint(new GarminToken("a", "b", DateTimeOffset.UtcNow)), new MockGarminTokenStore());
        await Assert.ThrowsAsync<ExecutorException>(() =>
            handler.ExecuteAsync(Request("""{ }"""), new RecordingSecretSource(Sentinels.ClientSecret)));
    }

    [Fact]
    public async Task EmptySecret_Throws_ExecutorException_WithoutLeaking()
    {
        var handler = new GarminOAuthExchangeExecutor(
            new MockGarminEndpoint(new GarminToken("a", "b", DateTimeOffset.UtcNow)), new MockGarminTokenStore());
        var ex = await Assert.ThrowsAsync<ExecutorException>(() =>
            handler.ExecuteAsync(Request(Fixtures.ValidParams), new RecordingSecretSource("")));
        Assert.Contains("client secret unavailable", ex.Message);
    }

    [Fact]
    public async Task ExecutorName_BindsToTheAllowlistExecutorField()
    {
        var handler = new GarminOAuthExchangeExecutor(
            new MockGarminEndpoint(new GarminToken("a", "b", DateTimeOffset.UtcNow)), new MockGarminTokenStore());
        Assert.Equal("garmin-oauth-exchange", handler.ExecutorName);
        await Task.CompletedTask;
    }
}
