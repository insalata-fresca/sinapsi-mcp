using System.Net;
using System.Net.Sockets;

namespace Gdrive.Mcp;

// ---------------------------------------------------------------------------
// Input validation for the Google Drive tool surface. Every tool parameter that
// reaches the Drive REST API (a URL path segment such as a file id, a raw `q`
// query string, or file metadata that becomes a request body) is checked here
// BEFORE any HTTP call is issued, so malformed input is rejected with a clear,
// structured error instead of being handed to Google (which would either 4xx
// opaquely or waste a round-trip).
//
// Note on injection safety: file ids reach Drive as URL PATH SEGMENTS via the
// generated client (which URL-encodes them), and `q` search strings are the
// documented Drive query language — this validation is a correctness + fail-fast
// guard, not the primary injection defence. We still reject control characters /
// newlines defensively (they can corrupt a header or split a request line) and a
// leading '-' on an id (a value that could be mistaken for a flag if it ever
// flowed to a CLI, and is never a legitimate Drive id).
//
// Every Validate* method returns null when the value is acceptable, otherwise a
// short human-readable reason. None of them throws.
// ---------------------------------------------------------------------------

/// <summary>
/// Static, throw-free input validation for the Drive tools. Mirrors the
/// StepCa.Mcp <c>StepCaValidation</c> exemplar: one <c>Validate*</c> method per
/// tool parameter, each returning <c>null</c> when valid or a human-readable
/// reason otherwise.
/// </summary>
internal static class GdriveValidation
{
    /// <summary>Upper bound on a Drive file/folder id. Real Drive ids are ~33
    /// chars (44 for some shared-drive ids); 512 is a generous cap that still
    /// refuses an obviously-bogus blob before it reaches a URL path segment.</summary>
    internal const int MaxIdLength = 512;

    /// <summary>Upper bound on a raw Drive <c>q</c> query string. The Drive API
    /// itself limits query complexity; 4 KiB is far past any legitimate query and
    /// refuses an unbounded blob.</summary>
    internal const int MaxQueryLength = 4_096;

    /// <summary>Upper bound on a file name. Drive's own limit is 32 767 chars for
    /// the metadata field; we cap well under that at a still-generous ceiling.</summary>
    internal const int MaxNameLength = 1_024;

    /// <summary>Upper bound on a MIME type string. RFC 6838 media types are short;
    /// 255 is generous and refuses a pathological value.</summary>
    internal const int MaxMimeTypeLength = 255;

    /// <summary>Upper bound on inline UTF-8 text content accepted by
    /// <c>create_file</c> / <c>update_file</c>. These tools are for text bodies;
    /// large binaries have their own paths. 10 MiB is a safe ceiling that keeps a
    /// single request off the large-object heap unexpectedly.</summary>
    internal const int MaxContentBytes = 10 * 1024 * 1024;

    /// <summary>Upper bound on the base64 TEXT accepted by <c>upload_file</c>.
    /// Base64 inflates bytes ~1.33x, so this ceiling (the encoded string length,
    /// not the decoded byte count) still bounds a request to a modest ~30 MiB of
    /// decoded binary — comfortably past a ~30s voice clip while refusing an
    /// unbounded blob before a single `Convert.FromBase64String` call runs.</summary>
    internal const int MaxBase64ContentLength = 40 * 1024 * 1024;

    /// <summary>Upper bound on a source URL for <c>upload_from_url</c>. RFC-practical
    /// URLs are short; 2 KiB refuses a pathological blob before any fetch.</summary>
    internal const int MaxUrlLength = 2_048;

    /// <summary>
    /// Validate the REQUIRED source URL for <c>upload_from_url</c>: the address the
    /// MCP host will fetch server-side. Enforces required/non-empty, a length cap,
    /// no control chars, an absolute <c>http</c>/<c>https</c> URL, and a minimal
    /// SSRF guard — a literal <b>link-local / cloud-metadata</b> host
    /// (169.254.0.0/16 incl. 169.254.169.254, or IPv6 fe80::/10) and the
    /// unspecified address are refused. Private/LAN and loopback targets are
    /// deliberately ALLOWED: the whole point is to fetch from homelab hosts
    /// (10.x/192.168.x) and localhost services. DNS names that resolve to a blocked
    /// range are not caught here — the agentgateway PEP authz is the primary
    /// control (only trusted identities may call this tool). Returns <c>null</c>
    /// when valid.
    /// </summary>
    internal static string? ValidateFetchUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return "url is required";
        if (url.Length > MaxUrlLength)
            return $"url too long ({url.Length} chars; max {MaxUrlLength})";
        if (ContainsControlOrNewline(url))
            return "url contains control characters";
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return "url is not a valid absolute URL";
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            return "url scheme must be http or https";

        if (IPAddress.TryParse(uri.Host, out var ip))
        {
            if (ip.Equals(IPAddress.Any) || ip.Equals(IPAddress.IPv6Any))
                return "url host must not be the unspecified address";
            var b = ip.GetAddressBytes();
            if (ip.AddressFamily == AddressFamily.InterNetwork && b[0] == 169 && b[1] == 254)
                return "url host must not be a link-local / metadata address";
            if (ip.AddressFamily == AddressFamily.InterNetworkV6 && b[0] == 0xfe && (b[1] & 0xc0) == 0x80)
                return "url host must not be a link-local address";
        }
        return null;
    }

    /// <summary>
    /// Validate REQUIRED base64-encoded binary content for <c>upload_file</c>.
    /// Checks required/non-empty, a length ceiling on the encoded TEXT (cheap,
    /// before decoding), and well-formedness via a real
    /// <see cref="Convert.FromBase64String"/> attempt (rejects padding errors,
    /// invalid characters, wrong length) so a malformed blob never reaches Drive.
    /// Returns <c>null</c> when valid.
    /// </summary>
    internal static string? ValidateBase64Content(string? contentBase64)
    {
        if (string.IsNullOrWhiteSpace(contentBase64))
            return "contentBase64 is required";
        if (contentBase64.Length > MaxBase64ContentLength)
            return $"contentBase64 too long ({contentBase64.Length} chars; max {MaxBase64ContentLength})";
        try
        {
            Convert.FromBase64String(contentBase64);
        }
        catch (FormatException)
        {
            return "contentBase64 is not well-formed base64";
        }
        return null;
    }

    /// <summary>
    /// Validate a REQUIRED parent folder id — used by <c>move_file</c>'s
    /// <c>destFolderId</c>/<c>removeFolderId</c>, where (unlike the optional
    /// <see cref="ValidateFolderId"/> used elsewhere) at least one parent must be
    /// supplied for the move to mean anything. <paramref name="field"/> names the
    /// parameter in the returned reason. Returns <c>null</c> when valid.
    /// </summary>
    internal static string? ValidateRequiredParentId(string? folderId, string field)
    {
        if (string.IsNullOrWhiteSpace(folderId))
            return $"{field} is required";
        if (folderId.Length > MaxIdLength)
            return $"{field} too long ({folderId.Length} chars; max {MaxIdLength})";
        if (ContainsControlOrNewline(folderId))
            return $"{field} contains control characters";
        if (folderId[0] == '-')
            return $"{field} must not start with '-'";
        return null;
    }

    /// <summary>
    /// Validate a required Drive file id (the <c>fileId</c> parameter shared by
    /// most tools). Returns <c>null</c> when valid, otherwise a reason.
    /// </summary>
    internal static string? ValidateFileId(string? fileId)
    {
        if (string.IsNullOrWhiteSpace(fileId))
            return "fileId is required";
        if (fileId.Length > MaxIdLength)
            return $"fileId too long ({fileId.Length} chars; max {MaxIdLength})";
        if (ContainsControlOrNewline(fileId))
            return "fileId contains control characters";
        if (fileId[0] == '-')
            return "fileId must not start with '-'";
        return null;
    }

    /// <summary>
    /// Validate the optional parent folder id for <c>list_files</c> /
    /// <c>create_file</c>. Null/empty is allowed (means "no folder scope");
    /// otherwise the same rules as a file id apply. Returns <c>null</c> when valid.
    /// </summary>
    internal static string? ValidateFolderId(string? folderId)
    {
        if (string.IsNullOrEmpty(folderId))
            return null; // optional — omitted means "all files" / "no parent"
        if (string.IsNullOrWhiteSpace(folderId))
            return "folderId must not be whitespace";
        if (folderId.Length > MaxIdLength)
            return $"folderId too long ({folderId.Length} chars; max {MaxIdLength})";
        if (ContainsControlOrNewline(folderId))
            return "folderId contains control characters";
        if (folderId[0] == '-')
            return "folderId must not start with '-'";
        return null;
    }

    /// <summary>
    /// Validate the raw Drive <c>q</c> query for <c>search_files</c>. The value is
    /// the documented Drive query language, so we only guard against an
    /// empty/oversize/control-char blob. Returns <c>null</c> when valid.
    /// </summary>
    internal static string? ValidateQuery(string? query)
    {
        if (string.IsNullOrWhiteSpace(query))
            return "query is required";
        if (query.Length > MaxQueryLength)
            return $"query too long ({query.Length} chars; max {MaxQueryLength})";
        if (ContainsControlOrNewline(query))
            return "query contains control characters";
        return null;
    }

    /// <summary>
    /// Validate a required file name for <c>create_file</c>. Returns <c>null</c>
    /// when valid, otherwise a reason.
    /// </summary>
    internal static string? ValidateName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return "name is required";
        if (name.Length > MaxNameLength)
            return $"name too long ({name.Length} chars; max {MaxNameLength})";
        if (ContainsControlOrNewline(name))
            return "name contains control characters";
        return null;
    }

    /// <summary>
    /// Validate the OPTIONAL new name for <c>update_file</c>. Null means "leave
    /// the name unchanged" and is allowed; a supplied value follows the name
    /// rules. Returns <c>null</c> when valid.
    /// </summary>
    internal static string? ValidateOptionalNewName(string? newName)
    {
        if (newName is null)
            return null; // omitted — keep current name
        return ValidateName(newName);
    }

    /// <summary>
    /// Validate a MIME type string. Returns <c>null</c> when valid, otherwise a
    /// reason. Empty is rejected because callers always supply a concrete default.
    /// </summary>
    internal static string? ValidateMimeType(string? mimeType)
    {
        if (string.IsNullOrWhiteSpace(mimeType))
            return "mimeType is required";
        if (mimeType.Length > MaxMimeTypeLength)
            return $"mimeType too long ({mimeType.Length} chars; max {MaxMimeTypeLength})";
        if (ContainsControlOrNewline(mimeType))
            return "mimeType contains control characters";
        return null;
    }

    /// <summary>
    /// Validate REQUIRED inline text content for <c>create_file</c>. An empty
    /// string is a legitimate empty file, so only the size ceiling is enforced.
    /// Returns <c>null</c> when valid.
    /// </summary>
    internal static string? ValidateContent(string? content)
    {
        if (content is null)
            return "content is required";
        return ContentSizeGuard(content, "content");
    }

    /// <summary>
    /// Validate OPTIONAL replacement content for <c>update_file</c>. Null means
    /// "leave the body unchanged"; a supplied value (including empty) is size-
    /// capped. Returns <c>null</c> when valid.
    /// </summary>
    internal static string? ValidateOptionalNewContent(string? newContent)
    {
        if (newContent is null)
            return null; // omitted — keep current content
        return ContentSizeGuard(newContent, "newContent");
    }

    private static string? ContentSizeGuard(string content, string field)
    {
        // Count UTF-8 bytes without materialising the encoded array.
        var bytes = System.Text.Encoding.UTF8.GetByteCount(content);
        if (bytes > MaxContentBytes)
            return $"{field} too large ({bytes} bytes; max {MaxContentBytes})";
        return null;
    }

    private static bool ContainsControlOrNewline(string s)
    {
        foreach (var c in s)
            if (char.IsControl(c)) return true;
        return false;
    }
}
