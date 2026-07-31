using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using Shouldly;
using Xunit;

namespace FlowX.Architecture.Tests;

/// <summary>
/// The security gates from docs/15-Security.md §10 and docs/21-Quality-Gates.md §2.3,
/// in executable form.
/// </summary>
/// <remarks>
/// <para>
/// These were listed as gates, cited in the OWASP mapping, and did not exist. That is a
/// worse position than never having claimed them: a reviewer reading the A01 row sees
/// <c>EveryCapabilityDeclaresAuthorization</c> and stops looking.
/// </para>
/// <para>
/// The implementable ones are here. The two that are not — <c>CrossTenantAccessIsDenied</c>
/// and <c>RedactionCannotBeBypassed</c> — are deliberately <em>absent</em> rather than
/// present and vacuous, because a test named after a gate is itself a claim of coverage.
/// Nothing consumes <c>TenantId</c> and three of redaction's four sinks do not exist; what
/// each is blocked on is written down in docs/21-Quality-Gates.md §2.4. A gate that
/// asserts a property of code that has not been written is decoration, and decoration is
/// what got us here.
/// </para>
/// </remarks>
public sealed class SecurityFitnessTests
{
    private static readonly Assembly Abstractions = typeof(ICapability<,>).Assembly;

    /// <summary>
    /// The stance each security-relevant enum takes when it is left alone, and the member
    /// that must never be arrived at by omission.
    /// </summary>
    /// <remarks>
    /// "Permissive" is the member that grants the most and asks the least. For
    /// <see cref="Authorization"/> that is <see cref="Authorization.Public"/>; for a scope
    /// enum it is the one that spans tenants; for <see cref="ConfirmationMode"/> it is the
    /// one that lets an agent act unasked.
    /// </remarks>
    private static readonly (Type Enum, object Permissive)[] StanceEnums =
    [
        (typeof(Authorization), Authorization.Public),
        (typeof(ConfirmationMode), ConfirmationMode.Never),
        (typeof(CacheScope), CacheScope.Global),
        (typeof(RateLimitScope), RateLimitScope.Global),
        (typeof(IdempotencyScope), IdempotencyScope.Global),
    ];

    // ------------------------------------------------------------------ the survey itself

    /// <summary>
    /// The capability survey finds the capabilities that are actually there.
    /// </summary>
    /// <remarks>
    /// Without this, every gate below degrades silently into a pass. A source scan that
    /// stops matching — a moved directory, a renamed interface, a parser that chokes on a
    /// file — reports an empty set, and an empty set satisfies "every capability declares
    /// a stance" perfectly. This repository has already shipped one test that could not
    /// fail; a family of security gates resting on a scan is exactly where the second one
    /// would come from.
    /// </remarks>
    [Fact]
    public void TheCapabilitySurveyFindsTheShippedCapabilities()
    {
        const string Blind =
            "The survey no longer finds the capabilities samples/ecommerce declares, so it " +
            "has stopped seeing the source. Every gate that reads it is now passing vacuously.";

        var ids = SourceSurvey.Capabilities.Select(static c => c.Id).ToArray();

        ids.ShouldContain("order.validate", Blind);
        ids.ShouldContain("inventory.reserve", Blind);
        ids.ShouldContain("inventory.release", Blind);
        ids.ShouldContain("payment.capture", Blind);
    }

    // ------------------------------------------------------------------ A01: access control

    /// <summary>
    /// Principle P11: no capability ships without an authorisation stance.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="ContractSurfaceTests.CapabilityMustDeclareVersionAndAuthorization"/> asserts
    /// that the stance cannot be <em>omitted from the attribute</em>, because it is a
    /// <c>required</c> member. That is a different claim from this one. A class can implement
    /// <c>ICapability&lt;,&gt;</c> and carry no <c>[Capability]</c> attribute at all, at which
    /// point there is no required member to omit and the type compiles cleanly. FLOWX1010
    /// catches it when the type is used as a step; a capability registered by hand, reached
    /// through a sub-flow, or simply not wired up yet is not a step, and nothing looks at it.
    /// </para>
    /// <para>
    /// This is the gate the OWASP A01 row in docs/21-Quality-Gates.md §3 has been citing.
    /// </para>
    /// </remarks>
    [Fact]
    public void EveryCapabilityDeclaresAuthorization()
    {
        var undeclared = SourceSurvey.Capabilities
            .Where(static c => !c.HasCapabilityAttribute || c.Authorization is null)
            .Select(static c => c.HasCapabilityAttribute
                ? $"{c.Where} has [Capability] but names no Authorization"
                : $"{c.Where} implements ICapability<,> with no [Capability] attribute")
            .ToArray();

        undeclared.ShouldBeEmpty(
            "A capability without an authorisation stance is a capability whose access " +
            "control is whatever the caller's transport happened to enforce (principle P11, " +
            "OWASP A01):" + Environment.NewLine + string.Join(Environment.NewLine, undeclared));
    }

    /// <summary>
    /// Every stance a capability declares is a member of <see cref="Authorization"/>.
    /// </summary>
    /// <remarks>
    /// The survey reads the stance as written. A typo, a renamed member or a constant
    /// forwarded from elsewhere would leave a stance the gate above accepts and no human
    /// recognises — and <c>PublicCapabilitiesAreReviewed</c> below decides what to review by
    /// matching on the spelling <c>Public</c>. If the spelling is not one this enum knows,
    /// that match is unsound.
    /// </remarks>
    [Fact]
    public void EveryDeclaredStanceIsAKnownAuthorizationMember()
    {
        var known = Enum.GetNames<Authorization>();

        var unrecognised = SourceSurvey.Capabilities
            .Where(static c => c.Authorization is not null)
            .Where(c => !known.Contains(c.Authorization, StringComparer.Ordinal))
            .Select(static c => $"{c.Where} declares Authorization '{c.Authorization}'")
            .ToArray();

        unrecognised.ShouldBeEmpty(
            "A stance the Authorization enum does not contain cannot be reviewed and cannot " +
            "be matched by PublicCapabilitiesAreReviewed:" + Environment.NewLine +
            string.Join(Environment.NewLine, unrecognised));
    }

    /// <summary>
    /// <see cref="Authorization.Public"/> is a decision someone signed for.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The point of making <c>Public</c> explicit is that it is greppable and reviewable;
    /// <c>[ApprovedBy]</c> is what turns "reviewable" into "reviewed". Without it the stance
    /// is still explicit and still nobody's decision.
    /// </para>
    /// <para>
    /// No capability in the repository declares <c>Public</c> today, so this currently has
    /// nothing to reject — which is the correct state for a prohibition, not a reason to
    /// weaken it into something that finds work to do.
    /// <see cref="TheCapabilitySurveyFindsTheShippedCapabilities"/> is what stops that
    /// emptiness from being the scan silently finding nothing at all.
    /// </para>
    /// </remarks>
    [Fact]
    public void PublicCapabilitiesAreReviewed()
    {
        var unreviewed = SourceSurvey.Capabilities
            .Where(static c => c.Authorization == nameof(Authorization.Public))
            .Where(static c => string.IsNullOrWhiteSpace(c.Reviewer) || !IsIsoDate(c.ReviewDate))
            .Select(static c => c.Where)
            .ToArray();

        unreviewed.ShouldBeEmpty(
            "Authorization.Public without [ApprovedBy(reviewer, \"yyyy-MM-dd\")] naming a " +
            "reviewer and an ISO-8601 date. Anyone may invoke these, and nobody said so:" +
            Environment.NewLine + string.Join(Environment.NewLine, unreviewed));
    }

    /// <summary>
    /// An <c>[ApprovedBy]</c> that no longer sits on a <c>Public</c> capability is removed.
    /// </summary>
    /// <remarks>
    /// A stale approval is worse than none. It reads as a review of the current stance while
    /// recording a review of a stance that has since changed — and if the capability is ever
    /// widened back to <c>Public</c>, the gate above finds an approval already in place and
    /// says nothing.
    /// </remarks>
    [Fact]
    public void ApprovalsDoNotOutliveTheStanceTheyApproved()
    {
        var stale = SourceSurvey.Capabilities
            .Where(static c => c.Reviewer is not null)
            .Where(static c => c.Authorization != nameof(Authorization.Public))
            .Select(static c => $"{c.Where} declares {c.Authorization} but still carries [ApprovedBy]")
            .ToArray();

        stale.ShouldBeEmpty(
            "[ApprovedBy] records the review of a Public declaration. Left behind after the " +
            "stance narrowed, it pre-approves the next widening:" + Environment.NewLine +
            string.Join(Environment.NewLine, stale));
    }

    // ------------------------------------------------------------------ A05: misconfiguration

    /// <summary>
    /// Principle P11 / OWASP A05: nothing on the contract surface reaches a permissive
    /// setting by being left alone.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Two shapes, because a default has two spellings in C#. A property arrives at its
    /// zero value when the caller says nothing — so a property of a stance type is either
    /// <c>required</c>, or it must be initialised to something that is not the permissive
    /// member. A parameter arrives at its declared default the same way, so an optional
    /// parameter of a stance type must not default to the permissive member either.
    /// </para>
    /// <para>
    /// This matters most where it is least visible. <see cref="Authorization.Public"/> is the
    /// <em>zero value</em> of its enum: <c>default(Authorization)</c> is "anyone may invoke
    /// it". Nothing about that is an accident — the ordering puts the loosest stance first —
    /// and the entire safety of it rests on there being no way to obtain an
    /// <see cref="Authorization"/> without writing one down. <c>required</c> on
    /// <see cref="CapabilityAttribute.Authorization"/> is that guarantee, and this gate is
    /// what stops a second, unguarded member of that type appearing later.
    /// </para>
    /// <para>
    /// Scoped to <c>FlowX.Abstractions</c>, which is where a default becomes a
    /// <em>published</em> default — every plugin and all user code inherit it, and constraint
    /// C7 makes it a forever commitment.
    /// </para>
    /// </remarks>
    [Fact]
    public void NoPermissiveDefaults()
    {
        var violations = new List<string>();

        foreach (var (stance, permissive) in StanceEnums)
        {
            violations.AddRange(PermissiveProperties(stance, permissive));
            violations.AddRange(PermissiveParameters(stance, permissive));
        }

        violations.ShouldBeEmpty(
            "A permissive default is a security decision nobody made (principle P11, " +
            "OWASP A05):" + Environment.NewLine + string.Join(Environment.NewLine, violations));
    }

    /// <summary>
    /// Every scope enum on the contract surface is tenant-scoped at zero.
    /// </summary>
    /// <remarks>
    /// <see cref="ContractSurfaceTests.TenantScopedIsTheDefaultForEveryScopeEnum"/> asserts
    /// this for the three scope enums that existed when it was written, by name. The rule is
    /// about the shape, not those three: a fourth scope enum added later would satisfy that
    /// test by not being mentioned in it. This is the same failure that
    /// <see cref="DependencyRuleTests.EverySourceProjectIsCoveredByTheLayeringRule"/> exists
    /// to prevent, one layer up.
    /// </remarks>
    [Fact]
    public void EveryScopeEnumDefaultsToTenant()
    {
        var scopeEnums = Abstractions.GetExportedTypes()
            .Where(static t => t.IsEnum)
            .Where(static t => t.Name.EndsWith("Scope", StringComparison.Ordinal))
            .ToList();

        scopeEnums.ShouldNotBeEmpty("The scope enums have moved or been renamed.");

        foreach (var scope in scopeEnums)
        {
            Enum.GetName(scope, Activator.CreateInstance(scope)!).ShouldBe(
                "Tenant",
                $"{scope.Name}'s zero value must be Tenant. Cross-tenant leakage is an " +
                "explicit, reviewable decision, never an omission (docs/16-Multi-Tenant.md §9).");

            StanceEnums.ShouldContain(
                s => s.Enum == scope,
                $"{scope.Name} is a scope enum that NoPermissiveDefaults does not know about, " +
                "so nothing checks where it is used. Add it to StanceEnums with its " +
                "cross-tenant member.");
        }
    }

    // ------------------------------------------------------------------ A02: the manifest

    /// <summary>
    /// The manifest describes structure and never carries a value.
    /// </summary>
    /// <remarks>
    /// <para>
    /// ADR-0005 makes the manifest a published build artifact — fed to an agent, diffed in
    /// CI, attached to a release. Everything downstream of that treats it as safe to move
    /// around, and that is only true if it says a capability accepts a <c>CaptureRequest</c>
    /// without ever saying what was in one.
    /// </para>
    /// <para>
    /// This scans the manifests the build actually emitted, which is what
    /// docs/15-Security.md §6 describes and what
    /// <c>ManifestWriterTests.ContainsStructureButNoValues</c> — the assertion that has been
    /// standing in for this name — does not: that one runs over a hand-built model, so it
    /// only ever sees the fields the test author supplied.
    /// </para>
    /// <para>
    /// It matches on the <em>shape of a secret</em> rather than on the word "token", and the
    /// difference is not pedantry. <c>samples/ecommerce</c> declares a
    /// <c>[Sensitive] PaymentToken</c> member, so the real emitted manifest contains the
    /// string <c>PaymentToken</c> — correctly, as the name of a field a reviewer needs to see
    /// listed. A word-list gate fails there, and the only ways to make it pass are to delete
    /// the word from the list or the member from the sample. Naming a secret is the manifest
    /// working; carrying one is the leak.
    /// </para>
    /// </remarks>
    [Fact]
    public void ManifestContainsNoSecrets()
    {
        var manifests = EmittedManifests();

        manifests.ShouldNotBeEmpty(
            "No emitted manifest was found. Build the solution before running the " +
            "architecture gates — a scan with nothing to scan is not a passing gate.");

        var findings = new List<string>();

        foreach (var manifest in manifests)
        {
            // The generated form is a C# verbatim literal, so every quote in the JSON is
            // doubled. Undoing that first means the patterns match the document as
            // published rather than as escaped, and one set of patterns serves both the
            // generated manifests and the committed .json fixtures.
            var text = File.ReadAllText(manifest.FullName).Replace("\"\"", "\"", StringComparison.Ordinal);

            foreach (var (name, pattern) in SecretShapes.All)
            {
                var match = pattern.Match(text);

                if (match.Success)
                {
                    var line = text.Take(match.Index).Count(static c => c == '\n') + 1;

                    findings.Add(
                        $"{SourceSurvey.RelativePath(manifest)}:{line} contains {name} " +
                        $"({Masked(match.Value)})");
                }
            }
        }

        findings.ShouldBeEmpty(
            "The manifest is published, diffed and handed to agents (ADR-0005). It describes " +
            "structure; a value in it is a value that has left the building:" +
            Environment.NewLine + string.Join(Environment.NewLine, findings));
    }

    // ------------------------------------------------------------------ helpers

    /// <summary>
    /// Every manifest this repository produces or commits.
    /// </summary>
    /// <remarks>
    /// The generator compiles the manifest into <c>FlowXManifest.Json</c> and, because
    /// <c>EmitCompilerGeneratedFiles</c> is on, drops the source under
    /// <c>obj/generated/</c>. That file is the real artifact, byte for byte. The committed
    /// fixtures are included as well: they are manifests that ship in the repository, and a
    /// secret pasted into one is a secret in the history.
    /// </remarks>
    private static List<FileInfo> EmittedManifests()
    {
        var root = RepositoryLayout.Root;

        var generated = root
            .EnumerateFiles("FlowXManifest.g.cs", SearchOption.AllDirectories)
            .Where(static f => f.FullName.Replace('\\', '/').Contains("/obj/generated/", StringComparison.Ordinal));

        var committed = root
            .EnumerateFiles("*.manifest.json", SearchOption.AllDirectories)
            .Where(static f => !f.FullName.Replace('\\', '/')
                .Split('/')
                .Any(static segment => segment is "obj" or "bin" or ".git"));

        return generated.Concat(committed)
            .DistinctBy(static f => f.FullName, StringComparer.Ordinal)
            .OrderBy(static f => f.FullName, StringComparer.Ordinal)
            .ToList();
    }

    private static IEnumerable<string> PermissiveProperties(Type stance, object permissive)
    {
        foreach (var type in Abstractions.GetExportedTypes())
        {
            foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (property.PropertyType != stance || property.DeclaringType != type)
                {
                    continue;
                }

                if (property.GetCustomAttribute<RequiredMemberAttribute>() is not null)
                {
                    // Omitting it does not compile, so it has no default to be permissive.
                    continue;
                }

                if (!TryDefaultValue(type, property, out var value))
                {
                    yield return
                        $"{type.Name}.{property.Name} is an optional {stance.Name} on a type " +
                        "this gate cannot construct, so its default cannot be read. Make it " +
                        "`required`, or give the type a parameterless constructor.";

                    continue;
                }

                if (Equals(value, permissive))
                {
                    yield return
                        $"{type.Name}.{property.Name} defaults to {stance.Name}.{permissive}. " +
                        "Make it `required`, or initialise it to a stance that grants less.";
                }
            }
        }
    }

    private static IEnumerable<string> PermissiveParameters(Type stance, object permissive)
    {
        foreach (var type in Abstractions.GetExportedTypes())
        {
            var methods = type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static)
                .Cast<MethodBase>()
                .Concat(type.GetConstructors());

            foreach (var method in methods)
            {
                if (method.DeclaringType != type)
                {
                    continue;
                }

                foreach (var parameter in method.GetParameters())
                {
                    if (parameter.ParameterType != stance || !parameter.HasDefaultValue)
                    {
                        continue;
                    }

                    if (Equals(parameter.DefaultValue, permissive))
                    {
                        yield return
                            $"{type.Name}.{method.Name}({parameter.Name}) defaults to " +
                            $"{stance.Name}.{permissive}. An omitted argument must never widen " +
                            "access.";
                    }
                }
            }
        }
    }

    /// <summary>
    /// Reads what a property holds on an instance nobody configured.
    /// </summary>
    /// <remarks>
    /// Construction is attempted rather than assumed: an attribute with a mandatory
    /// positional argument has no parameterless constructor, and reporting "cannot read the
    /// default" is the honest outcome — better than skipping the member and calling the gate
    /// green. <c>required</c> members are a compile-time construct and do not obstruct
    /// reflection, so an attribute like <see cref="AgentTriggerAttribute"/> is constructible
    /// here even though C# would refuse the same call.
    /// </remarks>
    private static bool TryDefaultValue(Type declaring, PropertyInfo property, out object? value)
    {
        value = null;

        if (declaring.IsAbstract || declaring.GetConstructor(Type.EmptyTypes) is null)
        {
            return false;
        }

        try
        {
            value = property.GetValue(Activator.CreateInstance(declaring));
            return true;
        }
        catch (TargetInvocationException)
        {
            // A constructor that refuses to run without arguments. Same answer as no
            // constructor at all: the default is unreadable, so the member must be required.
            return false;
        }
    }

    private static bool IsIsoDate(string? value) =>
        value is not null
        && DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _);

    /// <summary>
    /// Enough of the match to recognise it, and no more.
    /// </summary>
    /// <remarks>
    /// A gate that finds a leaked credential and then prints it into the CI log has moved
    /// the leak rather than caught it, and a build log is a widely readable artifact. Four
    /// characters and a length are enough to find the value in the file; they are not
    /// enough to use it.
    /// </remarks>
    private static string Masked(string match) =>
        match.Length <= 4
            ? new string('•', match.Length)
            : $"{match[..4]}… {match.Length} chars";
}
