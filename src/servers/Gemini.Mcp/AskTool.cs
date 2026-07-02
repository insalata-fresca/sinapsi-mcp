// ---------------------------------------------------------------------------
// Ask / ask_with_files / sandbox / image_describe — the synchronous, string-
// returning CLI-fronted tools. Each validates its params BEFORE spawning the
// subprocess (GeminiValidation), and every surfaced CLI failure routes its stderr
// tail through GeminiErrors so a secret in the CLI's diagnostics cannot leak.
// The error CHANNEL is unchanged (these tools already throw InvalidOperationException
// on failure) — only the message is now validated up front and scrubbed.
// ---------------------------------------------------------------------------
using System.ComponentModel;
using ModelContextProtocol.Server;

namespace Gemini.Mcp;

/// <summary>Shared stderr-tail length for the CLI-failure error messages.</summary>
internal static class AskToolShared
{
    internal const int StderrTailChars = 300;

    /// <summary>Build the scrubbed "gemini exited N: …" message for a failed run.</summary>
    internal static string Failure(string verb, int? exitCode, string stderr) =>
        $"{verb} exited {exitCode}: {GeminiErrors.SanitizedStderrTail(stderr, StderrTailChars)}";
}

[McpServerToolType]
public sealed class AskTool
{
    [McpServerTool(Name = "ask")]
    [Description("Ask Gemini a question (synchronous). Returns the model's text response.")]
    public static async Task<string> Ask(
        GeminiConfig cfg,
        [Description("The question/prompt to ask Gemini.")] string prompt,
        [Description("Model selector: auto | pro | flash | flash-lite")] string model = "auto",
        [Description("Optional system prefix prepended to the prompt (with double newline).")] string? system = null)
    {
        // Fail-fast validation BEFORE any subprocess is spawned.
        if (GeminiValidation.ValidatePrompt(prompt) is { } pErr) throw new InvalidOperationException(pErr);
        if (GeminiValidation.ValidateModel(model) is { } mErr) throw new InvalidOperationException(mErr);
        if (GeminiValidation.ValidateOptionalText(system, "system", GeminiValidation.MaxPromptLength) is { } sErr)
            throw new InvalidOperationException(sErr);

        var full = string.IsNullOrEmpty(system) ? prompt : $"{system}\n\n{prompt}";
        var r = await GeminiCli.RunAsync(cfg,
            ["-p", full, "--model", model, "--skip-trust", "--yolo"],
            cwd: cfg.SessionDir);

        if (r.ExitCode == 0) return r.Stdout.TrimEnd();
        throw new InvalidOperationException(AskToolShared.Failure("gemini", r.ExitCode, r.Stderr));
    }
}

[McpServerToolType]
public sealed class AskWithFilesTool
{
    [McpServerTool(Name = "ask_with_files")]
    [Description("Ask Gemini a question with attached file context. Files are referenced via @path mentions.")]
    public static async Task<string> AskWithFiles(
        GeminiConfig cfg,
        [Description("The question/prompt.")] string prompt,
        [Description("Absolute paths to files to include as context.")] string[] file_paths,
        [Description("Model selector: auto | pro | flash | flash-lite")] string model = "auto")
    {
        if (GeminiValidation.ValidatePrompt(prompt) is { } pErr) throw new InvalidOperationException(pErr);
        if (GeminiValidation.ValidateFilePaths(file_paths) is { } fErr) throw new InvalidOperationException(fErr);
        if (GeminiValidation.ValidateModel(model) is { } mErr) throw new InvalidOperationException(mErr);

        var mentions = string.Join(" ", file_paths.Select(p => $"@{p}"));
        var full = $"{prompt}\n\n{mentions}";
        var r = await GeminiCli.RunAsync(cfg,
            ["-p", full, "--model", model, "--skip-trust", "--yolo"],
            cwd: cfg.SessionDir);

        if (r.ExitCode == 0) return r.Stdout.TrimEnd();
        throw new InvalidOperationException(AskToolShared.Failure("gemini", r.ExitCode, r.Stderr));
    }
}

[McpServerToolType]
public sealed class SandboxTool
{
    [McpServerTool(Name = "sandbox")]
    [Description("Run a prompt through Gemini's sandbox mode (executes code in Gemini's sandbox, not the local machine).")]
    public static async Task<string> Sandbox(
        GeminiConfig cfg,
        [Description("The prompt (typically requesting code execution).")] string prompt)
    {
        if (GeminiValidation.ValidatePrompt(prompt) is { } pErr) throw new InvalidOperationException(pErr);

        var r = await GeminiCli.RunAsync(cfg,
            ["-p", prompt, "--sandbox", "--skip-trust", "--yolo"],
            cwd: cfg.SessionDir);

        if (r.ExitCode == 0) return r.Stdout.TrimEnd();
        throw new InvalidOperationException(AskToolShared.Failure("gemini sandbox", r.ExitCode, r.Stderr));
    }
}

[McpServerToolType]
public sealed class ImageDescribeTool
{
    [McpServerTool(Name = "image_describe")]
    [Description("Describe an image using Gemini Pro vision. The image must exist on disk and be readable.")]
    public static async Task<string> ImageDescribe(
        GeminiConfig cfg,
        [Description("Absolute path to the image (PNG, JPEG, WEBP).")] string image_path,
        [Description("Question/instruction about the image.")] string question = "Describe this image in detail.")
    {
        // Validate the path shape (control chars / leading '-' / length) before the
        // existence check + subprocess. The existing "image not found" behaviour is
        // preserved for a well-formed-but-absent path.
        if (GeminiValidation.ValidatePath(image_path, "image_path") is { } ipErr)
            throw new InvalidOperationException(ipErr);
        if (GeminiValidation.ValidateOptionalText(question, "question", GeminiValidation.MaxPromptLength) is { } qErr)
            throw new InvalidOperationException(qErr);

        if (!File.Exists(image_path))
            throw new InvalidOperationException($"image not found: {image_path}");

        var prompt = $"{question} @{image_path}";
        var r = await GeminiCli.RunAsync(cfg,
            ["-p", prompt, "--model", "pro", "--skip-trust", "--yolo"],
            cwd: cfg.SessionDir);

        if (r.ExitCode == 0) return r.Stdout.TrimEnd();
        throw new InvalidOperationException(AskToolShared.Failure("gemini", r.ExitCode, r.Stderr));
    }
}
