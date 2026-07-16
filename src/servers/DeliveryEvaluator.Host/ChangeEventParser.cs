using System.Text.Json;
using System.Text.Json.Nodes;
using Sinapsi.DeliveryEvaluator;

namespace DeliveryEvaluator.Host;

/// <summary>
/// Turns a merge/deploy change event (a CloudEvent v1.0 envelope observed on
/// <c>homelab.git.&gt;</c> / <c>homelab.release.&gt;</c> / <c>homelab.deploy.&gt;</c>) into the
/// <see cref="ChangeSet"/> the deterministic classifier grades.
///
/// <para><b>Tolerant by design, fail-safe by contract.</b> The fleet's git/deploy events are
/// heterogeneous and not all schema-fixed, so this parser reads whatever effect-bearing fields are
/// present (a file list; a released Quadlet unit; a released <c>config.env</c> default) and maps
/// them to <see cref="FileChange"/>s. When it can extract NO effect surface, it returns a
/// <see cref="ChangeSet"/> whose <see cref="ChangeSet.IsUnparseable"/> is true — which the
/// classifier fail-safe escalates to <c>requiresApproval</c> and dead-letters, never a silent
/// <c>allow</c> (<c>docs/65</c> principle 3). The untrusted title/body/labels are attached only as
/// <see cref="UntrustedChangeMetadata"/> (logged, never scored — <c>docs/65</c> principle 2).</para>
/// </summary>
public static class ChangeEventParser
{
    /// <summary>Parse the raw event bytes. Never throws: malformed JSON → an unparseable
    /// <see cref="ChangeSet"/> (fail-safe escalate + dead-letter), never a crash and never a
    /// silent allow.</summary>
    public static ChangeSet Parse(ReadOnlyMemory<byte> raw)
    {
        JsonNode? root;
        try { root = JsonNode.Parse(raw.Span); }
        catch (JsonException) { return Unparseable(); }
        if (root is not JsonObject env) return Unparseable();

        // CloudEvents envelope: the effect payload is under `data`; some producers publish the
        // effect object directly (no envelope). Accept both.
        var data = env["data"] as JsonObject ?? env;

        var files = ExtractFiles(data);
        var metadata = ExtractMetadata(data);
        var correlationId = FirstString(data, "correlation_id", "correlationId")
                            ?? (env["id"]?.GetValue<string>() ?? "");

        // No effect surface at all → let ChangeSet.IsUnparseable drive the fail-safe path.
        return ChangeSet.Of(files, metadata, correlationId);
    }

    private static ChangeSet Unparseable() =>
        ChangeSet.Of(Array.Empty<FileChange>(), UntrustedChangeMetadata.None);

    private static IReadOnlyList<FileChange> ExtractFiles(JsonObject data)
    {
        var files = new List<FileChange>();

        // (a) An explicit changed-file list (git-shaped events): array of strings OR of
        //     objects { path|filename, status, additions|added, deletions|removed }.
        var fileArray = (data["files"] ?? data["changed_files"] ?? data["changedFiles"]) as JsonArray;
        if (fileArray is not null)
            foreach (var el in fileArray)
                if (ToFileChange(el) is { } fc)
                    files.Add(fc);

        // (b) A released Quadlet unit (homelab.release.* carries `quadlet`): the deployed unit's
        //     content is the effect surface — classify it as a modified infra/config file.
        if (FirstString(data, "quadlet") is { Length: > 0 } quadlet)
            files.Add(new FileChange(
                PathFromServiceName(data, "systemd", ".container") ?? "systemd/service.container",
                ChangeKind.Modified, SplitLines(quadlet), Array.Empty<string>()));

        // (c) A released config default (homelab.release.* carries `config_default`): env content
        //     is where credentials / nats / auth flips would show up → a config-tier surface.
        if (FirstString(data, "config_default", "configDefault") is { Length: > 0 } cfg)
            files.Add(new FileChange(
                PathFromServiceName(data, "config", ".env") ?? "config.env",
                ChangeKind.Modified, SplitLines(cfg), Array.Empty<string>()));

        return files;
    }

    private static FileChange? ToFileChange(JsonNode? el)
    {
        switch (el)
        {
            case JsonValue v when v.TryGetValue<string>(out var p) && !string.IsNullOrWhiteSpace(p):
                return new FileChange(p, ChangeKind.Modified, Array.Empty<string>(), Array.Empty<string>());
            case JsonObject o:
            {
                var path = FirstString(o, "path", "filename", "file", "name");
                if (string.IsNullOrWhiteSpace(path)) return null;
                var kind = (FirstString(o, "status", "kind", "change") ?? "").ToLowerInvariant() switch
                {
                    "added" or "add" or "a" or "create" or "created" => ChangeKind.Added,
                    "removed" or "deleted" or "delete" or "d" => ChangeKind.Deleted,
                    "renamed" or "rename" or "r" => ChangeKind.Renamed,
                    _ => ChangeKind.Modified,
                };
                var added = LinesFrom(o, "added", "added_lines", "additions", "patch_added");
                var removed = LinesFrom(o, "removed", "removed_lines", "deletions", "patch_removed");
                return new FileChange(path!, kind, added, removed);
            }
            default:
                return null;
        }
    }

    private static UntrustedChangeMetadata ExtractMetadata(JsonObject data)
    {
        var title = FirstString(data, "title", "pr_title", "prTitle", "message", "summary") ?? "";
        var body = FirstString(data, "body", "pr_body", "prBody", "description") ?? "";
        var labels = (data["labels"] as JsonArray)?
            .Select(n => (n as JsonValue)?.TryGetValue<string>(out var s) == true ? s : null)
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Select(s => s!)
            .ToList();
        if (string.IsNullOrEmpty(title) && string.IsNullOrEmpty(body) && (labels is null || labels.Count == 0))
            return UntrustedChangeMetadata.None;
        return new UntrustedChangeMetadata(title, body, labels);
    }

    // Build a plausible surface path from a service name so path-tier signals (e.g. `systemd`,
    // `config.env`, `nats`) can fire. Best-effort; content scanning does the real work.
    private static string? PathFromServiceName(JsonObject data, string dir, string ext)
    {
        var svc = FirstString(data, "service", "svc", "image", "name");
        if (string.IsNullOrWhiteSpace(svc)) return null;
        // Strip any registry/namespace from an image ref → bare service name.
        var slash = svc!.LastIndexOf('/');
        if (slash >= 0) svc = svc[(slash + 1)..];
        var colon = svc.IndexOf(':');
        if (colon >= 0) svc = svc[..colon];
        return dir == "config" ? $"services/{svc}/config.env" : $"services/{svc}/{dir}/{svc}{ext}";
    }

    private static IReadOnlyList<string> LinesFrom(JsonObject o, params string[] keys)
    {
        foreach (var k in keys)
        {
            switch (o[k])
            {
                case JsonArray arr:
                    return arr.Select(n => (n as JsonValue)?.TryGetValue<string>(out var s) == true ? s : null)
                        .Where(s => s is not null).Select(s => s!).ToList();
                case JsonValue v when v.TryGetValue<string>(out var text) && text.Length > 0:
                    return SplitLines(text);
            }
        }
        return Array.Empty<string>();
    }

    private static string[] SplitLines(string text) =>
        text.Replace("\r\n", "\n").Split('\n', StringSplitOptions.RemoveEmptyEntries);

    private static string? FirstString(JsonObject o, params string[] keys)
    {
        foreach (var k in keys)
            if (o[k] is JsonValue v && v.TryGetValue<string>(out var s) && !string.IsNullOrEmpty(s))
                return s;
        return null;
    }
}
