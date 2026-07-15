using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;

namespace ApprovalBridge.Mcp;

/// <summary>
/// The agent-facing tool surface for the Operator Approval Bridge (home-server
/// <c>docs/66-operator-approval-bridge.md</c>, mission E1.6 / task #42). Exposes exactly ONE tool
/// — <c>approval_bridge_request</c> — the REQUEST path a BLOCKED agent calls to surface a
/// <c>requiresApproval</c> action to the operator via the <c>ApprovalBridge.Broker</c> (E1.3).
///
/// <para>
/// <b>This type declares no approve/reject tool, and never will:</b> docs/66 §8 T1 requires the
/// requesting agent be STRUCTURALLY unable to approve its own request. Two independent barriers
/// hold that here: (a) <see cref="ApprovalBridgeClient"/> has no method that can reach the
/// broker's <c>/approve</c> or <c>/reject</c> endpoint — there is nothing on this class's surface
/// to call even if a future edit tried; (b) the E1.5 approve-channel authz scopes agent identities
/// to <c>approval_bridge_request</c> only at the gateway, so even a differently-built MCP could
/// not reach <c>/approve</c> under an agent identity. This tool only ever returns a PENDING
/// handle — never an approval — and the action does not run until the OPERATOR approves in the
/// Console or chat (docs/66 §3.2).
/// </para>
///
/// <para>
/// Hardening mirrors the sibling security-adjacent tools in this repo (<c>InfisicalTools</c>,
/// <c>StepCaTools</c>): every parameter is validated (<see cref="ApprovalBridgeValidation"/>) at
/// the top, BEFORE any call to the broker, returning a structured <c>{ok:false,error}</c> envelope
/// on rejection — including the broker's own deny-by-default refusals (unknown action_id, params
/// failing <c>param_schema</c>, rate-limited). A transport/upstream failure is caught and surfaced
/// through <see cref="ApprovalBridgeErrors.Sanitize"/>.
/// </para>
/// </summary>
[McpServerToolType]
public sealed class ApprovalBridgeTools(ApprovalBridgeClient client)
{
    private static string Error(string reason) =>
        JsonSerializer.Serialize(new { ok = false, error = reason });

    [McpServerTool(Name = "approval_bridge_request"), Description(
        "Request operator approval for a pre-registered scoped action (home-server docs/66, the " +
        "Operator Approval Bridge). Submits action_id + typed params to the Bridge Broker's REQUEST " +
        "intake, which validates action_id against the server-side allowlist and params against its " +
        "param_schema, deny-by-default — an unknown action_id, schema-violating params, or a rate " +
        "limit hit is refused before any operator ever sees it. On acceptance the broker mints a " +
        "PENDING request and returns its request_id: a correlation handle for the operator's " +
        "Console/chat queue, NOT an approval. Nothing runs yet. This tool exposes ONLY the request " +
        "path — it has no way to approve or reject; only the operator can approve, and never the " +
        "requesting agent (self-approval is structurally impossible).")]
    public async Task<string> approval_bridge_request(
        [Description("The pre-registered action_id from the allowlist, e.g. 'garmin.oauth.exchange'.")]
        string action_id,
        [Description("JSON object of the action's typed params, matching its param_schema (e.g. " +
            "'{\"auth_code\":\"...\"}'). Omit or pass '{}' for a no-arg action.")]
        string? @params,
        CancellationToken ct)
    {
        if (ApprovalBridgeValidation.ValidateActionId(action_id) is { } actionErr)
            return Error(actionErr);
        if (ApprovalBridgeValidation.ValidateParamsJson(@params, out var normalizedParams) is { } paramsErr)
            return Error(paramsErr);

        ApprovalBridgeRequestResult result;
        try
        {
            result = await client.RequestAsync(action_id, normalizedParams, ct).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return Error(ApprovalBridgeErrors.Sanitize(e.Message));
        }

        if (!result.Accepted)
            return Error(result.DenialReason);

        return JsonSerializer.Serialize(new
        {
            ok = true,
            status = "pending",
            request_id = result.RequestId,
            action_id,
            message = "Awaiting operator approval in the Console/chat. This is a pending handle, " +
                      "NOT an approval — the action has not run, and this agent cannot approve it.",
        });
    }
}
