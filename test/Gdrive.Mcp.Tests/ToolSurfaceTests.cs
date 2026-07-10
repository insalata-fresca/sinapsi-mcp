using System.ComponentModel;
using System.Reflection;
using ModelContextProtocol.Server;
using Xunit;

namespace Gdrive.Mcp.Tests;

/// <summary>
/// Parity guard: <see cref="DriveTools"/> must declare exactly the 13 Drive tools by name,
/// each carrying both an <see cref="McpServerToolAttribute"/> and a description. A dropped
/// or renamed tool fails the test gate.
/// </summary>
public sealed class ToolSurfaceTests
{
    private static readonly string[] Expected =
    {
        "list_files", "search_files", "get_file_metadata",
        "download_file", "download_file_base64", "download_to_url",
        "create_file", "update_file", "delete_file",
        "create_folder", "upload_file", "move_file",
        "upload_from_url",
    };

    private static IEnumerable<(string name, MethodInfo m)> ToolMethods() =>
        typeof(DriveTools)
            .GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Select(m => (attr: m.GetCustomAttribute<McpServerToolAttribute>(), m))
            .Where(x => x.attr is not null)
            .Select(x => (x.attr!.Name!, x.m));

    [Fact]
    public void TypeCarriesMcpServerToolTypeAttribute()
    {
        Assert.NotNull(typeof(DriveTools).GetCustomAttribute<McpServerToolTypeAttribute>());
    }

    [Fact]
    public void ExposesExactlyTheThirteenTools()
    {
        var names = ToolMethods().Select(t => t.name).OrderBy(n => n).ToArray();
        Assert.Equal(Expected.OrderBy(n => n).ToArray(), names);
    }

    [Fact]
    public void UploadFromUrl_IsExposed()
    {
        // The server-side-streaming upload path — no bytes through the model context.
        Assert.Contains("upload_from_url", ToolMethods().Select(t => t.name).ToHashSet());
    }

    [Fact]
    public void EveryToolHasADescription()
    {
        foreach (var (name, m) in ToolMethods())
        {
            var desc = m.GetCustomAttribute<DescriptionAttribute>();
            Assert.True(desc is not null && !string.IsNullOrWhiteSpace(desc.Description),
                $"tool '{name}' is missing a [Description]");
        }
    }

    [Fact]
    public void TheTwoMutatingTools_AreExposed()
    {
        // update_file + delete_file are the reason this server exists beyond a read-only connector.
        var names = ToolMethods().Select(t => t.name).ToHashSet();
        Assert.Contains("update_file", names);
        Assert.Contains("delete_file", names);
    }

    [Fact]
    public void TheThreeV3VoiceprintNamingTools_AreExposed()
    {
        // create_folder + upload_file + move_file — added for docs/design/voiceprint-naming.md
        // §7/§8 V3: provisioning cervello/recordings/voiceprints/{,registry/}, uploading a
        // representative audio clip, and moving a named clip into registry/.
        var names = ToolMethods().Select(t => t.name).ToHashSet();
        Assert.Contains("create_folder", names);
        Assert.Contains("upload_file", names);
        Assert.Contains("move_file", names);
    }
}
