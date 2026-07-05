using System.Text;
using System.Text.RegularExpressions;
using Cervello.Watcher.Domain;

namespace Cervello.Watcher.Normalize;

/// <summary>
/// Hand-rolled, deterministic, idempotent writer for <c>recordings/manifest.yaml</c>
/// (§8, field order fixed). No YamlDotNet — a byte-stable append is easier to
/// guarantee by hand than to coax out of a general serializer.
///
/// Semantics (recording-normalize "Manifest registration is idempotent"):
/// <list type="bullet">
///   <item>Missing/empty file ⇒ start from a comment header + <c>[]</c>.</item>
///   <item>Append replaces a lone <c>[]</c> body with the first block; later entries
///   append after the existing blocks.</item>
///   <item>If an entry with the same <c>id</c> already exists ⇒ NO-OP, return false,
///   file byte-unchanged. Re-running the SAME append leaves the file byte-identical.</item>
///   <item>Leading comment lines are preserved.</item>
/// </list>
/// Uses LF newlines throughout so the byte content is platform-independent.
/// </summary>
public sealed class YamlManifestStore : IManifestStore
{
    private const string Header =
        "# recordings/manifest.yaml — the git-side §8 record of normalized recordings.\n" +
        "# Written by Cervello.Watcher (WATCH → NORMALIZE). References + checksums only;\n" +
        "# never audio bytes. Append-only, deduped by recording id.\n";

    private readonly string _path;

    public YamlManifestStore(string manifestPath) => _path = manifestPath;

    public Task<bool> AppendAsync(ManifestEntry entry, CancellationToken ct)
    {
        var existing = File.Exists(_path)
            ? File.ReadAllText(_path)
            : Header + "[]\n";

        // Normalize CRLF the file might already carry, so our comparisons + writes are LF.
        existing = existing.Replace("\r\n", "\n");

        if (ContainsId(existing, entry.Id))
            return Task.FromResult(false); // no-op, byte-unchanged (we do not rewrite)

        var block = RenderBlock(entry);
        string updated;

        // A lone empty flow list `[]` body ⇒ replace it with the first block.
        var emptyList = Regex.Match(existing, @"(?m)^\[\]\s*$");
        if (emptyList.Success)
        {
            var before = existing[..emptyList.Index];
            updated = before + block;
        }
        else
        {
            // Append after existing blocks; ensure a trailing newline separates them.
            var trimmed = existing.TrimEnd('\n');
            updated = trimmed + "\n" + block;
        }

        // Write the header once if the file had no leading comment (defensive; the
        // seed always carries it).
        if (!updated.StartsWith('#'))
            updated = Header + updated;

        File.WriteAllText(_path, updated, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        return Task.FromResult(true);
    }

    /// <summary>Scan for a list item whose <c>id:</c> equals <paramref name="id"/> exactly.</summary>
    internal static bool ContainsId(string yaml, string id)
    {
        var pattern = @"(?m)^\s*-\s*id:\s*" + Regex.Escape(id) + @"\s*$";
        return Regex.IsMatch(yaml, pattern);
    }

    /// <summary>
    /// Render one §8 block, field order fixed. LF-terminated, two-space indented
    /// under the list dash. Deterministic: the same entry renders byte-identical.
    /// </summary>
    internal static string RenderBlock(ManifestEntry e)
    {
        var sb = new StringBuilder();
        sb.Append("- id: ").Append(e.Id).Append('\n');
        sb.Append("  audio_sha256: ").Append(e.AudioSha256).Append('\n');
        sb.Append("  source_drive_id: ").Append(e.SourceDriveId).Append('\n');
        sb.Append("  transcript: ").Append(e.Transcript).Append('\n');
        sb.Append("  google_txt: ").Append(e.GoogleTxt ?? "").Append('\n');
        sb.Append("  attribution: ").Append(e.Attribution).Append('\n');
        sb.Append("  recorded_at: ").Append(e.RecordedAt).Append('\n');
        sb.Append("  state: ").Append(e.State).Append('\n');
        return sb.ToString();
    }
}
