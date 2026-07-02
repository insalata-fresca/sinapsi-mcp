using System.Text.Json;
using System.Text.Json.Serialization;

namespace Zitadel.Mcp.Auth;

/// <summary>
/// A ZITADEL service-account JSON key — the admin-console "Generate JSON key" output.
/// Shape: <c>{type, keyId, key (PEM RSA private), userId}</c>. The PEM <c>key</c> is the
/// secret material; it MUST NOT appear in any log line or exception message surfaced to a
/// caller.
/// </summary>
public sealed record ServiceAccount(string UserId, string KeyId, string PrivateKeyPem)
{
    private sealed record SaJson(
        [property: JsonPropertyName("type")]   string? Type,
        [property: JsonPropertyName("keyId")]  string? KeyId,
        [property: JsonPropertyName("key")]    string? Key,
        [property: JsonPropertyName("userId")] string? UserId);

    /// <summary>
    /// Load the service-account key from disk. Throws with a clear, instance-neutral hint when the
    /// file is missing or malformed — but NEVER includes the file content (the SA private key) in
    /// any error message.
    /// </summary>
    public static ServiceAccount LoadFromFile(string path)
    {
        if (!File.Exists(path))
            throw new InvalidOperationException(
                $"ZITADEL service-account key not found at {path}. " +
                "Create a machine user with the required manager role, generate a JSON key, and " +
                "place it at this path (mode 0600).");

        SaJson? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<SaJson>(File.ReadAllText(path));
        }
        catch (JsonException)
        {
            // Do NOT echo the file content — it contains the PEM private key.
            throw new InvalidOperationException(
                $"ZITADEL SA key at {path} is not valid JSON. Expected {{type, keyId, key, userId}}.");
        }

        if (parsed is null
            || string.IsNullOrWhiteSpace(parsed.UserId)
            || string.IsNullOrWhiteSpace(parsed.KeyId)
            || string.IsNullOrWhiteSpace(parsed.Key))
        {
            throw new InvalidOperationException(
                $"ZITADEL SA key at {path} is missing required fields. " +
                "Expected {type, keyId, key, userId} all non-empty.");
        }

        return new ServiceAccount(parsed.UserId!, parsed.KeyId!, parsed.Key!);
    }
}
