using System.Reflection;
using System.Runtime.CompilerServices;
using Shouldly;
using Xunit;

namespace FlowX.Architecture.Tests;

/// <summary>
/// Guards the properties of the contract surface that the whole platform depends on.
/// These assert design rules, not implementation details — each maps to a numbered rule
/// in docs/07-Capability-Model.md §3, an ADR, or a quality goal in docs/05 §1.2.
/// </summary>
public sealed class ContractSurfaceTests
{
    private static readonly Assembly Abstractions = typeof(ICapability<,>).Assembly;

    /// <summary>
    /// Principle P5: the failure path must not allocate. <see cref="Result{T}"/> being a
    /// readonly struct is what makes quality goal Q1 hold when things go wrong — which is
    /// exactly when latency matters most.
    /// </summary>
    [Fact]
    public void ResultIsAnAllocationFreeValueType()
    {
        typeof(Result<>).IsValueType.ShouldBeTrue(
            "Result<T> must be a struct — a class would allocate on every failure (ADR-0007).");
    }

    /// <summary>
    /// The transport mapping table in docs/04-Core-Concepts.md §8 is exhaustive over this
    /// enum. Adding a member silently changes the HTTP status, the gRPC status and the
    /// retry decision for every existing consumer, so the set is closed (ADR-0009).
    /// </summary>
    [Fact]
    public void ErrorCategoryRemainsClosed()
    {
        var actual = Enum.GetNames<ErrorCategory>().OrderBy(static n => n, StringComparer.Ordinal).ToArray();

        actual.ShouldBe(
            ["Conflict", "Forbidden", "Internal", "NotFound", "Unavailable", "Validation"],
            "ErrorCategory is a closed set. Add error *codes*, never categories.");
    }

    /// <summary>
    /// Terminal categories are dead-lettered rather than retried. Getting this wrong gives
    /// either infinite retries on invalid input, or data loss on a transient fault.
    /// </summary>
    [Theory]
    [InlineData(ErrorCategory.Validation, true)]
    [InlineData(ErrorCategory.NotFound, true)]
    [InlineData(ErrorCategory.Forbidden, true)]
    [InlineData(ErrorCategory.Conflict, false)]
    [InlineData(ErrorCategory.Unavailable, false)]
    [InlineData(ErrorCategory.Internal, false)]
    public void TerminalCategoriesAreNeverRetried(ErrorCategory category, bool expectedTerminal)
    {
        category.IsTerminal().ShouldBe(expectedTerminal);
        category.IsRetryable().ShouldBe(!expectedTerminal);
    }

    /// <summary>Every category maps to exactly one canonical HTTP status.</summary>
    [Theory]
    [InlineData(ErrorCategory.Validation, 400)]
    [InlineData(ErrorCategory.Forbidden, 403)]
    [InlineData(ErrorCategory.NotFound, 404)]
    [InlineData(ErrorCategory.Conflict, 409)]
    [InlineData(ErrorCategory.Internal, 500)]
    [InlineData(ErrorCategory.Unavailable, 503)]
    public void ErrorCategoryMapsToTheDocumentedHttpStatus(ErrorCategory category, int expected)
        => category.ToHttpStatusCode().ShouldBe(expected);

    /// <summary>
    /// Principle P11: there is no permissive default. A capability that forgets to declare
    /// a stance must fail the build, which requires these to be <c>required</c> members
    /// rather than defaulted properties.
    /// </summary>
    [Theory]
    [InlineData(nameof(CapabilityAttribute.Version))]
    [InlineData(nameof(CapabilityAttribute.Authorization))]
    public void CapabilityMustDeclareVersionAndAuthorization(string memberName)
    {
        var property = typeof(CapabilityAttribute).GetProperty(memberName);

        property.ShouldNotBeNull($"CapabilityAttribute.{memberName} must exist.");

        property!.GetCustomAttribute<RequiredMemberAttribute>().ShouldNotBeNull(
            $"CapabilityAttribute.{memberName} must be `required`. Deny-by-default only " +
            "works if omitting it is impossible, not merely discouraged (principle P11).");
    }

    /// <summary>
    /// ADR-0003: durability is opt-in. If Durable were the zero value, every read query in
    /// every application would silently pay roughly a thousand times the platform overhead.
    /// </summary>
    [Fact]
    public void ExecutionProfileDefaultsToEphemeral()
    {
        default(ExecutionProfile).ShouldBe(
            ExecutionProfile.Ephemeral,
            "Ephemeral must be the zero value. You opt into cost, never out of it (ADR-0003).");
    }

    /// <summary>
    /// ADR-0011: the stage numbers <em>are</em> the safety guarantees. Each assertion below
    /// corresponds to a recurring production incident class that this ordering makes
    /// unexpressible.
    /// </summary>
    [Fact]
    public void PolicyStageOrderEncodesTheSafetyGuarantees()
    {
        ((int)PolicyStage.Admission).ShouldBeLessThan(
            (int)PolicyStage.Identity,
            "Rate limiting must precede authentication, or an unauthenticated flood exhausts the token validator.");

        ((int)PolicyStage.Identity).ShouldBeLessThan(
            (int)PolicyStage.Efficiency,
            "Authorisation must precede caching, or tenant A is served tenant B's cached data.");

        ((int)PolicyStage.Integrity).ShouldBeLessThan(
            (int)PolicyStage.Resilience,
            "Idempotency must precede retry, or a retry becomes a duplicate charge.");

        ((int)PolicyStage.Execution).ShouldBeLessThan(
            (int)PolicyStage.Consistency,
            "Compensation is registered only after the step succeeded, or we compensate something that never happened.");
    }

    /// <summary>
    /// Cross-tenant leakage must require an explicit, reviewable decision — never an
    /// omission (docs/16-Multi-Tenant.md §9).
    /// </summary>
    [Fact]
    public void TenantScopedIsTheDefaultForEveryScopeEnum()
    {
        default(CacheScope).ShouldBe(CacheScope.Tenant);
        default(RateLimitScope).ShouldBe(RateLimitScope.Tenant);
        default(IdempotencyScope).ShouldBe(IdempotencyScope.Tenant);
    }

    /// <summary>
    /// Principle P3: a flow must not be able to observe its transport. The builder surface
    /// therefore exposes no trigger type at all — a flow that can see how it was activated
    /// is a flow that will eventually branch on it, and quality goal Q4 is lost.
    /// </summary>
    [Fact]
    public void FlowBuilderExposesNoTransportTypes()
    {
        var offending = typeof(IFlowBuilder<,>).GetMethods()
            .SelectMany(static m => m.GetParameters())
            .Select(static p => p.ParameterType.Name)
            .Where(static name => name.Contains("Trigger", StringComparison.Ordinal))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        offending.ShouldBeEmpty("IFlowBuilder must not expose trigger types (principle P3).");
    }

    /// <summary>
    /// There is deliberately no <c>Do(lambda)</c>: inline code would be invisible to the
    /// manifest, untestable in isolation and undetectable by determinism analysis, which
    /// would quietly break quality goal Q3.
    /// </summary>
    [Fact]
    public void FlowBuilderHasNoEscapeHatchForInlineCode()
    {
        var names = typeof(IFlowBuilder<,>).GetMethods().Select(static m => m.Name).ToArray();

        names.ShouldNotContain("Do", "If it is worth executing, it is worth naming — make it a capability.");
        names.ShouldNotContain("Execute", "Same reason as Do: unnamed work never reaches the manifest.");
        names.ShouldNotContain("Invoke", "Same reason as Do.");
    }

    /// <summary>Every public type is part of a forever commitment (constraint C7), so it lives under one namespace.</summary>
    [Fact]
    public void EveryPublicTypeIsInTheFlowXNamespace()
    {
        var stragglers = Abstractions.GetExportedTypes()
            .Where(static t => t.Namespace is null || !t.Namespace.StartsWith("FlowX", StringComparison.Ordinal))
            .Select(static t => t.FullName ?? t.Name)
            .ToArray();

        stragglers.ShouldBeEmpty("Every public type must live under the FlowX namespace.");
    }
}
