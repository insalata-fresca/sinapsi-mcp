using System.Text.Json;
using System.Text.Json.Serialization;

namespace Sshgw.Mcp;

/// <summary>
/// One server entry from the registry JSON file. Field names follow the common
/// SSH-gateway schema (name/host/port/username/privateKey/pty/whitelist) plus an
/// additive optional <see cref="ReadFilePolicy"/> block.
/// </summary>
public sealed class ServerEntry
{
    [JsonPropertyName("name")] public string Name { get; init; } = "";
    [JsonPropertyName("host")] public string Host { get; init; } = "";
    [JsonPropertyName("port")] public int Port { get; init; } = 22;
    [JsonPropertyName("username")] public string Username { get; init; } = "root";
    [JsonPropertyName("privateKey")] public string? PrivateKey { get; init; }
    [JsonPropertyName("pty")] public bool Pty { get; init; }

    /// <summary>Command allowlist string: patterns joined by '|', each compiled as
    /// its OWN self-anchored regex (see <see cref="CommandWhitelist"/>). Empty/null
    /// = allow-all (give a read-only server an explicit whitelist instead).</summary>
    [JsonPropertyName("whitelist")] public string? Whitelist { get; init; }

    /// <summary>Optional per-server read_file path policy. When absent, the global
    /// secret denylist applies.</summary>
    [JsonPropertyName("readFilePolicy")] public ReadFilePolicyConfig? ReadFilePolicy { get; init; }

    /// <summary>Optional pinned host-key fingerprint(s), OpenSSH SHA-256 form
    /// (<c>SHA256:&lt;base64&gt;</c> or raw hex; see <see cref="HostKeyPolicy"/>).
    /// Accepts either a single string or an array in the JSON. When present, the
    /// server's presented host key MUST match one of them or the connection is
    /// refused (MITM guard). Absent ⇒ trust-on-first-use unless the global
    /// <c>SSHGW_REQUIRE_HOST_KEY_PIN</c> posture forbids it.</summary>
    [JsonPropertyName("hostKeyFingerprint")]
    [JsonConverter(typeof(StringOrStringArrayConverter))]
    public string[]? HostKeyFingerprints { get; init; }
}

/// <summary>Deserialises a JSON field that may be either a single string or an array
/// of strings into a <c>string[]</c>. Lets <c>hostKeyFingerprint</c> be written as
/// the common single-value form or as a list (e.g. during a key rotation).</summary>
public sealed class StringOrStringArrayConverter : JsonConverter<string[]?>
{
    public override string[]? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.Null:
                return null;
            case JsonTokenType.String:
                var single = reader.GetString();
                return single is null ? null : new[] { single };
            case JsonTokenType.StartArray:
                var list = new List<string>();
                while (reader.Read())
                {
                    if (reader.TokenType == JsonTokenType.EndArray) break;
                    if (reader.TokenType == JsonTokenType.String)
                    {
                        var v = reader.GetString();
                        if (v is not null) list.Add(v);
                    }
                    else
                    {
                        throw new JsonException("hostKeyFingerprint array entries must be strings");
                    }
                }
                return list.ToArray();
            default:
                throw new JsonException("hostKeyFingerprint must be a string or an array of strings");
        }
    }

    public override void Write(Utf8JsonWriter writer, string[]? value, JsonSerializerOptions options)
    {
        if (value is null) { writer.WriteNullValue(); return; }
        writer.WriteStartArray();
        foreach (var s in value) writer.WriteStringValue(s);
        writer.WriteEndArray();
    }
}

/// <summary>Path policy for read_file: an optional allowlist (deny-by-default when
/// present) plus extra deny globs layered on the global secret denylist.</summary>
public sealed class ReadFilePolicyConfig
{
    [JsonPropertyName("allow")] public string[]? Allow { get; init; }
    [JsonPropertyName("deny")] public string[]? Deny { get; init; }
}

/// <summary>Loads + indexes the server registry once at startup.</summary>
public sealed class ServerRegistry
{
    private readonly Dictionary<string, ServerEntry> _byName;

    public ServerRegistry(SshgwOptions opts)
    {
        var json = File.ReadAllText(opts.ServerRegistryPath);
        var entries = JsonSerializer.Deserialize<List<ServerEntry>>(json)
                      ?? throw new InvalidOperationException(
                          $"{opts.ServerRegistryPath} did not parse as a server array");
        _byName = entries
            .Where(e => !string.IsNullOrWhiteSpace(e.Name))
            .ToDictionary(e => e.Name, StringComparer.Ordinal);
    }

    public IReadOnlyCollection<ServerEntry> All => _byName.Values;

    public ServerEntry? Get(string name) =>
        _byName.TryGetValue(name, out var e) ? e : null;

    /// <summary>Parse a registry from a JSON string. Exposed for unit tests and for
    /// callers that already hold the registry document in memory.</summary>
    public static IReadOnlyDictionary<string, ServerEntry> ParseJson(string json)
    {
        var entries = JsonSerializer.Deserialize<List<ServerEntry>>(json)
                      ?? throw new InvalidOperationException("registry did not parse as a server array");
        return entries
            .Where(e => !string.IsNullOrWhiteSpace(e.Name))
            .ToDictionary(e => e.Name, StringComparer.Ordinal);
    }
}
