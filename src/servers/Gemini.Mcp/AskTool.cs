using System.ComponentModel;
using ModelContextProtocol.Server;

namespace Gemini.Mcp;

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
        var full = string.IsNullOrEmpty(system) ? prompt : $"{system}\n\n{prompt}";
        var r = await GeminiCli.RunAsync(cfg,
            ["-p", full, "--model", model, "--skip-trust", "--yolo"],
            cwd: cfg.SessionDir);

        if (r.ExitCode == 0) return r.Stdout.TrimEnd();
        throw new InvalidOperationException($"gemini exited {r.ExitCode}: {Jsons.TailLeft(r.Stderr, 300)}");
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
        var mentions = string.Join(" ", file_paths.Select(p => $"@{p}"));
        var full = $"{prompt}\n\n{mentions}";
        var r = await GeminiCli.RunAsync(cfg,
            ["-p", full, "--model", model, "--skip-trust", "--yolo"],
            cwd: cfg.SessionDir);

        if (r.ExitCode == 0) return r.Stdout.TrimEnd();
        throw new InvalidOperationException($"gemini exited {r.ExitCode}: {Jsons.TailLeft(r.Stderr, 300)}");
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
        var r = await GeminiCli.RunAsync(cfg,
            ["-p", prompt, "--sandbox", "--skip-trust", "--yolo"],
            cwd: cfg.SessionDir);

        if (r.ExitCode == 0) return r.Stdout.TrimEnd();
        throw new InvalidOperationException($"gemini sandbox exited {r.ExitCode}: {Jsons.TailLeft(r.Stderr, 300)}");
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
        if (!File.Exists(image_path))
            throw new InvalidOperationException($"image not found: {image_path}");

        var prompt = $"{question} @{image_path}";
        var r = await GeminiCli.RunAsync(cfg,
            ["-p", prompt, "--model", "pro", "--skip-trust", "--yolo"],
            cwd: cfg.SessionDir);

        if (r.ExitCode == 0) return r.Stdout.TrimEnd();
        throw new InvalidOperationException($"gemini exited {r.ExitCode}: {Jsons.TailLeft(r.Stderr, 300)}");
    }
}
