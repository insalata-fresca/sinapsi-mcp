namespace Cervello.Enrichment.Ports;

/// <summary>
/// Port for publishing the SEARCHABLE SUBSTRATE (verbatim transcripts + enrichment bundles +
/// the recordings manifest) from the CT-local <c>ste/cervello</c> working tree into
/// <c>ste/cervello</c> git, so the strictly-git-sourced indexer (<c>:8009</c>) can index it and
/// <c>cervello_search</c> / <c>cervello_context_pack</c> return real recording content (DESIGN §3
/// lists <c>recordings/transcripts/</c> + <c>recordings/manifest.yaml</c> + <c>inbox/</c> bundles as
/// git paths — only AUDIO + VOICEPRINTS never enter git, LINT R7).
///
/// <para><b>Independent of the map-PR dry-run gate.</b> This publishes the grounded, NON-attribution
/// artifacts (transcripts/manifest/bundles carry no speaker attribution — they cite
/// <c>rec://</c>/<c>drive://</c> sources and pass cervello-lint R1/R4). It is a SEPARATE concern from
/// <see cref="IMapPrWriter"/> (the <c>map/</c> attribution review-PR, which stays dry-run by default):
/// searchability does not wait on the map-PR posture. Speaker attributions still escalate to
/// open-points and never auto-write to <c>map/</c>.</para>
///
/// <para><b>Confinement (LINT R7 preserved).</b> Only derived TEXT (transcript markdown, bundle
/// json/md, manifest yaml) is published — never an audio blob, never a biometric voiceprint vector.
/// The push targets the PRIVATE <c>ste/cervello</c> repo (cervello's own data plane), off the shared
/// NATS surface.</para>
/// </summary>
public interface IGitPublisher
{
    /// <summary>
    /// Commit + push the given repo-relative files (read from the CT working tree) to
    /// <c>ste/cervello</c> git. Idempotent per content: an unchanged file is a no-op; a changed one
    /// is updated in place (create-or-update by sha). Returns the result (files pushed / skipped).
    /// A no-op publisher (dry / fake) returns <see cref="GitPublishResult.NoOp"/>.
    /// </summary>
    Task<GitPublishResult> PublishAsync(GitPublishRequest request, CancellationToken ct = default);
}

/// <summary>
/// A request to publish a set of repo-relative paths on behalf of one recording. The paths are
/// resolved against the CT working tree by the adapter; a path that does not exist on-CT is skipped
/// (never fabricated). All paths MUST be git-eligible text (transcripts / manifest / bundles) — the
/// adapter refuses any path under a never-git prefix (audio / voiceprints), enforcing LINT R7.
/// </summary>
public sealed record GitPublishRequest(string RecordingId, IReadOnlyList<string> RepoRelativePaths)
{
    /// <summary>Repo path prefixes that MUST NEVER be committed to git (LINT R7): audio + voiceprints.</summary>
    public static readonly IReadOnlyList<string> NeverGitPrefixes =
        ["recordings/audio/", "recordings/voiceprints/", "voiceprints/", "audio/"];
}

/// <summary>The outcome of a publish: which paths were pushed vs skipped (absent / unchanged).</summary>
public sealed record GitPublishResult(
    IReadOnlyList<string> Pushed,
    IReadOnlyList<string> Skipped,
    bool WasNoOp)
{
    public static GitPublishResult NoOp { get; } =
        new(Array.Empty<string>(), Array.Empty<string>(), WasNoOp: true);
}

/// <summary>A failure publishing the searchable substrate to git (live path only).</summary>
public sealed class GitPublishException(string reason, Exception? inner = null) : Exception(reason, inner);
