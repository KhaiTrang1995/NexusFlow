using Xunit;

// Telemetry is process-wide, so tests that observe it cannot run beside tests that produce it.
//
// An ActivityListener and a MeterListener subscribe to a source and a meter, not to a caller.
// TelemetryConformanceTests attaches one and then asserts "exactly one span named
// 'flow order.place'" — and xUnit runs test classes in parallel by default, so DrainTests
// executing the same plan on another thread lands in the same recorder and the assertion is
// about both. That is not a flaky test; it is a correct observation of a shared subject.
//
// Serialising this assembly is the honest fix. The alternatives are worse: filtering captured
// spans by trace id assumes an ambient Activity survives a transport boundary it does not have
// to, and relaxing the assertions to "at least one" would give up the thing the conformance
// gate is for — that a flow emits one span rather than several, and that a step is not spanned
// twice by an accidentally doubled decorator.
//
// The cost is measured rather than assumed: this assembly runs in well under a second either
// way, because nothing in it sleeps for a real interval.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
