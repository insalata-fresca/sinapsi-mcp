using System.Net;
using System.Text.Json;
using Sinapsi.Forge.Tools;

namespace Github.Mcp.Forge;

/// <summary>
/// The GitHub repository <b>Insights</b> surface — traffic, commit/contributor statistics,
/// community profile, dependency-graph SBOM, forks, and languages.
///
/// <para>
/// <b>Why this file does not go through <c>GetJsonAsync</c>.</b> Three GitHub answers on this
/// surface are legitimate outcomes rather than failures, and the shared helper cannot express
/// any of them:
/// </para>
/// <list type="number">
///   <item><b>202 Accepted with an EMPTY body</b> on every <c>stats/*</c> endpoint. GitHub
///     computes those series asynchronously; the first request kicks the job off and answers
///     202, and only a later request gets 200 + JSON. <c>EnsureOkAsync</c> treats 202 as
///     success (<c>IsSuccessStatusCode</c>), so <c>GetJsonAsync</c> would then hand
///     <c>JsonDocument.Parse("")</c> an empty string and THROW — turning "come back in a
///     moment" into an opaque tool crash. <see cref="GetStatsAsync"/> intercepts the raw
///     response and returns a structured retry envelope instead. It deliberately does NOT
///     poll: blocking a tool call on GitHub's cache warm-up would burn the caller's HTTP
///     timeout for an answer the caller can simply ask for again.</item>
///   <item><b>403 on <c>traffic/*</c></b> — GitHub gates traffic data on push access. This is
///     the EXPECTED answer for a repo we can read but not write, so it must read as an answer,
///     not a tool failure.</item>
///   <item><b>422 on <c>stats/code_frequency</c></b> — the repo has too many commits for
///     GitHub to compute the series at all. Retrying will never help, so it gets its own
///     envelope with <c>retry:false</c> semantics (no <c>retry</c> flag).</item>
/// </list>
///
/// <para>
/// Everything else — 404 above all — falls through to <c>EnsureOkAsync</c> and the normal
/// <c>ForgeApiException</c> → <c>ForgeToolGuard</c> path, with the upstream body scrubbed by
/// <c>SinapsiForgeErrors.Sanitize</c>. Status verdicts are read from the RAW response before
/// any sanitising, so a redaction can never change what the caller is told.
/// </para>
/// </summary>
public sealed partial class GitHubForgeClient
{
    // ── envelopes ─────────────────────────────────────────────────────────────

    /// <summary>GitHub is warming a <c>stats/*</c> cache (202, empty body). The caller retries.</summary>
    private static object StatsComputing(string endpoint) => new
    {
        ok = false,
        status = 202,
        retry = true,
        endpoint,
        note = "GitHub is computing this statistic; retry in a few seconds",
    };

    /// <summary>
    /// GitHub refused a <c>traffic/*</c> read (403). Surfaces GitHub's OWN message rather than a
    /// guess, because the two causes need opposite fixes and the guess sends you the wrong way:
    ///   • classic PAT lacking push on the repo → "Must have push access to repository"
    ///   • FINE-GRAINED PAT without the <c>Administration: read</c> permission →
    ///     "Resource not accessible by personal access token" — and this one fires even when the
    ///     token holds admin+push on the repo, because for fine-grained tokens traffic is gated on
    ///     the Administration permission, NOT on repo push.
    /// This method previously hardcoded "requires push access", which cost real debugging time on a
    /// token that demonstrably had push (verified: permissions.admin=true, permissions.push=true,
    /// still 403). Never paraphrase an upstream authorization error — quote it.
    /// </summary>
    private static object TrafficForbidden(string endpoint, string upstreamMessage) => new
    {
        ok = false,
        status = 403,
        endpoint,
        note = string.IsNullOrWhiteSpace(upstreamMessage)
            ? "GitHub refused this traffic read (403). A classic PAT needs push on the repo; a "
              + "fine-grained PAT needs the 'Administration: read' permission."
            : upstreamMessage,
        hint = "classic PAT → needs push on the repo; fine-grained PAT → needs 'Administration: read'.",
    };

    /// <summary>The repo is too large for GitHub to compute code frequency (422). Not retryable.</summary>
    private static object CodeFrequencyTooLarge() => new
    {
        ok = false,
        status = 422,
        endpoint = "stats/code_frequency",
        note = "repository has too many commits for code-frequency stats",
    };

    // ── transport helpers ─────────────────────────────────────────────────────

    /// <summary>
    /// GET a <c>stats/*</c> endpoint, mapping <c>202</c> (and a 2xx with an empty body, which is
    /// the same "not ready" condition wearing a different status) to the retry envelope, and an
    /// optional <c>422</c> to <paramref name="onUnprocessable"/>. Every other non-2xx throws via
    /// <c>EnsureOkAsync</c>. A <c>204 No Content</c> — GitHub's answer for a repo with no
    /// activity — maps to <paramref name="empty"/> rather than being confused with "not ready".
    /// </summary>
    private async Task<object> GetStatsAsync(
        string endpoint,
        Func<JsonElement, object> map,
        Func<object> empty,
        Func<object>? onUnprocessable = null,
        CancellationToken ct = default)
    {
        using var resp = await http.GetAsync($"repos/{endpoint}", ct);

        if (resp.StatusCode == HttpStatusCode.Accepted)                       // 202 — cache warming
            return StatsComputing(endpoint);
        if (resp.StatusCode == HttpStatusCode.NoContent)                      // 204 — no activity
            return empty();
        if (onUnprocessable is not null && (int)resp.StatusCode == 422)       // 422 — too big to compute
            return onUnprocessable();

        await EnsureOkAsync(resp, ct);                                        // 404 & friends throw here

        var body = await resp.Content.ReadAsStringAsync(ct);
        if (string.IsNullOrWhiteSpace(body))                                  // 200 + empty body ⇒ not ready
            return StatsComputing(endpoint);

        using var doc = JsonDocument.Parse(body);
        return map(doc.RootElement);
    }

    /// <summary>
    /// GET a <c>traffic/*</c> endpoint, mapping <c>403</c> to a structured envelope carrying
    /// GitHub's own explanation instead of an exception. Every other non-2xx throws.
    /// </summary>
    private async Task<object> GetTrafficAsync(string endpoint, Func<JsonElement, object> map, CancellationToken ct)
    {
        using var resp = await http.GetAsync($"repos/{endpoint}", ct);

        if (resp.StatusCode == HttpStatusCode.Forbidden)
        {
            // Read GitHub's message so the caller learns WHICH permission is missing. Body is
            // best-effort: a malformed/absent body must not turn a clean 403 envelope into a throw.
            string upstream = "";
            try
            {
                var errBody = await resp.Content.ReadAsStringAsync(ct);
                using var errDoc = JsonDocument.Parse(errBody);
                if (errDoc.RootElement.TryGetProperty("message", out var m))
                    upstream = SinapsiForgeErrors.Sanitize(m.GetString() ?? "");
            }
            catch { /* leave upstream empty → the envelope falls back to the both-causes note */ }

            return TrafficForbidden(endpoint, upstream);
        }

        await EnsureOkAsync(resp, ct);

        var body = await resp.Content.ReadAsStringAsync(ct);
        if (string.IsNullOrWhiteSpace(body)) return map(default);

        using var doc = JsonDocument.Parse(body);
        return map(doc.RootElement);
    }

    // ── Traffic ───────────────────────────────────────────────────────────────
    //   GET repos/{o}/{r}/traffic/views?per=day|week     → {count, uniques, views:[…]}
    //   GET repos/{o}/{r}/traffic/clones?per=day|week    → {count, uniques, clones:[…]}
    //   GET repos/{o}/{r}/traffic/popular/referrers      → [{referrer, count, uniques}]
    //   GET repos/{o}/{r}/traffic/popular/paths          → [{path, title, count, uniques}]

    public Task<object> GetTrafficViewsAsync(string owner, string repo, string per, CancellationToken ct = default)
        => GetTrafficAsync($"{Esc(owner)}/{Esc(repo)}/traffic/views?per={Esc(per)}",
            e => new
            {
                owner,
                repo,
                per,
                count = Num(e, "count") ?? 0,
                uniques = Num(e, "uniques") ?? 0,
                views = MapTimeSeries(e, "views"),
            }, ct);

    public Task<object> GetTrafficClonesAsync(string owner, string repo, string per, CancellationToken ct = default)
        => GetTrafficAsync($"{Esc(owner)}/{Esc(repo)}/traffic/clones?per={Esc(per)}",
            e => new
            {
                owner,
                repo,
                per,
                count = Num(e, "count") ?? 0,
                uniques = Num(e, "uniques") ?? 0,
                clones = MapTimeSeries(e, "clones"),
            }, ct);

    public Task<object> GetTrafficReferrersAsync(string owner, string repo, CancellationToken ct = default)
        => GetTrafficAsync($"{Esc(owner)}/{Esc(repo)}/traffic/popular/referrers",
            e => new
            {
                owner,
                repo,
                referrers = Items(e).Select(r => new
                {
                    referrer = Str(r, "referrer"),
                    count = Num(r, "count") ?? 0,
                    uniques = Num(r, "uniques") ?? 0,
                }).ToList(),
            }, ct);

    public Task<object> GetTrafficPathsAsync(string owner, string repo, CancellationToken ct = default)
        => GetTrafficAsync($"{Esc(owner)}/{Esc(repo)}/traffic/popular/paths",
            e => new
            {
                owner,
                repo,
                paths = Items(e).Select(p => new
                {
                    path = Str(p, "path"),
                    title = Str(p, "title"),
                    count = Num(p, "count") ?? 0,
                    uniques = Num(p, "uniques") ?? 0,
                }).ToList(),
            }, ct);

    // ── Contributors & activity ───────────────────────────────────────────────

    /// <summary>GET repos/{o}/{r}/contributors — a plain paged list (NOT a stats/* cache).
    /// With <c>anon=1</c>, entries may carry <c>name</c>/<c>email</c> and no <c>login</c>.</summary>
    public async Task<object> ListContributorsAsync(string owner, string repo, bool anon, int limit, CancellationToken ct = default)
    {
        var doc = await GetJsonAsync(
            $"repos/{Esc(owner)}/{Esc(repo)}/contributors?anon={(anon ? "1" : "0")}&per_page={limit}", ct);
        return new
        {
            owner,
            repo,
            anon,
            contributors = Items(doc).Take(limit).Select(c => new
            {
                login = Str(c, "login"),
                id = Num(c, "id"),
                type = Str(c, "type"),
                name = Str(c, "name"),        // anonymous entries only
                email = Str(c, "email"),      // anonymous entries only
                contributions = Num(c, "contributions") ?? 0,
                html_url = Str(c, "html_url"),
                avatar_url = Str(c, "avatar_url"),
            }).ToList(),
        };
    }

    /// <summary>GET repos/{o}/{r}/stats/contributors → [{author, total, weeks:[{w,a,d,c}]}]</summary>
    public Task<object> GetContributorStatsAsync(string owner, string repo, CancellationToken ct = default)
        => GetStatsAsync($"{Esc(owner)}/{Esc(repo)}/stats/contributors",
            e => new
            {
                owner,
                repo,
                contributors = Items(e).Select(c => new
                {
                    login = c.TryGetProperty("author", out var a) ? Str(a, "login") : null,
                    id = c.TryGetProperty("author", out var a2) ? Num(a2, "id") : null,
                    total = Num(c, "total") ?? 0,
                    weeks = (c.TryGetProperty("weeks", out var w) && w.ValueKind == JsonValueKind.Array
                        ? w.EnumerateArray()
                        : Enumerable.Empty<JsonElement>()).Select(x => new
                        {
                            week_start_unix = Num(x, "w") ?? 0,
                            additions = Num(x, "a") ?? 0,
                            deletions = Num(x, "d") ?? 0,
                            commits = Num(x, "c") ?? 0,
                        }).ToList(),
                }).ToList(),
            },
            empty: () => new { owner, repo, contributors = Array.Empty<object>() },
            ct: ct);

    /// <summary>GET repos/{o}/{r}/stats/commit_activity → [{days:[7], total, week}]</summary>
    public Task<object> GetCommitActivityAsync(string owner, string repo, CancellationToken ct = default)
        => GetStatsAsync($"{Esc(owner)}/{Esc(repo)}/stats/commit_activity",
            e => new
            {
                owner,
                repo,
                weeks = Items(e).Select(w => new
                {
                    week_start_unix = Num(w, "week") ?? 0,
                    total = Num(w, "total") ?? 0,
                    days = Longs(w, "days"),      // Sunday-first, 7 entries
                }).ToList(),
            },
            empty: () => new { owner, repo, weeks = Array.Empty<object>() },
            ct: ct);

    /// <summary>GET repos/{o}/{r}/stats/code_frequency → [[week_unix, additions, deletions]]
    /// (deletions are NEGATIVE upstream; kept verbatim so the caller sees GitHub's own values).</summary>
    public Task<object> GetCodeFrequencyAsync(string owner, string repo, CancellationToken ct = default)
        => GetStatsAsync($"{Esc(owner)}/{Esc(repo)}/stats/code_frequency",
            e => new
            {
                owner,
                repo,
                weeks = Items(e)
                    .Where(t => t.ValueKind == JsonValueKind.Array && t.GetArrayLength() >= 3)
                    .Select(t => new
                    {
                        week_start_unix = AsLong(t[0]),
                        additions = AsLong(t[1]),
                        deletions = AsLong(t[2]),     // negative, as GitHub reports it
                    }).ToList(),
            },
            empty: () => new { owner, repo, weeks = Array.Empty<object>() },
            onUnprocessable: CodeFrequencyTooLarge,
            ct: ct);

    /// <summary>GET repos/{o}/{r}/stats/participation → {all:[52], owner:[52]}</summary>
    public Task<object> GetParticipationAsync(string owner, string repo, CancellationToken ct = default)
        => GetStatsAsync($"{Esc(owner)}/{Esc(repo)}/stats/participation",
            e => new
            {
                owner,
                repo,
                all = Longs(e, "all"),          // oldest week first, 52 entries
                owner_only = Longs(e, "owner"),
            },
            empty: () => new { owner, repo, all = Array.Empty<long>(), owner_only = Array.Empty<long>() },
            ct: ct);

    /// <summary>GET repos/{o}/{r}/stats/punch_card → [[day, hour, commits]] (day 0 = Sunday).</summary>
    public Task<object> GetPunchCardAsync(string owner, string repo, CancellationToken ct = default)
        => GetStatsAsync($"{Esc(owner)}/{Esc(repo)}/stats/punch_card",
            e => new
            {
                owner,
                repo,
                punches = Items(e)
                    .Where(t => t.ValueKind == JsonValueKind.Array && t.GetArrayLength() >= 3)
                    .Select(t => new
                    {
                        day = AsLong(t[0]),      // 0 = Sunday
                        hour = AsLong(t[1]),     // 0..23
                        commits = AsLong(t[2]),
                    }).ToList(),
            },
            empty: () => new { owner, repo, punches = Array.Empty<object>() },
            ct: ct);

    // ── Community / dependencies / languages ──────────────────────────────────

    /// <summary>GET repos/{o}/{r}/community/profile → health percentage + which community files exist.</summary>
    public async Task<object> GetCommunityProfileAsync(string owner, string repo, CancellationToken ct = default)
    {
        var e = await GetJsonAsync($"repos/{Esc(owner)}/{Esc(repo)}/community/profile", ct);
        var files = e.TryGetProperty("files", out var f) ? f : default;
        return new
        {
            owner,
            repo,
            health_percentage = Num(e, "health_percentage") ?? 0,
            description = Str(e, "description"),
            documentation = Str(e, "documentation"),
            updated_at = Str(e, "updated_at"),
            content_reports_count = Num(e, "content_reports_count"),
            files = new
            {
                readme = FileUrl(files, "readme"),
                license = FileUrl(files, "license"),
                contributing = FileUrl(files, "contributing"),
                code_of_conduct = FileUrl(files, "code_of_conduct"),
                issue_template = FileUrl(files, "issue_template"),
                pull_request_template = FileUrl(files, "pull_request_template"),
            },
        };
    }

    /// <summary>GET repos/{o}/{r}/dependency-graph/sbom → {sbom:{…SPDX…}}.
    /// The package list is capped at <paramref name="limit"/>; <c>package_count</c> always
    /// reports the FULL size so a truncated answer is legible as truncated.</summary>
    public async Task<object> GetSbomAsync(string owner, string repo, int limit, CancellationToken ct = default)
    {
        var doc = await GetJsonAsync($"repos/{Esc(owner)}/{Esc(repo)}/dependency-graph/sbom", ct);
        var sbom = doc.TryGetProperty("sbom", out var s) ? s : doc;
        var packages = (sbom.ValueKind == JsonValueKind.Object && sbom.TryGetProperty("packages", out var p) && p.ValueKind == JsonValueKind.Array
            ? p.EnumerateArray().ToList()
            : []);
        return new
        {
            owner,
            repo,
            spdx_id = Str(sbom, "SPDXID"),
            spdx_version = Str(sbom, "spdxVersion"),
            name = Str(sbom, "name"),
            data_license = Str(sbom, "dataLicense"),
            document_namespace = Str(sbom, "documentNamespace"),
            package_count = packages.Count,
            truncated = packages.Count > limit,
            packages = packages.Take(limit).Select(pkg => new
            {
                name = Str(pkg, "name"),
                version = Str(pkg, "versionInfo"),
                license = Str(pkg, "licenseConcluded") ?? Str(pkg, "licenseDeclared"),
                supplier = Str(pkg, "supplier"),
                purl = ExternalRef(pkg, "purl"),
            }).ToList(),
        };
    }

    /// <summary>GET repos/{o}/{r}/forks?sort=&amp;per_page= → the fork list.</summary>
    public async Task<object> ListForksAsync(string owner, string repo, string sort, int limit, CancellationToken ct = default)
    {
        var doc = await GetJsonAsync($"repos/{Esc(owner)}/{Esc(repo)}/forks?sort={Esc(sort)}&per_page={limit}", ct);
        return new
        {
            owner,
            repo,
            sort,
            forks = Items(doc).Take(limit).Select(f => new
            {
                full_name = Str(f, "full_name"),
                owner = f.TryGetProperty("owner", out var o) ? Str(o, "login") : null,
                html_url = Str(f, "html_url"),
                description = Str(f, "description"),
                @private = Bool(f, "private"),
                stars = Num(f, "stargazers_count") ?? 0,
                forks = Num(f, "forks_count") ?? 0,
                created_at = Str(f, "created_at"),
                updated_at = Str(f, "updated_at"),
            }).ToList(),
        };
    }

    /// <summary>GET repos/{o}/{r}/languages → {"C#": 12345, …} bytes of source per language.</summary>
    public async Task<object> GetLanguagesAsync(string owner, string repo, CancellationToken ct = default)
    {
        var doc = await GetJsonAsync($"repos/{Esc(owner)}/{Esc(repo)}/languages", ct);
        var languages = doc.ValueKind == JsonValueKind.Object
            ? doc.EnumerateObject()
                 .Where(p => p.Value.ValueKind == JsonValueKind.Number)
                 .ToDictionary(p => p.Name, p => p.Value.GetInt64())
            : new Dictionary<string, long>();
        return new { owner, repo, languages, total_bytes = languages.Values.Sum() };
    }

    // ── local mapping helpers ─────────────────────────────────────────────────

    /// <summary>Enumerate a JSON array, tolerating a non-array (⇒ empty).</summary>
    private static IEnumerable<JsonElement> Items(JsonElement e)
        => e.ValueKind == JsonValueKind.Array ? e.EnumerateArray() : [];

    /// <summary>A traffic views/clones series: [{timestamp, count, uniques}].</summary>
    private static List<object> MapTimeSeries(JsonElement e, string prop)
        => (e.ValueKind == JsonValueKind.Object && e.TryGetProperty(prop, out var arr) ? Items(arr) : [])
            .Select(x => (object)new
            {
                timestamp = Str(x, "timestamp"),
                count = Num(x, "count") ?? 0,
                uniques = Num(x, "uniques") ?? 0,
            }).ToList();

    /// <summary>A numeric array property → long[] (missing/!array ⇒ empty).</summary>
    private static long[] Longs(JsonElement e, string prop)
        => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(prop, out var arr) && arr.ValueKind == JsonValueKind.Array
            ? arr.EnumerateArray().Select(AsLong).ToArray()
            : [];

    private static long AsLong(JsonElement e)
        => e.ValueKind == JsonValueKind.Number && e.TryGetInt64(out var v) ? v : 0;

    /// <summary>A community-profile file slot → its html_url, or null when the file is absent.</summary>
    private static string? FileUrl(JsonElement files, string name)
        => files.ValueKind == JsonValueKind.Object && files.TryGetProperty(name, out var f) && f.ValueKind == JsonValueKind.Object
            ? Str(f, "html_url") ?? Str(f, "url")
            : null;

    /// <summary>Pull one externalRefs entry (e.g. the Package-URL) out of an SPDX package.</summary>
    private static string? ExternalRef(JsonElement pkg, string refType)
        => pkg.ValueKind == JsonValueKind.Object && pkg.TryGetProperty("externalRefs", out var refs) && refs.ValueKind == JsonValueKind.Array
            ? refs.EnumerateArray()
                  .Where(r => string.Equals(Str(r, "referenceType"), refType, StringComparison.OrdinalIgnoreCase))
                  .Select(r => Str(r, "referenceLocator"))
                  .FirstOrDefault()
            : null;
}
