using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Cervello.Enrichment.Ports;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cervello.Enrichment.Pipeline;

/// <summary>
/// The engine behind <c>cervello_capture_fact</c> (design §5.5) — the CAPTURE loop. Captures a fact
/// the operator states in chat into cervello, GROUNDED + SOURCED, and NEVER silently merged into
/// <c>map/</c>. It deposits a CANDIDATE bundle into <c>conversations/</c> + <c>inbox/</c> where the
/// E1 ingestion spine reviews it through the human GRAPH-ADD gate.
///
/// <para><b>Confirm-by-default (MC-ratified Q6).</b> <c>confirm=false</c> (the default) returns a
/// PREVIEW — exactly what will be written, where, with what provenance (<c>source: deposit://&lt;id&gt;</c>,
/// <c>basis: human://&lt;deposit-id&gt;</c>) — and writes NOTHING. <c>confirm=true</c> deposits the
/// bundle. A silent write is impossible: the deposit store is reached only on confirm.</para>
///
/// <para>Grounding floor: the fact carries the operator's stated provenance (<c>source_hint</c>) and a
/// <c>deposit://</c> ref; it never becomes a map fact except through the gate. No LLM, no invention.</para>
/// </summary>
public sealed class CaptureService(IDepositStore store, ILogger<CaptureService>? logger = null)
{
    private readonly IDepositStore _store = store ?? throw new ArgumentNullException(nameof(store));
    private readonly ILogger _log = logger ?? NullLogger<CaptureService>.Instance;
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    /// <summary>Capture a chat-fact. <paramref name="confirm"/>=false → preview only; true → deposit.</summary>
    public async Task<CaptureResult> CaptureAsync(
        string fact,
        string? sourceHint,
        IReadOnlyList<string> relatesTo,
        bool confirm,
        DateOnly today,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(fact))
            throw new ArgumentException("capture requires a non-empty fact", nameof(fact));

        var depositId = DepositId(fact, sourceHint, today);
        var depositRef = $"deposit://{depositId}";
        var basis = $"human://{depositId}";
        var path = $"inbox/{depositId}/";

        var conversationMd = RenderConversation(depositId, fact, sourceHint, relatesTo, today);
        var (bundleMd, dataJson) = RenderBundle(depositId, fact, sourceHint, relatesTo, depositRef, today);

        if (!confirm)
        {
            // PREVIEW — write nothing (confirm-by-default). Show exactly what will land + provenance.
            _log.LogInformation("capture preview {DepositId} (confirm=false — nothing written)", depositId);
            return new CaptureResult(
                Status: "preview",
                DepositId: depositId,
                Path: path,
                Commit: null,
                WillEnter: "inbox → graph-add gate",
                Basis: basis,
                Source: depositRef,
                Preview: bundleMd);
        }

        var result = await _store.WriteAsync(depositId, conversationMd, bundleMd, dataJson, ct).ConfigureAwait(false);
        _log.LogInformation("capture deposited {DepositId} at {Path} ({Sha})", depositId, result.Path, result.CommitSha);
        return new CaptureResult(
            Status: "deposited",
            DepositId: depositId,
            Path: result.Path,
            Commit: result.CommitSha,
            WillEnter: "inbox → graph-add gate",
            Basis: basis,
            Source: depositRef,
            Preview: null);
    }

    // ── rendering ────────────────────────────────────────────────────────────────────────────

    private static string RenderConversation(string id, string fact, string? hint, IReadOnlyList<string> relatesTo, DateOnly today)
    {
        var sb = new StringBuilder();
        sb.Append("---\ntype: conversation\ndeposit_id: ").Append(id).Append("\ncaptured: ").Append(today.ToString("yyyy-MM-dd")).Append("\n---\n\n");
        sb.Append("# Captured fact\n\n");
        sb.Append("- ").Append(fact.Trim());
        if (!string.IsNullOrWhiteSpace(hint)) sb.Append("  *(").Append(hint!.Trim()).Append(")*");
        sb.Append("  — source: deposit://").Append(id).Append('\n');
        if (relatesTo.Count > 0)
            sb.Append("\nRelates to: ").Append(string.Join(", ", relatesTo)).Append('\n');
        return sb.ToString();
    }

    private static (string bundleMd, string dataJson) RenderBundle(
        string id, string fact, string? hint, IReadOnlyList<string> relatesTo, string depositRef, DateOnly today)
    {
        var proposedLinks = relatesTo.Select(r => new { target = ToLink(r), confidence = 0.0 }).ToArray();
        var data = new
        {
            bundle_id = id,
            source_ref = depositRef,
            idempotency_key = $"deposit:{id}:<commitSha>",
            kind = "deposit",
            created_at = today.ToString("yyyy-MM-dd"),
            state = "queued",
            enrichment = new
            {
                summary = fact.Trim(),
                entities = Array.Empty<string>(),
                dates = new[] { today.ToString("yyyy-MM-dd") },
                proposed_links = proposedLinks,
                proposed_timeline = new[] { new { date = today.ToString("yyyy-MM-dd"), fact = fact.Trim(), source = depositRef } },
                attribution = Array.Empty<object>(),
            },
            attention = new { verdict = "promote", score = 0.0, reason = "operator-captured fact (human basis)" },
            provenance = new { source_hint = hint ?? "", basis = $"human://{id}" },
        };
        var dataJson = JsonSerializer.Serialize(data, JsonOpts);

        var md = new StringBuilder();
        md.Append("# Deposit ").Append(id).Append("\n\n");
        md.Append("Operator-captured fact (candidate — enters the graph-add gate, never auto-merged).\n\n");
        md.Append("**Fact:** ").Append(fact.Trim()).Append('\n');
        if (!string.IsNullOrWhiteSpace(hint)) md.Append("**Provenance:** ").Append(hint!.Trim()).Append('\n');
        md.Append("**Source:** ").Append(depositRef).Append('\n');
        md.Append("**Basis:** human://").Append(id).Append('\n');
        if (relatesTo.Count > 0) md.Append("**Relates to:** ").Append(string.Join(", ", relatesTo)).Append('\n');
        md.Append("\n**Proposed timeline line (candidate):**\n");
        md.Append("- ").Append(today.ToString("yyyy-MM-dd")).Append(" — ").Append(fact.Trim())
          .Append(" — source: ").Append(depositRef).Append('\n');
        return (md.ToString(), dataJson);
    }

    /// <summary>Turn a <c>person:slug</c>/<c>thread:slug</c>/<c>goal:slug</c> relation into a <c>[[slug]]</c> link.</summary>
    private static string ToLink(string relation)
    {
        var idx = relation.IndexOf(':');
        var slug = idx >= 0 ? relation[(idx + 1)..] : relation;
        return $"[[{slug}]]";
    }

    /// <summary>Deterministic deposit id: date + a short content hash (so a re-capture of the same fact is idempotent).</summary>
    private static string DepositId(string fact, string? hint, DateOnly today)
    {
        var material = $"{today:yyyy-MM-dd}|{fact.Trim()}|{hint?.Trim()}";
        var sha = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(material))).ToLowerInvariant();
        return $"{today:yyyyMMdd}-cap-{sha[..10]}";
    }
}

/// <summary>The result of a capture (design §5.5 response). <c>Preview</c> is set only for confirm=false.</summary>
public sealed record CaptureResult(
    string Status,
    string DepositId,
    string Path,
    string? Commit,
    string WillEnter,
    string Basis,
    string Source,
    string? Preview);
