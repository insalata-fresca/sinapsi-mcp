namespace SageCouncil.Mcp;

// -----------------------------------------------------------------------------
// Input validation for the council tools. Every tool parameter is checked here
// at the TOP of the tool BEFORE any side effect (before a job is dispatched or a
// store lookup runs), so malformed input is rejected with a clear, structured
// error instead of being composed into an outbound prompt, used as a persona /
// member selector, or handed to a store lookup.
//
// The values do not reach a shell (the server makes no subprocess calls), so
// this is a correctness + fail-fast + resource guard, not a shell-injection
// defence. `focus` and each `member` are identifier-like selectors, so a leading
// '-' and embedded control characters / newlines are rejected; `prompt` is
// genuine multi-line free text, so newlines/tabs are allowed but other control
// characters (incl. NUL) are rejected and the length is capped.
// -----------------------------------------------------------------------------

/// <summary>
/// Input validation for the <c>consult</c> / <c>consult_result</c> tools. Every
/// method returns <c>null</c> when the value is acceptable, otherwise a
/// human-readable reason. No method throws.
/// </summary>
internal static class CouncilValidation
{
    /// <summary>Upper bound on the free-text consult prompt. Generous: a real
    /// consult carries substantial context, but an unbounded blob would be
    /// composed into every member's outbound request.</summary>
    internal const int MaxPromptLength = 100_000;

    /// <summary>Upper bound on a focus/persona name (an identifier-like selector,
    /// optionally overlaid by a <c>&lt;name&gt;.md</c> persona file).</summary>
    internal const int MaxFocusLength = 128;

    /// <summary>Upper bound on a single member-type name.</summary>
    internal const int MaxMemberLength = 128;

    /// <summary>Maximum number of members accepted on a single consult.</summary>
    internal const int MaxMembers = 32;

    /// <summary>Upper bound on a job_id string (the store key is a
    /// <c>council-&lt;12 hex&gt;</c> token; 128 is a generous cap).</summary>
    internal const int MaxJobIdLength = 128;

    /// <summary>
    /// Validate the consult <c>prompt</c>. Required, length-capped, and free of
    /// non-whitespace control characters (NUL included). Newlines and tabs are
    /// allowed because a prompt is genuine multi-line free text.
    /// </summary>
    internal static string? ValidatePrompt(string? prompt)
    {
        if (string.IsNullOrWhiteSpace(prompt))
            return "prompt is required";
        if (prompt.Length > MaxPromptLength)
            return $"prompt too long ({prompt.Length} chars; max {MaxPromptLength})";
        if (ContainsDisallowedControl(prompt))
            return "prompt contains control characters";
        return null;
    }

    /// <summary>
    /// Validate the <c>focus</c> persona selector. It selects a research mandate
    /// (and, via a <c>&lt;name&gt;.md</c> overlay, a persona file lookup), so it
    /// must be a clean identifier: required, length-capped, no control characters
    /// or newlines, and no leading <c>-</c>.
    /// </summary>
    internal static string? ValidateFocus(string? focus)
    {
        if (string.IsNullOrWhiteSpace(focus))
            return "focus is required";
        if (focus.Length > MaxFocusLength)
            return $"focus too long ({focus.Length} chars; max {MaxFocusLength})";
        if (ContainsControlOrNewline(focus))
            return "focus contains control characters";
        if (focus[0] == '-')
            return "focus must not start with '-'";
        return null;
    }

    /// <summary>
    /// Validate the optional <c>members</c> roster. Returns <c>null</c> when valid
    /// (including when the list is null — the tool substitutes the default
    /// roster). Each entry is an identifier-like member-type selector.
    /// </summary>
    internal static string? ValidateMembers(string[]? members)
    {
        if (members is null || members.Length == 0)
            return null; // tool substitutes the default 3-member roster
        if (members.Length > MaxMembers)
            return $"too many members ({members.Length}; max {MaxMembers})";
        for (var i = 0; i < members.Length; i++)
        {
            var m = members[i];
            if (string.IsNullOrWhiteSpace(m))
                return $"member #{i + 1} is empty";
            if (m.Length > MaxMemberLength)
                return $"member #{i + 1} too long ({m.Length} chars; max {MaxMemberLength})";
            if (ContainsControlOrNewline(m))
                return $"member #{i + 1} contains control characters";
            if (m[0] == '-')
                return $"member #{i + 1} must not start with '-'";
        }
        return null;
    }

    /// <summary>
    /// Validate a <c>job_id</c> for <c>consult_result</c>. It is a store lookup
    /// key: required, length-capped, and free of control characters / newlines.
    /// (An unknown-but-well-formed id still returns the <c>not_found</c> envelope;
    /// this only rejects malformed input before the lookup.)
    /// </summary>
    internal static string? ValidateJobId(string? jobId)
    {
        if (string.IsNullOrWhiteSpace(jobId))
            return "job_id is required";
        if (jobId.Length > MaxJobIdLength)
            return $"job_id too long ({jobId.Length} chars; max {MaxJobIdLength})";
        if (ContainsControlOrNewline(jobId))
            return "job_id contains control characters";
        return null;
    }

    // A control char other than the whitespace tab/newline/carriage-return that a
    // free-text prompt legitimately contains.
    private static bool ContainsDisallowedControl(string s)
    {
        foreach (var c in s)
            if (char.IsControl(c) && c is not ('\t' or '\n' or '\r')) return true;
        return false;
    }

    private static bool ContainsControlOrNewline(string s)
    {
        foreach (var c in s)
            if (char.IsControl(c)) return true;
        return false;
    }
}
