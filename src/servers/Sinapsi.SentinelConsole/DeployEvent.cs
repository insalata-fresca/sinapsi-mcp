using System.Text.Json.Nodes;

namespace Sinapsi.SentinelConsole;

/// <summary>
/// One normalized deploy-visibility event, parsed from a bus CloudEvent, spanning the two
/// halves of the S34 image-first delivery pipeline (home-server <c>docs/23</c> §1.4 + §2,
/// <c>patterns/deploy-service.md</c>): the release plane (<c>homelab.release.&lt;svc&gt;.published</c>,
/// emitted by the <c>build-push-release</c> CI action once an image is pushed) and the
/// per-host deploy-controller's reconcile outcome (<c>homelab.deploy.&lt;ctid&gt;.&lt;svc&gt;.applied</c>
/// / <c>.failed</c>). Together these answer "did my merge actually deploy?" without an
/// SSH-and-grep round trip. This is the row the <see cref="DeployModel"/> stores and the
/// Console renders.
/// </summary>
public sealed record DeployEvent(
    string Kind,           // released | applied | failed
    string Svc,
    string Ctid,           // target container id (deploy events), or "" for a release event
    string Version,
    string Digest,         // sha256:... image digest, or "" when the payload omits it
    string Result,         // deploy-controller's raw result string ("ok" / "error: ...", or "" for a release)
    DateTimeOffset Time)
{
    public const string KindReleased = "released";
    public const string KindApplied = "applied";
    public const string KindFailed = "failed";

    /// <summary>
    /// Parse a deploy event from a bus CloudEvent (its raw JSON) delivered on
    /// <paramref name="subject"/>. Routes by subject family:
    ///   homelab.release.&lt;svc&gt;.published        → kind=released (svc from the subject —
    ///                                                  the release payload carries no svc field);
    ///   homelab.deploy.&lt;ctid&gt;.&lt;svc&gt;.applied  → kind=applied  (ctid/svc read from the
    ///   homelab.deploy.&lt;ctid&gt;.&lt;svc&gt;.failed   → kind=failed   payload, falling back to the
    ///                                                  subject when the payload omits them).
    /// Returns null for an unrecognized / unparseable event (dropped, never thrown).
    /// </summary>
    public static DeployEvent? TryParse(string subject, string json, DateTimeOffset now)
    {
        JsonNode? root;
        try { root = JsonNode.Parse(json); }
        catch { return null; }
        if (root is null) return null;

        var data = root["data"] as JsonObject;
        if (data is null) return null;
        var time = ParseTime(root["time"]?.GetValue<string>(), now);

        if (subject.StartsWith("homelab.release.", StringComparison.Ordinal) &&
            subject.EndsWith(".published", StringComparison.Ordinal))
        {
            var svc = SvcFromReleaseSubject(subject);
            if (svc.Length == 0) return null;
            return new DeployEvent(
                Kind: KindReleased,
                Svc: svc,
                Ctid: "",
                Version: Str(data["version"]),
                Digest: Str(data["digest"]),
                Result: "",
                Time: time);
        }

        if (subject.StartsWith("homelab.deploy.", StringComparison.Ordinal) &&
            (subject.EndsWith(".applied", StringComparison.Ordinal) || subject.EndsWith(".failed", StringComparison.Ordinal)))
        {
            var kind = subject.EndsWith(".applied", StringComparison.Ordinal) ? KindApplied : KindFailed;
            var svc = Str(data["svc"]);
            var ctid = Str(data["ctid"]);
            if (svc.Length == 0 || ctid.Length == 0)
            {
                var (subjCtid, subjSvc) = SplitDeploySubject(subject);
                if (svc.Length == 0) svc = subjSvc;
                if (ctid.Length == 0) ctid = subjCtid;
            }
            if (svc.Length == 0) return null;
            return new DeployEvent(
                Kind: kind,
                Svc: svc,
                Ctid: ctid,
                Version: Str(data["version"]),
                Digest: Str(data["digest"]),
                Result: Str(data["result"]),
                Time: time);
        }

        return null;
    }

    // "homelab.release.sinapsi-sentinel-console.published" -> "sinapsi-sentinel-console".
    private static string SvcFromReleaseSubject(string subject)
    {
        const string prefix = "homelab.release.";
        const string suffix = ".published";
        if (subject.Length <= prefix.Length + suffix.Length) return "";
        return subject[prefix.Length..^suffix.Length];
    }

    // "homelab.deploy.132.sinapsi-sentinel-console.applied" -> ("132", "sinapsi-sentinel-console").
    private static (string Ctid, string Svc) SplitDeploySubject(string subject)
    {
        const string prefix = "homelab.deploy.";
        if (!subject.StartsWith(prefix, StringComparison.Ordinal)) return ("", "");
        var rest = subject[prefix.Length..];
        int lastDot = rest.LastIndexOf('.');   // strip the trailing ".applied" / ".failed"
        if (lastDot < 0) return ("", "");
        rest = rest[..lastDot];
        int firstDot = rest.IndexOf('.');
        if (firstDot < 0) return ("", "");
        return (rest[..firstDot], rest[(firstDot + 1)..]);
    }

    private static string Str(JsonNode? n, string dflt = "")
        => n is null ? dflt : (n.GetValue<object?>() is { } ? n.ToString() : dflt);

    private static DateTimeOffset ParseTime(string? s, DateTimeOffset now)
        => DateTimeOffset.TryParse(s, out var t) ? t : now;
}
