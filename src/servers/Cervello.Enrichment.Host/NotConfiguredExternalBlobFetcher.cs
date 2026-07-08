using Cervello.Enrichment.Adapters;

namespace Cervello.Enrichment.Host;

/// <summary>
/// The L2 deploy-time registration of the <see cref="IExternalBlobFetcher"/> seam — the ONE port the
/// engine's live composition root (<c>AddCervelloEnrichment</c>) deliberately leaves unregistered
/// (documented + asserted by <c>CompositionCompletenessTests</c>: "LIST it, do not fake it"). Its live
/// implementation fetches <c>drive://</c> / <c>gmail://</c> evidence bytes through the CT121
/// agentgateway (a scoped MCP identity) so <see cref="Cervello.Enrichment.Adapters.CtPinStore"/> can
/// pin cited external evidence as <c>pin://&lt;sha256&gt;</c>.
///
/// <para><b>Why a CLEAR-THROWING placeholder, not a stub that returns bytes.</b> The RECORDINGS
/// ingestion path never cites <c>drive://</c>/<c>gmail://</c> external evidence — its evidence is the
/// on-CT audio + transcript, so <see cref="Cervello.Enrichment.Adapters.CtPinStore.PinAsync"/> (the
/// only caller of this fetcher) is not reached when draining a recording. Registering this throwing
/// adapter lets the FULL live DI graph resolve (so the host boots + the pipeline is constructible)
/// WITHOUT silently pinning garbage: a fake that returned bytes would let a future document/mail
/// ingestion path pin a wrong blob undetected. If this is ever actually called it throws a clear,
/// actionable message — the signal to wire the real agentgateway-backed adapter (deferred to the
/// doc/mail ingestion mission), never to paper over it.</para>
/// </summary>
public sealed class NotConfiguredExternalBlobFetcher : IExternalBlobFetcher
{
    public Task<ReadOnlyMemory<byte>> FetchAsync(string externalRef, CancellationToken ct = default) =>
        throw new NotSupportedException(
            "IExternalBlobFetcher is NOT configured on this host. The live drive://gmail:// evidence " +
            "fetcher (via the CT121 agentgateway + a scoped MCP identity) is deferred to the document/" +
            "mail ingestion mission. The RECORDINGS drain path never cites external evidence, so this " +
            "must not be reached during recording enrichment. Reaching it means a non-recording code " +
            "path needs pin://-on-cite — wire the real adapter before enabling that path. " +
            $"(externalRef='{externalRef}')");
}
