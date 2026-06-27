using Xunit;

namespace Sinapsi.Nats.Tests;

/// <summary>Serialises tests that mutate process environment variables so they do not
/// race each other (xunit runs test classes in parallel by default).</summary>
[CollectionDefinition("env", DisableParallelization = true)]
public sealed class EnvCollection { }
