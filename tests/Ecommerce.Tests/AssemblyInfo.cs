using Xunit;

// Serialised for the reason tests/FlowX.Hosting.Tests/AssemblyInfo.cs gives, and one more that
// is specific to this assembly.
//
// TelemetryTests scrapes the sample's /metrics endpoint and asserts on the counts it finds
// there. Each test host builds its own SampleTelemetry, but a MeterListener subscribes to the
// FlowX meter — which is a static, process-wide instrument set — so a listener built by one
// host records measurements produced by another host running in parallel. Asserting
// `flowx_flow_total{...,outcome="Success"} 1` is then a statement about however many flows the
// whole process happened to run, which is a number no assertion can name.
//
// That is a property of the metric being global, and it is the correct property: a Meter is
// per-process by design, and per-host meters would mean an operator scraping one application
// getting one series per DI container in it.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
