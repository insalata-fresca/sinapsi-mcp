using System.Security.Cryptography;

namespace Sshgw.Mcp;

/// <summary>
/// Per-server host-key pinning bound. SSH.NET's <c>HostKeyReceived</c> event fires
/// with the presented host key; by default the library trusts ANY key, which on a
/// free read tool is a silent man-in-the-middle risk (a spoofed host could harvest
/// the identity key's reach or feed forged output back to the caller). This policy
/// turns a per-server configured fingerprint into a trust decision.
///
/// <para>
/// The pin is expressed as the OpenSSH-standard SHA-256 host-key fingerprint. Two
/// input spellings are accepted and normalised to the same canonical form:
///   - <c>SHA256:&lt;base64-no-padding&gt;</c> — exactly what
///     <c>ssh-keyscan -t &lt;alg&gt; host | ssh-keygen -lf -</c> prints, and what
///     SSH.NET exposes as <c>HostKeyEventArgs.FingerPrintSHA256</c>; the
///     <c>SHA256:</c> prefix is optional.
///   - a raw hex SHA-256 (64 hex chars, ':' separators allowed) of the same key
///     blob, for operators who paste the hex digest.
/// Both reduce to the 32 raw digest bytes and are compared in constant time.
/// </para>
///
/// <para>
/// FAIL-CLOSED default: if a server carries a <c>hostKeyFingerprint</c> (or the
/// global default requires pinning), an unknown / non-matching key is REJECTED
/// (the connection is refused before any command runs). A server with no configured
/// fingerprint and no global requirement is trust-on-first-use as before — the pin
/// is opt-in per server, but once configured it is enforced.
/// </para>
/// </summary>
public sealed class HostKeyPolicy
{
    /// <summary>The canonical 32-byte SHA-256 digests this server will trust. Empty
    /// ⇒ no pin configured.</summary>
    private readonly byte[][] _pinnedDigests;

    /// <summary>When true, a server WITHOUT any configured pin must still be rejected
    /// (global "require pinning" posture). When false, an unpinned server is
    /// trust-on-first-use.</summary>
    public bool RequirePin { get; }

    /// <summary>True when this server carries at least one configured fingerprint.</summary>
    public bool HasPin => _pinnedDigests.Length > 0;

    /// <param name="fingerprints">Configured fingerprints for the server (any of the
    /// accepted spellings). Null/empty entries are ignored. A malformed entry throws
    /// at construction — a config typo must fail loudly, never silently disable the
    /// pin.</param>
    /// <param name="requirePin">Global posture: reject even an unpinned server.</param>
    public HostKeyPolicy(IEnumerable<string>? fingerprints, bool requirePin = false)
    {
        RequirePin = requirePin;
        _pinnedDigests = (fingerprints ?? Array.Empty<string>())
            .Where(f => !string.IsNullOrWhiteSpace(f))
            .Select(ParseFingerprintOrThrow)
            .ToArray();
    }

    /// <summary>
    /// Decide whether a presented host key (its raw SSH wire-format blob) is
    /// trusted. Returns true iff its SHA-256 digest matches a configured pin. If no
    /// pin is configured, returns <c>!RequirePin</c> (trust-on-first-use unless the
    /// global posture forbids it).
    /// </summary>
    public bool IsTrusted(ReadOnlySpan<byte> hostKeyBlob)
    {
        if (_pinnedDigests.Length == 0)
            return !RequirePin;

        Span<byte> digest = stackalloc byte[32];
        SHA256.HashData(hostKeyBlob, digest);
        foreach (var pin in _pinnedDigests)
            if (CryptographicOperations.FixedTimeEquals(digest, pin))
                return true;
        return false;
    }

    /// <summary>The OpenSSH-style SHA-256 fingerprint of a host-key blob, e.g.
    /// <c>SHA256:abc…</c> — used to name the offending key in a rejection reason so an
    /// operator can compare it against the expected pin.</summary>
    public static string FingerprintOf(ReadOnlySpan<byte> hostKeyBlob)
    {
        Span<byte> digest = stackalloc byte[32];
        SHA256.HashData(hostKeyBlob, digest);
        return "SHA256:" + Convert.ToBase64String(digest).TrimEnd('=');
    }

    /// <summary>Parse one configured fingerprint spelling into its 32 raw digest
    /// bytes, or throw a clear error naming the bad value.</summary>
    private static byte[] ParseFingerprintOrThrow(string raw)
    {
        var s = raw.Trim();

        // SHA256:<base64-no-padding> (prefix optional).
        if (s.StartsWith("SHA256:", StringComparison.OrdinalIgnoreCase))
            s = s["SHA256:".Length..];

        // Hex form: 64 hex chars, optionally ':'-separated. Detect by content after
        // stripping any ':' separators — if it is all hex and 64 long, treat as hex.
        var hexCandidate = s.Replace(":", "");
        if (hexCandidate.Length == 64 && IsHex(hexCandidate))
            return Convert.FromHexString(hexCandidate);

        // Otherwise treat as base64 (add back stripped padding).
        var b64 = s;
        switch (b64.Length % 4)
        {
            case 2: b64 += "=="; break;
            case 3: b64 += "="; break;
            case 1:
                throw new InvalidOperationException(
                    $"host-key fingerprint '{raw}' is not a valid SHA256 base64 or hex digest.");
        }

        try
        {
            var bytes = Convert.FromBase64String(b64);
            if (bytes.Length != 32)
                throw new InvalidOperationException(
                    $"host-key fingerprint '{raw}' decodes to {bytes.Length} bytes; a SHA-256 digest is 32.");
            return bytes;
        }
        catch (FormatException)
        {
            throw new InvalidOperationException(
                $"host-key fingerprint '{raw}' is not a valid SHA256 base64 or hex digest.");
        }
    }

    private static bool IsHex(string s)
    {
        foreach (var c in s)
            if (!Uri.IsHexDigit(c)) return false;
        return true;
    }
}
