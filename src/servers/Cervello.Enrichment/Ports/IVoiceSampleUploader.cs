namespace Cervello.Enrichment.Ports;

/// <summary>
/// Port for uploading one cut voice-sample clip to the operator's Drive <c>voiceprints/</c> folder
/// (design <c>ste/cervello</c> <c>docs/design/voiceprint-naming.md</c> §7 phase V4, §4.3/§8). The
/// live adapter calls the V3 gdrive MCP <c>create_folder</c>/<c>upload_file</c> tools through the
/// CT121 agentgateway as the scoped <c>agent-cervello-watcher</c> identity — the SAME transport +
/// identity <see cref="Cervello.Watcher.Drive.McpGdriveClient"/> already uses to read Drive.
///
/// <para>Confinement: the clip bytes are Drive-owned audio the operator already has (design §0 "the
/// clip is Drive→Drive, never git, never a shared NATS subject") — this port's only job is placing
/// them in the <c>voiceprints/</c> folder under a stable name; it never inspects the centroid.</para>
/// </summary>
public interface IVoiceSampleUploader
{
    /// <summary>
    /// Ensure the destination folder exists (create-if-absent, idempotent) and upload
    /// <paramref name="clipBytes"/> as <paramref name="fileName"/> (e.g. <c>unknown_03.m4a</c>) into
    /// it. Returns the Drive file id of the uploaded clip — the resolution key
    /// <see cref="Domain.VoiceprintNamingCandidate.DriveFileId"/> is keyed on.
    /// </summary>
    Task<string> UploadAsync(
        string fileName, ReadOnlyMemory<byte> clipBytes, string mimeType, CancellationToken ct = default);

    /// <summary>
    /// Delete a previously-uploaded sample by its Drive file id (a re-generate run clearing a stale,
    /// un-renamed candidate's clip — design §5: "clear prior un-renamed candidates + their Drive
    /// files"). A missing/already-gone file is a no-op, never an error — best-effort cleanup.
    /// </summary>
    Task DeleteAsync(string driveFileId, CancellationToken ct = default);
}
