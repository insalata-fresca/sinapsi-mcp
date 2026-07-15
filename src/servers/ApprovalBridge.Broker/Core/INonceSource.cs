using System.Security.Cryptography;

namespace ApprovalBridge.Broker.Core;

/// <summary>Mints the one-shot nonce held server-side for a pending request (docs/66 §5.1).</summary>
internal interface INonceSource
{
    /// <summary>A fresh, unguessable nonce.</summary>
    string Generate();
}

/// <summary>Cryptographically-random 256-bit nonce, base64url-encoded.</summary>
internal sealed class CryptoNonceSource : INonceSource
{
    public string Generate()
    {
        Span<byte> buf = stackalloc byte[32];
        RandomNumberGenerator.Fill(buf);
        return Convert.ToBase64String(buf).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }
}
