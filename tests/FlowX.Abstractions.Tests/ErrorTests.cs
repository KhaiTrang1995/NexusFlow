using Shouldly;
using Xunit;

namespace FlowX.Abstractions.Tests;

/// <summary>
/// Behavioural tests for <see cref="Error"/> and the category semantics every
/// transport derives from.
/// </summary>
public sealed class ErrorTests
{
    private static readonly Error Base =
        new("payment.declined", "the issuer declined the charge", ErrorCategory.Conflict);

    [Fact]
    public void CarriesItsCodeMessageAndCategory()
    {
        Base.Code.ShouldBe("payment.declined");
        Base.Message.ShouldBe("the issuer declined the charge");
        Base.Category.ShouldBe(ErrorCategory.Conflict);
        Base.Data.ShouldBeNull();
    }

    [Fact]
    public void WithAddsStructuredDetailWithoutMutatingTheOriginal()
    {
        var enriched = Base.With("issuerCode", "51");

        enriched.Data.ShouldNotBeNull();
        enriched.Data!["issuerCode"].ShouldBe("51");
        Base.Data.ShouldBeNull("Error is a value; With returns a copy.");
    }

    [Fact]
    public void WithAccumulatesAcrossCalls()
    {
        var enriched = Base.With("issuerCode", "51").With("attempt", 2);

        enriched.Data!.Count.ShouldBe(2);
        enriched.Data["issuerCode"].ShouldBe("51");
        enriched.Data["attempt"].ShouldBe(2);
    }

    [Fact]
    public void WithOverwritesAnExistingKey()
    {
        var enriched = Base.With("attempt", 1).With("attempt", 2);

        enriched.Data!.Count.ShouldBe(1);
        enriched.Data["attempt"].ShouldBe(2);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void WithRejectsABlankKey(string? key)
        => Should.Throw<ArgumentException>(() => Base.With(key!, "value"));

    [Fact]
    public void ToStringIsDiagnosable()
        => Base.ToString().ShouldBe("payment.declined (Conflict): the issuer declined the charge");

    [Fact]
    public void ErrorsWithTheSameValuesAreEqual()
    {
        var other = new Error("payment.declined", "the issuer declined the charge", ErrorCategory.Conflict);

        other.ShouldBe(Base);
        other.GetHashCode().ShouldBe(Base.GetHashCode());
    }

    [Fact]
    public void EveryCategoryIsEitherTerminalOrRetryableAndNeverBoth()
    {
        foreach (var category in Enum.GetValues<ErrorCategory>())
        {
            category.IsTerminal().ShouldNotBe(
                category.IsRetryable(),
                $"{category} must be exactly one of terminal or retryable.");
        }
    }

    [Fact]
    public void EveryCategoryMapsToAValidHttpStatus()
    {
        foreach (var category in Enum.GetValues<ErrorCategory>())
        {
            var status = category.ToHttpStatusCode();

            status.ShouldBeInRange(400, 599, $"{category} must map to a 4xx or 5xx status.");
        }
    }

    [Fact]
    public void TerminalCategoriesMapToClientErrorsAndRetryableToServerErrorsOrConflict()
    {
        // The correlation is not a coincidence: a 4xx says "do not send this again
        // unchanged", which is exactly what terminal means. Conflict is the one
        // deliberate exception — retryable, but still a 4xx.
        foreach (var category in Enum.GetValues<ErrorCategory>())
        {
            var status = category.ToHttpStatusCode();

            if (category.IsTerminal())
            {
                status.ShouldBeInRange(400, 499, $"{category} is terminal, so it is a client error.");
            }
            else
            {
                (status >= 500 || category == ErrorCategory.Conflict).ShouldBeTrue(
                    $"{category} is retryable, so it is a server error or the Conflict special case.");
            }
        }
    }

    [Fact]
    public void UnknownCategoryValuesFallBackToInternalServerError()
    {
        // Defence against a future member being added without the mapping table
        // being updated — the fallback must be the safe one, not a 200.
        ((ErrorCategory)999).ToHttpStatusCode().ShouldBe(500);
        ((ErrorCategory)999).IsTerminal().ShouldBeFalse();
    }
}
