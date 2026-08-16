using System.Text.Json;
using System.Text.RegularExpressions;
using Mono.Cecil;
using Shouldly;
using Xunit;

namespace FlowX.Architecture.Tests;

/// <summary>
/// The two rules about what this repository <em>publishes</em>: constraint C7's SemVer
/// commitment, and quality goal Q3's claim that the manifest describes the whole
/// application.
/// </summary>
/// <remarks>
/// <para>
/// Both are named in docs/05-Architecture.md §12 — as <c>EveryPublicContractIsVersioned</c>
/// and <c>ManifestIsComplete</c> — and neither existed. <c>ManifestIsComplete</c> is the
/// more expensive absence of the two: ADR-0005 makes the manifest a published artifact that
/// agents read, the CLI diffs and a release attaches, and every one of those consumers is
/// entitled to assume that what is not in the document is not in the application.
/// </para>
/// <para>
/// <c>ManifestTests</c> in <c>Ecommerce.Tests</c> asserts the sample's manifest names
/// <c>order.place</c>, its three capabilities and its one event — by hand, by id. That is a
/// test of one manifest. This is the rule: whatever a project declares, its manifest lists.
/// It is the same distinction as <c>EverySourceProjectIsCoveredByTheLayeringRule</c>, one
/// document down — a capability added tomorrow satisfies a hand-written list by not being
/// mentioned in it.
/// </para>
/// </remarks>
public sealed partial class PublishedContractTests
{
    private const string CapabilityInterface = "FlowX.ICapability`2";
    private const string FlowBaseType = "FlowX.Flow`2";

    /// <summary>
    /// A flow, a capability and an application version are the same shape as a step's, and
    /// FlowX.Core already says what that shape is.
    /// </summary>
    /// <remarks>
    /// Copied from <c>FlowX.Core.Identifiers.VersionPattern</c>, whose own remarks say why
    /// there is one definition: "two slightly different regexes in two files is how a
    /// platform ends up with two slightly different notions of a valid identity". The
    /// pattern lives in an <c>internal</c> type of an assembly this project must not take a
    /// reference to, so equality is asserted rather than assumed — see
    /// <see cref="TheVersionPatternMatchesTheRuntimes"/>.
    /// </remarks>
    [GeneratedRegex(
        @"^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)(?:-[0-9A-Za-z-.]+)?(?:\+[0-9A-Za-z-.]+)?$",
        RegexOptions.CultureInvariant)]
    private static partial Regex SemanticVersion();

    /// <summary>Pulls the manifest out of the C# constant the generator emits it into.</summary>
    [GeneratedRegex(@"public const string Json = @""(?<json>.*)"";", RegexOptions.Singleline)]
    private static partial Regex EmittedJson();

    /// <summary>
    /// Constraint C7: nothing this repository publishes as a contract is unversioned, and
    /// every version it publishes is a semantic one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A two-minor deprecation window is a promise about numbers, and it means nothing if
    /// the numbers are not comparable. <c>"1.0"</c>, <c>"v2"</c> and <c>"2024-06"</c> all
    /// look like versions and none of them can answer "is this within two minors of the one
    /// I built against" — which is the only question the constraint exists to answer.
    /// </para>
    /// <para>
    /// Three surfaces, because a contract is published three ways. A capability and a flow
    /// declare their own version, and it reaches the manifest and the agent tool catalogue.
    /// The manifest declares its schema version and the application's. A package declares
    /// an assembly version that NuGet resolves against. The first two are checked from
    /// metadata rather than from the attribute's declaration site, so a version supplied by
    /// a constant, a generator or a default is checked the same as one typed by hand.
    /// </para>
    /// </remarks>
    [Fact]
    public void EveryPublicContractIsVersioned()
    {
        var problems = new List<string>();
        var contracts = 0;

        foreach (var assembly in CompiledAssemblies.ShippingAssemblies)
        {
            using var module = CompiledAssemblies.Read(assembly);

            foreach (var contract in DeclaredContracts(module))
            {
                contracts++;
                problems.AddRange(VersionProblems($"{assembly}: {contract.Kind} '{contract.Id}'", contract.Version));
            }

            if (IsPackable(assembly))
            {
                contracts++;
                problems.AddRange(VersionProblems(
                    $"{assembly}: the package", InformationalVersion(module)));
            }
        }

        foreach (var (project, manifest) in EmittedManifests())
        {
            var root = manifest.RootElement;

            contracts++;
            problems.AddRange(VersionProblems($"{project} manifest: schemaVersion", Text(root, "schemaVersion")));
            problems.AddRange(VersionProblems(
                $"{project} manifest: application version",
                root.TryGetProperty("application", out var application) ? Text(application, "version") : null));

            foreach (var flow in Items(root, "flows"))
            {
                problems.AddRange(VersionProblems($"{project} manifest: flow '{Text(flow, "id")}'", Text(flow, "version")));
            }

            foreach (var capability in Items(root, "capabilities"))
            {
                problems.AddRange(VersionProblems(
                    $"{project} manifest: capability '{Text(capability, "id")}'", Text(capability, "version")));
            }

            foreach (var @event in Items(root, "events"))
            {
                problems.AddRange(VersionProblems(
                    $"{project} manifest: event '{Text(@event, "type")}'", Text(@event, "schemaVersion")));
            }
        }

        contracts.ShouldBeGreaterThan(
            0,
            "No versioned contract was found anywhere. Nothing was inspected, so this gate " +
            "is passing vacuously.");

        problems.ShouldBeEmpty(
            "A published contract carries no semantic version. A two-minor deprecation " +
            "window is a promise about numbers that can be compared (constraint C7):" +
            Environment.NewLine + string.Join(Environment.NewLine, problems));
    }

    /// <summary>
    /// Quality goal Q3 / ADR-0005: the manifest describes everything the application has.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The manifest is fed to agents, diffed by <c>flowx diff</c> and attached to releases.
    /// Each of those consumers reads absence as information: a capability that is not listed
    /// is a capability the agent will not call, the diff will not flag and the reviewer will
    /// not see. An incomplete manifest is therefore not a missing feature — it is a document
    /// that says something false, quietly, to a tool.
    /// </para>
    /// <para>
    /// Seven questions, which together are what "complete" means for the document as it is
    /// emitted today: every flow the assembly declares is listed; every capability it
    /// declares is listed; every capability a step invokes — including a compensation — has
    /// a full entry rather than only a mention; every event a flow emits is described in the
    /// event catalogue; every entry in that catalogue names the flows at both its ends and
    /// names no flow the document does not describe; the two indexes of an event — a flow's
    /// <c>emits</c> and the event's <c>producedBy</c> — agree; and every
    /// <c>.WithPolicy(...)</c> the assembly declares reaches a step's <c>policies</c> array.
    /// </para>
    /// <para>
    /// <strong>All four of Q3's nouns are covered, and the last two arrived together on
    /// 2026-08-15.</strong> This comment used to explain at length why <c>policies</c> was
    /// left out, and the explanation had already decayed twice: the first version said the
    /// generator emitted no <c>policies</c> section, which was false, and the second said
    /// nothing in this repository declared a policy, which is now false too — banking,
    /// healthcare, scheduler, workflow and realtime-stream each attach one, and ten kinds
    /// execute. A check written then would have passed vacuously; a check written now cannot,
    /// which is the only reason to write it now rather than then.
    /// </para>
    /// <para>
    /// <strong>The policy question is asked from the IL, not from the manifest.</strong>
    /// Counting the manifest's own <c>policies</c> arrays against each other would be the
    /// document agreeing with itself. <c>.WithPolicy(...)</c> is a call the flow's
    /// <c>Define</c> makes, so the call sites are the independent side, and the assertion is
    /// that the compiler dropped none of them between the builder and the document — which is
    /// exactly what <c>WritePolicies</c> does, silently, to a policy kind it has no stage for.
    /// </para>
    /// <para>
    /// <strong><c>events</c> is checked in both directions for the same reason.</strong>
    /// <c>.Emit&lt;T&gt;()</c> on a <c>Durable</c> flow stages its event in the step's own
    /// transaction and <c>PostgresOutboxPublisher</c> drains it, so the noun is real. The
    /// half this gate lacked was the catalogue's own: an entry that names nobody at either
    /// end is a subscriber's dead letter waiting to happen, and <c>producedBy</c> against
    /// each flow's <c>emits</c> is two independently written indexes of one fact, which is
    /// the only kind of agreement worth asserting.
    /// </para>
    /// </remarks>
    [Fact]
    public void ManifestIsComplete()
    {
        var problems = new List<string>();
        var manifests = 0;

        foreach (var assembly in CompiledAssemblies.ShippingAssemblies)
        {
            using var module = CompiledAssemblies.Read(assembly);

            var declared = DeclaredContracts(module);
            var manifest = EmittedManifestFor(assembly);

            if (manifest is null)
            {
                problems.AddRange(declared.Select(c =>
                    $"{assembly} declares {c.Kind} '{c.Id}' and emits no manifest at all. " +
                    "Nothing downstream can know it exists."));

                continue;
            }

            manifests++;
            problems.AddRange(Missing(assembly, declared, manifest.RootElement));
            problems.AddRange(Dangling(assembly, manifest.RootElement));
            problems.AddRange(EventsAreDescribed(assembly, manifest.RootElement));
            problems.AddRange(PoliciesSurvived(assembly, module, manifest.RootElement));
            manifest.Dispose();
        }

        manifests.ShouldBeGreaterThan(
            0,
            "No emitted manifest was found. Build the solution before running the " +
            "architecture gates — a completeness check with no document to check is not a " +
            "passing gate.");

        problems.ShouldBeEmpty(
            "The manifest does not describe the whole application. Every consumer of it — " +
            "the agent catalogue, `flowx diff`, the release artifact — reads absence as " +
            "information (ADR-0005, quality goal Q3):" +
            Environment.NewLine + string.Join(Environment.NewLine, problems));
    }

    /// <summary>
    /// The version pattern here and the one FlowX.Core enforces are the same pattern.
    /// </summary>
    /// <remarks>
    /// <c>Identifiers</c> is <c>internal</c> to FlowX.Core, and this project deliberately
    /// references only FlowX.Abstractions, so the pattern cannot be shared by reference. A
    /// copy that drifts would let this gate accept a version the runtime rejects at
    /// <c>CapabilityDescriptor.Create</c> — the gate would be green and the application would
    /// throw on startup.
    /// </remarks>
    [Fact]
    public void TheVersionPatternMatchesTheRuntimes()
    {
        var identifiers = new FileInfo(Path.Combine(
            RepositoryLayout.Root.FullName, "src", "FlowX.Core", "Identifiers.cs"));

        identifiers.Exists.ShouldBeTrue($"{identifiers.FullName} defines the version format.");

        File.ReadAllText(identifiers.FullName).ShouldContain(
            @"^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)(?:-[0-9A-Za-z-.]+)?(?:\+[0-9A-Za-z-.]+)?$",
            Case.Sensitive,
            "FlowX.Core's version pattern has changed and this copy has not. The two now " +
            "disagree about what a version is, and this gate would pass a version the " +
            "runtime throws on.");
    }

    /// <summary>
    /// The manifest reader finds the sample's manifest and can read it.
    /// </summary>
    /// <remarks>
    /// The completeness check compares two sets, and an unreadable manifest empties both
    /// sides at once: nothing declared is missing from a document with nothing in it. The
    /// generator emits the manifest as a C# verbatim literal, so a change to how that
    /// constant is written breaks the extraction and nothing else.
    /// </remarks>
    [Fact]
    public void TheManifestReaderFindsTheSamplesManifest()
    {
        using var manifest = EmittedManifestFor("Ecommerce");

        manifest.ShouldNotBeNull(
            "samples/ecommerce emits a manifest and the reader did not find it. " +
            "ManifestIsComplete is now comparing two empty sets.");

        Items(manifest!.RootElement, "capabilities")
            .Select(c => Text(c, "id"))
            .ShouldContain("payment.capture", "The manifest was found but did not parse into entries.");
    }

    // ------------------------------------------------------------------ helpers

    private static IEnumerable<string> VersionProblems(string what, string? version)
    {
        if (string.IsNullOrWhiteSpace(version))
        {
            yield return $"{what} declares no version.";
        }
        else if (!SemanticVersion().IsMatch(version))
        {
            yield return $"{what} declares '{version}', which is not a semantic version.";
        }
    }

    private static IEnumerable<string> Missing(
        string assembly,
        IReadOnlyList<Contract> declared,
        JsonElement manifest)
    {
        var flows = Items(manifest, "flows").Select(f => Text(f, "id")).ToHashSet(StringComparer.Ordinal);
        var capabilities = Items(manifest, "capabilities")
            .Select(c => Text(c, "id"))
            .ToHashSet(StringComparer.Ordinal);

        foreach (var contract in declared)
        {
            var listed = contract.Kind == "flow" ? flows : capabilities;

            if (contract.Id is null || !listed.Contains(contract.Id))
            {
                yield return
                    $"{assembly}: {contract.Kind} '{contract.Id ?? contract.TypeName}' is declared " +
                    $"in {contract.TypeName} and does not appear in the manifest.";
            }
        }
    }

    /// <summary>
    /// Entries the manifest refers to but never describes.
    /// </summary>
    /// <remarks>
    /// The other half of completeness, and the half that is easy to lose. A step names
    /// <c>payment.capture@2.1.0</c>; a reader who wants to know what that capability accepts,
    /// whether it is idempotent or what it can fail with has to find the entry. A step that
    /// names a capability the document does not describe is a dangling reference in a
    /// published contract.
    /// </remarks>
    private static IEnumerable<string> Dangling(string assembly, JsonElement manifest)
    {
        var described = Items(manifest, "capabilities")
            .Select(c => $"{Text(c, "id")}@{Text(c, "version")}")
            .ToHashSet(StringComparer.Ordinal);

        var events = Items(manifest, "events").Select(e => Text(e, "type")).ToHashSet(StringComparer.Ordinal);

        foreach (var flow in Items(manifest, "flows"))
        {
            var id = Text(flow, "id");

            foreach (var step in Items(flow, "steps"))
            {
                foreach (var key in (string[])["capability", "compensation"])
                {
                    if (Text(step, key) is { } reference && !described.Contains(reference))
                    {
                        yield return
                            $"{assembly}: flow '{id}' step {Text(step, "id")} names {key} " +
                            $"'{reference}', which has no entry under \"capabilities\".";
                    }
                }
            }

            foreach (var emitted in Items(flow, "emits").Select(e => e.GetString()))
            {
                if (emitted is not null && !events.Contains(emitted))
                {
                    yield return
                        $"{assembly}: flow '{id}' emits '{emitted}', which has no entry under " +
                        "\"events\". A consumer cannot subscribe to a schema nobody published.";
                }
            }
        }
    }

    /// <summary>
    /// The event catalogue's own half of completeness: every entry names the flows at its
    /// ends, and the two indexes of an event agree.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="Dangling"/> asks whether an emitted event has an entry. This asks the
    /// reverse — whether an entry describes anything — and the two are not the same question.
    /// An entry with no <c>producedBy</c> tells a subscriber that an event exists and gives
    /// them no flow to look at, which is the state every entry was in until the compiler
    /// began writing the index.
    /// </para>
    /// <para>
    /// The last check is the one that cannot be satisfied by accident. A flow's <c>emits</c>
    /// and an event's <c>producedBy</c> are written by different code from the same walk, so
    /// they are two claims about one fact; asserting they agree is what catches one of them
    /// being filtered, deduplicated or sorted into disagreement with the other.
    /// </para>
    /// </remarks>
    private static IEnumerable<string> EventsAreDescribed(string assembly, JsonElement manifest)
    {
        var flows = Items(manifest, "flows").Select(f => Text(f, "id")).ToHashSet(StringComparer.Ordinal);

        var emitters = Items(manifest, "flows")
            .SelectMany(f => Items(f, "emits")
                .Select(e => e.GetString())
                .Where(e => e is not null)
                .Select(e => (Event: e!, Flow: Text(f, "id"))))
            .ToLookup(pair => pair.Event, pair => pair.Flow, StringComparer.Ordinal);

        foreach (var evt in Items(manifest, "events"))
        {
            var type = Text(evt, "type");
            var produced = Strings(evt, "producedBy");

            if (produced.Count == 0 && Strings(evt, "consumedBy").Count == 0)
            {
                yield return
                    $"{assembly}: event '{type}' is published with neither \"producedBy\" nor " +
                    "\"consumedBy\". A consumer reading it learns that the event exists and has " +
                    "no flow to look at, in the document that is supposed to be the graph.";
            }

            foreach (var end in (string[])["producedBy", "consumedBy"])
            {
                foreach (var flow in Strings(evt, end).Where(f => !flows.Contains(f)))
                {
                    yield return
                        $"{assembly}: event '{type}' names flow '{flow}' under \"{end}\", and no " +
                        "entry under \"flows\" describes it.";
                }
            }

            var claimed = emitters[type!].ToHashSet(StringComparer.Ordinal);

            foreach (var flow in claimed.Except(produced, StringComparer.Ordinal).Order(StringComparer.Ordinal))
            {
                yield return
                    $"{assembly}: flow '{flow}' lists '{type}' under \"emits\", and event " +
                    $"'{type}' does not list it under \"producedBy\". The two indexes of one " +
                    "event disagree, so a consumer's answer depends on which end it read.";
            }
        }
    }

    /// <summary>
    /// Every <c>.WithPolicy(...)</c> the assembly declares reaches a step's <c>policies</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Q3's fourth noun, and the one that could only be asked honestly once something
    /// declared a policy and something executed one. <c>WritePolicies</c> skips a kind it has
    /// no stage for rather than guessing at the order it runs in — the right call, and a
    /// silent one, so a policy vocabulary that grew without the writer's table growing with
    /// it would drop out of the published contract and nothing would say so.
    /// </para>
    /// <para>
    /// Counted rather than matched by name, because the name is not in the IL: the call takes
    /// a <c>PolicySet</c> the flow built somewhere else, and resolving it would mean
    /// evaluating a property body. A count is enough for what this asks — that the document
    /// carries as many policied steps as the source declares — and it is the count that moves
    /// when the writer drops one.
    /// </para>
    /// </remarks>
    private static IEnumerable<string> PoliciesSurvived(
        string assembly, ModuleDefinition module, JsonElement manifest)
    {
        var declared = IlSurvey.AllTypes(module)
            .Where(IsFlow)
            .SelectMany(WithNested)
            .SelectMany(type => type.Methods)
            .Where(method => method.HasBody)
            .SelectMany(method => method.Body.Instructions)
            .Count(instruction =>
                instruction.Operand is MethodReference call &&
                call.Name.Equals("WithPolicy", StringComparison.Ordinal));

        var published = Items(manifest, "flows").Sum(flow => Policied(Items(flow, "steps")));

        if (declared != published)
        {
            yield return
                $"{assembly} declares {declared} .WithPolicy(...) call(s) and its manifest " +
                $"carries {published} step(s) with a \"policies\" array. A declared policy that " +
                "does not reach the document is a term of the published contract the runtime " +
                "applies and no consumer can see (quality goal Q3, ADR-0011).";
        }
    }

    /// <summary>Steps carrying a non-empty <c>policies</c> array, through nested branches.</summary>
    private static int Policied(IEnumerable<JsonElement> steps) => steps.Sum(step =>
        (Items(step, "policies").Count > 0 ? 1 : 0) +
        Items(step, "branches").Sum(branch => Policied(branch.EnumerateArray())));

    /// <summary>A type and every type nested inside it, to any depth.</summary>
    /// <remarks>
    /// A <c>Define</c> body puts each <c>Parallel</c> and <c>ForEach</c> block in a lambda,
    /// and the compiler moves those into nested display classes. A scan of the flow type's
    /// own methods would therefore miss every policy declared inside a fork — which is
    /// exactly where samples/banking declares two of its seven.
    /// </remarks>
    private static IEnumerable<TypeDefinition> WithNested(TypeDefinition type)
    {
        yield return type;

        foreach (var nested in type.NestedTypes.SelectMany(WithNested))
        {
            yield return nested;
        }
    }

    private static List<string> Strings(JsonElement element, string name) =>
        Items(element, name)
            .Select(item => item.GetString())
            .Where(value => value is not null)
            .Select(value => value!)
            .ToList();

    private static List<Contract> DeclaredContracts(ModuleDefinition module)
    {
        var contracts = new List<Contract>();

        foreach (var type in IlSurvey.AllTypes(module))
        {
            if (Attribute(type, "CapabilityAttribute") is { } capability)
            {
                contracts.Add(new Contract("capability", type.FullName, Id(capability), Property(capability, "Version")));
            }
            else if (Attribute(type, "FlowAttribute") is { } flow)
            {
                contracts.Add(new Contract("flow", type.FullName, Id(flow), Property(flow, "Version")));
            }
            else if (IsFlow(type) || Implements(type, CapabilityInterface))
            {
                // No attribute at all. EveryCapabilityDeclaresAuthorization covers the
                // capability case from source; recorded here so the manifest rule still
                // reports it rather than silently having nothing to look up.
                contracts.Add(new Contract(
                    IsFlow(type) ? "flow" : "capability", type.FullName, Id: null, Version: null));
            }
        }

        return contracts;
    }

    private static bool IsFlow(TypeDefinition type)
    {
        for (var current = type.BaseType; current is not null;)
        {
            if (current.GetElementType().FullName.Equals(FlowBaseType, StringComparison.Ordinal))
            {
                return true;
            }

            current = current is TypeDefinition definition ? definition.BaseType : null;
        }

        return false;
    }

    private static bool Implements(TypeDefinition type, string interfaceName) =>
        type.Interfaces.Any(i =>
            i.InterfaceType.GetElementType().FullName.Equals(interfaceName, StringComparison.Ordinal));

    private static CustomAttribute? Attribute(TypeDefinition type, string name) =>
        type.CustomAttributes.FirstOrDefault(a =>
            a.AttributeType.Name.Equals(name, StringComparison.Ordinal));

    private static string? Id(CustomAttribute attribute) =>
        attribute.ConstructorArguments.Count > 0 ? attribute.ConstructorArguments[0].Value as string : null;

    private static string? Property(CustomAttribute attribute, string name) =>
        attribute.Properties.FirstOrDefault(p => p.Name.Equals(name, StringComparison.Ordinal)).Argument.Value
            as string;

    /// <summary>
    /// The version a package publishes, as recorded in the assembly.
    /// </summary>
    /// <remarks>
    /// <c>AssemblyInformationalVersion</c> rather than <c>AssemblyVersion</c>: the latter is
    /// a four-part number that cannot express a pre-release, so <c>1.0.0-rc.1</c> and
    /// <c>1.0.0</c> are the same assembly version and a different NuGet package. What C7
    /// commits to is the one NuGet resolves against.
    /// </remarks>
    private static string? InformationalVersion(ModuleDefinition module) =>
        module.Assembly?.CustomAttributes
            .FirstOrDefault(static a =>
                a.AttributeType.FullName == "System.Reflection.AssemblyInformationalVersionAttribute")
            ?.ConstructorArguments[0].Value as string;

    private static bool IsPackable(string assembly)
    {
        var directory = CompiledAssemblies.ProjectDirectory(assembly);

        if (directory is null)
        {
            return false;
        }

        return directory.GetFiles("*.csproj")
            .All(static project => !File.ReadAllText(project.FullName)
                .Contains("<IsPackable>false</IsPackable>", StringComparison.OrdinalIgnoreCase));
    }

    private static IEnumerable<(string Project, JsonDocument Manifest)> EmittedManifests()
    {
        foreach (var assembly in CompiledAssemblies.ShippingAssemblies)
        {
            if (EmittedManifestFor(assembly) is { } manifest)
            {
                yield return (assembly, manifest);
            }
        }
    }

    /// <summary>
    /// The manifest a project's build emitted, or <c>null</c> when it declares no flow.
    /// </summary>
    /// <remarks>
    /// Read from <c>obj/generated</c> rather than from a committed <c>.json</c>: that file is
    /// the artifact, byte for byte, and <c>flowx manifest</c> only copies it to disk. A
    /// committed copy is a claim about a build; this is the build.
    /// </remarks>
    private static JsonDocument? EmittedManifestFor(string assembly)
    {
        var directory = CompiledAssemblies.ProjectDirectory(assembly);
        var generated = directory is null
            ? null
            : new DirectoryInfo(Path.Combine(directory.FullName, "obj", "generated"));

        var file = generated is { Exists: true }
            ? generated.EnumerateFiles("FlowXManifest.g.cs", SearchOption.AllDirectories).FirstOrDefault()
            : null;

        if (file is null)
        {
            return null;
        }

        var match = EmittedJson().Match(File.ReadAllText(file.FullName));

        match.Success.ShouldBeTrue(
            $"{SourceSurvey.RelativePath(file)} no longer holds the manifest in a " +
            "`public const string Json = @\"...\"` literal, so it cannot be read. The " +
            "completeness gate would otherwise compare two empty sets.");

        return JsonDocument.Parse(match.Groups["json"].Value.Replace("\"\"", "\"", StringComparison.Ordinal));
    }

    private static List<JsonElement> Items(JsonElement element, string name) =>
        element.TryGetProperty(name, out var array) && array.ValueKind == JsonValueKind.Array
            ? array.EnumerateArray().ToList()
            : [];

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    /// <summary>One versioned thing this repository publishes.</summary>
    private readonly record struct Contract(string Kind, string TypeName, string? Id, string? Version);
}
