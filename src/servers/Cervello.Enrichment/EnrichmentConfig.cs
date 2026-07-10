using Npgsql;

namespace Cervello.Enrichment;

/// <summary>
/// Runtime config for the enrichment engine, env-driven and fail-closed (mirrors
/// <c>Cervello.Watcher</c>'s <c>WatcherConfig</c>): a bad numeric / range / URL value throws at
/// startup naming the offending env var, rather than silently honouring a footgun. No host, path,
/// or credential is baked into source — every value defaults to a neutral local placeholder and is
/// overridden by the matching environment variable.
///
/// <para><b>Secrets are agent-free (L1 boundary).</b> The CT126 / forgejo bearer is MINTED at
/// runtime on-CT by <c>Sinapsi.AgentJwt.AgentJwtMinter</c> from a JWK provisioned by the
/// deploy-controller / Infisical <c>/ct146/cervello/</c> pattern; the brain-api bearer is the STATIC
/// <see cref="BrainBearerToken"/> (equal to brain-api's <c>BRAIN_BEARER_TOKEN</c>, fetched agent-free
/// from Infisical <c>/ct121/homelab-state-mcp</c>); the Postgres password is injected the same way.
/// NONE ever appears in source, in this config's defaults, or in agent context — this type only
/// carries the NON-secret coordinates (endpoints, agent name, DSN parts sans password) plus the two
/// secret slots that arrive only via their env vars at deploy (<c>CERVELLO_BRAIN_BEARER_TOKEN</c> /
/// <c>CERVELLO_DB_PASSWORD</c>).</para>
///
/// <para><b>Phase gate (escalate-only by default).</b> <see cref="GradedAutoApply"/> defaults to
/// <c>false</c> — the decision policy stays escalate-only (every band → open-point) until the
/// operator's E5 held-out validation passes and they explicitly flip
/// <c>CERVELLO_GRADED_AUTO_APPLY=true</c>. This is the design gate: "batch auto-apply stays dark
/// until a re-fit on the real enrollment set validates on held-out recordings" (DESIGN Decisions).</para>
/// </summary>
public sealed record EnrichmentConfig
{
    // ── seams: live vs fake selection (DI composition) ───────────────────────────
    /// <summary>
    /// When true, the DI root wires the LIVE adapters (brain-api / CT126 / CT146 pgvector /
    /// forgejo map-PR). When false (default), it wires the in-memory fakes — the offline slice /
    /// tests. Never a code change to switch: this is the config flag the design mandates.
    /// </summary>
    public required bool UseLiveAdapters { get; init; }

    /// <summary>
    /// Graded auto-apply gate. FALSE (default) = escalate-only: the decision policy sends every
    /// band to an open-point (no auto-write), regardless of cosine. TRUE = the P5 graded bands
    /// (auto ≥0.62 / review / reject) — enabled ONLY after the operator's held-out validation.
    /// </summary>
    public required bool GradedAutoApply { get; init; }

    // ── brain-api (diarize-embed sidecar proxy + correction LLM) ─────────────────
    /// <summary>Base URL of the CT139 brain-api (the diarize-embed proxy + correction routes).</summary>
    public required string BrainApiBaseUrl { get; init; }

    /// <summary>The scoped agentgateway machine identity the engine mints a bearer for (CT126 / forgejo egress).</summary>
    public required string EnrichmentAgent { get; init; }

    /// <summary>
    /// The STATIC brain-api bearer (<c>CERVELLO_BRAIN_BEARER_TOKEN</c>) presented on the three
    /// <c>/v1/enrich/*</c> routes (diarize-embed / correct / derive-facts). It MUST equal brain-api's
    /// own <c>BRAIN_BEARER_TOKEN</c> — those routes validate by plain string-equality against that
    /// static token (the minted <c>agent-cervello-enrichment</c> JWT is NOT accepted there; that
    /// audience is wired only to <c>/v1/sessions</c>). Injected agent-free at deploy from Infisical
    /// (<c>/ct121/homelab-state-mcp/BRAIN_BEARER_TOKEN</c>), NEVER committed / in the image / in agent
    /// context. Empty in fake mode (no live egress); the composition root FAILS CLOSED on an empty
    /// value in live mode (a mis-provisioned deploy never presents an empty brain bearer). CT126 +
    /// forgejo egress keep using the minted JWT (see <see cref="EnrichmentAgent"/>).
    /// <para>Scoped-JWT-for-enrich (per-route audience acceptance on brain-api) is a noted future
    /// hardening; today the static bearer is the smallest by-the-books fix.</para>
    /// </summary>
    public required string BrainBearerToken { get; init; }

    // ── CT126 speaches (base transcription + selective re-ASR) ───────────────────
    /// <summary>Base URL of the CT126 speaches service (:8000) for base transcription + re-ASR.</summary>
    public required string Ct126BaseUrl { get; init; }

    /// <summary>
    /// Correct-language config handed to CT126 base transcription (e.g. <c>fr</c>, <c>en</c>), or
    /// <c>auto</c> (the default) to OMIT the language field entirely so speaches auto-detects it per
    /// recording — the corpus is multilingual (Italian-dominant + French/other), so a single forced
    /// language mis-transcribes most of it. <see cref="Adapters.Ct126TranscribeClient"/> treats
    /// null/empty/<c>auto</c> identically.
    /// </summary>
    public required string TranscribeLanguage { get; init; }

    /// <summary>
    /// The speaches model id (<c>CERVELLO_TRANSCRIBE_MODEL</c>) sent as the REQUIRED <c>model</c>
    /// multipart field on <c>POST /v1/audio/transcriptions</c> — speaches is OpenAI-compatible and
    /// rejects a request with no <c>model</c> field with HTTP 422 Unprocessable Entity. Must match a
    /// model speaches has actually loaded on CT126 (default: <c>Systran/faster-whisper-large-v3</c>).
    /// </summary>
    public required string TranscribeModel { get; init; }

    /// <summary>
    /// OPTIONAL CT126 base RE-TRANSCRIPTION fallback. FALSE (default) = the ratified posture: the
    /// Google <c>.txt</c> IS the base and the engine NEVER re-transcribes the audio from scratch, so
    /// CT126 is not a hard dependency of a full drain. TRUE = if (and only if) a recording carries no
    /// Google <c>.txt</c>, fall back to CT126 base transcription. Flipping it is a config change
    /// (<c>CERVELLO_BASE_RETRANSCRIBE_ENABLED=true</c>), never a code change.
    /// </summary>
    public required bool BaseReTranscribeEnabled { get; init; }

    /// <summary>
    /// OPTIONAL selective RE-ASR of garbled spans (CT126). FALSE (default) = a garbled span is left
    /// as-is (omitted, never guessed) and the drain completes without CT126 — re-ASR is a LATER
    /// quality enhancement, not a drain dependency. TRUE = re-ASR garbled spans for correction
    /// evidence; even then a CT126 failure gracefully skips the span (never fails the drain).
    /// Enabling it is a config change (<c>CERVELLO_REASR_ENABLED=true</c>), never a code change.
    /// </summary>
    public required bool ReAsrEnabled { get; init; }

    // ── CT146 Postgres (voiceprints / attributions / open-points / correction-map) ─
    /// <summary>Npgsql DSN for the on-CT cervello Postgres (pgvector). Password injected agent-free.</summary>
    public required string PostgresDsn { get; init; }

    // ── forgejo map-PR writer (ste/cervello review-PR) ───────────────────────────
    /// <summary>Base URL of the forgejo (CT119) API the map-PR writer opens branches + PRs against.</summary>
    public required string ForgejoBaseUrl { get; init; }

    /// <summary>The <c>owner/repo</c> the map review-PR targets (default <c>ste/cervello</c>).</summary>
    public required string ForgejoRepo { get; init; }

    /// <summary>The base branch the map review-PR is opened against (default <c>main</c>).</summary>
    public required string ForgejoBaseBranch { get; init; }

    /// <summary>
    /// The STATIC forgejo access token (<c>CERVELLO_FORGE_REPO_TOKEN</c>) presented on every forgejo
    /// egress (the searchable-transcript git push AND the map-PR writer — both tag audience
    /// <c>forgejo</c>). Forgejo's REST API validates a native forgejo access token, NOT a Zitadel OIDC
    /// JWT, so the minted <c>agent-cervello-enrichment</c> JWT is REJECTED there with 401 — the engine
    /// must present this real forgejo token instead. It MUST equal cervello's forgejo write token
    /// (Infisical <c>/ct146/cervello/FORGE_REPO_TOKEN</c>, the map-PR writer's token). Injected
    /// agent-free at deploy, NEVER committed / in the image / in agent context. Empty in fake mode (no
    /// live egress); the composition root FAILS CLOSED on an empty value in live mode (a mis-provisioned
    /// deploy never presents an empty forgejo token). CT126 egress keeps the minted JWT (its own
    /// <c>ct126-speaches</c> audience routes through the fall-through).
    /// <para>Scoped-JWT-for-forgejo (per-route audience acceptance on forgejo) is a noted future
    /// hardening; today the static bearer is the smallest by-the-books fix (same pattern as the
    /// brain-api audience's <see cref="BrainBearerToken"/>).</para>
    /// </summary>
    public required string ForgejoRepoToken { get; init; }

    /// <summary>
    /// Map-PR writer DRY-RUN. TRUE (default) = assemble + self-lint + log the PR but DO NOT open a
    /// real forgejo PR (the L1 boundary: no real map-PRs). FALSE = open the live review-PR (L2, on-CT).
    /// </summary>
    public required bool MapPrDryRun { get; init; }

    // ── open-points MCP bearer gate ──────────────────────────────────────────────
    /// <summary>Whether the open-points auth gate is enabled (always true in prod; a test may disable).</summary>
    public required bool OpenPointsAuthEnabled { get; init; }

    // ── cervello indexer (:8009 hybrid FTS+semantic search — the pack + cervello_search reuse) ─────
    /// <summary>Base URL of the cervello indexer's hybrid <c>GET /search</c> (design §2.2/§5.2). CT146 :8009.</summary>
    public required string IndexerBaseUrl { get; init; }

    /// <summary>
    /// The STATIC indexer search bearer (<c>CERVELLO_INDEXER_SEARCH_TOKEN</c> == the indexer's
    /// <c>INDEXER_SEARCH_TOKEN</c>). Injected agent-free at deploy, never committed. Empty in fake mode
    /// (no live search); the pack assembler degrades gracefully (coverage.gaps) if search is unavailable.
    /// </summary>
    public required string IndexerSearchToken { get; init; }

    /// <summary>Default context-pack char budget (design §5.1: 30000; MC Q7: a sane bounded default, tunable).</summary>
    public required int PackBudgetDefault { get; init; }

    // ── voiceprint naming — V1 one-shot review clustering ────────────────────────
    /// <summary>
    /// The cross-recording review-cluster cosine cutoff (<c>CERVELLO_VOICE_REVIEW_CUT</c>) for
    /// <see cref="Pipeline.VoiceReviewClusterer"/> (design <c>ste/cervello</c>
    /// <c>docs/design/voiceprint-naming.md</c> §7 phase V1). Deliberately configurable, not a
    /// constant: the design notes cross-recording same-speaker cosine runs lower/noisier than the
    /// within-recording merge cutoff and MUST be fit on the operator's real corpus at P5, not
    /// assumed. Default 0.58 is a starting point.
    /// </summary>
    public required double VoiceReviewCutoff { get; init; }

    // ── HTTP ceilings ────────────────────────────────────────────────────────────
    /// <summary>Per-request ceiling (seconds) applied to the outbound HttpClients.</summary>
    public required int HttpTimeoutSeconds { get; init; }

    // ── defaults (fail-closed) ───────────────────────────────────────────────────
    internal const string DefaultBrainApiBaseUrl = "http://127.0.0.1:8081";
    internal const string DefaultEnrichmentAgent = "agent-cervello-enrichment";
    internal const string DefaultCt126BaseUrl = "http://10.42.0.126:8000";
    internal const string DefaultTranscribeLanguage = "auto";
    internal const string DefaultTranscribeModel = "Systran/faster-whisper-large-v3";
    internal const string DefaultForgejoBaseUrl = "https://forgejo.insalata-fresca.ch";
    internal const string DefaultForgejoRepo = "ste/cervello";
    internal const string DefaultForgejoBaseBranch = "main";
    internal const string DefaultIndexerBaseUrl = "http://127.0.0.1:8009";

    internal const int DefaultHttpTimeoutSeconds = 100;
    internal const int MaxHttpTimeoutSeconds = 3_600;
    internal const int DefaultPackBudget = 30_000;
    internal const int MinPackBudget = 2_000;
    internal const int MaxPackBudget = 200_000;

    /// <summary>Read config from the process environment (production path).</summary>
    public static EnrichmentConfig FromEnvironment() => From(Environment.GetEnvironmentVariable);

    /// <summary>
    /// Read config from an INJECTABLE env source (test-isolation: tests pass a LOCAL map instead of
    /// mutating the process environment). <see cref="FromEnvironment"/> supplies
    /// <c>Environment.GetEnvironmentVariable</c>.
    /// </summary>
    public static EnrichmentConfig From(Func<string, string?> getEnv)
    {
        string Env(string k, string dflt) => getEnv(k) is { Length: > 0 } v ? v : dflt;

        return new EnrichmentConfig
        {
            UseLiveAdapters = ReadBool(getEnv, "CERVELLO_USE_LIVE_ADAPTERS", false),
            GradedAutoApply = ReadBool(getEnv, "CERVELLO_GRADED_AUTO_APPLY", false),
            BrainApiBaseUrl = ReadHttpUrl(getEnv, "CERVELLO_BRAIN_API_BASE_URL", DefaultBrainApiBaseUrl),
            EnrichmentAgent = Env("CERVELLO_ENRICHMENT_AGENT", DefaultEnrichmentAgent),
            // The static brain-api bearer. Empty default (fake mode needs none); the composition root
            // fails closed on an empty value in LIVE mode. Never a baked-in secret.
            BrainBearerToken = getEnv("CERVELLO_BRAIN_BEARER_TOKEN") ?? "",
            Ct126BaseUrl = ReadHttpUrl(getEnv, "CERVELLO_CT126_BASE_URL", DefaultCt126BaseUrl),
            TranscribeLanguage = Env("CERVELLO_TRANSCRIBE_LANGUAGE", DefaultTranscribeLanguage),
            TranscribeModel = Env("CERVELLO_TRANSCRIBE_MODEL", DefaultTranscribeModel),
            BaseReTranscribeEnabled = ReadBool(getEnv, "CERVELLO_BASE_RETRANSCRIBE_ENABLED", false),
            ReAsrEnabled = ReadBool(getEnv, "CERVELLO_REASR_ENABLED", false),
            PostgresDsn = ReadPostgresDsn(getEnv),
            ForgejoBaseUrl = ReadHttpUrl(getEnv, "CERVELLO_FORGEJO_BASE_URL", DefaultForgejoBaseUrl),
            ForgejoRepo = Env("CERVELLO_FORGEJO_REPO", DefaultForgejoRepo),
            ForgejoBaseBranch = Env("CERVELLO_FORGEJO_BASE_BRANCH", DefaultForgejoBaseBranch),
            // The static forgejo access token. Empty default (fake mode needs none); the composition
            // root fails closed on an empty value in LIVE mode. Never a baked-in secret.
            ForgejoRepoToken = getEnv("CERVELLO_FORGE_REPO_TOKEN") ?? "",
            MapPrDryRun = ReadBool(getEnv, "CERVELLO_MAP_PR_DRY_RUN", true),
            OpenPointsAuthEnabled = ReadBool(getEnv, "CERVELLO_OPEN_POINTS_AUTH_ENABLED", true),
            IndexerBaseUrl = ReadHttpUrl(getEnv, "CERVELLO_INDEXER_BASE_URL", DefaultIndexerBaseUrl),
            IndexerSearchToken = getEnv("CERVELLO_INDEXER_SEARCH_TOKEN") ?? "",
            PackBudgetDefault = ReadBoundedInt(getEnv, "CERVELLO_PACK_BUDGET_DEFAULT", DefaultPackBudget, MinPackBudget, MaxPackBudget),
            VoiceReviewCutoff = ReadBoundedDouble(
                getEnv, "CERVELLO_VOICE_REVIEW_CUT", Pipeline.VoiceReviewClusterer.DefaultReviewCutoff, 0.0, 1.0),
            HttpTimeoutSeconds = ReadBoundedInt(getEnv,
                "CERVELLO_ENRICHMENT_HTTP_TIMEOUT_SECONDS", DefaultHttpTimeoutSeconds, 1, MaxHttpTimeoutSeconds),
        };
    }

    /// <summary>Convenience overload: read from a LOCAL dictionary (tests).</summary>
    public static EnrichmentConfig From(IReadOnlyDictionary<string, string?> env) =>
        From(k => env.TryGetValue(k, out var v) ? v : null);

    /// <summary>Fail-closed bool: only <c>true</c>/<c>false</c> (case-insensitive) accepted; else throws.</summary>
    private static bool ReadBool(Func<string, string?> getEnv, string envVar, bool dflt)
    {
        var raw = getEnv(envVar);
        if (string.IsNullOrEmpty(raw))
            return dflt;
        if (bool.TryParse(raw, out var v))
            return v;
        throw new InvalidOperationException(
            $"{envVar}='{raw}' is invalid: expected 'true' or 'false' (default {dflt}).");
    }

    /// <summary>Fail-closed http(s) url: an unparseable / non-http(s) value throws naming the var.</summary>
    private static string ReadHttpUrl(Func<string, string?> getEnv, string envVar, string dflt)
    {
        var raw = getEnv(envVar);
        if (string.IsNullOrEmpty(raw))
            return dflt;
        if (!Uri.TryCreate(raw, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            throw new InvalidOperationException(
                $"{envVar}='{raw}' is invalid: expected an absolute http(s) URL (default {dflt}).");
        return raw;
    }

    /// <summary>
    /// Build the Postgres DSN either from a full DSN env var, or from discrete CERVELLO_DB_* parts
    /// with a neutral local default (mirrors WatcherConfig.ReadPostgresDsn). The password is read
    /// from <c>CERVELLO_DB_PASSWORD</c> ONLY — injected agent-free at deploy, never committed.
    /// </summary>
    private static string ReadPostgresDsn(Func<string, string?> getEnv)
    {
        string Env(string k, string dflt) => getEnv(k) is { Length: > 0 } v ? v : dflt;

        var dsn = getEnv("CERVELLO_ENRICHMENT_DB_DSN");
        if (!string.IsNullOrEmpty(dsn))
            return dsn;
        return new NpgsqlConnectionStringBuilder
        {
            Host = Env("CERVELLO_DB_HOST", "127.0.0.1"),
            Port = ReadBoundedInt(getEnv, "CERVELLO_DB_PORT", 5432, 1, 65_535),
            Database = Env("CERVELLO_DB_NAME", "cervello"),
            Username = Env("CERVELLO_DB_USER", "cervello"),
            Password = getEnv("CERVELLO_DB_PASSWORD") ?? "",
            SslMode = SslMode.Prefer,
            Pooling = true,
            MaxPoolSize = 10,
            Timeout = 15,
        }.ConnectionString;
    }

    /// <summary>Fail-closed bounded int: non-numeric / out-of-range throws naming the var.</summary>
    private static int ReadBoundedInt(Func<string, string?> getEnv, string envVar, int dflt, int min, int max)
    {
        var raw = getEnv(envVar);
        if (string.IsNullOrEmpty(raw))
            return dflt;
        if (!int.TryParse(raw, out var v) || v < min || v > max)
            throw new InvalidOperationException(
                $"{envVar}='{raw}' is invalid: expected an integer in {min}..{max} (default {dflt}).");
        return v;
    }

    /// <summary>Fail-closed bounded double: non-numeric / out-of-range throws naming the var.</summary>
    private static double ReadBoundedDouble(Func<string, string?> getEnv, string envVar, double dflt, double min, double max)
    {
        var raw = getEnv(envVar);
        if (string.IsNullOrEmpty(raw))
            return dflt;
        if (!double.TryParse(raw, System.Globalization.CultureInfo.InvariantCulture, out var v) || v < min || v > max)
            throw new InvalidOperationException(
                $"{envVar}='{raw}' is invalid: expected a number in {min}..{max} (default {dflt}).");
        return v;
    }
}
