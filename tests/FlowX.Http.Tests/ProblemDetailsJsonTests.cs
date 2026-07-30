using System.Text;
using System.Text.Json;
using FlowX.Http;
using Microsoft.AspNetCore.Mvc;
using Shouldly;
using Xunit;

namespace FlowX.Http.Tests;

/// <summary>
/// The wire format. <see cref="ProblemDetailsJson"/> is hand-written because
/// <c>ProblemDetails.Extensions</c> holds <c>object</c> values, and serialising those
/// needs runtime type resolution — which constraint C2 forbids. Hand-written means
/// hand-tested.
/// </summary>
public sealed class ProblemDetailsJsonTests
{
    private static JsonDocument Render(ProblemDetails problem) =>
        JsonDocument.Parse(Encoding.UTF8.GetString(ProblemDetailsJson.ToUtf8(problem)));

    private static ProblemDetails Sample() => new()
    {
        Type = "https://flowx.dev/errors/inventory.out_of_stock",
        Title = "The request conflicts with the current state",
        Status = 409,
        Detail = "SKU-1 is unavailable",
        Instance = "/api/v1/orders",
    };

    [Fact]
    public void WritesTheFiveRfc7807Members()
    {
        using var document = Render(Sample());
        var root = document.RootElement;

        root.GetProperty("type").GetString().ShouldBe("https://flowx.dev/errors/inventory.out_of_stock");
        root.GetProperty("title").GetString().ShouldBe("The request conflicts with the current state");
        root.GetProperty("status").GetInt32().ShouldBe(409);
        root.GetProperty("detail").GetString().ShouldBe("SKU-1 is unavailable");
        root.GetProperty("instance").GetString().ShouldBe("/api/v1/orders");
    }

    [Fact]
    public void OmitsMembersThatAreAbsentRatherThanWritingNull()
    {
        using var document = Render(new ProblemDetails { Status = 500 });

        document.RootElement.TryGetProperty("detail", out _).ShouldBeFalse(
            "RFC 7807 members are optional. Writing them as null adds noise a client " +
            "then has to distinguish from a meaningful null.");
        document.RootElement.TryGetProperty("instance", out _).ShouldBeFalse();
        document.RootElement.GetProperty("status").GetInt32().ShouldBe(500);
    }

    [Theory]
    [InlineData("sku", "SKU-1", JsonValueKind.String)]
    [InlineData("count", 42, JsonValueKind.Number)]
    [InlineData("retryable", true, JsonValueKind.True)]
    [InlineData("missing", null, JsonValueKind.Null)]
    public void WritesEachPrimitiveExtensionAsItsOwnJsonType(string key, object? value, JsonValueKind expected)
    {
        var problem = Sample();
        problem.Extensions[key] = value;

        using var document = Render(problem);

        document.RootElement.GetProperty(key).ValueKind.ShouldBe(expected,
            "Writing every extension as a string would force clients to parse numbers " +
            "and booleans back out of text.");
    }

    [Fact]
    public void WritesNumericTypesWithoutLosingPrecision()
    {
        var problem = Sample();
        problem.Extensions["long"] = 9_007_199_254_740_993L;
        problem.Extensions["double"] = 1.5d;
        problem.Extensions["decimal"] = 19.99m;

        using var document = Render(problem);

        document.RootElement.GetProperty("long").GetInt64().ShouldBe(9_007_199_254_740_993L);
        document.RootElement.GetProperty("double").GetDouble().ShouldBe(1.5d);
        document.RootElement.GetProperty("decimal").GetDecimal().ShouldBe(19.99m);
    }

    [Fact]
    public void WritesTemporalAndIdentityTypesInTheirStandardForms()
    {
        var problem = Sample();
        problem.Extensions["at"] = DateTimeOffset.UnixEpoch;
        problem.Extensions["id"] = Guid.Empty;

        using var document = Render(problem);

        document.RootElement.GetProperty("at").GetDateTimeOffset().ShouldBe(DateTimeOffset.UnixEpoch);
        document.RootElement.GetProperty("id").GetGuid().ShouldBe(Guid.Empty);
    }

    [Fact]
    public void WritesAnUnrecognisedTypeAsTextRatherThanDroppingIt()
    {
        var problem = Sample();
        problem.Extensions["odd"] = new Uri("https://example.test/a");

        using var document = Render(problem);

        document.RootElement.GetProperty("odd").GetString().ShouldBe("https://example.test/a",
            "An extension that vanishes in production but not in a test is a worse " +
            "outcome than one that reads oddly.");
    }

    [Fact]
    public void EscapesTextThatWouldOtherwiseBreakTheDocument()
    {
        var problem = new ProblemDetails
        {
            Status = 400,
            Detail = "He said \"no\", then\nnewline\\backslash",
        };

        // Parsing is the assertion: a botched escape produces invalid JSON.
        using var document = Render(problem);

        document.RootElement.GetProperty("detail").GetString()
            .ShouldBe("He said \"no\", then\nnewline\\backslash");
    }

    [Fact]
    public void ProducesTheRfc7807MediaType()
        => ProblemDetailsJson.ContentType.ShouldBe("application/problem+json",
            "A Problem Details body served as application/json is one clients will not " +
            "recognise as a problem.");

    [Fact]
    public void RejectsNullArguments()
    {
        Should.Throw<ArgumentNullException>(() => ProblemDetailsJson.ToUtf8(null!));

        using var buffer = new MemoryStream();
        using var writer = new Utf8JsonWriter(buffer);

        Should.Throw<ArgumentNullException>(() => ProblemDetailsJson.Write(writer, null!));
        Should.Throw<ArgumentNullException>(() => ProblemDetailsJson.Write(null!, Sample()));
    }
}
