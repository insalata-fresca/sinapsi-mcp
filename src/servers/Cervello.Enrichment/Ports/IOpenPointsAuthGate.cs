namespace Cervello.Enrichment.Ports;

/// <summary>
/// The token gate on the open-points MCP tools (spec <c>open-points-mcp</c> → "The operator's only
/// enrichment UI is the MCP" / "Calls are scoped and logged"; SearchAuth lesson — M5's <c>/search</c>
/// is bearer-gated, so this private-plane surface MUST be too, never unauthenticated).
///
/// <para>The engine-side gate is a swappable seam: the tool entrypoints call
/// <see cref="Authorize"/> BEFORE any store I/O and throw <see cref="OpenPointsUnauthorizedException"/>
/// (→ HTTP 401 at the connector edge) when the bearer is missing/wrong. The live connector adapter
/// (Bridge.Mcp, the deploy slice) validates the real bearer + cervello project-binding; the
/// in-engine <see cref="Cervello.Enrichment.Adapters.TokenOpenPointsAuthGate"/> proves the gate
/// against a configured token.</para>
/// </summary>
public interface IOpenPointsAuthGate
{
    /// <summary>
    /// Authorize a tool call. Returns the caller's scope context on success; throws
    /// <see cref="OpenPointsUnauthorizedException"/> when the presented bearer is missing or invalid.
    /// </summary>
    OpenPointsCaller Authorize(string? presentedToken);
}

/// <summary>The authorized caller context (cervello scope) returned by the gate.</summary>
public sealed record OpenPointsCaller(string Scope)
{
    /// <summary>The only scope the open-points tools operate within (DESIGN §2.3 project-binding).</summary>
    public const string CervelloScope = "cervello";
}

/// <summary>
/// Thrown when an open-points tool is called without a valid bearer. Maps to HTTP 401 at the
/// connector edge (never 500 / never a silent success) — mirrors the Bridge <c>missing_bearer</c>
/// posture.
/// </summary>
public sealed class OpenPointsUnauthorizedException(string reason = "missing_or_invalid_bearer")
    : Exception($"open-points tool call unauthorized: {reason}")
{
    public string Reason { get; } = reason;
}
