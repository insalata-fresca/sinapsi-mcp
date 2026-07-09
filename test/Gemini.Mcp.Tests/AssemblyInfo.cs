// Disable xUnit's default parallel execution of test COLLECTIONS in this assembly.
//
// Some tests here mutate process-global state — environment variables
// (GeminiConfigTests via Environment.SetEnvironmentVariable) and a persisted
// task-JSON file read back by ToolSurfaceTests.GetStatus_*. xUnit runs distinct
// test classes in parallel by default, so these classes race on that shared
// state, producing INTERMITTENT failures (observed on CI run #180:
// GetStatus_returns_the_persisted_task_json_after_research → JsonReaderException
// "',' is invalid after a single JSON value" from a half-written/overlapping file).
//
// Serialising the collections removes the shared-state race. Tests WITHIN a class
// already run sequentially. Test-isolation fix only — no production code affected.
[assembly: Xunit.CollectionBehavior(DisableTestParallelization = true)]
