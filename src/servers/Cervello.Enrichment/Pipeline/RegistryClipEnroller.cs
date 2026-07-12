using Cervello.Enrichment.Domain;
using Cervello.Enrichment.Ports;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cervello.Enrichment.Pipeline;

/// <summary>
/// The REGISTRY-PILOT enrol coordinator (spec <c>voiceprint-store</c> → "Enroll / refine on confirmation,
/// under the allowlist"; design <c>ste/cervello</c> <c>docs/design/voiceprint-naming.md</c> §7 phase V5, §10).
/// It seeds the initial enrolled voiceprints from the operator's HUMAN-NAMED clips already in the Drive
/// <c>voiceprints/registry/</c> folder — the pilot bootstrap the V5 rename-poller cannot do, because those
/// clips have no <c>VoiceprintNamingCandidate</c> row (they were placed/renamed directly, not produced by a
/// corpus-driven V4 sample-generation).
///
/// <para><b>Reuses EVERY real guard — never a raw store write.</b> For each requested slug it:
/// <list type="number">
///   <item>Finds the operator's clip(s) in <c>registry/</c> whose file name CONTAINS the slug (the operator
///     supplies the exact slug — this entrypoint never fabricates a name from a filename).</item>
///   <item>Downloads the clip's RAW bytes (<see cref="IVoiceprintRegistryClipReader"/>) and embeds them via the
///     LIVE 256-d diarize-embed sidecar (<see cref="IDiarizeEmbedClient"/>) — never a 192-d artifact.</item>
///   <item>Picks the DOMINANT speaker's 256-d embedding (the speaker with the most diarized talk-time — a
///     representative registry clip is a single person, so this is that person).</item>
///   <item>Records the slug's §10 consent (<see cref="IEnrollmentConsentStore.AddConsentAsync"/>) under a
///     <c>human://registry-pilot:&lt;slug&gt;</c> basis (the operator naming the clip IS the consent), then
///     enrols/refines via <see cref="VoiceprintEnrollment.EnrollOnConfirmationAsync"/> — the ONLY centroid-write
///     path, whose in-store §10 gate stays authoritative.</item>
/// </list>
/// Multiple clips for one person are enrolled in sequence, so the 2nd..Nth REFINE the running centroid
/// (spec parity with the rename poller). A clip that yields no 256-d embedding, or whose download/embed
/// fails, is SKIPPED (logged, never fabricated) and the person is left unenrolled for that clip.</para>
///
/// <para><b>Confinement.</b> The clip bytes are transient (embed input only); only the derived 256-d centroid
/// is persisted, CT146-side, by the store. No centroid, no audio, ever leaves the CT or enters git.</para>
/// </summary>
public sealed class RegistryClipEnroller(
    IVoiceprintRegistryClipReader reader,
    IDiarizeEmbedClient embedClient,
    IEnrollmentConsentStore consentStore,
    VoiceprintEnrollment enrollment,
    ILogger<RegistryClipEnroller>? logger = null)
{
    private readonly IVoiceprintRegistryClipReader _reader = reader ?? throw new ArgumentNullException(nameof(reader));
    private readonly IDiarizeEmbedClient _embed = embedClient ?? throw new ArgumentNullException(nameof(embedClient));
    private readonly IEnrollmentConsentStore _consent = consentStore ?? throw new ArgumentNullException(nameof(consentStore));
    private readonly VoiceprintEnrollment _enroll = enrollment ?? throw new ArgumentNullException(nameof(enrollment));
    private readonly ILogger _log = logger ?? NullLogger<RegistryClipEnroller>.Instance;

    /// <summary>The audio container the registry clips are cut as (V4/POC produce m4a/AAC).</summary>
    private const string ClipFormat = "m4a";

    /// <summary>
    /// Enrol each slug in <paramref name="personSlugs"/> from its registry clip(s). Returns a per-slug
    /// outcome (enrolled / refined / skipped-with-reason) for the caller's report. Never throws for a
    /// per-slug failure — it is captured as a skip; only a bad argument throws.
    /// </summary>
    public async Task<RegistryEnrollResult> EnrollAsync(
        IReadOnlyList<string> personSlugs, DateOnly on, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(personSlugs);
        if (personSlugs.Count == 0)
            throw new ArgumentException("personSlugs must be non-empty", nameof(personSlugs));

        var listing = await _reader.ListRegistryFolderAsync(ct).ConfigureAwait(false);

        var enrolled = new List<RegistryEnrollOutcome>();
        var skipped = new List<RegistryEnrollSkip>();

        foreach (var rawSlug in personSlugs)
        {
            ct.ThrowIfCancellationRequested();

            if (!PersonSlug.TrySlugify(rawSlug, out var slug) || slug is null)
            {
                skipped.Add(new RegistryEnrollSkip(rawSlug, "(none)", "not a valid slug"));
                continue;
            }

            // Match the operator's named clip(s) for this slug by file name containing the slug. A
            // registry file is named e.g. "013_stefano-ursino" / "stefano-ursino_01" / "marwin-henry_02".
            var clips = listing
                .Where(f => f.Name.Contains(slug, StringComparison.OrdinalIgnoreCase))
                .OrderBy(f => f.Name, StringComparer.Ordinal)
                .ToList();
            if (clips.Count == 0)
            {
                _log.LogWarning("registry-pilot: no clip in registry/ matches slug {Slug} — skipped", slug);
                skipped.Add(new RegistryEnrollSkip(slug, "(none)", "no matching registry clip"));
                continue;
            }

            var enrolledAnyForSlug = false;
            foreach (var clip in clips)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    var centroid = await EmbedDominantSpeakerAsync(clip.FileId, ct).ConfigureAwait(false);
                    if (centroid is null)
                    {
                        _log.LogWarning("registry-pilot: clip {File} ({Name}) yielded no 256-d embedding — skipped",
                            clip.FileId, clip.Name);
                        skipped.Add(new RegistryEnrollSkip(slug, clip.Name, "no embedding produced"));
                        continue;
                    }

                    // §10 consent: the operator naming this clip IS the consent to enrol (design §9 fork 1).
                    // Durable + idempotent; UNIONed into the store's §10 gate so the enrol below passes.
                    var basis = ConfirmationBasis.Human($"registry-pilot:{slug}"); // human://registry-pilot:<slug>
                    await _consent.AddConsentAsync(slug, basis.Id, ct).ConfigureAwait(false);

                    var sourceSegments = new[] { $"registry://{clip.FileId}" };
                    var result = await _enroll.EnrollOnConfirmationAsync(
                        slug, centroid, sourceSegments, matchCosine: null, basis, on, ct).ConfigureAwait(false);

                    enrolledAnyForSlug = true;
                    enrolled.Add(new RegistryEnrollOutcome(
                        slug, clip.Name, clip.FileId, result.WasRefine, result.Print.SampleCount));
                    _log.LogInformation("registry-pilot: {Action} {Slug} from registry clip {File} (basis {Basis}); sample_count={Count}",
                        result.WasRefine ? "refined" : "enrolled", slug, clip.FileId, basis.Id, result.Print.SampleCount);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (EnrollmentNotAllowedException e)
                {
                    // Should not happen (we just added consent), but if the store's gate is stricter, never
                    // claim a success — leave the person unenrolled for this clip.
                    _log.LogWarning("registry-pilot: enrol of {Slug} refused by §10 gate — clip {File} skipped", slug, clip.FileId);
                    skipped.Add(new RegistryEnrollSkip(slug, clip.Name, $"§10 gate refused: {e.Message}"));
                }
                catch (Exception e)
                {
                    _log.LogWarning(e, "registry-pilot: clip {File} for {Slug} failed — skipped", clip.FileId, slug);
                    skipped.Add(new RegistryEnrollSkip(slug, clip.Name, e.Message));
                }
            }

            if (!enrolledAnyForSlug)
                _log.LogWarning("registry-pilot: {Slug} matched {Count} clip(s) but none enrolled", slug, clips.Count);
        }

        _log.LogInformation("registry-pilot: {Enrolled} clip enrol(s), {Skipped} skipped", enrolled.Count, skipped.Count);
        return new RegistryEnrollResult(enrolled, skipped);
    }

    /// <summary>
    /// Download + embed a registry clip and return the DOMINANT speaker's 256-d centroid, or null if the
    /// sidecar produced no embedding. Dominant = the speaker with the most diarized talk-time across the
    /// clip (a representative registry clip is one person; picking the dominant speaker is robust to a
    /// stray short interjection).
    /// </summary>
    private async Task<IReadOnlyList<float>?> EmbedDominantSpeakerAsync(string fileId, CancellationToken ct)
    {
        var audio = await _reader.DownloadClipAsync(fileId, ct).ConfigureAwait(false);
        var response = await _embed.DiarizeEmbedAsync(new DiarizeEmbedRequest(audio, ClipFormat), ct).ConfigureAwait(false);
        if (response.Embeddings.Count == 0)
            return null;
        if (response.Embeddings.Count == 1)
            return response.Embeddings[0].Vector;

        // Rank speakers by total diarized talk-time; take the dominant speaker's embedding.
        var talkTime = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var seg in response.Segments)
        {
            var duration = seg.End - seg.Start;
            talkTime[seg.Speaker] = talkTime.TryGetValue(seg.Speaker, out var acc) ? acc + duration : duration;
        }

        var dominant = response.Embeddings
            .OrderByDescending(e => talkTime.TryGetValue(e.Speaker, out var t) ? t : 0.0)
            .ThenBy(e => e.Speaker, StringComparer.Ordinal)
            .First();
        return dominant.Vector;
    }
}

/// <summary>One successful registry-pilot enrol/refine: the slug, the source clip, and whether it refined.</summary>
public sealed record RegistryEnrollOutcome(string Slug, string ClipName, string DriveFileId, bool WasRefine, int SampleCount);

/// <summary>One registry clip the entrypoint could not enrol this run, and why (never fabricated — always logged).</summary>
public sealed record RegistryEnrollSkip(string Slug, string ClipName, string Reason);

/// <summary>The outcome of one registry-pilot enrol call: the clips enrolled/refined, and the ones skipped.</summary>
public sealed record RegistryEnrollResult(IReadOnlyList<RegistryEnrollOutcome> Enrolled, IReadOnlyList<RegistryEnrollSkip> Skipped)
{
    public static RegistryEnrollResult Empty { get; } = new(Array.Empty<RegistryEnrollOutcome>(), Array.Empty<RegistryEnrollSkip>());
}
