namespace Cervello.Enrichment.Ports;

/// <summary>
/// The registry-pilot enrol surface's Drive READ port (design <c>ste/cervello</c>
/// <c>docs/design/voiceprint-naming.md</c> §7 phase V5, §6.5): LIST the human-owned
/// <c>cervello/recordings/voiceprints/registry/</c> subfolder as <c>{fileId → name}</c>, and
/// DOWNLOAD one registry clip's RAW audio bytes by file id (for on-demand 256-d embedding).
///
/// <para><b>Distinct from <see cref="IVoiceprintRegistryDrive"/> (V5 poller).</b> That port lists the
/// <c>voiceprints/</c> staging folder and MOVES a renamed sample into <c>registry/</c>; it never reads
/// bytes. This port reads the <c>registry/</c> folder that already holds the operator's named clips and
/// fetches their bytes so the registry-pilot entrypoint can embed + enrol them via the LIVE
/// diarize-embed sidecar. The two ride the SAME transport + identity (<c>agent-cervello-watcher</c>
/// through the CT121 agentgateway) the recordings watcher already uses.</para>
///
/// <para><b>Confinement.</b> This port touches only Drive file ids, names, and TRANSIENT clip bytes it
/// hands straight to the embed sidecar — never a centroid, never git, never a shared subject. The clips
/// it reads are already in the operator's Drive (design §0); reading one Drive→CT146 exposes nothing new,
/// and the derived 256-d centroid stays CT146-side (spec <c>voiceprint-store</c>).</para>
/// </summary>
public interface IVoiceprintRegistryClipReader
{
    /// <summary>
    /// List the current non-trashed files in the <c>registry/</c> folder as <c>{fileId, name}</c> pairs.
    /// The <c>registry/</c> parent id comes from <c>CERVELLO_VOICEPRINTS_REGISTRY_DRIVE_FOLDER_ID</c>.
    /// Any nested folder is excluded (a folder is never a clip). Throws on a Drive list failure so the
    /// caller reports it rather than silently enrolling nothing.
    /// </summary>
    Task<IReadOnlyList<DriveFileEntry>> ListRegistryFolderAsync(CancellationToken ct = default);

    /// <summary>
    /// Download <paramref name="fileId"/>'s RAW audio bytes from Drive (via the gdrive MCP
    /// <c>download_file_base64</c> tool, looped until <c>eof</c>). The bytes are TRANSIENT — handed
    /// straight to the diarize-embed sidecar and never retained. Throws on a Drive download failure so
    /// the caller leaves the person UNENROLLED (never fabricate a centroid).
    /// </summary>
    Task<ReadOnlyMemory<byte>> DownloadClipAsync(string fileId, CancellationToken ct = default);
}
