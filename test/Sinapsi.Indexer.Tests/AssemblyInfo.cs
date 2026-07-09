// Disable xUnit's default parallel execution of test COLLECTIONS in this assembly.
//
// Several config tests here mutate PROCESS-GLOBAL environment variables
// (INDEXER_RESCAN_INTERVAL_MIN, INDEXER_CAP_*, INDEXER_NATS_MODE, …) via
// Environment.SetEnvironmentVariable. xUnit runs distinct test classes in
// parallel by default, so IndexerConfigTests and TimerOnlyIndexWorkerTests
// race on the same env vars — one class's "not-a-number" / garbage value bleeds
// into another class mid-run, producing INTERMITTENT failures (observed on CI
// run #180/#181 and reproducible ~1-in-3 locally in isolation):
//   - TimerOnlyIndexWorkerTests … : INDEXER_RESCAN_INTERVAL_MIN='not-a-number' is invalid
//   - IndexerConfigTests.CapFlag_GarbageValue_Throws_NamingTheVar : expected throw, none thrown
//
// Serialising the collections removes the env-var bleed (each test observes only
// the state it sets). Tests WITHIN a class already run sequentially. This is a
// test-isolation fix only — no production code is affected.
[assembly: Xunit.CollectionBehavior(DisableTestParallelization = true)]
