using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;

namespace Gemini.Mcp;

[McpServerToolType]
public sealed class ImageGenerateTool
{
    /// <summary>
    /// Delegate image generation to Gemini's nanobanana extension: mint a call id,
    /// create a per-call temp dir, invoke gemini with a nanobanana directive in that
    /// cwd, then scan for output images under <c>&lt;cwd&gt;/nanobanana-output/</c>.
    ///
    /// Requires <c>NANOBANANA_API_KEY</c> in the extension environment; the tool
    /// returns a structured error when the key is missing and no image is produced.
    /// </summary>
    [McpServerTool(Name = "image_generate")]
    [Description("Generate an image via Gemini's nanobanana extension. Requires NANOBANANA_API_KEY in the extension env.")]
    public static async Task<string> ImageGenerate(
        GeminiConfig cfg,
        [Description("The image generation prompt.")] string prompt,
        [Description("Optional aspect ratio: 1:1 | 16:9 | 9:16 | 4:3 | 3:4")] string? aspect_ratio = null)
    {
        var callId = Guid.NewGuid().ToString();
        var callDir = Path.Combine(cfg.OutputDir, callId);
        Directory.CreateDirectory(callDir);

        var aspectClause = !string.IsNullOrEmpty(aspect_ratio) ? $"Use aspect ratio {aspect_ratio}. " : "";
        // Sanitise the user-supplied prompt before interpolating it into a quoted
        // directive string. Without escaping, a caller could close the `\"<prompt>\"`
        // quotes mid-string and inject new instructions for the gemini CLI to follow.
        // Escape backslashes first, then double-quotes.
        var safePrompt = prompt.Replace("\\", "\\\\").Replace("\"", "\\\"");
        var directive =
            $"Use the generate_image tool from the nanobanana extension to create exactly one image. " +
            $"Prompt: \"{safePrompt}\". " +
            aspectClause +
            "Do not describe the image; only call the tool and confirm the saved filename.";

        var r = await GeminiCli.RunAsync(cfg,
            ["-p", directive, "--yolo", "--skip-trust"],
            cwd: callDir);

        var outDir = Path.Combine(callDir, "nanobanana-output");
        string[] outputs = Array.Empty<string>();
        if (Directory.Exists(outDir))
        {
            outputs = Directory.GetFiles(outDir)
                .Where(p => p.EndsWith(".png", StringComparison.OrdinalIgnoreCase)
                         || p.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase)
                         || p.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase))
                .ToArray();
        }

        if (outputs.Length == 0)
        {
            // Best-effort cleanup of the empty temp directory.
            try { Directory.Delete(callDir, recursive: true); } catch { /* ignore */ }
            throw new InvalidOperationException(JsonSerializer.Serialize(new
            {
                error = "no image produced — nanobanana extension likely missing NANOBANANA_API_KEY",
                hint = "set NANOBANANA_API_KEY in the extension environment for this server",
                gemini_stderr_tail = Jsons.TailLeft(r.Stderr, 300),
            }, Jsons.IndentedWeb));
        }

        var first = outputs[0];
        var size = new FileInfo(first).Length;
        return JsonSerializer.Serialize(new
        {
            path = first,
            size_bytes = size,
            count = outputs.Length,
            all_paths = outputs,
        }, Jsons.IndentedWeb);
    }
}
