using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Cervello.Enrichment.Domain;
using Cervello.Enrichment.Ports;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cervello.Enrichment.Adapters;

/// <summary>
/// Live <see cref="IMapPrWriter"/> that opens the back-linked <c>map/</c> review-PR against
/// <c>ste/cervello</c> on forgejo (CT119, gitea-compatible REST). The PR body is the graph-writer's
/// self-linted rendering; <c>cervello-lint</c> re-runs as the pre-merge check on the forgejo side.
/// The PR is NEVER auto-merged — a human gate merges it (like the UI Factory). Bearer-gated via
/// <see cref="IBearerProvider"/> (agent-free — the forgejo token/JWK is provisioned on-CT, never in
/// agent context).
///
/// <para><b>L1 boundary — DRY-RUN by default.</b> When <see cref="EnrichmentConfig.MapPrDryRun"/> is
/// true (the default), this adapter ASSEMBLES the branch name + the mutation/stub file set + the PR
/// body and LOGS them, but makes NO live forgejo call — so L1 opens no real map-PR. L2 flips
/// <c>CERVELLO_MAP_PR_DRY_RUN=false</c> on-CT to open the live review-PR. The dry-run path returns a
/// handle whose <c>Number</c> is null (no PR number) so a caller can tell a dry-run from a live open.</para>
///
/// <para>Live open = three gitea REST calls: create the branch from base → commit the rendered
/// files (bundle + stubs) onto it → open the PR. The engine composes the review artifact; the
/// mutation content itself already passed the graph-writer's R1/R4/R5/R11 self-lint upstream.</para>
/// </summary>
public sealed class ForgejoMapPrWriter : IMapPrWriter
{
    /// <summary>The logical bearer audience for forgejo egress.</summary>
    public const string Audience = "forgejo";

    private static readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _http;
    private readonly IBearerProvider _bearer;
    private readonly EnrichmentConfig _cfg;
    private readonly ILogger _log;
    private readonly string _owner;
    private readonly string _repo;

    public ForgejoMapPrWriter(HttpClient http, IBearerProvider bearer, EnrichmentConfig cfg, ILogger<ForgejoMapPrWriter>? log = null)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _bearer = bearer ?? throw new ArgumentNullException(nameof(bearer));
        _cfg = cfg ?? throw new ArgumentNullException(nameof(cfg));
        _log = log ?? NullLogger<ForgejoMapPrWriter>.Instance;

        var parts = _cfg.ForgejoRepo.Split('/', 2, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2)
            throw new InvalidOperationException(
                $"CERVELLO_FORGEJO_REPO='{_cfg.ForgejoRepo}' is invalid: expected 'owner/repo'.");
        _owner = parts[0];
        _repo = parts[1];
    }

    public async Task<MapPrHandle> OpenPrAsync(MapReviewPr pr, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(pr);
        var body = pr.RenderBody();

        if (_cfg.MapPrDryRun)
        {
            // L1 boundary: assemble + log, but open NO real PR. Number stays null → dry-run marker.
            _log.LogInformation(
                "map-PR DRY-RUN: would open '{Title}' on {Owner}/{Repo} (branch {Branch}, {Mutations} mutations, {Stubs} stubs) — no live forgejo call (L2)",
                pr.Title, _owner, _repo, pr.Branch, pr.Mutations.Count, pr.Stubs.Count);
            return new MapPrHandle(pr.Branch, pr.Title, Number: null);
        }

        var bearer = await _bearer.GetBearerAsync(Audience, ct).ConfigureAwait(false);

        // 1) create the branch from base.
        await CreateBranchAsync(pr.Branch, bearer, ct).ConfigureAwait(false);

        // 2) commit the rendered files (stub files) onto the branch. The mutations are described in
        //    the PR body; stub dossiers are authored as real files so lint R4 resolves them.
        foreach (var stub in pr.Stubs)
            await CreateOrUpdateFileAsync(stub.Path, stub.RenderStub(DateOnly.FromDateTime(DateTime.UtcNow).ToString("yyyy-MM-dd")),
                $"cervello: declare stub {stub.Slug} (R4)", pr.Branch, bearer, ct).ConfigureAwait(false);

        // 3) open the PR.
        var number = await OpenPullAsync(pr.Title, body, pr.Branch, bearer, ct).ConfigureAwait(false);
        _log.LogInformation("map-PR opened #{Number} on {Owner}/{Repo} ({Branch})", number, _owner, _repo, pr.Branch);
        return new MapPrHandle(pr.Branch, pr.Title, number);
    }

    private async Task CreateBranchAsync(string branch, string bearer, CancellationToken ct)
    {
        var req = _post($"/api/v1/repos/{_owner}/{_repo}/branches", bearer,
            new { new_branch_name = branch, old_branch_name = _cfg.ForgejoBaseBranch });
        using var res = await _http.SendAsync(req, ct).ConfigureAwait(false);
        // 201 created, or 409 if the branch already exists (idempotent re-run) — both acceptable.
        if (!res.IsSuccessStatusCode && res.StatusCode != System.Net.HttpStatusCode.Conflict)
            throw new MapPrWriteException($"create branch {branch}: {(int)res.StatusCode} {res.ReasonPhrase}");
    }

    private async Task CreateOrUpdateFileAsync(string path, string content, string message, string branch, string bearer, CancellationToken ct)
    {
        var contentB64 = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(content));
        var req = _post($"/api/v1/repos/{_owner}/{_repo}/contents/{path}", bearer,
            new { content = contentB64, message, branch });
        using var res = await _http.SendAsync(req, ct).ConfigureAwait(false);
        if (!res.IsSuccessStatusCode)
            throw new MapPrWriteException($"write {path}: {(int)res.StatusCode} {res.ReasonPhrase}");
    }

    private async Task<int?> OpenPullAsync(string title, string body, string branch, string bearer, CancellationToken ct)
    {
        var req = _post($"/api/v1/repos/{_owner}/{_repo}/pulls", bearer,
            new { title, body, head = branch, @base = _cfg.ForgejoBaseBranch });
        using var res = await _http.SendAsync(req, ct).ConfigureAwait(false);
        if (!res.IsSuccessStatusCode)
            throw new MapPrWriteException($"open PR: {(int)res.StatusCode} {res.ReasonPhrase}");
        var pr = await res.Content.ReadFromJsonAsync<WirePull>(_json, ct).ConfigureAwait(false);
        return pr?.Number;
    }

    private HttpRequestMessage _post(string path, string bearer, object payload)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(payload, options: _json) };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        return req;
    }

    private sealed record WirePull([property: JsonPropertyName("number")] int Number);
}

/// <summary>A failure opening the map review-PR (live path). Never thrown on the dry-run path.</summary>
public sealed class MapPrWriteException(string reason, Exception? inner = null) : Exception(reason, inner);
