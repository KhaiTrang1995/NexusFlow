using FlowX.Compiler.Analysis;
using Shouldly;
using Xunit;

namespace FlowX.Compiler.Tests;

/// <summary>
/// FLOWX1016 — whether a capability expresses its failures as values.
/// </summary>
/// <remarks>
/// Both directions for every case, and the silent direction carries more weight than the
/// firing one. The rule decides "is this an expected outcome?" from an exception type,
/// which is a judgement it cannot make in general — so every case it gets wrong in the
/// firing direction is a capability author reaching for a file-level suppression, after
/// which the rule protects nothing at all.
/// </remarks>
public sealed class CapabilityThrowAnalyzerTests
{
    private const string Preamble = """
        using System;
        using System.Collections.Generic;
        using System.Threading;
        using System.Threading.Tasks;
        using FlowX;

        namespace Sample;

        public sealed record CaptureRequest(string OrderId, decimal Amount);
        public sealed record Capture(string Reference);

        public sealed class PaymentDeclinedException : Exception
        {
            public PaymentDeclinedException(string message) : base(message) { }
        }
        """;

    private static string With(string body) => Preamble + "\n\n" + body;

    private static string[] Analyze(string body) =>
        GeneratorHarness.Analyze(With(body), new CapabilityThrowAnalyzer());

    private static string[] Messages(string body) =>
        GeneratorHarness.AnalyzeWithMessages(With(body), new CapabilityThrowAnalyzer());

    /// <summary>The shape every capability in the reference sample has.</summary>
    [Fact]
    public void ACapabilityThatReturnsResultsIsClean()
    {
        Analyze("""
            [Capability("payment.capture", Version = "1.0.0", Authorization = Authorization.Internal)]
            public sealed class CapturePayment : ICapability<CaptureRequest, Capture>
            {
                public ValueTask<Result<Capture>> ExecuteAsync(CaptureRequest input, CapabilityContext ctx, CancellationToken ct)
                    => ValueTask.FromResult(input.Amount > 0
                        ? Result.Ok(new Capture("ref"))
                        : Result.Fail<Capture>(new Error("payment.declined", "declined", ErrorCategory.Conflict)));
            }
            """).ShouldBeEmpty();
    }

    [Fact]
    public void ReportsFLOWX1016WhenABusinessOutcomeIsThrown()
    {
        // The case ADR-0007 is written against: "declined" is an outcome a caller handles,
        // and thrown it is invisible in the signature and in the error catalogue.
        Analyze("""
            [Capability("payment.capture", Version = "1.0.0", Authorization = Authorization.Internal)]
            public sealed class CapturePayment : ICapability<CaptureRequest, Capture>
            {
                public ValueTask<Result<Capture>> ExecuteAsync(CaptureRequest input, CapabilityContext ctx, CancellationToken ct)
                {
                    if (input.Amount <= 0)
                    {
                        throw new PaymentDeclinedException("amount must be positive");
                    }

                    return ValueTask.FromResult(Result.Ok(new Capture("ref")));
                }
            }
            """).ShouldBe(["FLOWX1016"]);
    }

    [Fact]
    public void TheMessageNamesTheCapabilityAndTheExceptionType()
    {
        // Which capability and which exception, because the fix is a rewrite of one
        // statement and a message naming neither sends the reader looking for it.
        Messages("""
            [Capability("payment.capture", Version = "1.0.0", Authorization = Authorization.Internal)]
            public sealed class CapturePayment : ICapability<CaptureRequest, Capture>
            {
                public ValueTask<Result<Capture>> ExecuteAsync(CaptureRequest input, CapabilityContext ctx, CancellationToken ct)
                    => throw new PaymentDeclinedException("declined");
            }
            """).ShouldHaveSingleItem()
                .ShouldSatisfyAllConditions(
                    message => message.ShouldContain("CapturePayment"),
                    message => message.ShouldContain("PaymentDeclinedException"));
    }

    [Fact]
    public void ReportsFLOWX1016ForAFrameworkExceptionThatDescribesAnOutcome()
    {
        // InvalidOperationException is the idiomatic .NET spelling of "not in a state
        // where that is allowed", which is a business outcome wearing a framework type.
        Analyze("""
            [Capability("order.cancel", Version = "1.0.0", Authorization = Authorization.Internal)]
            public sealed class CancelOrder : ICapability<CaptureRequest, Capture>
            {
                public ValueTask<Result<Capture>> ExecuteAsync(CaptureRequest input, CapabilityContext ctx, CancellationToken ct)
                    => throw new InvalidOperationException("order is not cancellable");
            }
            """).ShouldBe(["FLOWX1016"]);
    }

    [Fact]
    public void ReportsFLOWX1016InsideALambdaWrittenInTheBody()
    {
        // A lambda declared in ExecuteAsync runs inside the call the engine made, so the
        // throw leaks exactly as far as one written at statement level.
        Analyze("""
            [Capability("order.total", Version = "1.0.0", Authorization = Authorization.Internal)]
            public sealed class TotalOrder : ICapability<CaptureRequest, Capture>
            {
                public ValueTask<Result<Capture>> ExecuteAsync(CaptureRequest input, CapabilityContext ctx, CancellationToken ct)
                {
                    Func<decimal, decimal> check = amount => amount > 0
                        ? amount
                        : throw new PaymentDeclinedException("non-positive");

                    return ValueTask.FromResult(Result.Ok(new Capture(check(input.Amount).ToString())));
                }
            }
            """).ShouldBe(["FLOWX1016"]);
    }

    /// <summary>
    /// The guard clause CA1062 asks for is not a business outcome.
    /// </summary>
    /// <remarks>
    /// A null input is the engine handing a capability something it promised not to. It is
    /// a platform defect, and the runtime already reports it as one — this rule firing on
    /// it would be firing on the pattern the other analyzers demand.
    /// </remarks>
    [Fact]
    public void AGuardClauseIsNotAnExpectedFailure()
    {
        Analyze("""
            [Capability("payment.capture", Version = "1.0.0", Authorization = Authorization.Internal)]
            public sealed class CapturePayment : ICapability<CaptureRequest, Capture>
            {
                public ValueTask<Result<Capture>> ExecuteAsync(CaptureRequest input, CapabilityContext ctx, CancellationToken ct)
                {
                    if (input is null)
                    {
                        throw new ArgumentNullException(nameof(input));
                    }

                    return ValueTask.FromResult(Result.Ok(new Capture("ref")));
                }
            }
            """).ShouldBeEmpty();
    }

    [Fact]
    public void AnUnreachableSwitchArmIsNotAnExpectedFailure()
    {
        // `_ => throw new ArgumentOutOfRangeException(...)` over a closed enum is the
        // standard way to say "this cannot happen", which is a defect claim, not an outcome.
        Analyze("""
            public enum Channel { Retail, Wholesale }

            [Capability("order.route", Version = "1.0.0", Authorization = Authorization.Internal)]
            public sealed class RouteOrder : ICapability<CaptureRequest, Capture>
            {
                public ValueTask<Result<Capture>> ExecuteAsync(CaptureRequest input, CapabilityContext ctx, CancellationToken ct)
                {
                    var channel = Channel.Retail;

                    var reference = channel switch
                    {
                        Channel.Retail => "retail",
                        Channel.Wholesale => "wholesale",
                        _ => throw new ArgumentOutOfRangeException(nameof(channel)),
                    };

                    return ValueTask.FromResult(Result.Ok(new Capture(reference)));
                }
            }
            """).ShouldBeEmpty();
    }

    [Fact]
    public void AnUnimplementedCapabilityIsNotAnExpectedFailure()
    {
        // Scaffolding. `NotImplementedException` says the code does not exist, which is
        // not a failure mode a caller can be handed as a value.
        Analyze("""
            [Capability("payment.refund", Version = "1.0.0", Authorization = Authorization.Internal)]
            public sealed class RefundPayment : ICapability<CaptureRequest, Capture>
            {
                public ValueTask<Result<Capture>> ExecuteAsync(CaptureRequest input, CapabilityContext ctx, CancellationToken ct)
                    => throw new NotImplementedException();
            }
            """).ShouldBeEmpty();
    }

    [Fact]
    public void ARethrowIsNotReported()
    {
        // `throw;` re-raises something the capability did not create — ADR-0007's
        // infrastructure-fault signal — and there is no `new` to point a diagnostic at.
        Analyze("""
            [Capability("payment.capture", Version = "1.0.0", Authorization = Authorization.Internal)]
            public sealed class CapturePayment : ICapability<CaptureRequest, Capture>
            {
                public ValueTask<Result<Capture>> ExecuteAsync(CaptureRequest input, CapabilityContext ctx, CancellationToken ct)
                {
                    try
                    {
                        return ValueTask.FromResult(Result.Ok(new Capture("ref")));
                    }
                    catch (TimeoutException)
                    {
                        throw;
                    }
                }
            }
            """).ShouldBeEmpty();
    }

    [Fact]
    public void AThrowTheCapabilityCatchesItselfIsNotReported()
    {
        // It may never leave the method, and the analyzer cannot tell whether the catch
        // turns it into a Result. Silence is the deliberate direction to be wrong in.
        Analyze("""
            [Capability("payment.capture", Version = "1.0.0", Authorization = Authorization.Internal)]
            public sealed class CapturePayment : ICapability<CaptureRequest, Capture>
            {
                public ValueTask<Result<Capture>> ExecuteAsync(CaptureRequest input, CapabilityContext ctx, CancellationToken ct)
                {
                    try
                    {
                        if (input.Amount <= 0)
                        {
                            throw new PaymentDeclinedException("non-positive");
                        }
                    }
                    catch (PaymentDeclinedException e)
                    {
                        return ValueTask.FromResult(
                            Result.Fail<Capture>(new Error("payment.declined", e.Message, ErrorCategory.Conflict)));
                    }

                    return ValueTask.FromResult(Result.Ok(new Capture("ref")));
                }
            }
            """).ShouldBeEmpty();
    }

    [Fact]
    public void AThrowInAHelperMethodIsNotSeen()
    {
        // The stated limit, pinned so that it is a known gap rather than a surprise:
        // nothing here is interprocedural. Documented on the page.
        Analyze("""
            [Capability("payment.capture", Version = "1.0.0", Authorization = Authorization.Internal)]
            public sealed class CapturePayment : ICapability<CaptureRequest, Capture>
            {
                public ValueTask<Result<Capture>> ExecuteAsync(CaptureRequest input, CapabilityContext ctx, CancellationToken ct)
                    => ValueTask.FromResult(Result.Ok(Check(input)));

                private static Capture Check(CaptureRequest input)
                    => input.Amount > 0 ? new Capture("ref") : throw new PaymentDeclinedException("non-positive");
            }
            """).ShouldBeEmpty();
    }

    [Fact]
    public void AThrowOutsideACapabilityIsNotReported()
    {
        // The rule is about capabilities. An ordinary class named ExecuteAsync is not one,
        // and infrastructure adapters throw by nature — that is why capabilities translate.
        Analyze("""
            public sealed class PaymentGatewayClient
            {
                public ValueTask<Result<Capture>> ExecuteAsync(CaptureRequest input, CapabilityContext ctx, CancellationToken ct)
                    => throw new PaymentDeclinedException("declined");
            }
            """).ShouldBeEmpty();
    }
}
