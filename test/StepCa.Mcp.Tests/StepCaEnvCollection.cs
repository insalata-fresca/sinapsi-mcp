using Xunit;

namespace StepCa.Mcp.Tests;

/// <summary>
/// Serializes all test classes marked [Collection("stepca-env")] so that:
/// - <see cref="StepCaOptionsTests"/> (env-var mutations) never run in
///   parallel with each other or with the subprocess-spawn tests, avoiding
///   races on the process environment.
/// - <see cref="SerialAndCliTests"/> (real subprocess / timeout kill path)
///   is not preempted by other test-runner threads competing for the same
///   OS scheduler quanta, which caused the 200 ms kill-timeout assertion to
///   report stale state intermittently under parallel load.
///
/// DisableParallelization = true means every class in this collection is run
/// sequentially (one at a time), while other collections in the solution still
/// run in parallel as normal.
/// </summary>
[CollectionDefinition("stepca-env", DisableParallelization = true)]
public sealed class StepCaEnvCollection { }
