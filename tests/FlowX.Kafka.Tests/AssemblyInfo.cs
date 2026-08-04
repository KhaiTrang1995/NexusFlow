using Xunit;

// One cluster, and every fixture creates its own topics — so isolation is real and this could run
// in parallel. It does not, because a Kafka consumer group rebalance is cluster-wide work and
// eight of them at once turns a 30-second bound into a coin toss. Slower, and deterministic.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
