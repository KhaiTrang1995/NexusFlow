using System.Collections.Frozen;
using FlowX.Http;
using Shouldly;
using Xunit;

namespace FlowX.Http.Tests;

/// <summary>
/// RFC 7807 mapping. One mapping for the whole platform, derived from the error's
/// category — because a per-endpoint mapping is how a codebase ends up returning 400,
/// 409 and 422 for the same business condition depending on who wrote the handler.
/// </summary>
public sealed class ProblemDetailsMapperTests
{
    private static Microsoft.AspNetCore.Mvc.ProblemDetails Map(Error error) =>
        ProblemDetailsMapper.ToProblemDetails(error, "/api/v1/orders", "corr-1");

    [Theory]
    [InlineData(ErrorCategory.Validation, 400)]
    [InlineData(ErrorCategory.Forbidden, 403)]
    [InlineData(ErrorCategory.NotFound, 404)]
    [InlineData(ErrorCategory.Conflict, 409)]
    [InlineData(ErrorCategory.Internal, 500)]
    [InlineData(ErrorCategory.Unavailable, 503)]
    public void EveryCategoryMapsToItsDocumentedStatus(ErrorCategory category, int expected)
        => Map(new Error("a.b", "message", category)).Status.ShouldBe(expected);

    [Fact]
    public void TheTypeUriIdentifiesTheErrorCode()
    {
        var problem = Map(new Error("inventory.out_of_stock", "no stock", ErrorCategory.Conflict));

        problem.Type.ShouldBe("https://flowx.dev/errors/inventory.out_of_stock");
        problem.Extensions["code"].ShouldBe("inventory.out_of_stock");
        // The code is duplicated into an extension deliberately: parsing a URI to
        // recover it is the sort of thing every client would get subtly wrong.
    }

    [Fact]
    public void TheTitleIsFixedPerCategoryAndTheDetailIsPerOccurrence()
    {
        var first = Map(new Error("inventory.out_of_stock", "SKU-1 is unavailable", ErrorCategory.Conflict));
        var second = Map(new Error("order.already_placed", "order 42 already exists", ErrorCategory.Conflict));

        first.Title.ShouldBe(second.Title,
            "RFC 7807: the title must not change between occurrences of the same problem type.");
        first.Detail.ShouldNotBe(second.Detail, "The occurrence-specific part is the detail.");
    }

    [Fact]
    public void TheInstanceIdentifiesTheRequest()
        => Map(new Error("a.b", "m", ErrorCategory.Validation)).Instance.ShouldBe("/api/v1/orders");

    [Fact]
    public void TheCorrelationIdIsAlwaysPresent()
        => Map(new Error("a.b", "m", ErrorCategory.Validation))
            .Extensions["correlationId"].ShouldBe("corr-1");

    /// <summary>
    /// The security property this whole class exists for.
    /// </summary>
    /// <remarks>
    /// An internal error's message is written by us, for us: it names types, hosts,
    /// query shapes and occasionally data. Returning it to a caller is an information
    /// leak (OWASP A05), and it is the single easiest one to ship by accident.
    /// </remarks>
    [Fact]
    public void AnInternalErrorNeverLeaksItsMessage()
    {
        var leaky = new Error(
            "db.query_failed",
            "Npgsql.PostgresException: relation \"public.customer_pii\" does not exist at 10.0.4.17:5432",
            ErrorCategory.Internal);

        var problem = Map(leaky);

        var detail = problem.Detail.ShouldNotBeNull();

        detail.ShouldBe(ProblemDetailsMapper.InternalDetail);
        detail.ShouldNotContain("customer_pii");
        detail.ShouldNotContain("10.0.4.17");
        detail.ShouldNotContain("Npgsql");

        problem.Extensions["correlationId"].ShouldBe("corr-1",
            "The client gets the correlation id instead. The real message goes to the " +
            "log, and the id joins them back up.");
    }

    [Fact]
    public void AnInternalErrorAlsoWithholdsItsStructuredData()
    {
        var leaky = new Error("db.query_failed", "boom", ErrorCategory.Internal)
            .With("connectionString", "Host=10.0.4.17;Password=hunter2")
            .With("sql", "SELECT * FROM customer_pii");

        var problem = Map(leaky);

        problem.Extensions.ContainsKey("connectionString").ShouldBeFalse(
            "Structured data on an internal error is diagnostic, not something a caller " +
            "can act on. Withholding the message but publishing the data would be an " +
            "obvious hole and an easy one to miss.");
        problem.Extensions.ContainsKey("sql").ShouldBeFalse();
    }

    [Fact]
    public void ABusinessErrorPublishesItsStructuredDataBecauseTheCallerCanActOnIt()
    {
        var actionable = new Error("inventory.out_of_stock", "SKU-1 unavailable", ErrorCategory.Conflict)
            .With("sku", "SKU-1")
            .With("availableQuantity", 0);

        var problem = Map(actionable);

        problem.Extensions["sku"].ShouldBe("SKU-1");
        problem.Extensions["availableQuantity"].ShouldBe(0);
        problem.Detail.ShouldBe("SKU-1 unavailable");
    }

    [Fact]
    public void HandlesAnErrorWithNoStructuredData()
    {
        var problem = Map(new Error("a.b", "m", ErrorCategory.Validation, Data: null));

        problem.Extensions.Count.ShouldBe(2, "Just the code and the correlation id.");
    }

    [Fact]
    public void HandlesAnErrorWithEmptyStructuredData()
    {
        var empty = new Error(
            "a.b", "m", ErrorCategory.Validation,
            FrozenDictionary<string, object?>.Empty);

        Map(empty).Extensions.Count.ShouldBe(2);
    }

    [Fact]
    public void EveryCategoryHasADistinctTitle()
    {
        var titles = Enum.GetValues<ErrorCategory>()
            .Select(ProblemDetailsMapper.TitleFor)
            .ToArray();

        titles.Distinct(StringComparer.Ordinal).Count().ShouldBe(titles.Length,
            "Two categories sharing a title makes the distinction invisible to a caller.");
        titles.ShouldAllBe(t => !string.IsNullOrWhiteSpace(t));
    }

    [Fact]
    public void RejectsANullError()
        => Should.Throw<ArgumentNullException>(
            () => ProblemDetailsMapper.ToProblemDetails(null!, "/x", "corr"));

    [Fact]
    public void RedactsStructuredDetailNamingASensitiveMember()
    {
        // The one path in this release that serialises anything a capability attached to
        // an error. Before this, `[Sensitive]` recorded a fact and stripped nothing.
        var error = new Error("payment.declined", "no", ErrorCategory.Conflict)
            .With("paymentToken", "tok_live_secret")
            .With("attempt", 2);

        var problem = ProblemDetailsMapper.ToProblemDetails(
            error, "/orders", "corr", ["PaymentToken"]);

        problem.Extensions["paymentToken"].ShouldBe(ProblemDetailsMapper.Redacted);

        // Everything else still travels. A redactor that swallowed the whole payload
        // would take the caller's ability to act on the error with it.
        problem.Extensions["attempt"].ShouldBe(2);
    }

    [Fact]
    public void TheMatchIsCaseInsensitive()
    {
        // The wire contract is camelCase and the member is PascalCase. A case-sensitive
        // match would let through exactly the spelling a capability actually writes.
        var error = new Error("x", "y", ErrorCategory.Validation).With("paymentToken", "secret");

        ProblemDetailsMapper.ToProblemDetails(error, "/x", "corr", ["PaymentToken"])
            .Extensions["paymentToken"].ShouldBe(ProblemDetailsMapper.Redacted);
    }

    [Fact]
    public void RedactionUsesAPlaceholderRatherThanDroppingTheKey()
    {
        // A key that silently vanishes reads as a field the server never received.
        var error = new Error("x", "y", ErrorCategory.Validation).With("cardNumber", "4111");

        var problem = ProblemDetailsMapper.ToProblemDetails(error, "/x", "corr", ["CardNumber"]);

        problem.Extensions.ShouldContainKey("cardNumber");
    }

    [Fact]
    public void NoSensitiveMembersMeansNoRedaction()
    {
        var error = new Error("x", "y", ErrorCategory.Validation).With("sku", "SKU-1");

        ProblemDetailsMapper.ToProblemDetails(error, "/x", "corr").Extensions["sku"].ShouldBe("SKU-1");
        ProblemDetailsMapper.ToProblemDetails(error, "/x", "corr", []).Extensions["sku"].ShouldBe("SKU-1");
    }

    [Fact]
    public void AnInternalErrorWithholdsEveryDetailSensitiveOrNot()
    {
        // Internal already withholds all structured detail, so redaction never has to
        // carry that case — asserted so a future change to either cannot open a gap.
        var error = new Error("boom", "stack", ErrorCategory.Internal).With("paymentToken", "secret");

        var problem = ProblemDetailsMapper.ToProblemDetails(error, "/x", "corr", ["PaymentToken"]);

        problem.Extensions.ShouldNotContainKey("paymentToken");
    }
}
