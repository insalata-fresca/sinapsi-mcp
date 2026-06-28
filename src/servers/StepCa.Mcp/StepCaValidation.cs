using System.Globalization;
using System.Numerics;

namespace StepCa.Mcp;

/// <summary>
/// Input validation for the mutating step-ca tools. Every tool parameter that
/// flows into a <c>step</c> subprocess is checked here BEFORE the process is
/// spawned, so malformed input is rejected with a clear, structured error
/// instead of being handed to the CA (which would either fail opaquely or, for
/// pathological values, waste a subprocess).
///
/// <para>
/// Note on shell safety: arguments are passed via
/// <see cref="System.Diagnostics.ProcessStartInfo.ArgumentList"/> (no shell
/// interpolation), so this validation is a correctness + fail-fast guard, not
/// the primary injection defence. We still reject leading-dash values defensively
/// so a hostile value can never be mistaken for a <c>step</c> CLI flag.
/// </para>
/// </summary>
internal static class StepCaValidation
{
    /// <summary>Upper bound on a CN / SAN string (RFC 5280 caps a DNS label at
    /// 63 octets and a name at 255; 253 is the practical FQDN ceiling, but a CN
    /// is free-form text so we allow a generous 255).</summary>
    internal const int MaxNameLength = 255;

    /// <summary>Maximum number of SAN entries accepted on a single issuance.</summary>
    internal const int MaxSans = 100;

    /// <summary>Upper bound on a serial-number string. A 20-octet serial is the
    /// RFC 5280 ceiling → at most 40 hex chars / ~49 decimal digits; 128 is a
    /// safe, generous cap that still refuses an obviously-bogus blob.</summary>
    internal const int MaxSerialLength = 128;

    /// <summary>
    /// Validate the common name for <c>issue_certificate</c>. Returns
    /// <c>null</c> when valid, otherwise a human-readable reason.
    /// </summary>
    internal static string? ValidateCommonName(string? commonName)
    {
        if (string.IsNullOrWhiteSpace(commonName))
            return "common_name is required";
        if (commonName.Length > MaxNameLength)
            return $"common_name too long ({commonName.Length} chars; max {MaxNameLength})";
        if (ContainsControlOrNewline(commonName))
            return "common_name contains control characters";
        if (commonName[0] == '-')
            return "common_name must not start with '-'";
        return null;
    }

    /// <summary>
    /// Validate the optional SAN list for <c>issue_certificate</c>. Returns
    /// <c>null</c> when valid (including when the list is empty/null), otherwise
    /// a human-readable reason.
    /// </summary>
    internal static string? ValidateSans(string[]? sans)
    {
        if (sans is null || sans.Length == 0)
            return null;
        if (sans.Length > MaxSans)
            return $"too many SANs ({sans.Length}; max {MaxSans})";
        for (var i = 0; i < sans.Length; i++)
        {
            var s = sans[i];
            if (string.IsNullOrWhiteSpace(s))
                return $"SAN #{i + 1} is empty";
            if (s.Length > MaxNameLength)
                return $"SAN #{i + 1} too long ({s.Length} chars; max {MaxNameLength})";
            if (ContainsControlOrNewline(s))
                return $"SAN #{i + 1} contains control characters";
            if (s[0] == '-')
                return $"SAN #{i + 1} must not start with '-'";
        }
        return null;
    }

    /// <summary>
    /// Validate the serial number for <c>revoke_certificate</c>. A step-ca serial
    /// is a non-negative integer rendered either in decimal or as <c>0x…</c> hex.
    /// Returns <c>null</c> when valid, otherwise a human-readable reason.
    /// </summary>
    internal static string? ValidateSerialNumber(string? serial)
    {
        if (string.IsNullOrWhiteSpace(serial))
            return "serial_number is required";
        var s = serial.Trim();
        if (s.Length > MaxSerialLength)
            return $"serial_number too long ({s.Length} chars; max {MaxSerialLength})";

        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            var hex = s[2..];
            if (hex.Length == 0 || !hex.All(Uri.IsHexDigit))
                return "serial_number is not a valid hex integer";
            return null;
        }

        // Decimal: must parse as a non-negative integer of arbitrary size.
        if (!BigInteger.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out var value)
            || value.Sign < 0)
            return "serial_number must be a non-negative integer (decimal or 0x-hex)";
        return null;
    }

    /// <summary>
    /// Validate the CRL reason code for <c>revoke_certificate</c>. RFC 5280 §5.3.1
    /// defines reason codes 0–10 (7 is unused/reserved but still in range). We
    /// accept the full 0–10 range and reject anything outside it.
    /// </summary>
    internal static string? ValidateReasonCode(int reasonCode)
    {
        if (reasonCode is < 0 or > 10)
            return $"reason_code {reasonCode} out of range (RFC 5280 defines 0–10)";
        return null;
    }

    private static bool ContainsControlOrNewline(string s)
    {
        foreach (var c in s)
            if (char.IsControl(c)) return true;
        return false;
    }
}
