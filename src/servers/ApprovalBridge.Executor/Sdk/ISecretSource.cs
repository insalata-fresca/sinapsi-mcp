namespace ApprovalBridge.Executor.Sdk;

/// <summary>
/// The register-secret <b>Path D</b> abstraction, generalized (home-server
/// <c>services/claude-root/patterns/register-secret.md</c> Path D; <c>docs/66 §4</c>). A secret source
/// reads a named secret <b>target-side, under the target's own scoped identity</b> — an
/// <c>infisical run</c> injection or a <c>0600</c> file on the target host. It is the ONLY place a target
/// secret is ever materialised, and it exists only inside the executor process on the target: the broker
/// and the requesting agent never construct one and never see its output.
///
/// <para>The seal (I2) is structural precisely because the secret enters the world here and leaves only
/// as a validated, non-secret <see cref="ExecutorResult"/>. An implementation MUST NOT log the value, put
/// it in an exception message, or return it in any <c>result_schema</c> field.</para>
/// </summary>
public interface ISecretSource
{
    /// <summary>Read the secret named <paramref name="name"/> (e.g. <c>GARMIN_OAUTH_CLIENT_SECRET</c>)
    /// target-side under the target identity. The value never leaves the executor.</summary>
    Task<string> GetSecretAsync(string name, CancellationToken ct = default);
}
