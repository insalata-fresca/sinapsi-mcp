using System.Text.Json.Nodes;

namespace Sinapsi.SentinelConsole;

/// <summary>
/// One normalized authorization decision, parsed from a bus CloudEvent, across all
/// three layers of the authorization plane (home-server <c>docs/61</c>): Q1
/// gateway/OpenFGA, Q2 in-MCP command authorizer, Q3 harness gates. This is the row the
/// read-model stores and the Console renders.
/// </summary>
public sealed record AuthzDecision(
    string Layer,          // q1 | q2 | q3
    string Tool,           // <backend>.<tool>, e.g. sshgw.execute-command
    string Server,         // target server (q2/q3), or ""
    string Verb,           // first command token (q2), or ""
    string Verdict,        // allow | requiresApproval | deny
    string Reason,
    string Command,        // secret-scrubbed, truncated (q2/q3), or ""
    string CorrelationId,
    DateTimeOffset Time)
{
    public const string VerdictAllow = "allow";
    public const string VerdictApproval = "requiresApproval";
    public const string VerdictDeny = "deny";

    /// <summary>
    /// Parse a decision from a bus CloudEvent (its raw JSON) delivered on
    /// <paramref name="subject"/>. Routes by subject family:
    ///   homelab.security.authz.q2.*   → the canonical Q2 envelope (docs/61 §8);
    ///   homelab.security.ask-gate.*   → Q3 harness ASK (→ requiresApproval);
    ///   homelab.security.deny-floor.* / tier4.* / credential-guard.* → Q3 harness DENY.
    /// Returns null for an unrecognized / unparseable event (dropped, never thrown).
    /// </summary>
    public static AuthzDecision? TryParse(string subject, string json, DateTimeOffset now)
    {
        JsonNode? root;
        try { root = JsonNode.Parse(json); }
        catch { return null; }
        if (root is null) return null;

        var data = root["data"] as JsonObject;
        var time = ParseTime(root["time"]?.GetValue<string>(), now);

        // Any authz.<layer> subject carries the canonical envelope: an explicit verdict +
        // its own layer (q1 gateway/OpenFGA, q2 in-MCP command authorizer, …).
        if (subject.Contains(".authz.") && data is not null)
        {
            var verdict = Str(data["verdict"]);
            var layer = Str(data["layer"]);
            if (layer.Length == 0) layer = LayerFromSubject(subject);   // fallback: authz.<layer>
            if (verdict.Length == 0 || layer.Length == 0) return null;
            return new AuthzDecision(
                Layer: layer,
                Tool: Str(data["tool"]),
                Server: Str(data["server"]),
                Verb: Str(data["verb"]),
                Verdict: verdict,
                Reason: Str(data["reason"]),
                Command: Str(data["command"]),
                CorrelationId: Str(data["correlation_id"]),
                Time: time);
        }

        // Q3 — the harness security-audit events (compatible-but-not-yet-aligned shape).
        if (data is not null && IsHarnessSecurity(subject, out var q3Verdict))
        {
            return new AuthzDecision(
                Layer: "q3",
                Tool: Str(data["tool"]),
                Server: "",
                Verb: FirstToken(Str(data["command"])),
                Verdict: q3Verdict,
                Reason: Str(data["matched"], Str(data["mode"])),
                Command: Str(data["command"]),
                CorrelationId: Str(data["correlation_id"]),
                Time: time);
        }

        return null;
    }

    // "homelab.security.authz.q1.deny.cse" → "q1" (the token after ".authz.").
    private static string LayerFromSubject(string subject)
    {
        const string marker = ".authz.";
        int i = subject.IndexOf(marker, StringComparison.Ordinal);
        if (i < 0) return "";
        int start = i + marker.Length;
        int end = subject.IndexOf('.', start);
        return end < 0 ? subject[start..] : subject[start..end];
    }

    private static bool IsHarnessSecurity(string subject, out string verdict)
    {
        if (subject.Contains(".ask-gate.")) { verdict = VerdictApproval; return true; }
        if (subject.Contains(".deny-floor.") || subject.Contains(".tier4.") ||
            subject.Contains(".credential-guard.") || subject.Contains(".scope_gate."))
        { verdict = VerdictDeny; return true; }
        verdict = ""; return false;
    }

    private static string Str(JsonNode? n, string dflt = "")
        => n is null ? dflt : (n.GetValue<object?>() is { } ? n.ToString() : dflt);

    private static DateTimeOffset ParseTime(string? s, DateTimeOffset now)
        => DateTimeOffset.TryParse(s, out var t) ? t : now;

    private static string FirstToken(string cmd)
    {
        var t = cmd.TrimStart();
        int i = t.IndexOfAny(new[] { ' ', '\t' });
        return i < 0 ? t : t[..i];
    }
}
