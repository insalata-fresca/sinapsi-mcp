using System.Net;
using Google.Apis.Http;

namespace Cervello.Watcher.Drive;

/// <summary>
/// D2 — routes ALL Drive HTTP traffic through the CT egress proxy
/// (tinyproxy-cervello, default <c>http://127.0.0.1:13130</c>). nftables drops
/// direct Google egress (invariant 7), so proxying is a PROPERTY OF THE CLIENT,
/// not luck: this factory is passed as the DriveService's
/// <c>BaseClientService.Initializer.HttpClientFactory</c>.
///
/// The base <see cref="Google.Apis.Http.HttpClientFactory"/> already applies its
/// <c>Proxy</c> to every handler it creates; we subclass only to (a) construct it
/// from a URL string and (b) expose the <see cref="WebProxy"/> so a unit test can
/// assert the address WITHOUT a live Google call.
/// </summary>
public sealed class ProxyHttpClientFactory : Google.Apis.Http.HttpClientFactory
{
    /// <summary>The proxy this factory sets on every client it creates. Test-assertable.</summary>
    public WebProxy WebProxy { get; }

    public ProxyHttpClientFactory(string proxyUrl)
        : base(new WebProxy(new Uri(proxyUrl)))
    {
        // base(IWebProxy) stores it in the inherited Proxy property and applies it
        // to CreateHandler; we retain the concrete type for the assertion seam.
        WebProxy = (WebProxy)Proxy!;
    }

    /// <summary>The configured proxy address (e.g. <c>http://127.0.0.1:13130/</c>).</summary>
    public Uri ProxyAddress => WebProxy.Address!;
}
