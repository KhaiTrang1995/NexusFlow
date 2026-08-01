using System.Text.Json;
using Shouldly;
using Xunit;

namespace FlowX.Abstractions.Tests;

/// <summary>
/// The two clauses of <a href="../../docs/adr/ADR-0008-serialization-and-schema.md">ADR-0008</a>'s
/// Decision that were in no plan until WP-59: the <c>schemaVersion</c> stamp every persisted
/// payload carries, and the <c>IPayloadSerializer</c> seam a binary plugin needs to be
/// expressible at all.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Both are tested here rather than against a store</strong>, because both are
/// properties of the payload and not of the table it lands in. A store receives a
/// <see cref="JournalPayload"/> and calls one method on it; if the stamp were a store's job,
/// there would be as many stamps as stores and one of them would be missing.
/// </para>
/// <para>
/// The third subject of this file is the composed state-bag payload. It is here for the
/// reason the redaction tests are next door: it is the shape that opens a
/// <em>second</em> way to build a payload, and the whole point of building it inside
/// <see cref="JournalPayload"/> is that it inherits the one redaction pass instead of
/// standing beside it.
/// </para>
/// </remarks>
public sealed class PayloadContractTests
{
    private const string Secret = "tok_live_4242424242424242";

    // ---------------------------------------------------------------------------------
    // The stamp
    // ---------------------------------------------------------------------------------

    /// <summary>Every persisted payload carries <c>schemaVersion</c>, and it is readable.</summary>
    /// <remarks>
    /// ADR-0008's Decision says "every persisted payload carries <c>schemaVersion</c>" and
    /// nothing did. Read back off the document rather than off a property, because a stamp a
    /// reader cannot see in the stored row is not a stamp.
    /// </remarks>
    [Fact]
    public void APersistedPayloadCarriesTheSchemaVersionStamp()
    {
        var json = JournalPayload
            .Of(new Order("order-1", Secret, new Customer("Ada", Secret)), PayloadJson.Default.Order)
            .ToJson();

        using var document = JsonDocument.Parse(json.ShouldNotBeNull());

        document.RootElement.GetProperty(JournalPayload.SchemaVersionMember).GetString()
            .ShouldBe(JournalPayload.SchemaVersion,
                "a row stored today has to say which envelope rules wrote it, or a later " +
                "release reading it is guessing.");
    }

    /// <summary>The stamp does not cost the payload its own members.</summary>
    [Fact]
    public void TheStampIsAddedBesideTheContractRatherThanInsteadOfIt()
    {
        var json = JournalPayload
            .Of(new Order("order-1", Secret, new Customer("Ada", Secret)), PayloadJson.Default.Order)
            .ToJson();

        using var document = JsonDocument.Parse(json.ShouldNotBeNull());

        document.RootElement.GetProperty("OrderId").GetString().ShouldBe("order-1");
        document.RootElement.GetProperty("Customer").GetProperty("Name").GetString().ShouldBe("Ada");
    }

    /// <summary>An empty payload is still no payload, stamp or no stamp.</summary>
    /// <remarks>
    /// The column is null when there is nothing to record. Stamping a row that holds nothing
    /// would make "there was no payload" indistinguishable from "there was one and it was
    /// empty", which is the distinction a replay needs.
    /// </remarks>
    [Fact]
    public void NothingIsStampedOntoAnAbsentPayload() =>
        JournalPayload.Empty.ToJson().ShouldBeNull();

    // ---------------------------------------------------------------------------------
    // The seam
    // ---------------------------------------------------------------------------------

    /// <summary>
    /// A serialiser other than JSON can be handed the payload, and sees it already redacted.
    /// </summary>
    /// <remarks>
    /// This is the whole of the <c>IPayloadSerializer</c> guarantee. ADR-0008 allows a binary
    /// serialiser as a plugin; the plugin must not become a second exit for a
    /// <c>[Sensitive]</c> value, so it is handed the redacted document and never the object
    /// graph. The assertion is that a hostile implementation still cannot see the secret.
    /// </remarks>
    [Fact]
    public void APluggedSerialiserSeesTheRedactedDocumentAndNeverTheValue()
    {
        var capture = new CapturingSerializer();

        JournalPayload
            .Of(
                new Order("order-1", Secret, new Customer("Ada", Secret)),
                PayloadJson.Default.Order,
                ["PaymentToken"])
            .Serialize(capture);

        capture.Seen.ShouldNotBeNull();
        capture.Seen.ShouldNotContain(Secret, Case.Sensitive,
            "a plugin that could read the value would be the leak the payload type exists " +
            "to make unreachable.");
        capture.Seen.ShouldContain(JournalPayload.Redacted, Case.Sensitive);
        capture.Seen.ShouldContain(JournalPayload.SchemaVersionMember, Case.Sensitive,
            "and the stamp is part of the document every serialiser writes, not of the JSON one.");
    }

    /// <summary><c>ToJson</c> is the JSON serialiser, not a second implementation.</summary>
    [Fact]
    public void ToJsonIsWhatTheDefaultSerialiserWrites()
    {
        var payload = JournalPayload.Of(
            new Order("order-1", Secret, new Customer("Ada", Secret)),
            PayloadJson.Default.Order,
            ["PaymentToken"]);

        payload.Serialize(JsonPayloadSerializer.Default).ShouldBe(payload.ToJson());
    }

    // ---------------------------------------------------------------------------------
    // The composed state bag
    // ---------------------------------------------------------------------------------

    /// <summary>A state bag is one document with one member per contract in it.</summary>
    /// <remarks>
    /// The engine's bag is a <c>Dictionary&lt;Type, object&gt;</c> and no single
    /// <c>JsonTypeInfo</c> describes it, so the snapshot is composed from the members'
    /// own metadata — which is still generated metadata, so the write path stays
    /// reflection-free (constraint C2).
    /// </remarks>
    [Fact]
    public void AStateBagIsComposedFromTheMembersOwnGeneratedMetadata()
    {
        var json = JournalPayload
            .OfState(
                [
                    JournalMember.Of("Order", new Order("order-1", Secret, new Customer("Ada", Secret)), PayloadJson.Default.Order),
                ])
            .ToJson();

        using var document = JsonDocument.Parse(json.ShouldNotBeNull());

        document.RootElement.GetProperty(JournalPayload.SchemaVersionMember).GetString()
            .ShouldBe(JournalPayload.SchemaVersion);
        document.RootElement.GetProperty("Order").GetProperty("OrderId").GetString().ShouldBe("order-1");
    }

    /// <summary>
    /// The composed shape inherits the redaction pass rather than re-implementing it.
    /// </summary>
    /// <remarks>
    /// <strong>This is the assertion the package turns on.</strong> A generated payload
    /// writer is a second exit from the journal, and the risk it carries is that the state
    /// bag gets its own serialisation and quietly loses the redaction the single exit had.
    /// The bag is composed inside <see cref="JournalPayload"/>, so it leaves through
    /// <c>ToJson</c> like everything else and is matched by the same rule at every depth.
    /// </remarks>
    [Fact]
    public void AComposedStateBagIsRedactedByTheSameRuleAtEveryDepth()
    {
        var json = JournalPayload
            .OfState(
                [
                    JournalMember.Of("Order", new Order("order-1", Secret, new Customer("Ada", Secret)), PayloadJson.Default.Order),
                ],
                ["PaymentToken"])
            .ToJson();

        (json ?? string.Empty).ShouldNotContain(Secret, Case.Sensitive,
            "the bag is a journal row like any other and is retained for as long.");

        using var document = JsonDocument.Parse(json.ShouldNotBeNull());
        var order = document.RootElement.GetProperty("Order");

        order.GetProperty("PaymentToken").GetString().ShouldBe(JournalPayload.Redacted);
        order.GetProperty("Customer").GetProperty("PaymentToken").GetString().ShouldBe(JournalPayload.Redacted);
        order.GetProperty("Customer").GetProperty("Name").GetString().ShouldBe("Ada");
    }

    /// <summary>A bag with nothing in it is no payload at all.</summary>
    /// <remarks>
    /// A flow whose first step has not run has an empty bag, and writing <c>{}</c> for it
    /// would make a resumed instance rehydrate from a snapshot that says nothing — which is
    /// different from having no snapshot, and the engine treats the two differently.
    /// </remarks>
    [Fact]
    public void AnEmptyStateBagIsEmptyRatherThanAnEmptyObject()
    {
        JournalPayload.OfState([]).IsEmpty.ShouldBeTrue();
        JournalPayload.OfState([]).ToJson().ShouldBeNull();
    }

    /// <summary>A member cannot be built without generated metadata for its contract.</summary>
    /// <remarks>
    /// The same requirement <c>JournalPayload.Of</c> makes, restated on the composed shape so
    /// that the second way into the journal is not a weaker one. This is what
    /// <c>FLOWX1006</c> checks at build time.
    /// </remarks>
    [Fact]
    public void AMemberWithoutGeneratedMetadataIsRefused() =>
        Should.Throw<ArgumentNullException>(
            () => JournalMember.Of(
                "Order",
                new Order("order-1", Secret, new Customer("Ada", Secret)),
                (System.Text.Json.Serialization.Metadata.JsonTypeInfo<Order>)null!));

    /// <summary>A serialiser that captures what it was handed. The hostile plugin.</summary>
    private sealed class CapturingSerializer : IPayloadSerializer
    {
        public string? Seen { get; private set; }

        public string ContentType => "application/x-test";

        public string Serialize(in JsonElement document)
        {
            Seen = document.GetRawText();

            return Seen;
        }
    }
}
