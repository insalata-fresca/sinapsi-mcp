namespace Cervello.Enrichment.Domain;

/// <summary>
/// The confirmation-basis id every applied attribution MUST carry (lint R9; SCHEMAS §6;
/// DESIGN §5.1/§10.2). An attribution reaching <c>map/</c> is a *mechanized or human*
/// confirmation, never a bare voice match:
/// <list type="bullet">
/// <item><c>auto://&lt;rule&gt;@&lt;version&gt;</c> — a whitelisted auto-basis rule admitted it
///   under the grounded-autonomy decision policy (§5.1).</item>
/// <item><c>human://&lt;answer-id&gt;</c> — the operator answered the open-point via the MCP
///   (§6.1).</item>
/// </list>
/// A bare voice match with no basis id MUST NOT appear in <c>map/</c> (lint R9). This type is
/// the parsed, validated form of that id — a value object so an invalid basis cannot be
/// constructed.
/// </summary>
public sealed record ConfirmationBasis
{
    /// <summary>The auto-basis rule name for a voice-match auto-apply (SCHEMAS §6 / DESIGN §10.2).</summary>
    public const string VoiceMatchRule = "voice-match";

    /// <summary>
    /// The auto-basis rule name for a PARTICIPANT-HINT auto-apply (M6) — a metadata (filename/
    /// participant) assignment vetted by the decision policy, distinct from a voice match so an audit
    /// can tell a hint from a voiceprint (<c>auto://participant-hint@&lt;version&gt;</c>).
    /// </summary>
    public const string ParticipantHintRule = "participant-hint";

    private ConfirmationBasis(ConfirmationBasisKind kind, string id, string? rule, string? version)
    {
        Kind = kind;
        Id = id;
        Rule = rule;
        Version = version;
    }

    public ConfirmationBasisKind Kind { get; }

    /// <summary>The full basis id string, e.g. <c>auto://voice-match@v1</c> or <c>human://op_abc</c>.</summary>
    public string Id { get; }

    /// <summary>For an <see cref="ConfirmationBasisKind.Auto"/> basis, the rule name (else null).</summary>
    public string? Rule { get; }

    /// <summary>For an <see cref="ConfirmationBasisKind.Auto"/> basis, the rule version (else null).</summary>
    public string? Version { get; }

    /// <summary>
    /// A whitelisted auto-basis id <c>auto://&lt;rule&gt;@&lt;version&gt;</c>. Only whitelisted
    /// rules may form an auto basis (DESIGN §5.1: "a whitelisted auto-basis rule"); the default
    /// rule set is <see cref="VoiceMatchRule"/>.
    /// </summary>
    public static ConfirmationBasis Auto(string version, string rule = VoiceMatchRule)
    {
        if (string.IsNullOrWhiteSpace(rule))
            throw new ArgumentException("auto-basis rule must be non-empty", nameof(rule));
        if (string.IsNullOrWhiteSpace(version))
            throw new ArgumentException("auto-basis version must be non-empty", nameof(version));
        if (rule.Contains('@') || rule.Contains("://", StringComparison.Ordinal))
            throw new ArgumentException($"auto-basis rule '{rule}' must be a bare rule name", nameof(rule));
        return new ConfirmationBasis(ConfirmationBasisKind.Auto, $"auto://{rule}@{version}", rule, version);
    }

    /// <summary>A human basis id <c>human://&lt;answer-id&gt;</c> for an operator-answered open-point.</summary>
    public static ConfirmationBasis Human(string answerId)
    {
        if (string.IsNullOrWhiteSpace(answerId))
            throw new ArgumentException("human-basis answer id must be non-empty", nameof(answerId));
        if (answerId.Contains("://", StringComparison.Ordinal))
            throw new ArgumentException($"human-basis answer id '{answerId}' must be a bare id", nameof(answerId));
        return new ConfirmationBasis(ConfirmationBasisKind.Human, $"human://{answerId}", null, null);
    }

    /// <summary>
    /// Parse a basis id string, enforcing the lint R9 grammar. Returns <c>false</c> for a bare
    /// voice match / any unrecognised or malformed scheme (which lint R9 rejects).
    /// </summary>
    public static bool TryParse(string? id, out ConfirmationBasis? basis)
    {
        basis = null;
        if (string.IsNullOrWhiteSpace(id)) return false;

        if (id.StartsWith("human://", StringComparison.Ordinal))
        {
            var answer = id["human://".Length..];
            if (string.IsNullOrWhiteSpace(answer)) return false;
            basis = Human(answer);
            return true;
        }

        if (id.StartsWith("auto://", StringComparison.Ordinal))
        {
            var rest = id["auto://".Length..];
            var at = rest.IndexOf('@');
            if (at <= 0 || at == rest.Length - 1) return false;
            var rule = rest[..at];
            var version = rest[(at + 1)..];
            if (string.IsNullOrWhiteSpace(rule) || string.IsNullOrWhiteSpace(version)) return false;
            basis = Auto(version, rule);
            return true;
        }

        return false; // any other scheme (or a bare "X said") is not a valid basis (lint R9)
    }

    public override string ToString() => Id;
}

/// <summary>The two confirmation-basis kinds lint R9 accepts.</summary>
public enum ConfirmationBasisKind
{
    /// <summary><c>auto://&lt;rule&gt;@&lt;version&gt;</c> — mechanized confirmation under the policy.</summary>
    Auto,

    /// <summary><c>human://&lt;answer-id&gt;</c> — operator answered the open-point.</summary>
    Human,
}
