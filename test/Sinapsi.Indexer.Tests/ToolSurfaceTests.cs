// ---------------------------------------------------------------------------
// ToolSurfaceTests - parity guard: the indexer must declare exactly its four
// tools by name (3 read on IndexTools + 1 write on LearnTools), each carrying an
// McpServerTool attribute and a description. A dropped/renamed tool fails here.
// Plain-ASCII banner so this source diffs as TEXT, never binary.
// ---------------------------------------------------------------------------

using System.Reflection;
using ModelContextProtocol.Server;
using Sinapsi.Indexer;
using Xunit;

namespace Sinapsi.Indexer.Tests;

public sealed class ToolSurfaceTests
{
    private static readonly string[] Expected =
    {
        "search_index", "semantic_search", "get_learning", "publish_learning",
    };

    private static IEnumerable<(string name, MethodInfo m)> ToolMethods(Type t) =>
        t.GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Select(m => (attr: m.GetCustomAttribute<McpServerToolAttribute>(), m))
            .Where(x => x.attr is not null)
            .Select(x => (x.attr!.Name!, x.m));

    private static IEnumerable<(string name, MethodInfo m)> AllToolMethods() =>
        ToolMethods(typeof(IndexTools)).Concat(ToolMethods(typeof(LearnTools)));

    [Fact]
    public void ToolTypesCarryMcpServerToolTypeAttribute()
    {
        Assert.NotNull(typeof(IndexTools).GetCustomAttribute<McpServerToolTypeAttribute>());
        Assert.NotNull(typeof(LearnTools).GetCustomAttribute<McpServerToolTypeAttribute>());
    }

    [Fact]
    public void ExposesExactlyTheFourTools()
    {
        var names = AllToolMethods().Select(t => t.name).OrderBy(n => n).ToArray();
        Assert.Equal(Expected.OrderBy(n => n).ToArray(), names);
    }

    [Fact]
    public void EveryToolHasADescription()
    {
        foreach (var (name, m) in AllToolMethods())
        {
            var desc = m.GetCustomAttribute<System.ComponentModel.DescriptionAttribute>();
            Assert.True(desc is not null && !string.IsNullOrWhiteSpace(desc.Description),
                $"tool '{name}' is missing a [Description]");
        }
    }
}
