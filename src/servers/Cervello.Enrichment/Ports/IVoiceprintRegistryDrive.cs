namespace Cervello.Enrichment.Ports;

/// <summary>
/// The V5 rename-poller's Drive surface (design <c>ste/cervello</c>
/// <c>docs/design/voiceprint-naming.md</c> §7 phase V5, §6.1/§6.5): LIST the
/// <c>cervello/recordings/voiceprints/</c> folder as <c>{fileId → name}</c> (so the poller can diff
/// on NAME, not md5, to catch a rename), and MOVE a renamed+enrolled file into the
/// <c>registry/</c> subfolder (the durable human-owned reference set).
///
/// <para><b>Distinct from <see cref="IVoiceSampleUploader"/> (V4).</b> V4 uploads + deletes clips; V5
/// reads the folder listing + moves a file between folders (<c>move_file</c> = the V3 gdrive MCP
/// <c>addParents</c>/<c>removeParents</c> tool). Both ride the SAME transport + identity
/// (<c>agent-cervello-watcher</c> through the CT121 agentgateway) the recordings watcher already uses.</para>
///
/// <para>Confinement: this port touches only Drive file ids + names — never a centroid. The audio
/// clips it lists/moves are already in the operator's Drive (design §0); moving one Drive→Drive
/// exposes nothing new.</para>
/// </summary>
public interface IVoiceprintRegistryDrive
{
    /// <summary>
    /// List the current non-trashed files in the <c>voiceprints/</c> folder as
    /// <c>{fileId, name}</c> pairs. The poller diffs this against the candidate rows by file id, and
    /// compares each file's current name to the candidate's upload-time <c>unknown_NN</c> name to
    /// detect a rename. The <c>registry/</c> subfolder itself (a folder, not a sample) is excluded.
    /// </summary>
    Task<IReadOnlyList<DriveFileEntry>> ListVoiceprintsFolderAsync(CancellationToken ct = default);

    /// <summary>
    /// Move <paramref name="fileId"/> from the <c>voiceprints/</c> folder into the <c>registry/</c>
    /// subfolder (add the registry parent, remove the voiceprints parent). The durable move
    /// (<c>move_file</c>) is required, not a copy — a copy would leave a stale original that
    /// re-triggers the poller (design §9 fork 4). Throws on failure so the caller leaves the candidate
    /// UNRESOLVED to retry next poll (never partially-enroll-then-lose-track).
    /// </summary>
    Task MoveToRegistryAsync(string fileId, CancellationToken ct = default);
}

/// <summary>One Drive file the V5 poller sees: its stable id + its CURRENT (post-rename) name.</summary>
public sealed record DriveFileEntry(string FileId, string Name);
