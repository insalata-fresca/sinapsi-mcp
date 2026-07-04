// ---------------------------------------------------------------------------
// McpComposition - the ONE place that wires the capability-selected MCP tool
// types onto the MCP server builder.
//
// WHY this is its own method (not inline in Program.cs): the tool-registration
// call is a KNOWN overload-resolution footgun (see AddCapabilityTools below),
// and the regression it caused - the MCP host serving ZERO tools at runtime -
// is invisible to a compile and to a reflection-only unit test. Factoring it
// here lets McpBootSmokeTests boot the REAL registration path (this exact call)
// against a fake store/embedder and assert tools/list over HTTP, so the bug can
// never silently regress. Program.cs and the smoke test call the SAME method.
//
// Plain-ASCII banner so this source diffs as TEXT, never binary.
// ---------------------------------------------------------------------------

using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Server;

namespace Sinapsi.Indexer;

/// <summary>
/// Wires the MCP server + HTTP transport + the capability-selected tool TYPES
/// onto <paramref name="services"/>. This is the exact composition Program.cs
/// uses; it is factored out ONLY so the runtime boot smoke can exercise the real
/// registration path.
/// </summary>
public static class McpComposition
{
    /// <summary>
    /// Add the MCP server (HTTP transport, stateless) and register the tool types
    /// selected by <paramref name="caps"/>. Returns the builder for chaining.
    /// </summary>
    public static IMcpServerBuilder AddIndexerMcp(this IServiceCollection services, IndexerCapabilities caps)
    {
        var mcpToolTypes = caps.McpToolTypes();
        var mcpBuilder = services
            .AddMcpServer(o => o.ServerInfo = new() { Name = "sinapsi-indexer", Version = "1.0.0" })
            // Stateless transport strips a forwarded Mcp-Session-Id so a fronting proxy can't 400 it.
            .WithHttpTransport(o => o.Stateless = true);
        if (mcpToolTypes.Count > 0)
            AddCapabilityTools(mcpBuilder, mcpToolTypes);
        return mcpBuilder;
    }

    /// <summary>
    /// Register the given tool TYPES with the MCP server. The explicit
    /// <see cref="IEnumerable{Type}"/> cast is LOAD-BEARING, not cosmetic.
    ///
    /// <para>
    /// Passing an <c>IReadOnlyList&lt;Type&gt;</c> (or any concrete list type)
    /// POSITIONALLY to <c>WithTools</c> binds the WRONG overload: C# overload
    /// resolution prefers the generic
    /// <c>WithTools&lt;TToolType&gt;(this IMcpServerBuilder, TToolType target, ...)</c>
    /// - an exact generic-inference match with <c>TToolType</c> = the list type,
    /// needing NO conversion - over the intended
    /// <c>WithTools(this IMcpServerBuilder, IEnumerable&lt;Type&gt;, ...)</c>,
    /// which requires a covariant reference conversion. The generic-target
    /// overload then scans the LIST's own methods for
    /// <c>[McpServerTool]</c> (there are none) and registers ZERO tools, so the
    /// MCP host serves no tools at runtime (<c>tools/list</c> -&gt; -32601
    /// "not available") even though the image builds and indexes fine.
    /// </para>
    ///
    /// <para>The cast forces the intended non-generic type-list overload. Guarded
    /// at runtime by <c>McpBootSmokeTests</c>. See docs indexer-generalization.</para>
    /// </summary>
    public static IMcpServerBuilder AddCapabilityTools(IMcpServerBuilder builder, IEnumerable<Type> toolTypes)
        => builder.WithTools(toolTypes);
}
