using Gdrive.Mcp;
using Sinapsi.Mcp;

var builder = WebApplication.CreateBuilder(args);

var cfg = GdriveConfig.FromEnvironment();
builder.Services.AddSingleton(cfg);

// Build the authenticated DriveService once at startup — fail fast if the OAuth client
// secrets or the persisted user token are missing (the one-time human consent step was
// skipped; see README → Google-console checklist).
var drive = await DriveClientFactory.CreateAsync(cfg, CancellationToken.None);
builder.Services.AddSingleton(drive);
// Backs download_to_url: short-lived capability tickets for the /gdrive-dl/{token} stream endpoint.
builder.Services.AddSingleton<DownloadTicketStore>();
// Outbound fetch client for upload_from_url — the MCP host pulls the source bytes and streams
// them into Drive. Its per-request Timeout is left infinite here; the tool bounds the whole
// fetch+upload with a linked CTS from GDRIVE_MCP_UPLOAD_TIMEOUT_SECONDS.
builder.Services.AddHttpClient("gdrive-fetch", c => c.Timeout = Timeout.InfiniteTimeSpan);

builder
    .AddSinapsiMcpServer("gdrive-mcp", "0.1.0")
    // Stateless transport strips a forwarded Mcp-Session-Id so a fronting proxy cannot
    // 400 an otherwise-valid request (e.g. a tools/list probe). See Sinapsi.Mcp MapSinapsiMcp.
    .WithHttpTransport(o => o.Stateless = true)
    .WithTools<DriveTools>();

var app = builder.Build();
var mapped = app.MapSinapsiMcp(envPrefix: "GDRIVE_MCP", defaultPort: 9217);
// Server-side streaming download endpoint (download_to_url). Lives on the same Kestrel host,
// outside /mcp — guarded by the unguessable short-lived ticket token.
mapped.MapDownloadEndpoint();
mapped.Run();
