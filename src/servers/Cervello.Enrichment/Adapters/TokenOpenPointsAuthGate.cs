using System.Security.Cryptography;
using System.Text;
using Cervello.Enrichment.Ports;

namespace Cervello.Enrichment.Adapters;

/// <summary>
/// A static-bearer <see cref="IOpenPointsAuthGate"/> (the E5 engine proof of the token gate; the
/// live connector adapter uses the Bridge JWT/OIDC path). Holds the expected token, compares in
/// constant time, and refuses any missing/blank/mismatched bearer with a 401-mapping exception.
///
/// <para>An UNCONFIGURED gate (empty expected token) fails CLOSED — every call is unauthorized —
/// so a mis-provisioned deploy can never expose the private-plane tools open (SearchAuth lesson,
/// stricter than M5's "not_configured" read-only fallback).</para>
/// </summary>
public sealed class TokenOpenPointsAuthGate : IOpenPointsAuthGate
{
    private readonly byte[]? _expected;

    public TokenOpenPointsAuthGate(string? expectedToken)
    {
        _expected = string.IsNullOrEmpty(expectedToken) ? null : Encoding.UTF8.GetBytes(expectedToken);
    }

    public OpenPointsCaller Authorize(string? presentedToken)
    {
        if (_expected is null)
            throw new OpenPointsUnauthorizedException("gate_not_configured");          // fail closed
        if (string.IsNullOrEmpty(presentedToken))
            throw new OpenPointsUnauthorizedException("missing_bearer");
        var presented = Encoding.UTF8.GetBytes(presentedToken);
        if (!CryptographicOperations.FixedTimeEquals(presented, _expected))
            throw new OpenPointsUnauthorizedException("invalid_bearer");
        return new OpenPointsCaller(OpenPointsCaller.CervelloScope);
    }
}
