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
        var existing = ReadNormalized();

        if (ContainsId(existing, entry.Id))
            return Task.FromResult(false); // no-op, byte-unchanged (we do not rewrite)

        return Task.FromResult(WriteAppended(existing, RenderBlock(entry)));
    }

    public Task<bool> UpsertAsync(ManifestEntry entry, CancellationToken ct)
    {
        var existing = ReadNormalized();
        var block = RenderBlock(entry);

        if (!ContainsId(existing, entry.Id))
            return Task.FromResult(WriteAppended(existing, block)); // first sight ⇒ plain append

        // The id is present: replace its block. If the current block is byte-identical, it is a
        // genuine no-op (a re-register of the same sides) and the file stays byte-unchanged.
        var replaced = ReplaceBlock(existing, entry.Id, block);
        if (replaced == existing)
            return Task.FromResult(false);

        File.WriteAllText(_path, replaced, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        return Task.FromResult(true);
    }

    /// <summary>Read the manifest as LF-normalized text (or a fresh header + empty list when absent).</summary>
    private string ReadNormalized()
    {
        var existing = File.Exists(_path) ? File.ReadAllText(_path) : Header + "[]\n";
        return existing.Replace("\r\n", "\n");
    }

    /// <summary>Append <paramref name="block"/> to <paramref name="existing"/> and write; returns true.</summary>
    private bool WriteAppended(string existing, string block)
    {
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
        return true;
    }

    /// <summary>
    /// Replace the whole block for <paramref name="id"/> (the <c>- id: &lt;id&gt;</c> line through the last
    /// of its indented fields, before the next <c>- id:</c> / EOF) with <paramref name="newBlock"/>.
    /// Returns the input unchanged if the block is byte-identical or the id is absent.
    /// </summary>
    internal static string ReplaceBlock(string yaml, string id, string newBlock)
    {
        // Match "- id: <id>" and everything up to (but not including) the next list item or EOF.
        var pattern = @"(?m)^-\s*id:\s*" + Regex.Escape(id) + @"\s*\n(?:[ \t]+.*\n?)*";
        var m = Regex.Match(yaml, pattern);
        if (!m.Success)
            return yaml; // id not found as a block head (defensive; caller checks ContainsId first)
        if (m.Value == newBlock)
            return yaml; // identical block already present ⇒ byte-unchanged no-op
        return yaml[..m.Index] + newBlock + yaml[(m.Index + m.Length)..];
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
