using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Server;

namespace ConfigSpine.Mcp;

/// <summary>
/// The config-event publish tool surface. It exposes exactly ONE tool, <c>publish_config_event</c>,
/// which lets an agent self-record a config mutation it just made by emitting a
/// <c>homelab.config.&lt;ctid&gt;.&lt;entity&gt;.&lt;action&gt;</c> CloudEvent on the NATS event
/// spine (CLAUDE.md rule 6 — every config mutation must emit that subject so the
/// state-materialiser can update the CT's state doc).
///
/// <para>
/// Two layers keep it least-privilege. First, <b>subject validation</b>
/// (<see cref="ConfigEventValidation"/>): every token is checked and the composed subject is proven
/// to live inside <c>homelab.config.&gt;</c> BEFORE any publish, so a caller cannot compose a
/// subject outside that subtree. Second, and structurally, the server runs under a <b>dedicated
/// publish-only nkey identity</b> scoped to <c>publish: ["homelab.config.&gt;"]</c> — so even a bug
/// here cannot forge an event on any other subject; the bus rejects it.
/// </para>
/// </summary>
[McpServerToolType]
public sealed class ConfigSpineTools(IConfigEventSink sink)
{
    private static string Error(string reason) =>
        JsonSerializer.Serialize(new { ok = false, error = reason });

    [McpServerTool, Description(
        "Record a config mutation you JUST made on a CT by publishing a "
        + "homelab.config.<ctid>.<entity>.<action> CloudEvent to the NATS event spine (CLAUDE.md "
        + "rule 6). Call this immediately after a config change so it self-records for the "
        + "state-materialiser. `ctid` is the numeric container id (e.g. 105). `entity` is the "
        + "config surface that changed (e.g. acl, cert, env, route) and `action` is the change verb "
        + "(e.g. added, rotated, updated, removed) — each a single subject token. `payload` is an "
        + "optional free-form detail of the change. The subject is validated to be EXACTLY within "
        + "homelab.config.> ; anything outside that subtree is rejected. Returns {ok:true,subject} "
        + "or {ok:false,error}.")]
    public async Task<string> publish_config_event(
        [Description("Numeric container id, e.g. 105")] string ctid,
        [Description("Config entity/surface that changed, e.g. acl, cert, env, route")] string entity,
        [Description("Change action/verb, e.g. added, rotated, updated, removed")] string action,
        [Description("Optional free-form detail of the change")] string? payload = null,
        CancellationToken ct = default)
    {
        // Fail-fast input validation BEFORE any subject is composed or published. Structured error;
        // never throws.
        if (ConfigEventValidation.ValidateCtid(ctid) is { } ctidErr) return Error(ctidErr);
        if (ConfigEventValidation.ValidateToken(entity, "entity") is { } entityErr) return Error(entityErr);
        if (ConfigEventValidation.ValidateToken(action, "action") is { } actionErr) return Error(actionErr);
        if (ConfigEventValidation.ValidatePayload(payload) is { } payloadErr) return Error(payloadErr);

        var subject = ConfigEventValidation.BuildSubject(ctid, entity, action);

        // Defence in depth: prove the composed subject is inside homelab.config.> before publishing.
        if (ConfigEventValidation.EnsureInConfigSubtree(subject) is { } subjErr) return Error(subjErr);

        // Data shape mirrors the reference emit_config_event.py exactly ({ctid,entity,action,detail})
        // so the state-materialiser consumes events from this tool identically.
        var data = new JsonObject
        {
            ["ctid"] = ctid,
            ["entity"] = entity,
            ["action"] = action,
            ["detail"] = payload ?? string.Empty,
        };

        try
        {
            await sink.PublishAsync(subject, data, ct).ConfigureAwait(false);
        }
        catch (Exception e)
        {
            return Error(ConfigEventErrors.Sanitize(e.Message));
        }

        return JsonSerializer.Serialize(new { ok = true, subject });
    }
}
