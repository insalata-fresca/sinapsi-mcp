using ApprovalBridge.Executor.Registry;
using ApprovalBridge.Executor.Sdk;

namespace ApprovalBridge.Executor.PathD;

/// <summary>
/// The register-secret Path D <b>file-based delivery</b> variant (pattern step 4: "delivered to disk via
/// Ansible to <c>/etc/&lt;service&gt;/&lt;id&gt;.seed</c> (0600); the service reads the file path"). Reads a
/// secret from a <c>0600</c> file named after the secret under <see cref="_secretsDir"/>. It exists only
/// target-side, inside the executor process, under the target's own identity — the broker never constructs one.
/// </summary>
public sealed class FileSecretSource : ISecretSource
{
    private readonly string _secretsDir;

    public FileSecretSource(string secretsDir)
    {
        if (string.IsNullOrWhiteSpace(secretsDir))
            throw new ArgumentException("secrets directory is required", nameof(secretsDir));
        _secretsDir = secretsDir;
    }

    public async Task<string> GetSecretAsync(string name, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Contains('/') || name.Contains('\\') || name.Contains(".."))
            throw new ExecutorException("invalid secret name"); // never echo the value or the resolved path
        var path = Path.Combine(_secretsDir, name);
        if (!File.Exists(path))
            throw new ExecutorException($"secret '{name}' not provisioned target-side");
        return (await File.ReadAllTextAsync(path, ct)).Trim();
    }
}

/// <summary>Builds a <see cref="FileSecretSource"/> per action, rooted at a per-target-identity directory
/// (<c>&lt;root&gt;/&lt;target-identity&gt;/</c>) so each target reads only its own secrets (I2, per-target
/// scoping like the per-host <c>deploy-controller</c> nkeys).</summary>
public sealed class FileSecretSourceFactory : ISecretSourceFactory
{
    private readonly string _rootDir;

    public FileSecretSourceFactory(string rootDir)
    {
        if (string.IsNullOrWhiteSpace(rootDir))
            throw new ArgumentException("secrets root directory is required", nameof(rootDir));
        _rootDir = rootDir;
    }

    public ISecretSource ForTarget(ExecutorActionDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        return new FileSecretSource(Path.Combine(_rootDir, definition.TargetIdentity));
    }
}
