using System.Text.Json.Serialization;
using Shouldly;
using Xunit;

namespace FlowX.Abstractions.Tests;

/// <summary>
/// The redaction rule itself, attacked directly.
/// </summary>
/// <remarks>
/// <para>
/// <c>JournalConformance.ASensitiveMemberIsNotWrittenToTheJournal</c> asserts the property a
/// store must uphold, and it is a weak test by construction: <see cref="JournalPayload"/>
/// exposes no accessor for the value it carries, so no store has a route to the object graph
/// and the leak it looks for is structurally unreachable. That is the design working, and it
/// is also exactly the shape of a test that cannot fail.
/// </para>
/// <para>
/// These can fail. They are aimed at the rule rather than at a store: the wrong member
/// redacted, the right member missed because the wire form is camelCase, a nested object
/// treated as opaque, an array walked past. Each of those is a real leak, and each of them
/// turns this file red.
/// </para>
/// </remarks>
public sealed class JournalPayloadTests
{
    private const string Secret = "tok_live_4242424242424242";

    /// <summary>A payload with no declared sensitive members is stored verbatim.</summary>
    /// <remarks>
    /// The journal is the incident view. Over-redaction is not a safe default — it makes the
    /// row useless without making anything safer, because a member nobody marked was never
    /// the thing being protected.
    /// </remarks>
    [Fact]
    public void APayloadWithNothingDeclaredIsWrittenAsItIs()
    {
        var json = JournalPayload
            .Of(new Order("order-1", Secret, new Customer("Ada", Secret)), PayloadJson.Default.Order)
            .ToJson();

        json.ShouldNotBeNull();
        ShouldCarry(json, Secret, "nothing was declared sensitive, so nothing is withheld.");
    }

    /// <summary>A declared member is replaced, and only that member.</summary>
    [Fact]
    public void ADeclaredMemberIsReplacedAndNothingElseIs()
    {
        var json = JournalPayload
            .Of(
                new Order("order-1", Secret, new Customer("Ada", "unused")),
                PayloadJson.Default.Order,
                ["PaymentToken"])
            .ToJson();

        json.ShouldNotBeNull();
        ShouldNotCarry(json, Secret, "the declared member never reaches the store.");
        ShouldCarry(json, JournalPayload.Redacted, "and the reader can see it was withheld.");
        ShouldCarry(json, "order-1", "an undeclared member is untouched.");
        ShouldCarry(json, "Ada", "so is one nested inside another object.");
    }

    /// <summary>
    /// Matching is case-insensitive, because the wire form and the declaration disagree.
    /// </summary>
    /// <remarks>
    /// The contract member is <c>PaymentToken</c> and the serialised property may be
    /// <c>paymentToken</c>. An exact match would let through precisely the realistic spelling,
    /// which is the failure mode the RFC 7807 sink already had to answer the same way.
    /// </remarks>
    [Fact]
    public void MatchingIsCaseInsensitive()
    {
        var json = JournalPayload
            .Of(
                new Order("order-1", Secret, new Customer("Ada", "unused")),
                PayloadJson.Default.Order,
                ["paymenttoken"])
            .ToJson();

        json.ShouldNotBeNull();
        ShouldNotCarry(json, Secret, "the declaration and the wire form differ only in case.");
    }

    /// <summary>A marked member is redacted at every depth, not only at the top level.</summary>
    /// <remarks>
    /// A contract that nests a type carrying a marked member is not less sensitive for being
    /// one level down. A top-level-only rule would redact the order's token and store the
    /// customer's.
    /// </remarks>
    [Fact]
    public void ANestedMemberIsRedactedToo()
    {
        var json = JournalPayload
            .Of(
                new Order("order-1", "unused", new Customer("Ada", Secret)),
                PayloadJson.Default.Order,
                ["PaymentToken"])
            .ToJson();

        json.ShouldNotBeNull();
        ShouldNotCarry(json, Secret, "depth is not a hiding place.");
        ShouldCarry(json, "Ada", "the rest of the nested object survives.");
    }

    /// <summary>Arrays are walked, so an element's marked member is redacted.</summary>
    [Fact]
    public void AMemberInsideAnArrayIsRedactedToo()
    {
        var json = JournalPayload
            .Of(
                new Basket([new Customer("Ada", Secret), new Customer("Grace", "unused")]),
                PayloadJson.Default.Basket,
                ["PaymentToken"])
            .ToJson();

        json.ShouldNotBeNull();
        ShouldNotCarry(json, Secret, "a collection is not a hiding place either.");
        ShouldCarry(json, "Grace", "the other elements are intact.");
    }

    /// <summary>Redaction keeps the document's shape, so it stays deserialisable.</summary>
    /// <remarks>
    /// The property is replaced rather than removed. Old rows must remain readable by the
    /// same generated context for the whole retention window, and a missing required property
    /// would break that at exactly the moment someone is reading a year-old row to work out
    /// what happened.
    /// </remarks>
    [Fact]
    public void RedactionKeepsThePropertyRatherThanRemovingIt()
    {
        var json = JournalPayload
            .Of(
                new Order("order-1", Secret, new Customer("Ada", "unused")),
                PayloadJson.Default.Order,
                ["PaymentToken"])
            .ToJson();

        var reread = System.Text.Json.JsonSerializer.Deserialize(json!, PayloadJson.Default.Order);

        reread.ShouldNotBeNull("the stored row deserialises with the same generated context.");
        reread.PaymentToken.ShouldBe(
            JournalPayload.Redacted,
            "the field was considered and withheld, which is different from never received.");
    }

    /// <summary>An empty payload writes nothing at all.</summary>
    [Fact]
    public void AnEmptyPayloadWritesNoJson()
    {
        JournalPayload.Empty.IsEmpty.ShouldBeTrue();
        JournalPayload.Empty.ToJson().ShouldBeNull("the column is null, not the string \"null\".");
    }

    /// <summary>A payload cannot be built without the generated metadata for its type.</summary>
    /// <remarks>
    /// Commitment 5 of ADR-0015 made structural: there is no overload that reflects over a
    /// type, so a contract outside the generated context cannot reach the journal, and the
    /// write path stays NativeAOT- and trim-safe.
    /// </remarks>
    [Fact]
    public void APayloadRequiresGeneratedTypeMetadata()
    {
        Should.Throw<ArgumentNullException>(
            () => JournalPayload.Of<Order>(null!, null!));
    }

    private static void ShouldCarry(string? json, string expected, string because) =>
        (json ?? string.Empty).Contains(expected, StringComparison.Ordinal).ShouldBeTrue(
            $"{because} The payload wrote: {json ?? "(null)"}");

    private static void ShouldNotCarry(string? json, string expected, string because) =>
        (json ?? string.Empty).Contains(expected, StringComparison.Ordinal).ShouldBeFalse(
            $"{because} The payload wrote: {json ?? "(null)"}");
}

/// <summary>A contract carrying a marked member at the top level and one nested inside it.</summary>
internal sealed record Order(string OrderId, string PaymentToken, Customer Customer);

/// <summary>A nested contract carrying the same marked member one level down.</summary>
internal sealed record Customer(string Name, string PaymentToken);

/// <summary>A contract carrying marked members inside a collection.</summary>
internal sealed record Basket(IReadOnlyList<Customer> Customers);

/// <summary>The generated context these payloads are written through (ADR-0008).</summary>
[JsonSerializable(typeof(Order))]
[JsonSerializable(typeof(Basket))]
internal sealed partial class PayloadJson : JsonSerializerContext;
