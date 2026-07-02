using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Zitadel.Mcp;
using Zitadel.Mcp.Api;
using Zitadel.Mcp.Tools;
using Xunit;

namespace Zitadel.Mcp.Tests;

/// <summary>
/// Marshaller-level regression test for the camelCase param-binding bug.
///
/// The deployed <c>zitadel-mcp</c> image threw, for every tool with a camelCase C# parameter,
/// <c>System.ArgumentException: The arguments dictionary is missing a value for the required
/// parameter 'userId'</c> at the MCP SDK's <c>AIFunctionFactory</c> argument marshaller —
/// BEFORE the method body ran. Root cause: the MCP wire schema advertises the parameter in
/// snake_case (<c>user_id</c>), but the SDK marshaller looks up the ORIGINAL C# parameter name
/// (<c>userId</c>) in the incoming arguments dictionary, so a caller sending the snake_case name
/// from the schema can never be bound.
///
/// The previous PR (#38) only removed an interleaved DI param and its tests called the C# method
/// DIRECTLY (bypassing the marshaller), so the bug survived. This test exercises the ACTUAL MCP
/// tool-invocation path: it registers the real tool classes via the same <c>WithTools&lt;T&gt;()</c>
/// the server uses, builds a real <see cref="McpServer"/>, resolves the registered
/// <see cref="McpServerTool"/>, and invokes it through
/// <see cref="McpServerTool.InvokeAsync(RequestContext{CallToolRequestParams}, System.Threading.CancellationToken)"/>
/// with a snake_case argument dictionary — the same shape a caller sends from the advertised
/// schema. It asserts the marshaller BINDS (no missing-parameter <see cref="System.ArgumentException"/>).
///
/// On the OLD code (camelCase C# params) these tests FAIL with the exact deployed ArgumentException;
/// on the FIXED code (snake_case C# params == wire names) they PASS.
/// </summary>
public sealed class ZitadelToolMarshallerTests
{
    /// <summary>
    /// Build a real MCP server hosting the four Zitadel tool classes (exactly as Program.cs does),
    /// with a DI-registered <see cref="ZitadelClient"/> so the leading DI param resolves.
    /// </summary>
    private static (McpServer Server, IReadOnlyList<McpServerTool> Tools) BuildServer()
    {
        var services = new ServiceCollection();
        services.AddLogging();

        // A ZitadelClient is required as the leading DI param on every tool. The tools we invoke
        // fail input-validation first (we pass ids that fail ValidateId), so no HTTP call is made —
        // the client only needs to be resolvable, never used.
        services.AddSingleton(new ZitadelClient(new HttpClient(), ZitadelConfigForTest()));

        services
            .AddMcpServer()
            .WithTools<UserTools>()
            .WithTools<ProjectTools>()
            .WithTools<OidcAppTools>()
            .WithTools<MachineUserTools>();

        var sp = services.BuildServiceProvider();
        var opts = sp.GetRequiredService<IOptions<McpServerOptions>>().Value;
        var server = McpServer.Create(
            new InMemoryTransport(), opts, sp.GetRequiredService<ILoggerFactory>(), sp);
        return (server, opts.ToolCollection!.ToList());
    }

    private static ZitadelConfig ZitadelConfigForTest() =>
        new(
            BaseUrl: "https://zitadel.example.test",
            AuthMode: ZitadelAuthMode.Pat,
            Token: "test-token",
            SaKeyFile: null,
            Issuer: "https://zitadel.example.test",
            HostHeader: "zitadel.example.test",
            Port: ZitadelConfig.DefaultPort,
            AgentKeyDir: Path.Combine(Path.GetTempPath(), "zitadel-mcp-test-keys"),
            HttpTimeoutMs: ZitadelConfig.DefaultHttpTimeoutMs);

    /// <summary>
    /// Invoke a registered tool via the SDK marshaller with the given snake_case argument dictionary.
    /// Returns the tool result; a binding failure surfaces as the thrown ArgumentException.
    /// </summary>
    private static async Task<CallToolResult> InvokeAsync(string toolName, Dictionary<string, JsonElement> args)
    {
        var (server, tools) = BuildServer();
        var tool = tools.Single(t => t.ProtocolTool.Name == toolName);
        var req = new CallToolRequestParams { Name = toolName, Arguments = args };
        var jrpc = new JsonRpcRequest { Method = "tools/call", Id = new RequestId(1) };
        var ctx = new RequestContext<CallToolRequestParams>(server, jrpc, req);
        return await tool.InvokeAsync(ctx, CancellationToken.None);
    }

    private static JsonElement Str(string s) => JsonSerializer.SerializeToElement(s);

    [Fact]
    public async Task get_user_binds_a_snake_case_user_id_argument()
    {
        // OLD code: throws ArgumentException("...missing a value for the required parameter 'userId'").
        // We pass an id that fails ValidateId (path separator) so the body returns a structured error
        // WITHOUT any HTTP call — reaching that body PROVES the marshaller bound the argument.
        var result = await InvokeAsync("get_user", new() { ["user_id"] = Str("a/b") });

        // No exception thrown => the marshaller found "user_id". The tool then rejected the id.
        Assert.False(result.IsError ?? false);
        Assert.Contains("path separator", TextOf(result));
    }

    [Fact]
    public async Task create_machine_key_binds_snake_case_user_id_and_agent_file_arguments()
    {
        // The specific tool the incident named. Same discriminator: a path-separator user_id fails
        // validation in-body, so binding success is the only way we get a structured result rather
        // than the ArgumentException.
        var result = await InvokeAsync("create_machine_key", new()
        {
            ["user_id"] = Str("a/b"),
            ["agent_file"] = Str("agent-journey-ux"),
        });

        Assert.False(result.IsError ?? false);
        Assert.Contains("path separator", TextOf(result));
    }

    [Fact]
    public async Task create_machine_user_still_binds_its_lowercase_arguments()
    {
        // Regression guard for the already-working all-lowercase tool: renaming its camelCase
        // access_token_type param must not break username/name/description binding.
        var result = await InvokeAsync("create_machine_user", new()
        {
            ["username"] = Str(""),  // empty -> fails ValidateName -> structured error, no HTTP call
        });

        Assert.False(result.IsError ?? false);
        Assert.Contains("username is required", TextOf(result));
    }

    private static string TextOf(CallToolResult result) =>
        string.Concat(result.Content.OfType<TextContentBlock>().Select(b => b.Text));

    /// <summary>Minimal no-op transport so <see cref="McpServer.Create"/> can build a server for
    /// direct tool invocation in-process (no real client session is driven).</summary>
    private sealed class InMemoryTransport : ITransport
    {
        private readonly Channel<JsonRpcMessage> _ch = Channel.CreateUnbounded<JsonRpcMessage>();
        public string? SessionId => "test-session";
        public ChannelReader<JsonRpcMessage> MessageReader => _ch.Reader;
        public Task SendMessageAsync(JsonRpcMessage message, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
        public ValueTask DisposeAsync() { _ch.Writer.TryComplete(); return ValueTask.CompletedTask; }
    }
}
