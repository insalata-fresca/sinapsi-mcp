using Cervello.Enrichment.Domain;
using Cervello.Enrichment.Ports;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cervello.Enrichment.Pipeline;

/// <summary>
/// V5 — the rename → enroll → move-to-registry resolver (design <c>ste/cervello</c>
/// <c>docs/design/voiceprint-naming.md</c> §7 phase V5, §6.1–6.6). One <see cref="RunCycleAsync"/> is
/// one poll cycle: list the <c>voiceprints/</c> folder, find any file whose id matches an UNRESOLVED
/// candidate row AND whose current name differs from the upload-time <c>unknown_NN</c> sample name (a
/// rename), then for each such rename — the operator naming that voice:
///
/// <list type="number">
///   <item><b>Derive the slug</b> from the new name (<c>Marco → marco</c>), skipping any rename that
///     yields no valid slug (<see cref="PersonSlug.TrySlugify"/> — the no-fabrication floor).</item>
///   <item><b>Consent + enroll</b> — add the slug to the durable §10 consent store (the rename IS the
///     operator's consent; design §9 fork 1) then <see cref="VoiceprintEnrollment.EnrollOnConfirmationAsync"/>
///     the candidate's EXACT stored centroid under that slug with a <c>human://rename:&lt;fileId&gt;</c>
///     basis. Enrolls only the centroid mapped to THIS file id — never a different voice.</item>
///   <item><b>Move</b> the file to the <c>registry/</c> subfolder (<see cref="IVoiceprintRegistryDrive.MoveToRegistryAsync"/>).</item>
///   <item><b>Mark resolved</b> — only AFTER enroll + move succeed, so a mid-way failure leaves the
///     candidate UNRESOLVED to retry next cycle (never partially-enroll-then-lose-track).</item>
///   <item><b>Kick V6</b> — re-attribute the corpus for the newly-enrolled person
///     (<see cref="CorpusReattributor"/>).</item>
/// </list>
///
/// <para><b>Idempotent + safe.</b> The cycle only ever acts on files that are UNRESOLVED candidate rows
/// (a file with no candidate row, or an already-resolved one, is skipped — never an arbitrary Drive
/// file). A file still named <c>unknown_NN</c> (unchanged) is skipped. A slug-empty rename is skipped.
/// Enroll/move happen exactly once per rename because <see cref="IVoiceprintNamingCandidateStore.MarkResolvedAsync"/>
/// is the single-shot guard: a re-processed cycle re-reads the row as resolved and skips it.</para>
/// </summary>
public sealed class VoiceprintRenameResolver(
    IVoiceprintRegistryDrive drive,
    IVoiceprintNamingCandidateStore candidateStore,
    IEnrollmentConsentStore consentStore,
    VoiceprintEnrollment enrollment,
    IRecentEnrollmentStore? recentEnrollment = null,
    CorpusReattributor? reattributor = null,
    IAccessLog? accessLog = null,
    ILogger<VoiceprintRenameResolver>? logger = null)
{
    private readonly IVoiceprintRegistryDrive _drive = drive ?? throw new ArgumentNullException(nameof(drive));
    private readonly IVoiceprintNamingCandidateStore _candidates =
        candidateStore ?? throw new ArgumentNullException(nameof(candidateStore));
    private readonly IEnrollmentConsentStore _consent =
        consentStore ?? throw new ArgumentNullException(nameof(consentStore));
    private readonly VoiceprintEnrollment _enroll = enrollment ?? throw new ArgumentNullException(nameof(enrollment));
    private readonly IRecentEnrollmentStore? _recent = recentEnrollment;
    private readonly CorpusReattributor? _reattributor = reattributor;
    private readonly IAccessLog? _accessLog = accessLog;
    private readonly ILogger _log = logger ?? NullLogger<VoiceprintRenameResolver>.Instance;

    /// <summary>Run one poll cycle. Returns the resolutions performed (for the health/log line).</summary>
    public async Task<RenameCycleResult> RunCycleAsync(DateOnly on, CancellationToken ct = default)
    {
        var unresolved = await _candidates.GetUnresolvedAsync(ct).ConfigureAwait(false);
        if (unresolved.Count == 0)
            return RenameCycleResult.Empty;

        // Index unresolved candidates by Drive file id — the STABLE resolution key (§6.2).
        var byFileId = unresolved.ToDictionary(c => c.DriveFileId, c => c, StringComparer.Ordinal);

        IReadOnlyList<DriveFileEntry> listing;
        try
        {
            listing = await _drive.ListVoiceprintsFolderAsync(ct).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // A Drive list failure (e.g. the grant not yet widened → 403) is transient for the poller:
            // leave everything unresolved, retry next cycle. Never fabricate a rename.
            _log.LogWarning(e, "voiceprints poll: list folder failed — retrying next cycle");
            return RenameCycleResult.Empty;
        }

        var resolved = new List<string>();
        var skipped = new List<RenameSkip>();

        foreach (var file in listing)
        {
            ct.ThrowIfCancellationRequested();

            // Act ONLY on files that ARE an unresolved candidate row — never an arbitrary Drive file.
            if (!byFileId.TryGetValue(file.FileId, out var candidate))
                continue;

            // A file still named unknown_NN (or unknown_NN.<ext>, the upload name) is NOT a rename — skip.
            if (NameIsStillTheSample(file.Name, candidate.SampleName))
                continue;

            // Derive the person slug — no-fabrication floor: a rename that slugifies empty is skipped.
            if (!PersonSlug.TrySlugify(file.Name, out var slug) || slug is null)
            {
                _log.LogInformation("voiceprints poll: rename of {File} to '{Name}' yields no valid slug — skipped (never fabricated)",
                    file.FileId, file.Name);
                skipped.Add(new RenameSkip(file.FileId, file.Name, "no valid slug"));
                continue;
            }

            var outcome = await ResolveOneAsync(candidate, slug, on, ct).ConfigureAwait(false);
            if (outcome.Resolved)
                resolved.Add(slug);
            else
                skipped.Add(new RenameSkip(file.FileId, file.Name, outcome.Reason ?? "unresolved"));
        }

        _log.LogInformation("voiceprints poll: {Resolved} enrolled+moved, {Skipped} skipped",
            resolved.Count, skipped.Count);
        return new RenameCycleResult(resolved, skipped);
    }

    private async Task<(bool Resolved, string? Reason)> ResolveOneAsync(
        VoiceprintNamingCandidate candidate, string slug, DateOnly on, CancellationToken ct)
    {
        var basisId = $"rename:{candidate.DriveFileId}";
        var basis = ConfirmationBasis.Human(basisId); // human://rename:<fileId>

        try
        {
            // 1) Consent: the rename IS the operator's consent to enroll (design §9 fork 1). Durable +
            //    idempotent; UNIONed into the §10 gate so the enroll below passes.
            await _consent.AddConsentAsync(slug, basis.Id, ct).ConfigureAwait(false);

            // 2) Enroll the EXACT centroid mapped to this drive file id — never a different voice.
            var sourceSegments = candidate.SourceMembers
                .Select(m => $"rec://{m.RecordingId}#cluster{m.ClusterIndex}")
                .ToList();
            var result = await _enroll.EnrollOnConfirmationAsync(
                slug, candidate.Centroid, sourceSegments, matchCosine: null, basis, on, ct).ConfigureAwait(false);

            // 3) Move the file to registry/. If this throws, the catch below leaves the candidate
            //    UNRESOLVED — the enroll already happened (idempotent refine on retry), the move retries.
            await _drive.MoveToRegistryAsync(candidate.DriveFileId, ct).ConfigureAwait(false);

            // 4) Mark resolved ONLY after enroll + move both succeeded (single-shot).
            await _candidates.MarkResolvedAsync(candidate.DriveFileId, ct).ConfigureAwait(false);

            if (_accessLog is not null)
                await _accessLog.AppendAsync(new AccessLogEntry(
                    "voiceprint_rename_enroll", "cervello",
                    Outcome: $"{(result.WasRefine ? "refined" : "enrolled")}:{slug}:moved-to-registry",
                    PointId: candidate.DriveFileId), ct).ConfigureAwait(false);
            _log.LogInformation("voiceprints poll: {Action} {Slug} from rename of {File} (basis {Basis}); moved to registry",
                result.WasRefine ? "refined" : "enrolled", slug, candidate.DriveFileId, basis.Id);

            // 5) Kick V6 — re-attribute the corpus for the newly-enrolled person (best-effort: a failure
            //    here never un-does the enroll/move; the enrollment stands and a future generate/rename
            //    can retrigger). Marks the recent-enrollment auto-apply signal internally.
            if (_reattributor is not null)
            {
                try
                {
                    var reattr = await _reattributor
                        .ReattributeAsync(slug, candidate.Centroid, basis.Id, ct).ConfigureAwait(false);
                    _log.LogInformation("voiceprints poll: V6 re-attribution for {Slug}: {Matched} matched, {Requeued} requeued",
                        slug, reattr.MatchedCount, reattr.RequeuedRecordingIds.Count);
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    _log.LogWarning(e, "voiceprints poll: V6 re-attribution for {Slug} failed (non-fatal — enrollment stands)", slug);
                }
            }
            else if (_recent is not null)
            {
                // No reattributor wired but we still record the just-enrolled signal so a subsequent
                // drain attributes correctly (defence in depth).
                await _recent.MarkAsync(slug, basis.Id, ct).ConfigureAwait(false);
            }

            return (true, null);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (EnrollmentNotAllowedException e)
        {
            // Should not happen (we just added consent), but if the store's gate is stricter, leave the
            // candidate unresolved and never claim a success.
            _log.LogWarning("voiceprints poll: enroll of {Slug} refused by §10 gate — candidate left unresolved", slug);
            return (false, e.Message);
        }
        catch (Exception e)
        {
            // Enroll or move failed — leave the candidate UNRESOLVED so the next cycle retries. The
            // enroll is idempotent (a refine), so a retry after a move-only failure does not double-count
            // in a way that misattributes: it re-refines the SAME centroid under the SAME slug.
            _log.LogWarning(e, "voiceprints poll: resolve of {Slug} (file {File}) failed — left unresolved for retry",
                slug, candidate.DriveFileId);
            return (false, e.Message);
        }
    }

    /// <summary>
    /// True if the current Drive name still equals the upload-time sample name — i.e. NOT a rename.
    /// The sample name is <c>unknown_NN</c> (no extension); V4 uploaded it as <c>unknown_NN.&lt;ext&gt;</c>,
    /// so an un-renamed file's current name is <c>unknown_NN</c> or <c>unknown_NN.&lt;audio-ext&gt;</c>.
    /// Any other name is an operator rename.
    /// </summary>
    private static bool NameIsStillTheSample(string currentName, string sampleName)
    {
        if (string.Equals(currentName, sampleName, StringComparison.Ordinal))
            return true;
        foreach (var ext in KnownAudioExtensions)
            if (string.Equals(currentName, sampleName + ext, StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }

    private static readonly string[] KnownAudioExtensions = [".m4a", ".mp3", ".wav", ".aac", ".ogg", ".mp4", ".flac"];
}

/// <summary>One file the V5 poller could not resolve this cycle, and why (never fabricated — always logged).</summary>
public sealed record RenameSkip(string DriveFileId, string CurrentName, string Reason);

/// <summary>The outcome of one V5 poll cycle: the slugs enrolled+moved this cycle, and the files skipped.</summary>
public sealed record RenameCycleResult(IReadOnlyList<string> ResolvedSlugs, IReadOnlyList<RenameSkip> Skipped)
{
    public static RenameCycleResult Empty { get; } = new(Array.Empty<string>(), Array.Empty<RenameSkip>());
}
