namespace ConfigSpine.Mcp;

/// <summary>
/// Input + subject validation for the config-event publish tool. This is the load-bearing
/// least-privilege guard: the ONLY subject the tool is allowed to publish is exactly
/// <c>homelab.config.&lt;ctid&gt;.&lt;entity&gt;.&lt;action&gt;</c>. Every caller-supplied token is
/// validated here BEFORE a subject is composed, and the composed subject is re-checked against the
/// <c>homelab.config.&gt;</c> subtree (defence in depth) BEFORE anything is published.
///
/// <para>
/// The tokens flow directly into a NATS subject, so a stray <c>.</c> (extra token), a <c>*</c> or
/// <c>&gt;</c> (wildcard), or whitespace could otherwise let a caller escape the intended subtree
/// (e.g. <c>entity = "*"</c> → <c>homelab.config.105.*.x</c>, or a dotted <c>ctid</c> →
/// <c>homelab.config.105.foo.bar.baz</c>). Each is rejected defensively. This is a correctness +
/// least-privilege guard on top of — never instead of — the dedicated publish-only nkey ACL
/// (<c>publish: ["homelab.config.&gt;"]</c>), which is the structural backstop enforced by the bus.
/// </para>
///
/// <para>Every method returns <c>null</c> when the value is valid, otherwise a short
/// human-readable reason. None of them throw.</para>
/// </summary>
internal static class ConfigEventValidation
{
    /// <summary>The one and only subject root this tool may publish under.</summary>
    internal const string Root = "homelab.config.";

    /// <summary>Upper bound on an entity / action token. NATS subject tokens are short slugs;
    /// 64 is a generous cap that still refuses an unbounded blob.</summary>
    internal const int MaxTokenLength = 64;

    /// <summary>Upper bound on a numeric ctid string (the CT number, e.g. 105). Six digits is far
    /// past any real container id.</summary>
    internal const int MaxCtidLength = 6;

    /// <summary>Upper bound on the optional free-form payload/detail string.</summary>
    internal const int MaxPayloadLength = 4_096;

    /// <summary>Validate the <c>ctid</c>. It is the numeric container id that becomes the third
    /// subject token, so it must be a bounded run of ASCII digits — nothing that could introduce a
    /// dot, wildcard, or extra token.</summary>
    internal static string? ValidateCtid(string? ctid)
    {
        if (string.IsNullOrWhiteSpace(ctid))
            return "ctid is required";
        if (ctid.Length > MaxCtidLength)
            return $"ctid too long ({ctid.Length} chars; max {MaxCtidLength})";
        foreach (var c in ctid)
            if (c is < '0' or > '9')
                return "ctid must be numeric (the CT number, e.g. 105)";
        return null;
    }

    /// <summary>Validate a single NATS subject token (<c>entity</c> or <c>action</c>). It must be
    /// present, bounded, control/whitespace free, must NOT be a wildcard or contain a subject
    /// separator, and must not start with <c>-</c>.</summary>
    internal static string? ValidateToken(string? value, string paramName)
    {
        if (string.IsNullOrWhiteSpace(value))
            return $"{paramName} is required";
        if (value.Length > MaxTokenLength)
            return $"{paramName} too long ({value.Length} chars; max {MaxTokenLength})";
        foreach (var c in value)
        {
            if (char.IsControl(c) || char.IsWhiteSpace(c))
                return $"{paramName} contains control or whitespace characters";
            if (c is '.' or '*' or '>' or '/' or '\\')
                return $"{paramName} must be a single NATS subject token (no '.', '*', '>', '/', '\\')";
        }
        if (value[0] == '-')
            return $"{paramName} must not start with '-'";
        return null;
    }

    /// <summary>Validate the optional free-form <c>payload</c> detail. Null is allowed (absent).
    /// A non-null value is only rejected for being over-long or carrying control characters
    /// (bar the usual newline/tab whitespace a human note may contain).</summary>
    internal static string? ValidatePayload(string? payload)
    {
        if (payload is null)
            return null;
        if (payload.Length > MaxPayloadLength)
            return $"payload too long ({payload.Length} chars; max {MaxPayloadLength})";
        foreach (var c in payload)
            if (char.IsControl(c) && c is not ('\n' or '\r' or '\t'))
                return "payload contains control characters";
        return null;
    }

    /// <summary>Compose the config-event subject from validated tokens.</summary>
    internal static string BuildSubject(string ctid, string entity, string action) =>
        $"{Root}{ctid}.{entity}.{action}";

    /// <summary>
    /// Defence in depth: prove the composed subject is EXACTLY
    /// <c>homelab.config.&lt;ctid&gt;.&lt;entity&gt;.&lt;action&gt;</c> — five tokens, the literal
    /// <c>homelab.config</c> prefix, no empty tokens, no wildcards. Nothing outside
    /// <c>homelab.config.&gt;</c> can be published even if a token check were ever loosened.
    /// Returns <c>null</c> when the subject is inside the subtree.
    /// </summary>
    internal static string? EnsureInConfigSubtree(string subject)
    {
        if (!subject.StartsWith(Root, StringComparison.Ordinal))
            return "subject escaped the homelab.config.> subtree";
        var parts = subject.Split('.');
        if (parts.Length != 5)
            return "subject must be exactly homelab.config.<ctid>.<entity>.<action>";
        if (parts[0] != "homelab" || parts[1] != "config")
            return "subject must be under homelab.config.";
        foreach (var p in parts)
        {
            if (p.Length == 0)
                return "subject has an empty token";
            if (p is "*" or ">")
                return "subject must not contain wildcards";
        }
        return null;
    }
}
