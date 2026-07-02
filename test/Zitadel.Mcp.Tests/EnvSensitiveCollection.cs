using Xunit;

namespace Zitadel.Mcp.Tests;

/// <summary>
/// Shared xUnit collection for tests that mutate process-global environment variables around
/// <see cref="Zitadel.Mcp.ZitadelConfig.FromEnv"/>. xUnit runs test classes in <b>different</b>
/// collections in parallel; two classes both calling <c>Environment.SetEnvironmentVariable</c>
/// would race on the same global state (each restores its own vars in a <c>finally</c>, but an
/// interleaved read from the other class sees the wrong value). Placing every env-mutating class
/// in this one collection makes them run serially — never in parallel with each other.
/// </summary>
[CollectionDefinition(Name)]
public sealed class EnvSensitiveCollection
{
    public const string Name = "env-sensitive";
}
