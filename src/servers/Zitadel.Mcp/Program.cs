using System.Net.Http.Headers;
using Sinapsi.Mcp;
using Zitadel.Mcp;
using Zitadel.Mcp.Api;
using Zitadel.Mcp.Auth;
using Zitadel.Mcp.Tools;

var builder = WebApplication.CreateBuilder(args);
var cfg = ZitadelConfig.FromEnv();
builder.Services.AddSingleton(cfg);

// Typed HttpClient → ZitadelClient. The bearer is held server-side; a fronting gateway, if any,
// terminates the caller's own auth — the host only ever holds the service credential.
//
// Two auth modes, selected fail-closed in ZitadelConfig:
//   • SA-key mode (ZITADEL_SA_KEY_FILE set): a SaKeyAuthHandler mints + auto-refreshes a
//     short-lived JWT bearer per request (RFC 7523 jwt-bearer grant) — no long-lived credential is
//     held. The handler also sets the Host + X-Forwarded-Proto: https headers ZITADEL needs when
//     the API root is a LAN-bypass origin.
//   • PAT mode (only ZITADEL_TOKEN set): the static bearer is set once on the client's default
//     headers (unchanged legacy behaviour); no auth handler is added.
var httpClientBuilder = builder.Services.AddHttpClient<ZitadelClient>(c =>
{
    c.BaseAddress = new Uri($"{cfg.BaseUrl}/");
    if (cfg.AuthMode == ZitadelAuthMode.Pat)
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", cfg.Token);
    c.DefaultRequestHeaders.UserAgent.ParseAdd("sinapsi-zitadel-mcp/1.0");
    // Hard per-request ceiling (ZITADEL_HTTP_TIMEOUT_MS, default 30 s, validated fail-closed
    // in ZitadelConfig) so a hung upstream cannot wedge a tool call indefinitely.
    c.Timeout = TimeSpan.FromMilliseconds(cfg.HttpTimeoutMs);
});

if (cfg.AuthMode == ZitadelAuthMode.ServiceAccountKey)
{
    // A dedicated HttpClient for the token-mint call. AllowAutoRedirect = false so a 30x can't
    // silently drop the per-request Host override on the /oauth/v2/token call (Timeout is
    // Infinite; the per-call CancelAfter in the provider is the only deadline in play).
    builder.Services.AddHttpClient<JwtBearerTokenProvider>(c =>
        {
            c.Timeout = Timeout.InfiniteTimeSpan;
        })
        .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });

    builder.Services.AddTransient<SaKeyAuthHandler>();
    // Attach the mint-and-authorize handler to the ZitadelClient pipeline. Also disable
    // auto-redirect on the ZitadelClient's primary handler for the same host-override reason.
    httpClientBuilder
        .AddHttpMessageHandler<SaKeyAuthHandler>()
        .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
}

builder
    .AddSinapsiMcpServer("zitadel-mcp", "1.0.0")
    // Stateless transport strips a forwarded Mcp-Session-Id so a fronting proxy can't 400 it.
    .WithHttpTransport(o => o.Stateless = true)
    .WithTools<UserTools>()
    .WithTools<ProjectTools>()
    .WithTools<OidcAppTools>()
    .WithTools<MachineUserTools>();

var app = builder.Build();

// Boot log — name the active auth mode + key config inputs (no secret material).
{
    var bootLog = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("zitadel-mcp");
    var apiHost = new Uri(cfg.BaseUrl).Host;
    var deployMode = apiHost == cfg.HostHeader ? "normal" : "LAN-bypass";
    if (cfg.AuthMode == ZitadelAuthMode.ServiceAccountKey)
    {
        bootLog.LogInformation(
            "zitadel-mcp starting (auth=SA-key, {DeployMode}: api={BaseUrl}, issuer={Issuer}, Host header={HostHeader}, sa_key={SaKeyFile})",
            deployMode, cfg.BaseUrl, cfg.Issuer, cfg.HostHeader, cfg.SaKeyFile);
        // Boot-time SA-key validation. Warning only (NOT fail-fast) so a hot-rotate contract still
        // works if the secret mounts after Kestrel binds — the first tool call fails until it does.
        try
        {
            ServiceAccount.LoadFromFile(cfg.SaKeyFile!);
            bootLog.LogInformation("ZITADEL SA key validated at boot ({Path})", cfg.SaKeyFile);
        }
        catch (Exception e)
        {
            bootLog.LogWarning(
                "ZITADEL SA key validation FAILED at boot ({Path}): {Reason}. " +
                "The first tool call will fail until the SA file is present + valid.",
                cfg.SaKeyFile, e.Message);
        }
    }
    else
    {
        bootLog.LogInformation(
            "zitadel-mcp starting (auth=PAT, {DeployMode}: api={BaseUrl})", deployMode, cfg.BaseUrl);
    }
}

app.MapSinapsiMcp(envPrefix: "ZITADEL_MCP", defaultPort: cfg.Port).Run();
