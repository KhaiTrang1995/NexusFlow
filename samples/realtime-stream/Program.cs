using FlowX;
using FlowX.Generated;
using FlowX.Hosting;
using FlowX.Postgres;
using FlowX.Redis;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;
using RealtimeStream;
using StackExchange.Redis;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddFlowX(options =>
{
    options.ApplicationName = "RealtimeStream";

    // Which node this is, as a lease records its owner. One node reads one subscription at a
    // time — two readers would each hold half of every window — so running this sample twice
    // with two names is how "the second node is refused and waits" is demonstrated without a
    // cluster.
    if (Environment.GetEnvironmentVariable("FLOWX_SAMPLE_NODE") is { Length: > 0 } node)
    {
        options.NodeName = node;
    }

    // How often a node looks at the stream. It is NOT the latency of a window: a window closes
    // when the watermark reaches its upper bound, and the watermark moves only when a record
    // with a later event time arrives. A stream that goes quiet leaves its last window open
    // indefinitely, and no value here changes that (ADR-0056).
    options.StreamScanInterval = TimeSpan.FromMilliseconds(250);

    // The two numbers the sample's whole memory story is made of, and the reason they are here
    // rather than on the [StreamTrigger].
    //
    // The window is a property of the business operation — `tumbling:1m` is what the flow's
    // output *means* — so it travels with the registration the compiler emits. These two are
    // tuning: how much of the stream this node is willing to hold in RAM while it works. A
    // deployment may raise them because it has the memory; raising them changes no aggregate.
    //
    //   StreamChannelCapacity     how many records may sit between the reader and the windowing
    //                             half. When it is full the engine issues no read at all, so
    //                             the backlog stays in Redis. This is the bound
    //                             BoundedMemoryTests measures.
    //   StreamMaxResidentRecords  how many records may sit in open windows at once. Exceeding
    //                             it is a refusal, not an eviction: a window emitted without
    //                             some of its records is an aggregate that is quietly wrong.
    options.StreamChannelCapacity = 256;
    options.StreamMaxResidentRecords = 20_000;
});

// The journal, the lease store and the checkpoint store's data source.
//
// A Streaming flow is journaled exactly as a Durable one is, and that is not incidental to this
// sample: the instance id a closed window derives is only exclusive because flow_instance's
// primary key refuses the second start. A host that registered no journal is refused before the
// first step — `flow.durability_not_configured` — and FlowStreamScan.IsEnabled is false, so the
// subscription would be registered and never read.
builder.Services.AddFlowXPostgres(
    builder.Configuration.GetConnectionString("FlowX")
    ?? Environment.GetEnvironmentVariable("FLOWX_POSTGRES_CONNECTION")
    ?? "Host=localhost;Port=5432;Database=postgres;Username=postgres");

// Where `.Emit<AggregateComputed>` is staged. The event and the step that produced it are one
// write, so a window whose aggregate is durable has announced itself or neither happened.
builder.Services.AddFlowXPostgresOutbox();

// The stream. One multiplexer for the process, because StackExchange.Redis multiplexes every
// command over one connection and one per scope is the standard way to exhaust a server's
// connection limit.
var redis = builder.Configuration.GetConnectionString("Redis")
    ?? Environment.GetEnvironmentVariable("FLOWX_REDIS_CONNECTION")
    ?? "localhost:6379";

builder.Services.AddSingleton<IConnectionMultiplexer>(_ => ConnectionMultiplexer.Connect(redis));

// The three seams a stream subscription needs, and none of them has a default.
//
//   IStreamSource          reads the log forwards from a position the engine supplies. XRANGE
//                          rather than a consumer group, because the engine must be able to
//                          re-read from its checkpoint to rebuild an open window after a crash.
//   IStreamCheckpointStore one row per subscription holding one opaque position, and no window
//                          state at all — a restart rebuilds every open window by re-reading
//                          (ADR-0055).
//   IStreamSideOutput      where a record later than the declared lateness goes. Required: a
//                          host without one builds no stream pass, because the alternative is a
//                          default that discards, which is the silent drop the promise in
//                          docs/09 §9 exists to forbid.
builder.Services.AddSingleton<IStreamSource>(provider =>
    new RedisStreamSource(provider.GetRequiredService<IConnectionMultiplexer>()));

builder.Services.AddSingleton<IStreamCheckpointStore>(provider =>
    new PostgresStreamCheckpointStore(provider.GetRequiredService<NpgsqlDataSource>()));

builder.Services.AddSingleton<IStreamSideOutput, TelemetrySideOutput>();

// Infrastructure. In memory here; the capabilities do not know or care.
builder.Services.AddSingleton<InMemoryAggregateStore>();
builder.Services.AddSingleton<InMemoryLateReadingLog>();
builder.Services.AddSingleton<IAggregateStore>(p => p.GetRequiredService<InMemoryAggregateStore>());
builder.Services.AddSingleton<ILateReadingLog>(p => p.GetRequiredService<InMemoryLateReadingLog>());

// The capabilities. The generated dispatcher takes them as constructor parameters, so a missing
// registration is a startup failure that names the type rather than a null reference on the
// first window.
builder.Services.AddFlowXCapabilities();

// The sample's own producer and reporter. Neither is part of the platform's story — see
// TelemetryProducer for why a sample that needed a second process to show anything is a sample
// nobody runs.
builder.Services.AddSingleton<TelemetryProducerOptions>();
builder.Services.AddHostedService<TelemetryProducer>();
builder.Services.AddHostedService<AggregateReporter>();

var app = builder.Build();

// Applying DDL is a decision, not a consequence of building a container. A real deployment calls
// this from wherever it runs schema changes; a sample calls it here, once, so that `dotnet run`
// against an empty database works. Migration 0011 is the one that creates stream_checkpoint.
await app.Services.GetRequiredService<PostgresMigrator>()
    .MigrateAsync(CancellationToken.None)
    .ConfigureAwait(false);

// Every stream subscription this application declares, generated from the [StreamTrigger] on the
// flow that declares it — the same arrangement `app.MapFlowX()` has with [HttpTrigger] and
// `AddFlowXSchedules()` has with [CronTrigger].
//
// One line registers `telemetry.aggregate` over `device.telemetry` as `tumbling:1m` with PT10S
// of lateness, a PT5S checkpoint interval and a parallelism of 8. From here on FlowStreamService
// sweeps for it, one node holds the subscription's lease at a time, and every closed window
// derives its own instance id from (flow, version, source, group, interval) — so a window
// rebuilt after a node death is refused by the journal's primary key rather than aggregated
// twice (ADR-0055).
//
// There is no window width, no lateness and no checkpoint interval anywhere in this file. That
// is the claim the sample is here to make.
app.Services.AddFlowXStreamSubscriptions();

await app.RunAsync().ConfigureAwait(false);
