// ---------------------------------------------------------------------------
// GeminiValidation — fail-fast input validation for every gemini tool parameter
// that flows into a subprocess arg, a filesystem path segment, or a directive
// string. Mirrors the StepCa.Mcp exemplar (StepCaValidation): one Validate*
// method per param, each returning string? (null = ok, else a human-readable
// reason), never throwing. Called at the TOP of each tool BEFORE any side effect.
//
// Shell safety note: subprocess args are passed via ProcessStartInfo.ArgumentList
// (no shell interpolation), so this is a correctness + fail-fast guard, not the
// primary injection defence. We still reject leading-'-' on non-content values so
// a hostile value can never be mistaken for a gemini CLI flag, and we reject path
// separators / traversal in the id params that become filesystem path segments.
// ---------------------------------------------------------------------------
namespace Gemini.Mcp;

/// <summary>
/// Input validation for the gemini tools. Every method returns <c>null</c> when
/// the value is acceptable, otherwise a short human-readable reason; none throw.
/// </summary>
internal static class GeminiValidation
{
    /// <summary>Upper bound on a free-text prompt / query / system / question. A
    /// generous cap that still refuses an unbounded blob that could exhaust the
    /// subprocess arg list or wedge the model.</summary>
    internal const int MaxPromptLength = 100_000;

    /// <summary>Upper bound on a filesystem path handed to the CLI.</summary>
    internal const int MaxPathLength = 4_096;

    /// <summary>Upper bound on an id (session_id / task_id). A UUID is 36 chars;
    /// 128 is a generous ceiling that still refuses a pathological value.</summary>
    internal const int MaxIdLength = 128;

    /// <summary>Upper bound on the optional focus marker.</summary>
    internal const int MaxFocusLength = 1_024;

    /// <summary>Maximum number of file paths accepted by <c>ask_with_files</c>.</summary>
    internal const int MaxFilePaths = 100;

    internal static readonly string[] AllowedModels = { "auto", "pro", "flash", "flash-lite" };
    internal static readonly string[] AllowedDepths = { "quick", "standard", "deep" };
    internal static readonly string[] AllowedAspectRatios = { "1:1", "16:9", "9:16", "4:3", "3:4" };

    // ── free-text content (prompt / query / system / question) ───────────────
    // These are the actual text sent to the model, so newlines and rich content
    // are legitimate — we do NOT reject control characters here (that would break
    // multi-line prompts). We reject only the NUL character (which cannot appear in
    // a process argument and would truncate it) and cap the length.

    internal static string? ValidatePrompt(string? prompt, string field = "prompt")
    {
        if (string.IsNullOrEmpty(prompt))
            return $"{field} is required";
        if (prompt.Length > MaxPromptLength)
            return $"{field} too long ({prompt.Length} chars; max {MaxPromptLength})";
        if (prompt.Contains('\0'))
            return $"{field} contains a NUL character";
        return null;
    }

    /// <summary>Optional free-text (system prefix): null/empty is allowed; only a
    /// non-empty, malformed value is rejected.</summary>
    internal static string? ValidateOptionalText(string? value, string field, int maxLength)
    {
        if (string.IsNullOrEmpty(value))
            return null;
        if (value.Length > maxLength)
            return $"{field} too long ({value.Length} chars; max {maxLength})";
        if (value.Contains('\0'))
            return $"{field} contains a NUL character";
        return null;
    }

    // ── constrained enums ────────────────────────────────────────────────────

    internal static string? ValidateModel(string? model)
    {
        if (string.IsNullOrEmpty(model))
            return null; // tools default to "auto" via the parameter default
        return AllowedModels.Contains(model)
            ? null
            : $"model '{Clip(model)}' invalid (allowed: {string.Join(", ", AllowedModels)})";
    }

    internal static string? ValidateDepth(string? depth)
    {
        if (string.IsNullOrEmpty(depth))
            return null;
        return AllowedDepths.Contains(depth)
            ? null
            : $"depth '{Clip(depth)}' invalid (allowed: {string.Join(", ", AllowedDepths)})";
    }

    internal static string? ValidateAspectRatio(string? aspectRatio)
    {
        if (string.IsNullOrEmpty(aspectRatio))
            return null; // optional
        return AllowedAspectRatios.Contains(aspectRatio)
            ? null
            : $"aspect_ratio '{Clip(aspectRatio)}' invalid (allowed: {string.Join(", ", AllowedAspectRatios)})";
    }

    // ── filesystem paths (image_path / file_paths) ───────────────────────────
    // These reach the CLI as @-mentions / vision args. Reject empty, over-long,
    // control-char/newline-bearing, and leading-'-' values (so they cannot be
    // mistaken for a CLI flag).

    internal static string? ValidatePath(string? path, string field = "path")
    {
        if (string.IsNullOrWhiteSpace(path))
            return $"{field} is required";
        if (path.Length > MaxPathLength)
            return $"{field} too long ({path.Length} chars; max {MaxPathLength})";
        if (ContainsControlOrNewline(path))
            return $"{field} contains control characters";
        if (path[0] == '-')
            return $"{field} must not start with '-'";
        return null;
    }

    internal static string? ValidateFilePaths(string[]? filePaths)
    {
        if (filePaths is null || filePaths.Length == 0)
            return "file_paths is required";
        if (filePaths.Length > MaxFilePaths)
            return $"too many file_paths ({filePaths.Length}; max {MaxFilePaths})";
        for (var i = 0; i < filePaths.Length; i++)
        {
            if (ValidatePath(filePaths[i], $"file_paths[{i}]") is { } reason)
                return reason;
        }
        return null;
    }

    // ── id params that become filesystem path segments ───────────────────────
    // session_id / task_id are interpolated into a path (Path.Combine / "{id}.json"),
    // so a value containing a path separator or ".." would escape the intended
    // directory. We accept only a clean id: non-empty, bounded, no control chars,
    // no path separators, and not a "." / ".." traversal token.

    internal static string? ValidateId(string? id, string field)
    {
        if (string.IsNullOrWhiteSpace(id))
            return $"{field} is required";
        if (id.Length > MaxIdLength)
            return $"{field} too long ({id.Length} chars; max {MaxIdLength})";
        if (ContainsControlOrNewline(id))
            return $"{field} contains control characters";
        if (id.Contains('/') || id.Contains('\\'))
            return $"{field} must not contain a path separator";
        if (id is "." or "..")
            return $"{field} must not be a path-traversal token";
        return null;
    }

    // ── optional focus marker ────────────────────────────────────────────────

    internal static string? ValidateFocus(string? focus)
    {
        if (string.IsNullOrEmpty(focus))
            return null; // optional
        if (focus.Length > MaxFocusLength)
            return $"focus too long ({focus.Length} chars; max {MaxFocusLength})";
        if (focus.Contains('\0'))
            return "focus contains a NUL character";
        return null;
    }

    private static bool ContainsControlOrNewline(string s)
    {
        foreach (var c in s)
            if (char.IsControl(c)) return true;
        return false;
    }

    /// <summary>Clip a rejected value so an over-long/hostile value cannot itself
    /// bloat the error message.</summary>
    private static string Clip(string s) => s.Length <= 64 ? s : s[..64] + "…";
}
