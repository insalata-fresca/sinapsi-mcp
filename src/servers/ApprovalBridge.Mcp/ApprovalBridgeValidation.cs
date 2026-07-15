namespace ApprovalBridge.Mcp;

/// <summary>
/// Input validation for the <c>approval_bridge_request</c> tool call SHAPE — deliberately a
/// separate, cheaper gate than the broker's own <c>action_id ∈ allowlist</c> / <c>params ∈
/// param_schema</c> check (docs/66 §2, §3.1, I6). This class never consults the allowlist or any
/// schema; it only rejects a call that is malformed on its face (empty/oversize/control-char
/// action_id, or params that aren't a JSON object) BEFORE the broker is ever called — the same
/// "fail fast before any I/O" shape as <c>InfisicalValidation</c> / <c>StepCaValidation</c> in
/// this repo. The broker remains the deny-by-default source of truth for whether the action is
/// actually registered and the params actually satisfy its schema.
///
/// <para>Every method returns <c>null</c> when the value is valid, otherwise a human-readable
/// reason. None of them throw.</para>
/// </summary>
internal static class ApprovalBridgeValidation
{
    /// <summary>Upper bound on <c>action_id</c>. Registered action ids are short, stable, dotted
    /// slugs (e.g. <c>garmin.oauth.exchange</c>, docs/66 §2); 128 is a generous cap that still
    /// refuses an unbounded blob.</summary>
    internal const int MaxActionIdLength = 128;

    /// <summary>Upper bound on the raw <c>params</c> JSON text. A real action's typed params are
    /// small (docs/66 §2 examples are single-field objects); 16 KiB is generous headroom while
    /// still refusing a pathological paste before it reaches the broker.</summary>
    internal const int MaxParamsJsonLength = 16_384;

    /// <summary>Validate <c>action_id</c>: non-empty, bounded, no control characters, no path
    /// separators (it must never be usable to traverse or inject into a downstream path/command —
    /// the broker only ever treats it as an allowlist lookup key, but this tool refuses the
    /// malformed shape regardless of what the broker would do with it).</summary>
    internal static string? ValidateActionId(string? actionId)
    {
        if (string.IsNullOrWhiteSpace(actionId))
            return "action_id is required";
        if (actionId.Length > MaxActionIdLength)
            return $"action_id too long ({actionId.Length} chars; max {MaxActionIdLength})";
        foreach (var c in actionId)
        {
            if (char.IsControl(c))
                return "action_id contains control characters";
            if (c is '/' or '\\')
                return "action_id must not contain a path separator";
        }
        return null;
    }

    /// <summary>
    /// Validate the raw <c>params</c> argument and normalise it to a JSON object string. A
    /// missing/blank value normalises to <c>"{}"</c> (mirrors the broker's own
    /// <c>body.@params ?? "{}"</c> handling) so an action with no required params can still be
    /// requested. Anything else must parse as JSON and be a top-level OBJECT — the broker's
    /// <c>param_schema</c> is always <c>type: object</c> (docs/66 §2), so a JSON array/string/
    /// number/bool is refused here rather than left for a confusing schema-mismatch error.
    /// </summary>
    internal static string? ValidateParamsJson(string? paramsJson, out string normalized)
    {
        normalized = "{}";
        if (string.IsNullOrWhiteSpace(paramsJson))
            return null; // defaults to "{}" — a no-arg action stays requestable.

        if (paramsJson.Length > MaxParamsJsonLength)
            return $"params too large ({paramsJson.Length} chars; max {MaxParamsJsonLength})";

        System.Text.Json.JsonDocument doc;
        try
        {
            doc = System.Text.Json.JsonDocument.Parse(paramsJson);
        }
        catch (System.Text.Json.JsonException)
        {
            return "params is not valid JSON";
        }

        using (doc)
        {
            if (doc.RootElement.ValueKind != System.Text.Json.JsonValueKind.Object)
                return "params must be a JSON object";
            normalized = paramsJson;
            return null;
        }
    }
}
