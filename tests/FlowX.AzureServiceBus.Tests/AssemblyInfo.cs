using Xunit;

// One namespace, one topic, one set of subscriptions.
//
// ADR-0074 is why: this package creates no entities, so a fixture cannot give itself a private
// topology the way RabbitMqBrokerUnderTest does. Every test in this assembly therefore shares the
// entities emulator-config.json declares, and two classes running at once would read each other's
// messages — which shows up as an ordering failure in the conformance suite and as a stray
// delivery in the consumer suite, both of them looking like defects in the plugin.
//
// Slower, and correct. The alternative is a suite whose failures are about xUnit's scheduler.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
