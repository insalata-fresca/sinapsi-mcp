using System.ComponentModel;
using Metabase.Mcp.Api;
using ModelContextProtocol.Server;

namespace Metabase.Mcp.Tools;

/// <summary>The generic Metabase escape hatch (<c>request</c>) + cross-entity <c>search</c>.
/// The host injects the <see cref="MetabaseClient"/>.</summary>
[McpServerToolType]
public sealed class ApiTools
{
    [McpServerTool(Name = "request", Destructive = true)]
    [Description("Escape hatch: call ANY Metabase API endpoint. method = GET|POST|PUT|DELETE; "
        + "path like '/api/dashboard/2'; body_json is an optional JSON string. Use this for "
        + "anything the typed tools don't cover (alerts, pulses, permissions, settings, etc.). "
        + "Can MUTATE depending on method — marked destructive.")]
    public static Task<object> Request(
        MetabaseClient metabase,
        [Description("HTTP method: GET, POST, PUT, or DELETE.")] string method,
        [Description("API path, e.g. /api/dashboard/2.")] string path,
        [Description("Optional request body as a JSON string.")] string? bodyJson = null,
        CancellationToken ct = default)
    {
        if (MetabaseValidation.ValidateMethod(method) is { } e1) return Task.FromResult(MetabaseToolGuard.Rejected(e1));
        if (MetabaseValidation.ValidatePath(path) is { } e2) return Task.FromResult(MetabaseToolGuard.Rejected(e2));
        if (MetabaseValidation.ValidateOptionalJson("body_json", bodyJson) is { } e3) return Task.FromResult(MetabaseToolGuard.Rejected(e3));
        return MetabaseToolGuard.RunAsync(async () => await metabase.SendRawAsync(method, path, MetabaseJson.ParseElement(bodyJson), ct));
    }

    [McpServerTool(Name = "search", ReadOnly = true)]
    [Description("Search across Metabase entities (cards, dashboards, tables, collections…).")]
    public static Task<object> Search(
        MetabaseClient metabase,
        [Description("Search text.")] string q,
        [Description("Optional comma-list of models to filter: card,dashboard,table,collection,database.")] string? models = null,
        CancellationToken ct = default)
    {
        if (MetabaseValidation.ValidateQuery(q) is { } e1) return Task.FromResult(MetabaseToolGuard.Rejected(e1));
        if (MetabaseValidation.ValidateModels(models) is { } e2) return Task.FromResult(MetabaseToolGuard.Rejected(e2));
        var qs = $"/api/search?q={Uri.EscapeDataString(q)}";
        if (!string.IsNullOrWhiteSpace(models))
            foreach (var m in models.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                qs += $"&models={Uri.EscapeDataString(m)}";
        return MetabaseToolGuard.RunAsync(async () => await metabase.GetAsync(qs, ct));
    }
}
