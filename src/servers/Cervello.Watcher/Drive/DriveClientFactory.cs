using Google.Apis.Auth.OAuth2;
using Google.Apis.Drive.v3;
using Google.Apis.Services;

namespace Cervello.Watcher.Drive;

/// <summary>
/// Builds a single read-only <see cref="DriveService"/> for the process from the
/// service-account JSON key (D1 divergence from Gdrive.Mcp's OAuth refresh token).
///
/// - Scope is <see cref="DriveService.ScopeConstants.DriveReadonly"/> only —
///   least-privilege, folder-limited by the SA's share (no interactive consent).
/// - Egress is forced through <see cref="ProxyHttpClientFactory"/> (D2) —
///   nftables drops direct Google egress (invariant 7).
/// - The per-request timeout is clamped (fail-closed in WatcherConfig) so a hung
///   Google call cannot pin the poll loop forever.
/// </summary>
public sealed class DriveClientFactory
{
    public static DriveService Create(WatcherConfig cfg)
    {
        using var keyStream = File.OpenRead(cfg.ServiceAccountKeyPath);
        // Non-obsolete path (GoogleCredential.FromStream is deprecated): build the
        // read-only SA credential explicitly (D1), then adapt to a GoogleCredential
        // and scope it to DriveReadonly.
        var credential = CredentialFactory
            .FromStream<ServiceAccountCredential>(keyStream)
            .ToGoogleCredential()
            .CreateScoped(DriveService.ScopeConstants.DriveReadonly);

        var drive = new DriveService(new BaseClientService.Initializer
        {
            HttpClientInitializer = credential,
            HttpClientFactory = new ProxyHttpClientFactory(cfg.HttpProxyUrl),
            ApplicationName = cfg.ApplicationName,
        });

        // Clamp the per-request timeout (ceiling validated fail-closed in
        // WatcherConfig). MediaDownloader still chunks large files, resetting the
        // timeout per chunk rather than racing it.
        drive.HttpClient.Timeout = TimeSpan.FromSeconds(cfg.HttpTimeoutSeconds);
        return drive;
    }
}
