using System.Text.Json;
using System.Text.Json.Nodes;

namespace Metabase.Mcp.Tools;

/// <summary>JSON-string parsing helpers shared by the Metabase mutating tools. Tools accept
/// free-form JSON bodies/patches as strings (so any field shape is reachable without a fixed
/// schema); these turn an optional string into a parsed value or null.</summary>
internal static class MetabaseJson
{
    /// <summary>Parse a JSON string into a detached <see cref="JsonElement"/>, or null if blank.</summary>
    public static JsonElement? ParseElement(string? s)
        => string.IsNullOrWhiteSpace(s) ? null : JsonDocument.Parse(s).RootElement.Clone();

    /// <summary>Parse a JSON string into a mutable <see cref="JsonNode"/>, or null if blank.</summary>
    public static JsonNode? ParseNode(string? s)
        => string.IsNullOrWhiteSpace(s) ? null : JsonNode.Parse(s);
}
