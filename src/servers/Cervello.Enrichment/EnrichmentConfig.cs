using Npgsql;

namespace Cervello.Enrichment;

/// <summary>
/// Runtime config for the enrichment engine, env-driven and fail-closed (mirrors
/// <c>Cervello.Watcher</c>'s <c>WatcherConfig</c>): a bad numeric / range / URL value throws at
/// startup naming the offending env var, rather than silently honouring a footgun. No host, path,
/// or credential is baked into source — every value defaults to a neutral local placeholder and is
/// overridden by the matching environment variable.
///
/// <para><b>Secrets are agent-free (L1 boundary).</b> The brain-api / gateway bearer is MINTED at
/// runtime on-CT by <c>Sinapsi.AgentJwt.AgentJwtMinter</c> from a JWK provisioned by the
/// deploy-controller / Infisical <c>/ct146/cervello/</c> pattern; the Postgres password is injected
/// the same way. NEITHER ever appears in source, in this config's defaults, or in agent context —
/// this type only carries the NON-secret coordinates (endpoints, agent name, DSN parts sans
/// password). The password arrives only via <c>CERVELLO_DB_PASSWORD</c> at deploy.</para>
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

    /// <summary>The scoped agentgateway machine identity the engine mints a bearer for (brain-api/CT126/gateway).</summary>
    public required string EnrichmentAgent { get; init; }

    // ── CT126 speaches (base transcription + selective re-ASR) ───────────────────
    /// <summary>Base URL of the CT126 speaches service (:8000) for base transcription + re-ASR.</summary>
    public required string Ct126BaseUrl { get; init; }

    /// <summary>Correct-language config handed to CT126 base transcription (e.g. <c>fr</c>, <c>en</c>).</summary>
    public required string TranscribeLanguage { get; init; }

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
    /// Map-PR writer DRY-RUN. TRUE (default) = assemble + self-lint + log the PR but DO NOT open a
    /// real forgejo PR (the L1 boundary: no real map-PRs). FALSE = open the live review-PR (L2, on-CT).
    /// </summary>
    public required bool MapPrDryRun { get; init; }

    // ── open-points MCP bearer gate ──────────────────────────────────────────────
    /// <summary>Whether the open-points auth gate is enabled (always true in prod; a test may disable).</summary>
    public required bool OpenPointsAuthEnabled { get; init; }

    // ── HTTP ceilings ────────────────────────────────────────────────────────────
    /// <summary>Per-request ceiling (seconds) applied to the outbound HttpClients.</summary>
    public required int HttpTimeoutSeconds { get; init; }

    // ── defaults (fail-closed) ───────────────────────────────────────────────────
    internal const string DefaultBrainApiBaseUrl = "http://127.0.0.1:8081";
    internal const string DefaultEnrichmentAgent = "agent-cervello-enrichment";
    internal const string DefaultCt126BaseUrl = "http://10.42.0.126:8000";
    internal const string DefaultTranscribeLanguage = "fr";
    internal const string DefaultForgejoBaseUrl = "https://forgejo.insalata-fresca.ch";
    internal const string DefaultForgejoRepo = "ste/cervello";
    internal const string DefaultForgejoBaseBranch = "main";

    internal const int DefaultHttpTimeoutSeconds = 100;
    internal const int MaxHttpTimeoutSeconds = 3_600;

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
            Ct126BaseUrl = ReadHttpUrl(getEnv, "CERVELLO_CT126_BASE_URL", DefaultCt126BaseUrl),
            TranscribeLanguage = Env("CERVELLO_TRANSCRIBE_LANGUAGE", DefaultTranscribeLanguage),
            BaseReTranscribeEnabled = ReadBool(getEnv, "CERVELLO_BASE_RETRANSCRIBE_ENABLED", false),
            ReAsrEnabled = ReadBool(getEnv, "CERVELLO_REASR_ENABLED", false),
            PostgresDsn = ReadPostgresDsn(getEnv),
            ForgejoBaseUrl = ReadHttpUrl(getEnv, "CERVELLO_FORGEJO_BASE_URL", DefaultForgejoBaseUrl),
            ForgejoRepo = Env("CERVELLO_FORGEJO_REPO", DefaultForgejoRepo),
            ForgejoBaseBranch = Env("CERVELLO_FORGEJO_BASE_BRANCH", DefaultForgejoBaseBranch),
            MapPrDryRun = ReadBool(getEnv, "CERVELLO_MAP_PR_DRY_RUN", true),
            OpenPointsAuthEnabled = ReadBool(getEnv, "CERVELLO_OPEN_POINTS_AUTH_ENABLED", true),
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
}
