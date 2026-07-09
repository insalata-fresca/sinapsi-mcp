using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Cervello.Enrichment.Ports;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cervello.Enrichment.Adapters;

/// <summary>
/// Live <see cref="IGitPublisher"/> that commits the SEARCHABLE SUBSTRATE (verbatim transcripts +
/// enrichment bundles + the recordings manifest) DIRECTLY to <c>ste/cervello</c> <c>main</c> via the
/// forgejo (CT119, gitea-compatible) contents REST API — the SAME bearer-gated, agent-free egress the
/// <see cref="ForgejoMapPrWriter"/> uses, so no <c>.git</c> working tree is required on-CT.
///
/// <para>Each file is read from the CT working tree and committed create-or-update by sha: a first
/// push POSTs (create); an existing path is updated with a PUT carrying its current sha; an unchanged
/// blob is a no-op (skipped). The push emits <c>cervello.git.cervello.push.main</c> on the forgejo
/// side, which the strictly-git-sourced indexer (<c>:8009</c>) reacts to — re-indexing the recording
/// content so recall returns it.</para>
///
/// <para><b>Independent of the map-PR dry-run gate</b> (that gate governs <c>map/</c> ATTRIBUTIONS —
/// a separate concern). <b>LINT R7 preserved:</b> a path under any never-git prefix (audio /
/// voiceprints) is REFUSED — only derived text reaches git.</para>
/// </summary>
public sealed class ForgejoContentsPublisher : IGitPublisher
{
    /// <summary>The logical bearer audience for forgejo egress (shared with the map-PR writer).</summary>
    public const string Audience = "forgejo";

    private static readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _http;
    private readonly IBearerProvider _bearer;
    private readonly EnrichmentConfig _cfg;
    private readonly string _repoRoot;
    private readonly ILogger _log;
    private readonly string _owner;
    private readonly string _repo;

    public ForgejoContentsPublisher(
        HttpClient http, IBearerProvider bearer, EnrichmentConfig cfg, string repoWorkingTree,
        ILogger<ForgejoContentsPublisher>? log = null)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _bearer = bearer ?? throw new ArgumentNullException(nameof(bearer));
        _cfg = cfg ?? throw new ArgumentNullException(nameof(cfg));
        if (string.IsNullOrWhiteSpace(repoWorkingTree))
            throw new ArgumentException("repoWorkingTree must be non-empty", nameof(repoWorkingTree));
        _repoRoot = repoWorkingTree;
        _log = log ?? NullLogger<ForgejoContentsPublisher>.Instance;

        var parts = _cfg.ForgejoRepo.Split('/', 2, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2)
            throw new InvalidOperationException(
                $"CERVELLO_FORGEJO_REPO='{_cfg.ForgejoRepo}' is invalid: expected 'owner/repo'.");
        _owner = parts[0];
        _repo = parts[1];
    }

    public async Task<GitPublishResult> PublishAsync(GitPublishRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var pushed = new List<string>();
        var skipped = new List<string>();
        var bearer = await _bearer.GetBearerAsync(Audience, ct).ConfigureAwait(false);
        var branch = _cfg.ForgejoBaseBranch;

        foreach (var rel in request.RepoRelativePaths.Distinct(StringComparer.Ordinal))
        {
            // LINT R7 hard floor: never commit audio / voiceprints, whatever the caller passes.
            if (GitPublishRequest.NeverGitPrefixes.Any(p => rel.StartsWith(p, StringComparison.Ordinal)))
                throw new GitPublishException(
                    $"refusing to publish '{rel}': audio + voiceprints never enter git (LINT R7).");

            var abs = Path.Combine(_repoRoot, rel);
            if (!File.Exists(abs))
            {
                // A path the run didn't produce (e.g. no bundle on a re-run) — skip, never fabricate.
                skipped.Add(rel);
                continue;
            }

            var localBytes = await File.ReadAllBytesAsync(abs, ct).ConfigureAwait(false);
            var (existingSha, existingBytes) = await GetExistingAsync(rel, branch, bearer, ct).ConfigureAwait(false);

            // Idempotency: an unchanged blob is a no-op (avoids empty commits + churn on re-runs).
            if (existingSha is not null && existingBytes is not null && localBytes.AsSpan().SequenceEqual(existingBytes))
            {
                skipped.Add(rel);
                continue;
            }

            await CommitAsync(rel, localBytes, existingSha, branch, bearer, request.RecordingId, ct)
                .ConfigureAwait(false);
            pushed.Add(rel);
        }

        if (pushed.Count > 0)
            _log.LogInformation(
                "cervello git publish: recording {Rec} → {Pushed} file(s) pushed to {Owner}/{Repo}@{Branch} ({Skipped} unchanged/absent)",
                request.RecordingId, pushed.Count, _owner, _repo, branch, skipped.Count);
        else
            _log.LogInformation(
                "cervello git publish: recording {Rec} → nothing to push ({Skipped} unchanged/absent)",
                request.RecordingId, skipped.Count);

        return new GitPublishResult(pushed, skipped, WasNoOp: pushed.Count == 0);
    }

    /// <summary>Fetch the current blob sha + bytes for a path on the branch, or (null,null) if absent.</summary>
    private async Task<(string? sha, byte[]? bytes)> GetExistingAsync(string path, string branch, string bearer, CancellationToken ct)
    {
        var req = new HttpRequestMessage(HttpMethod.Get,
            $"/api/v1/repos/{_owner}/{_repo}/contents/{Uri.EscapeDataString(path).Replace("%2F", "/", StringComparison.Ordinal)}?ref={Uri.EscapeDataString(branch)}");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        using var res = await _http.SendAsync(req, ct).ConfigureAwait(false);
        if (res.StatusCode == HttpStatusCode.NotFound)
            return (null, null);
        if (!res.IsSuccessStatusCode)
            throw new GitPublishException($"read {path}: {(int)res.StatusCode} {res.ReasonPhrase}");
        var wire = await res.Content.ReadFromJsonAsync<WireContent>(_json, ct).ConfigureAwait(false);
        if (wire?.Sha is null)
            return (null, null);
        byte[]? bytes = null;
        if (wire.Content is { Length: > 0 } && string.Equals(wire.Encoding, "base64", StringComparison.OrdinalIgnoreCase))
        {
            try { bytes = Convert.FromBase64String(wire.Content.Replace("\n", "", StringComparison.Ordinal)); }
            catch (FormatException) { bytes = null; }
        }
        return (wire.Sha, bytes);
    }

    /// <summary>Create (POST, no sha) or update (PUT, with sha) one file on the branch.</summary>
    private async Task CommitAsync(
        string path, byte[] content, string? sha, string branch, string bearer, string recordingId, CancellationToken ct)
    {
        var contentB64 = Convert.ToBase64String(content);
        var message = sha is null
            ? $"cervello: index recording {recordingId} — add {path}"
            : $"cervello: index recording {recordingId} — update {path}";
        object payload = sha is null
            ? new { content = contentB64, message, branch }
            : new { content = contentB64, message, branch, sha };
        var method = sha is null ? HttpMethod.Post : HttpMethod.Put;

        var req = new HttpRequestMessage(method,
            $"/api/v1/repos/{_owner}/{_repo}/contents/{Uri.EscapeDataString(path).Replace("%2F", "/", StringComparison.Ordinal)}")
        { Content = JsonContent.Create(payload, options: _json) };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);

        using var res = await _http.SendAsync(req, ct).ConfigureAwait(false);
        if (!res.IsSuccessStatusCode)
            throw new GitPublishException($"publish {path}: {(int)res.StatusCode} {res.ReasonPhrase}");
    }

    private sealed record WireContent(
        [property: JsonPropertyName("sha")] string? Sha,
        [property: JsonPropertyName("content")] string? Content,
        [property: JsonPropertyName("encoding")] string? Encoding);
}

/// <summary>
/// A no-op <see cref="IGitPublisher"/> for fake mode / tests / a disabled deploy: publishes nothing
/// and reports a no-op, so the graph resolves offline and a host can disable the push by config
/// without a code change. NEVER used when live git publishing is enabled.
/// </summary>
public sealed class NoOpGitPublisher : IGitPublisher
{
    public Task<GitPublishResult> PublishAsync(GitPublishRequest request, CancellationToken ct = default) =>
        Task.FromResult(GitPublishResult.NoOp);
}
