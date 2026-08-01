namespace FlowX.Cli.Tests;

/// <summary>Manifests the <c>replay</c> tests join a journal against.</summary>
internal static class TestManifests
{
    /// <summary>One flow, one step, no branching.</summary>
    public const string Minimal = """
        {
          "schemaVersion": "0.1.0",
          "application": { "name": "Sample.App", "version": "1.0.0" },
          "flows": [{
            "id": "order.place", "version": "1.0.0", "profile": "Ephemeral",
            "steps": [{ "id": 0, "kind": "Capability", "capability": "order.validate@1.0.0" }],
            "emits": []
          }],
          "capabilities": [
            { "id": "order.validate", "version": "1.0.0", "idempotent": true, "sideEffects": [] }
          ]
        }
        """;

    /// <summary>
    /// A flow whose steps 1 and 2 are the two branches of a <c>Parallel</c> at step 0.
    /// </summary>
    /// <remarks>
    /// The shape <c>ReplayDeterminismTests.AForkAttributesOneBranchsCapturedIdToItsSiblingsRow</c>
    /// pins: two overlapping branches sharing one execution context, so a capture taken at one
    /// branch's commit may carry the other's values.
    /// </remarks>
    public const string Parallel = """
        {
          "schemaVersion": "0.1.0",
          "application": { "name": "Sample.App", "version": "1.0.0" },
          "flows": [{
            "id": "order.screen", "version": "1.0.0", "profile": "Durable",
            "steps": [
              { "id": 0, "kind": "Parallel", "merge": "AllMustSucceed", "branches": [
                  [{ "id": 1, "kind": "Capability", "capability": "screen.sanctions@1.0.0" }],
                  [{ "id": 2, "kind": "Capability", "capability": "screen.fraud@1.0.0" }]
              ]},
              { "id": 3, "kind": "Capability", "capability": "order.accept@1.0.0" }
            ],
            "emits": []
          }],
          "capabilities": [
            { "id": "screen.sanctions", "version": "1.0.0", "idempotent": true, "sideEffects": [] },
            { "id": "screen.fraud", "version": "1.0.0", "idempotent": true, "sideEffects": [] },
            { "id": "order.accept", "version": "1.0.0", "idempotent": true, "sideEffects": [] }
          ]
        }
        """;

    /// <summary>Writes <see cref="Parallel"/> to a temporary file and returns its path.</summary>
    /// <returns>The path to the written manifest.</returns>
    public static string WriteParallelManifest()
    {
        var path = Path.Combine(
            Path.GetTempPath(), "flowx-parallel-" + Guid.NewGuid().ToString("n") + ".json");

        File.WriteAllText(path, Parallel);

        return path;
    }
}
