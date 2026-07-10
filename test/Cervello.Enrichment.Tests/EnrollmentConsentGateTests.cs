using Cervello.Enrichment.Adapters;
using Cervello.Enrichment.Domain;
using Xunit;

namespace Cervello.Enrichment.Tests;

/// <summary>
/// The §10 gate UNIONs the static allowlist with the durable rename-consent store (V5, design §9 fork
/// 1). A person not on the deploy-time allowlist but consented-by-rename enrolls; a person on NEITHER
/// is still hard-refused (the biometric write-gate never opens for an unconsented person).
/// </summary>
public sealed class EnrollmentConsentGateTests
{
    private static readonly DateOnly On = new(2026, 7, 10);

    [Fact] // rename-consent opens the gate for a person NOT on the static allowlist
    public async Task Consent_by_rename_lets_an_off_allowlist_person_enroll()
    {
        var consent = new InMemoryEnrollmentConsentStore();
        var store = new InMemoryVoiceprintStore(EnrollmentAllowlist.Empty, consent);

        // Without consent, the empty allowlist hard-refuses.
        await Assert.ThrowsAsync<EnrollmentNotAllowedException>(() =>
            store.EnrollOrRefineAsync("marco", TestVectors.Axis(1), ["seg"], null, On));

        // Add rename-consent → the same enroll now passes.
        await consent.AddConsentAsync("marco", "human://rename:file-3");
        var print = await store.EnrollOrRefineAsync("marco", TestVectors.Axis(1), ["seg"], null, On);
        Assert.Equal("marco", print.PersonSlug);
    }

    [Fact] // a person on NEITHER the allowlist nor the consent store is still refused (gate never opens blindly)
    public async Task Unconsented_person_is_still_refused()
    {
        var consent = new InMemoryEnrollmentConsentStore();
        var store = new InMemoryVoiceprintStore(EnrollmentAllowlist.Empty, consent);
        await consent.AddConsentAsync("marco", "human://rename:file-3"); // a DIFFERENT person consented

        await Assert.ThrowsAsync<EnrollmentNotAllowedException>(() =>
            store.EnrollOrRefineAsync("eve", TestVectors.Axis(2), ["seg"], null, On));
    }

    [Fact] // the static allowlist still works with no consent store wired (null → behaviour unchanged)
    public async Task Static_allowlist_still_gates_when_no_consent_store()
    {
        var store = new InMemoryVoiceprintStore(new EnrollmentAllowlist(["marco"])); // no consent store
        var print = await store.EnrollOrRefineAsync("marco", TestVectors.Axis(1), ["seg"], null, On);
        Assert.Equal("marco", print.PersonSlug);
        await Assert.ThrowsAsync<EnrollmentNotAllowedException>(() =>
            store.EnrollOrRefineAsync("eve", TestVectors.Axis(2), ["seg"], null, On));
    }
}
