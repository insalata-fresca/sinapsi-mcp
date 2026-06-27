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
