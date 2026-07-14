using Sinapsi.Mcp;
using Sshgw.Mcp;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSingleton(SshgwOptions.FromEnvironment());
// The server registry + per-server command whitelist + per-server read_file path
// policy are all loaded once from a JSON registry file (path is env-driven; see
// SshgwOptions). This is a config-driven gateway: it reaches only the servers the
// registry names, with only the commands/paths the registry permits.
builder.Services.AddSingleton<ServerRegistry>();
builder.Services.AddSingleton<SshClient>();
// So execute-command can read Envoy's x-request-id as the cross-layer correlation id.
builder.Services.AddHttpContextAccessor();

// Q2 decision emission (authorization plane, docs/61). Opt-in: only when a scoped
// publish-only NATS identity is configured (SSHGW_AUTHZ_NATS_*). Absent ⇒ null ⇒ the
// ExecuteCommand `authz` param stays null ⇒ no emission, MCP runs unchanged.
if (AuthzDecisionPublisher.FromEnvironmentOrNull() is { } authzPublisher)
    builder.Services.AddSingleton(authzPublisher);

builder
    .AddSinapsiMcpServer("sshgw-mcp", "0.1.0")
    .WithHttpTransport(o => o.Stateless = true)
    .WithTools<SshgwTools>();

var app = builder.Build();

app.MapSinapsiMcp(envPrefix: "SSHGW_MCP", defaultPort: 9204).Run();
