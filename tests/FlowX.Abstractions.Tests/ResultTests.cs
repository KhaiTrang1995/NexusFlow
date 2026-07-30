using Shouldly;
using Xunit;

namespace FlowX.Abstractions.Tests;

/// <summary>
/// Behavioural tests for <see cref="Result{T}"/>. The architecture suite asserts
/// that it <em>is</em> a struct; these assert that it <em>works</em> — a distinction
/// that matters, because the type carries every capability outcome in the platform.
/// </summary>
public sealed class ResultTests
{
    private static readonly Error OutOfStock =
        new("inventory.out_of_stock", "SKU-1 is not available", ErrorCategory.Conflict);

    [Fact]
    public void SuccessCarriesItsValue()
    {
        var result = Result.Ok(42);

        result.IsSuccess.ShouldBeTrue();
        result.IsFailure.ShouldBeFalse();
        result.Value.ShouldBe(42);
    }

    [Fact]
    public void FailureCarriesItsError()
    {
        var result = Result.Fail<int>(OutOfStock);

        result.IsFailure.ShouldBeTrue();
        result.IsSuccess.ShouldBeFalse();
        result.Error.ShouldBe(OutOfStock);
    }

    [Fact]
    public void ReadingTheValueOfAFailureIsACallerDefect()
    {
        var result = Result.Fail<int>(OutOfStock);

        var thrown = Should.Throw<InvalidOperationException>(() => _ = result.Value);
        thrown.Message.ShouldContain("inventory.out_of_stock",
            Case.Sensitive,
            "The message must name the error, or the developer has to debug to find out why.");
    }

    [Fact]
    public void ReadingTheErrorOfASuccessIsACallerDefect()
        => Should.Throw<InvalidOperationException>(() => _ = Result.Ok(1).Error);

    [Fact]
    public void TryGetValueReportsSuccessWithoutThrowing()
    {
        var ok = Result.Ok("value").TryGetValue(out var value, out var error);

        ok.ShouldBeTrue();
        value.ShouldBe("value");
        error.ShouldBeNull();
    }

    [Fact]
    public void TryGetValueReportsFailureWithoutThrowing()
    {
        var ok = Result.Fail<string>(OutOfStock).TryGetValue(out var value, out var error);

        ok.ShouldBeFalse();
        value.ShouldBeNull();
        error.ShouldBe(OutOfStock);
    }

    [Fact]
    public void MapProjectsASuccess()
        => Result.Ok(21).Map(static x => x * 2).Value.ShouldBe(42);

    [Fact]
    public void MapPropagatesAFailureWithoutInvokingTheProjection()
    {
        var invoked = false;

        var mapped = Result.Fail<int>(OutOfStock).Map(x =>
        {
            invoked = true;
            return x * 2;
        });

        mapped.IsFailure.ShouldBeTrue();
        mapped.Error.ShouldBe(OutOfStock);
        invoked.ShouldBeFalse("Mapping a failure must not run the projection.");
    }

    [Fact]
    public void MapChangesTheValueTypeWhilePreservingTheError()
    {
        Result<string> mapped = Result.Fail<int>(OutOfStock)
            .Map(static x => x.ToString(System.Globalization.CultureInfo.InvariantCulture));

        mapped.Error.ShouldBe(OutOfStock);
    }

    [Fact]
    public void MatchCollapsesBothOutcomes()
    {
        Result.Ok(7).Match(static v => $"ok:{v}", static e => $"err:{e.Code}").ShouldBe("ok:7");

        Result.Fail<int>(OutOfStock)
            .Match(static v => $"ok:{v}", static e => $"err:{e.Code}")
            .ShouldBe("err:inventory.out_of_stock");
    }

    [Fact]
    public void RejectsNullDelegates()
    {
        Should.Throw<ArgumentNullException>(() => Result.Ok(1).Map<int>(null!));
        Should.Throw<ArgumentNullException>(() => Result.Ok(1).Match(null!, static _ => 0));
        Should.Throw<ArgumentNullException>(() => Result.Ok(1).Match(static _ => 0, null!));
    }

    [Fact]
    public void RejectsANullError()
        => Should.Throw<ArgumentNullException>(() => Result.Fail<int>(null!));

    [Fact]
    public void FailFromPartsBuildsTheSameResult()
    {
        var result = Result.Fail<int>("order.not_found", "no such order", ErrorCategory.NotFound);

        result.Error.Code.ShouldBe("order.not_found");
        result.Error.Category.ShouldBe(ErrorCategory.NotFound);
    }

    [Fact]
    public void ADefaultResultIsASuccessCarryingTheDefaultValue()
    {
        // `default(Result<T>)` has no error, so it reports success. Documented here
        // because it is surprising, and because a future change that made it report
        // failure would silently alter every uninitialised field in the runtime.
        default(Result<int>).IsSuccess.ShouldBeTrue();
        default(Result<int>).Value.ShouldBe(0);
    }

    [Fact]
    public void AValueConvertsImplicitlyToASuccess()
    {
        // This is the shape every capability returns: `return new Reservation(...)`.
        // Without the conversion the documented style does not compile, which is how
        // the ecommerce sample found it.
        Result<int> result = 42;

        result.IsSuccess.ShouldBeTrue();
        result.Value.ShouldBe(42);
    }

    [Fact]
    public void AnErrorConvertsImplicitlyToAFailure()
    {
        Result<int> result = new Error("order.rejected", "no", ErrorCategory.Conflict);

        result.IsFailure.ShouldBeTrue();
        result.Error.Code.ShouldBe("order.rejected");
    }

    [Fact]
    public void TheConversionsSurviveTheConditionalOperator()
    {
        // A capability that returns one branch or the other in a single expression is
        // the case that actually exercises both conversions against one target type.
        static Result<string> Pick(bool ok) => ok
            ? "receipt-1"
            : new Error("payment.declined", "no funds", ErrorCategory.Conflict);

        Pick(true).Value.ShouldBe("receipt-1");
        Pick(false).Error.Code.ShouldBe("payment.declined");
    }

    [Fact]
    public void ResultOfErrorStillWorksThroughTheFactories()
    {
        // `Result<Error>` is the one instantiation where the two conversions collide,
        // so an implicit conversion to it is a CS0457 at the call site — loud, never
        // silent. The factories stay unambiguous, which is what makes that acceptable.
        var carried = new Error("audit.finding", "found", ErrorCategory.Validation);

        Result.Ok(carried).Value.ShouldBeSameAs(carried);
        Result.Fail<Error>(carried).Error.ShouldBeSameAs(carried);
    }
}
