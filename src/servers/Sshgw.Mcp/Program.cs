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

builder
    .AddSinapsiMcpServer("sshgw-mcp", "0.1.0")
    .WithHttpTransport(o => o.Stateless = true)
    .WithTools<SshgwTools>();

var app = builder.Build();

app.MapSinapsiMcp(envPrefix: "SSHGW_MCP", defaultPort: 9204).Run();
