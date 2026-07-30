using FlowX.Cli.Manifest;

namespace FlowX.Cli.Diffing;

/// <summary>
/// Compares two manifests and classifies every difference as breaking, additive or
/// neutral — the check that makes emitting a manifest worth the trouble
/// (<a href="../../../docs/adr/ADR-0005-manifest-as-build-artifact.md">ADR-0005</a>).
/// </summary>
/// <remarks>
/// <para>
/// <strong>The compatibility unit is <c>id@major</c>, not <c>id@version</c>.</strong>
/// Flows pin the major of every capability they compiled against, and a capability may
/// legitimately exist at two majors side by side. Keying on the exact version would
/// report a patch bump as a removal plus an addition; keying on identity alone would
/// make two side-by-side majors collide into one entry and hide a real removal. Keying
/// on the major is the only choice that classifies both correctly, and it removes the
/// need for any "was the version bumped?" waiver: bumping the major *is* publishing a
/// new contract, and deleting the old one is what breaks people.
/// </para>
/// <para>
/// <strong>What is deliberately not compared, and why.</strong> A rule that fires on
/// every build is worse than no rule, because it teaches people to ignore the output.
/// </para>
/// <list type="bullet">
///   <item><c>source</c> (file:line) — moves whenever anyone edits the file above a
///   declaration. It is navigation metadata, not contract.</item>
///   <item><c>application.version</c>, <c>commit</c>, <c>builtAt</c> — they change on
///   every release by design; that is what they are for.</item>
///   <item>A flow's <c>steps</c> — the implementation of a flow, not its contract.
///   Reordering, adding or replacing a step is exactly the refactoring that FlowX exists
///   to make safe, and gating it would punish the thing being encouraged.</item>
///   <item>A flow's <c>emits</c> and <c>errors</c> — both are aggregated by the compiler
///   from the flow's steps and capabilities. The same facts are stated once more, with
///   versions attached, in <c>events</c> and in each capability's <c>errors</c>, and
///   diffing derived data as well as its source reports every change twice. A consumer
///   subscribes to an event type, not to a particular producer of it.</item>
///   <item>Array order anywhere — every set is compared as a set.</item>
/// </list>
/// <para>
/// Pure: two documents in, a report out. The classification rules are the product here,
/// and they are only worth asserting if deciding is separable from printing.
/// </para>
/// </remarks>
public static class ManifestDiff
{
    /// <summary>Classifies every difference between a released manifest and a new one.</summary>
    /// <param name="baseline">The manifest already in production, or the last released one.</param>
    /// <param name="candidate">The manifest the current build produced.</param>
    /// <returns>Findings ordered breaking-first, then by rule code — never in file order.</returns>
    public static DiffReport Compare(ManifestDocument baseline, ManifestDocument candidate)
    {
        ArgumentNullException.ThrowIfNull(baseline);
        ArgumentNullException.ThrowIfNull(candidate);

        var findings = new List<DiffFinding>();

        CompareDocuments(findings, baseline, candidate);
        CompareFlows(findings, baseline, candidate);
        CompareCapabilities(findings, baseline, candidate);
        CompareEvents(findings, baseline, candidate);

        return new DiffReport
        {
            Application = candidate.Application?.Name ?? baseline.Application?.Name ?? "unknown",
            BaselineVersion = baseline.Application?.Version ?? string.Empty,
            CandidateVersion = candidate.Application?.Version ?? string.Empty,

            // Ordered by what a reader needs first, not by where it appeared in the file.
            // Determinism matters as much as the order: this output is compared between
            // builds and pasted into review comments.
            Findings = [.. findings
                .OrderByDescending(f => f.Severity)
                .ThenBy(f => f.Code, StringComparer.Ordinal)
                .ThenBy(f => f.Subject, StringComparer.Ordinal)
                .ThenBy(f => f.Summary, StringComparer.Ordinal)],
        };
    }

    // ---------------------------------------------------------------- document

    private static void CompareDocuments(
        List<DiffFinding> findings, ManifestDocument baseline, ManifestDocument candidate)
    {
        // Neutral, not breaking: the manifest schema's major says how to read the
        // document, not what the application promises. But it is worth one line, because
        // across a schema major a field present in both files may no longer mean the same
        // thing — and a reader deserves to know the rest of the report is approximate.
        if (!string.Equals(
            SemVer.Major(baseline.SchemaVersion), SemVer.Major(candidate.SchemaVersion), StringComparison.Ordinal))
        {
            findings.Add(new DiffFinding
            {
                Code = "FLOWX-DIFF-200",
                Severity = DiffSeverity.Neutral,
                Subject = "manifest",
                Summary = $"schema version {Show(baseline.SchemaVersion)} -> {Show(candidate.SchemaVersion)}",
                Consequence =
                    "The documents were written against different majors of the manifest schema, " +
                    "so a field present in both may not mean the same thing. Treat the rest as advisory.",
            });
        }

        // Not a compatibility statement at all — it is almost always a sign that the wrong
        // pair of files was passed, which is worth saying before the reader trusts a
        // hundred findings produced by comparing two unrelated applications.
        var baselineName = baseline.Application?.Name;
        var candidateName = candidate.Application?.Name;

        if (baselineName is not null && candidateName is not null
            && !string.Equals(baselineName, candidateName, StringComparison.Ordinal))
        {
            findings.Add(new DiffFinding
            {
                Code = "FLOWX-DIFF-201",
                Severity = DiffSeverity.Neutral,
                Subject = "manifest",
                Summary = $"application renamed: {baselineName} -> {candidateName}",
                Consequence =
                    "These manifests describe different applications. Check that the intended " +
                    "baseline was passed before acting on anything else in this report.",
            });
        }
    }

    // -------------------------------------------------------------------- flows

    private static void CompareFlows(
        List<DiffFinding> findings, ManifestDocument baseline, ManifestDocument candidate)
    {
        var before = ByMajor(baseline.Flows, f => f.Id, f => f.Version);
        var after = ByMajor(candidate.Flows, f => f.Id, f => f.Version);

        // A removed flow takes its triggers with it: routes 404, consumer groups stop
        // consuming, and a caller pinned to this major has nothing left to call.
        foreach (var key in Removed(before, after))
        {
            findings.Add(new DiffFinding
            {
                Code = "FLOWX-DIFF-001",
                Severity = DiffSeverity.Breaking,
                Subject = "flow " + key,
                Summary = "removed",
                Consequence = "Every trigger bound to this flow stops resolving.",
            });
        }

        foreach (var key in Added(before, after))
        {
            findings.Add(new DiffFinding
            {
                Code = "FLOWX-DIFF-100",
                Severity = DiffSeverity.Additive,
                Subject = "flow " + key,
                Summary = "added",
            });
        }

        foreach (var key in Common(before, after))
        {
            CompareFlow(findings, "flow " + key, before[key], after[key]);
        }
    }

    private static void CompareFlow(
        List<DiffFinding> findings, string subject, ManifestFlow before, ManifestFlow after)
    {
        // The input contract is what a caller constructs and a trigger binds a request
        // body to. A different type does not bind from the same payload.
        if (Changed(before.Input?.Type, after.Input?.Type))
        {
            findings.Add(new DiffFinding
            {
                Code = "FLOWX-DIFF-002",
                Severity = DiffSeverity.Breaking,
                Subject = subject,
                Summary = $"input contract changed: {Show(before.Input?.Type)} -> {Show(after.Input?.Type)}",
                Consequence = "Callers construct this type; the same request no longer binds.",
            });
        }

        // The output contract is what a caller deserialises. Changing it breaks everyone
        // parsing the response, whether or not the new shape is "better".
        if (Changed(before.Output?.Type, after.Output?.Type))
        {
            findings.Add(new DiffFinding
            {
                Code = "FLOWX-DIFF-003",
                Severity = DiffSeverity.Breaking,
                Subject = subject,
                Summary = $"output contract changed: {Show(before.Output?.Type)} -> {Show(after.Output?.Type)}",
                Consequence = "Callers deserialise this type; the response no longer round-trips.",
            });
        }

        CompareProfile(findings, subject, before, after);
        CompareSensitive(findings, subject + " input", before.Input, after.Input);
        CompareSensitive(findings, subject + " output", before.Output, after.Output);
        CompareTriggers(findings, subject, before, after);

        // Neutral. A deadline is an operational budget, tuned against production latency,
        // not a promise in the contract — so shortening one must not fail a build. It is
        // still reported, because shortening a deadline turns slow-but-successful calls
        // into timeouts, and that is worth a reviewer's eye rather than silence.
        if (Changed(before.Deadline, after.Deadline))
        {
            findings.Add(new DiffFinding
            {
                Code = "FLOWX-DIFF-203",
                Severity = DiffSeverity.Neutral,
                Subject = subject,
                Summary = $"deadline changed: {Show(before.Deadline)} -> {Show(after.Deadline)}",
                Consequence = "A shorter budget can turn slow-but-successful executions into timeouts.",
            });
        }
    }

    private static void CompareProfile(
        List<DiffFinding> findings, string subject, ManifestFlow before, ManifestFlow after)
    {
        if (!Changed(before.Profile, after.Profile))
        {
            return;
        }

        // Losing Durable is the one profile change that withdraws a guarantee a consumer
        // was entitled to rely on: instances stop surviving a process kill, and in-flight
        // work is lost rather than resumed elsewhere. Nothing in the type signature moves,
        // which is exactly why a shape-only diff would miss it and a human review usually
        // does too. Every other profile transition — gaining Durable, moving to or from
        // Streaming — either strengthens the guarantee or trades one execution model for
        // another that is not ordered against it, so it is reported and not gated.
        var withdrawn = string.Equals(before.Profile, "Durable", StringComparison.Ordinal);

        findings.Add(withdrawn
            ? new DiffFinding
            {
                Code = "FLOWX-DIFF-004",
                Severity = DiffSeverity.Breaking,
                Subject = subject,
                Summary = $"durability withdrawn: {Show(before.Profile)} -> {Show(after.Profile)}",
                Consequence =
                    "Instances no longer survive a process kill; in-flight work is lost rather " +
                    "than resumed. The signature is unchanged, so nothing else will catch this.",
            }
            : new DiffFinding
            {
                Code = "FLOWX-DIFF-202",
                Severity = DiffSeverity.Neutral,
                Subject = subject,
                Summary = $"execution profile changed: {Show(before.Profile)} -> {Show(after.Profile)}",
                Consequence = "Cost and delivery semantics change; the contract does not.",
            });
    }

    /// <summary>Classifies a change to which members of a contract carry secrets.</summary>
    /// <remarks>
    /// <para>
    /// The asymmetry here is the interesting part, and it is deliberate.
    /// </para>
    /// <para>
    /// <strong>Marking a member sensitive is additive.</strong> It strictly increases
    /// protection: the value stops reaching logs, traces and the durable journal. It can
    /// break something — a dashboard that scraped the value out of a log line loses it —
    /// but a secret leaking into a log is not a dependency this platform undertakes to
    /// preserve, and a gate that fails the build when an engineer marks a password would
    /// teach engineers not to mark passwords. That is the opposite of the intended effect.
    /// </para>
    /// <para>
    /// <strong>Un-marking one is breaking, and is the more serious of the two.</strong> A
    /// value that was redacted now flows into logs, traces and a journal that is retained
    /// for the replay window — so the regression is not merely visible, it is durable and
    /// retroactive to every instance recorded after the change. It is the same class of
    /// finding as a relaxed authorisation stance: no signature moved, and the security
    /// posture got worse, which is precisely the change that review misses and a manifest
    /// diff catches.
    /// </para>
    /// </remarks>
    private static void CompareSensitive(
        List<DiffFinding> findings, string subject, ManifestTypeRef? before, ManifestTypeRef? after)
    {
        var wasSensitive = before?.Sensitive ?? [];
        var isSensitive = after?.Sensitive ?? [];

        foreach (var member in NotIn(wasSensitive, isSensitive))
        {
            findings.Add(new DiffFinding
            {
                Code = "FLOWX-DIFF-006",
                Severity = DiffSeverity.Breaking,
                Subject = subject,
                Summary = $"member is no longer sensitive: {member}",
                Consequence =
                    "Data exposure regression: this value was redacted from logs, traces and the " +
                    "durable journal, and now reaches all three for the whole retention window.",
            });
        }

        foreach (var member in NotIn(isSensitive, wasSensitive))
        {
            findings.Add(new DiffFinding
            {
                Code = "FLOWX-DIFF-107",
                Severity = DiffSeverity.Additive,
                Subject = subject,
                Summary = $"member marked sensitive: {member}",
                Consequence = "Redaction tightened. The value stops appearing in telemetry.",
            });
        }
    }

    private static void CompareTriggers(
        List<DiffFinding> findings, string subject, ManifestFlow before, ManifestFlow after)
    {
        var wasBound = before.Triggers.Select(Describe).ToList();
        var isBound = after.Triggers.Select(Describe).ToList();

        // A trigger is the flow's address. Removing one is the most externally visible
        // break there is: a route stops answering, or a consumer group stops draining a
        // topic that producers keep filling.
        foreach (var trigger in NotIn(wasBound, isBound))
        {
            findings.Add(new DiffFinding
            {
                Code = "FLOWX-DIFF-005",
                Severity = DiffSeverity.Breaking,
                Subject = subject,
                Summary = $"trigger removed: {trigger}",
                Consequence = "Callers using this address can no longer reach the flow.",
            });
        }

        foreach (var trigger in NotIn(isBound, wasBound))
        {
            findings.Add(new DiffFinding
            {
                Code = "FLOWX-DIFF-103",
                Severity = DiffSeverity.Additive,
                Subject = subject,
                Summary = $"trigger added: {trigger}",
            });
        }
    }

    /// <summary>A trigger's externally visible address, which is what a caller depends on.</summary>
    /// <remarks>
    /// Identity is the address, not the object: a consumer group renamed on a Kafka
    /// trigger is an operational change, while a topic renamed is a break. Only the
    /// fields a caller can observe take part.
    /// </remarks>
    private static string Describe(ManifestTrigger trigger)
    {
        var parts = new[] { trigger.Kind, trigger.Method, trigger.Route, trigger.Transport, trigger.Topic, trigger.Cron }
            .Where(p => !string.IsNullOrEmpty(p));

        return string.Join(' ', parts) is { Length: > 0 } description ? description : "(unspecified)";
    }

    // ------------------------------------------------------------- capabilities

    private static void CompareCapabilities(
        List<DiffFinding> findings, ManifestDocument baseline, ManifestDocument candidate)
    {
        var before = ByMajor(baseline.Capabilities, c => c.Id, c => c.Version);
        var after = ByMajor(candidate.Capabilities, c => c.Id, c => c.Version);

        // The removal that side-by-side versioning exists to avoid. Because the key is the
        // major, publishing 3.0.0 alongside 2.1.0 lands here as an addition only; deleting
        // 2.1.0 in the same commit is what produces this finding.
        foreach (var key in Removed(before, after))
        {
            findings.Add(new DiffFinding
            {
                Code = "FLOWX-DIFF-010",
                Severity = DiffSeverity.Breaking,
                Subject = "capability " + key,
                Summary = "removed",
                Consequence =
                    "Flows pinned to this major no longer resolve. Keep the old major alongside " +
                    "the new one until every caller has moved.",
            });
        }

        foreach (var key in Added(before, after))
        {
            findings.Add(new DiffFinding
            {
                Code = "FLOWX-DIFF-101",
                Severity = DiffSeverity.Additive,
                Subject = "capability " + key,
                Summary = "added",
            });
        }

        foreach (var key in Common(before, after))
        {
            CompareCapability(findings, "capability " + key, before[key], after[key]);
        }
    }

    private static void CompareCapability(
        List<DiffFinding> findings, string subject, ManifestCapability before, ManifestCapability after)
    {
        // Within one major, the contract is frozen. A retyped input or output here is not
        // "a major change that was not versioned" — it is a change nobody can detect from
        // the version, which is the worst kind.
        if (Changed(before.Input, after.Input))
        {
            findings.Add(new DiffFinding
            {
                Code = "FLOWX-DIFF-011",
                Severity = DiffSeverity.Breaking,
                Subject = subject,
                Summary = $"input contract changed: {Show(before.Input)} -> {Show(after.Input)}",
                Consequence =
                    "Callers construct this type. Publish the new shape as a new major instead of " +
                    "changing this one.",
            });
        }

        if (Changed(before.Output, after.Output))
        {
            findings.Add(new DiffFinding
            {
                Code = "FLOWX-DIFF-012",
                Severity = DiffSeverity.Breaking,
                Subject = subject,
                Summary = $"output contract changed: {Show(before.Output)} -> {Show(after.Output)}",
                Consequence =
                    "Callers deserialise this type. Publish the new shape as a new major instead of " +
                    "changing this one.",
            });
        }

        CompareIdempotency(findings, subject, before, after);
        CompareAuthorization(findings, subject, before.Authorization, after.Authorization);
        CompareSideEffects(findings, subject, before, after);
        CompareErrors(findings, subject, before, after);

        // Neutral: a deprecation notice removes nothing. It is reported because it is the
        // one signal that tells a consumer to start migrating, and because it changes only
        // when somebody decides it should — never on a rebuild.
        if (Changed(before.Deprecated, after.Deprecated))
        {
            findings.Add(new DiffFinding
            {
                Code = "FLOWX-DIFF-204",
                Severity = DiffSeverity.Neutral,
                Subject = subject,
                Summary = $"deprecation changed: {Show(before.Deprecated)} -> {Show(after.Deprecated)}",
                Consequence = "Nothing breaks today; callers should plan the migration now.",
            });
        }
    }

    /// <summary>Classifies a change to whether a capability is safe to invoke twice.</summary>
    /// <remarks>
    /// Losing idempotency is breaking for a reason that is easy to miss: it is not the
    /// capability's own callers that break, it is the policies attached to it at every
    /// call site. <c>Idempotent = false</c> plus a retry policy is a compile error
    /// (<c>FLOWX1014</c>) — FlowX refuses to retry what is unsafe to retry — so a flow
    /// that compiles against the baseline stops compiling against the candidate, in a
    /// different repository, owned by a different team. Gaining idempotency only widens
    /// what is permitted and cannot invalidate a policy anyone already wrote.
    /// </remarks>
    private static void CompareIdempotency(
        List<DiffFinding> findings, string subject, ManifestCapability before, ManifestCapability after)
    {
        if (before.Idempotent == after.Idempotent)
        {
            return;
        }

        findings.Add(before.Idempotent
            ? new DiffFinding
            {
                Code = "FLOWX-DIFF-013",
                Severity = DiffSeverity.Breaking,
                Subject = subject,
                Summary = "idempotent: true -> false",
                Consequence =
                    "Retry policies already attached to this capability become illegal (FLOWX1014); " +
                    "flows that build against the baseline stop building.",
            }
            : new DiffFinding
            {
                Code = "FLOWX-DIFF-105",
                Severity = DiffSeverity.Additive,
                Subject = subject,
                Summary = "idempotent: false -> true",
                Consequence = "Retry policies become permissible at call sites; none become illegal.",
            });
    }

    /// <summary>Classifies a change to who may invoke a capability.</summary>
    /// <remarks>
    /// <para>
    /// Both directions are breaking, for different reasons, and the report says which.
    /// </para>
    /// <para>
    /// <strong>Relaxing</strong> — <c>Permission</c> to <c>Authenticated</c>, anything to
    /// <c>Public</c> — is a security regression. No signature moves, no test fails, and
    /// the capability becomes reachable by principals the baseline refused. It is stated
    /// as such rather than folded in with the rest, because "authorization changed" in a
    /// CI log does not make anybody stop.
    /// </para>
    /// <para>
    /// <strong>Tightening</strong> is breaking too, which is worth defending: it is
    /// unambiguously the right change to make, and it still stops callers that worked
    /// yesterday from working today. The gate does not say tightening is wrong; it says
    /// tightening needs the same coordination as any other break, and that shipping it
    /// unannounced turns a security improvement into an outage. <c>Permission</c> and
    /// <c>Policy</c> are not ordered against each other — a named policy can be broader or
    /// narrower than a named permission — so a move between them, or a change of the named
    /// value alone, is reported as a change rather than guessed at in either direction.
    /// </para>
    /// </remarks>
    private static void CompareAuthorization(
        List<DiffFinding> findings, string subject, ManifestAuthorization? before, ManifestAuthorization? after)
    {
        var beforeMode = before?.Mode;
        var afterMode = after?.Mode;

        if (Changed(beforeMode, afterMode))
        {
            var relaxed = Reach(beforeMode) is { } was && Reach(afterMode) is { } now && now > was;

            findings.Add(relaxed
                ? new DiffFinding
                {
                    Code = "FLOWX-DIFF-014",
                    Severity = DiffSeverity.Breaking,
                    Subject = subject,
                    Summary = $"authorization relaxed: {Show(beforeMode)} -> {Show(afterMode)}",
                    Consequence =
                        "Security regression: the capability is now reachable by principals the " +
                        "baseline refused. Nothing else in the build will notice this.",
                }
                : new DiffFinding
                {
                    Code = "FLOWX-DIFF-015",
                    Severity = DiffSeverity.Breaking,
                    Subject = subject,
                    Summary = $"authorization changed: {Show(beforeMode)} -> {Show(afterMode)}",
                    Consequence = "Callers authorised under the baseline may now be denied.",
                });

            return;
        }

        // Same stance, different named permission or policy. The mode alone would say
        // nothing changed, and the set of principals that passes the check just moved.
        if (Changed(before?.Value, after?.Value))
        {
            findings.Add(new DiffFinding
            {
                Code = "FLOWX-DIFF-015",
                Severity = DiffSeverity.Breaking,
                Subject = subject,
                Summary = $"authorization value changed: {Show(before?.Value)} -> {Show(after?.Value)}",
                Consequence = "Callers holding the previous grant may now be denied.",
            });
        }
    }

    /// <summary>
    /// How many principals a stance admits, where a comparison is meaningful at all.
    /// </summary>
    /// <remarks>
    /// <c>Permission</c> and <c>Policy</c> share a rank because neither contains the
    /// other, and an unknown mode has none at all — a stance this build does not recognise
    /// is not silently assumed to be the most permissive one, nor the least.
    /// </remarks>
    private static int? Reach(string? mode) => mode switch
    {
        "Public" => 4,
        "Authenticated" => 3,
        "Permission" => 2,
        "Policy" => 2,
        "Internal" => 1,
        _ => null,
    };

    /// <summary>Classifies a change to a capability's declared external effects.</summary>
    /// <remarks>
    /// A new side effect is breaking, which is the least obvious rule here. Nothing about
    /// the call changes — but <c>sideEffects</c> is what blast-radius review reads and what
    /// decides whether an agent asks a human before invoking the tool. A capability that
    /// consumers assessed as touching nothing outside the process now writes to a payment
    /// gateway, and every decision taken on the old declaration is stale. Losing a side
    /// effect narrows the blast radius and invalidates no decision anyone made.
    /// </remarks>
    private static void CompareSideEffects(
        List<DiffFinding> findings, string subject, ManifestCapability before, ManifestCapability after)
    {
        foreach (var effect in NotIn(after.SideEffects, before.SideEffects))
        {
            findings.Add(new DiffFinding
            {
                Code = "FLOWX-DIFF-016",
                Severity = DiffSeverity.Breaking,
                Subject = subject,
                Summary = $"side effect added: {effect}",
                Consequence =
                    "Blast radius widened. Agent confirmation prompts and every impact assessment " +
                    "made against the baseline are now wrong.",
            });
        }

        foreach (var effect in NotIn(before.SideEffects, after.SideEffects))
        {
            findings.Add(new DiffFinding
            {
                Code = "FLOWX-DIFF-106",
                Severity = DiffSeverity.Additive,
                Subject = subject,
                Summary = $"side effect removed: {effect}",
                Consequence = "Blast radius narrowed.",
            });
        }
    }

    private static void CompareErrors(
        List<DiffFinding> findings, string subject, ManifestCapability before, ManifestCapability after)
    {
        var wasDeclared = Catalogue(before);
        var isDeclared = Catalogue(after);

        // A new error code is compatible and documented: a consumer that does not know it
        // falls through to whatever it already does with an unrecognised failure.
        foreach (var code in NotIn(isDeclared.Keys, wasDeclared.Keys))
        {
            findings.Add(new DiffFinding
            {
                Code = "FLOWX-DIFF-104",
                Severity = DiffSeverity.Additive,
                Subject = subject,
                Summary = $"error code added: {code} ({Show(isDeclared[code])})",
            });
        }

        // A disappearing code reads like "that failure can no longer happen", and in
        // practice it almost always means the code was renamed. Either way a consumer
        // branching on it now has a branch that never runs, silently.
        foreach (var code in NotIn(wasDeclared.Keys, isDeclared.Keys))
        {
            findings.Add(new DiffFinding
            {
                Code = "FLOWX-DIFF-017",
                Severity = DiffSeverity.Breaking,
                Subject = subject,
                Summary = $"error code removed: {code}",
                Consequence =
                    "Consumers branching on this code silently stop matching. If it was renamed, " +
                    "keep the old code until callers have migrated.",
            });
        }

        // The category is what the transport maps to a status code, so recategorising is a
        // wire-visible change even though the code is identical: a client that retried on
        // Unavailable now sees a Conflict it will not retry, or vice versa.
        foreach (var code in Common(wasDeclared, isDeclared)
            .Where(c => Changed(wasDeclared[c], isDeclared[c])))
        {
            findings.Add(new DiffFinding
            {
                Code = "FLOWX-DIFF-018",
                Severity = DiffSeverity.Breaking,
                Subject = subject,
                Summary = $"error {code} recategorised: {Show(wasDeclared[code])} -> {Show(isDeclared[code])}",
                Consequence =
                    "The category drives the transport status code, so clients keyed on the old " +
                    "status — including their retry decisions — change behaviour.",
            });
        }
    }

    private static Dictionary<string, string?> Catalogue(ManifestCapability capability)
    {
        var catalogue = new Dictionary<string, string?>(StringComparer.Ordinal);

        foreach (var error in capability.Errors.Where(e => !string.IsNullOrEmpty(e.Code)))
        {
            catalogue[error.Code!] = error.Category;
        }

        return catalogue;
    }

    // ------------------------------------------------------------------- events

    private static void CompareEvents(
        List<DiffFinding> findings, ManifestDocument baseline, ManifestDocument candidate)
    {
        var before = ByMajor(baseline.Events, e => e.Type, e => e.SchemaVersion);
        var after = ByMajor(candidate.Events, e => e.Type, e => e.SchemaVersion);

        // Keyed by major like everything else, so bumping an event's schema major without
        // continuing to publish the old one lands here as a removal — which is exactly what
        // it is for a subscriber that pinned the old major.
        foreach (var key in Removed(before, after))
        {
            findings.Add(new DiffFinding
            {
                Code = "FLOWX-DIFF-020",
                Severity = DiffSeverity.Breaking,
                Subject = "event " + key,
                Summary = "removed",
                Consequence =
                    "Subscribers pinned to this major stop receiving anything, and nothing in " +
                    "their build will tell them.",
            });
        }

        foreach (var key in Added(before, after))
        {
            findings.Add(new DiffFinding
            {
                Code = "FLOWX-DIFF-102",
                Severity = DiffSeverity.Additive,
                Subject = "event " + key,
                Summary = "added",
            });
        }
    }

    // -------------------------------------------------------------------- plumbing

    /// <summary>Indexes manifest entries by <c>id@major</c>, keeping the newest of each.</summary>
    /// <remarks>
    /// An entry with no identity is dropped rather than keyed on the empty string: it
    /// cannot be tracked across two builds, so pairing two such entries would invent a
    /// comparison rather than report one.
    /// </remarks>
    private static Dictionary<string, T> ByMajor<T>(
        IEnumerable<T> items, Func<T, string?> identity, Func<T, string?> version)
    {
        var indexed = new Dictionary<string, T>(StringComparer.Ordinal);

        foreach (var item in items)
        {
            if (identity(item) is not { Length: > 0 } id)
            {
                continue;
            }

            var key = id + "@" + SemVer.Major(version(item));

            if (!indexed.TryGetValue(key, out var existing)
                || SemVer.Compare(version(existing), version(item)) < 0)
            {
                indexed[key] = item;
            }
        }

        return indexed;
    }

    private static IEnumerable<string> Removed<T>(Dictionary<string, T> before, Dictionary<string, T> after)
        => NotIn(before.Keys, after.Keys);

    private static IEnumerable<string> Added<T>(Dictionary<string, T> before, Dictionary<string, T> after)
        => NotIn(after.Keys, before.Keys);

    private static IEnumerable<string> Common<T>(Dictionary<string, T> before, Dictionary<string, T> after)
        => before.Keys.Intersect(after.Keys, StringComparer.Ordinal).Order(StringComparer.Ordinal);

    /// <summary>Members of <paramref name="left"/> absent from <paramref name="right"/>, ordered.</summary>
    /// <remarks>Ordered here so no caller has to remember that the report must be stable.</remarks>
    private static IEnumerable<string> NotIn(IEnumerable<string> left, IEnumerable<string> right)
        => left.Except(right, StringComparer.Ordinal).Order(StringComparer.Ordinal);

    private static bool Changed(string? before, string? after)
        => !string.Equals(before, after, StringComparison.Ordinal);

    /// <summary>Renders a value for a message, so an absent one reads as absent.</summary>
    private static string Show(string? value)
        => string.IsNullOrEmpty(value) ? "(none)" : value;
}
