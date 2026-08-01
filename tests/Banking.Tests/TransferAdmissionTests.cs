using FlowX;
using FlowX.Runtime;
using Shouldly;
using Xunit;

namespace Banking.Tests;

/// <summary>
/// What <c>Policies.Admission</c> does to this bank now that stage 1 executes.
/// </summary>
/// <remarks>
/// <para>
/// <strong>This file is the sample's half of the narrowing.</strong> Until stage 1 landed,
/// <c>ExecuteTransferFlow</c> carried a <c>#pragma warning disable FLOWX1032</c> on its first
/// step arguing that the flow was survivable with the rate limit unenforced. That pragma is
/// gone, and this is what replaced it: the limit refuses, the ledger is not touched, and the
/// refusal names itself.
/// </para>
/// <para>
/// <strong>The other half of that pragma is gone in a different way, and it is the more
/// interesting one.</strong> <c>Policies.Admission</c> also declared
/// <c>.Idempotency(TimeSpan.FromHours(24))</c>. Stage 3 executes too — so it would have run —
/// and it was <em>deleted</em> rather than left in, because
/// <see cref="ExecuteTransfer"/> marks two IBANs <c>[Sensitive]</c> and
/// <c>FLOWX1039</c> refuses a window whose recorded result would carry <c>[redacted]</c> where
/// an account number was. <see cref="TheIdempotencyWindowThisFlowCannotDeclare"/> is that fact
/// as an assertion rather than as a comment.
/// </para>
/// </remarks>
public sealed class TransferAdmissionTests
{
    private const string Debtor = "GB33BUKB20201555555555";
    private const string Creditor = "DE89370400440532013000";

    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    private static ExecuteTransfer Transfer(decimal amount) =>
        new(Debtor, Creditor, amount, "EUR", TransferChannel.Book);

    /// <summary>
    /// The transfer past the budget is refused, and no money moves.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>The ledger assertion is the one that matters.</strong> A limiter that refused the
    /// flow after posting the debit would satisfy every assertion about the result and none
    /// about the money — and admission control that lets the call happen has protected nothing,
    /// which is the whole of why ADR-0011 puts stage 1 in front of stage 6.
    /// </para>
    /// <para>
    /// Two permits rather than one, so this distinguishes "the limit counts" from "the limit
    /// refuses everything". A store that always said no would pass a one-permit version of this
    /// on its second transfer and would have taken the bank offline.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task ATransferPastTheDeclaredRateIsRefusedBeforeTheLedgerIsTouched()
    {
        var harness = new TransferHarness().WithPermits(2);

        (await harness.RunAsync(Transfer(120m), "a-1", Cancellation)).IsSuccess.ShouldBeTrue(
            "the first of two permits.");

        (await harness.RunAsync(Transfer(120m), "a-2", Cancellation)).IsSuccess.ShouldBeTrue(
            "the second of two permits.");

        var balanceBefore = await harness.Ledger.AvailableAsync(Debtor, Cancellation);
        var entriesBefore = harness.Ledger.Keys.Count;
        var dispatchedBefore = harness.Executed.Count;

        var refused = await harness.RunAsync(Transfer(120m), "a-3", Cancellation);

        refused.IsSuccess.ShouldBeFalse(
            "Policies.Admission declares twenty per second per principal and this harness " +
            "narrowed it to two. A transfer that settles here is a transfer the declaration " +
            "said would not, which is what the sample's pragma used to apologise for.");

        refused.Error!.Code.ShouldBe(
            FlowErrors.RateLimitedCode,
            "and the refusal names itself, so a transport can answer 429 with a Retry-After " +
            "rather than 500.");

        (await harness.Ledger.AvailableAsync(Debtor, Cancellation)).ShouldBe(
            balanceBefore,
            "No money moved. The refusal happened before transfer.validate, so nothing read " +
            "the balance and nothing posted against it — which is the property that makes a " +
            "limit in front of a ledger worth declaring.");

        harness.Ledger.Keys.Count.ShouldBe(
            entriesBefore,
            "and not one ledger entry was written on the refused run.");

        harness.Executed.Count.ShouldBe(
            dispatchedBefore,
            "and not one capability was entered on the refused run — the trace is cumulative " +
            "across the three transfers, so what is asserted is that it did not grow.");
    }

    /// <summary>The budget is the principal's, because the declaration says so.</summary>
    /// <remarks>
    /// <c>Policies.Admission</c> declares <c>RateLimitScope.Principal</c>, which
    /// <c>docs/10 §11</c> recommends over a global limit — "rate limiting only globally: one
    /// tenant starves the rest". The key the engine builds carries the scope, so two principals
    /// are two budgets and a flood from one cannot take the endpoint away from the other.
    /// </remarks>
    [Fact]
    public async Task TheBudgetIsKeyedByTheDeclaredScope()
    {
        var harness = new TransferHarness().WithPermits(1);

        await harness.RunAsync(Transfer(120m), "s-1", Cancellation);

        harness.Limiter.Keys.ShouldNotBeEmpty("the limiter was consulted.");

        harness.Limiter.Keys[0].ShouldContain(
            nameof(RateLimitScope.Principal),
            Case.Sensitive,
            "Policies.Admission declares Principal scope, and a key that ignored it would put " +
            "every caller in one bucket — a global limit wearing a per-principal declaration.");

        harness.Limiter.Keys[0].ShouldContain(
            "transfer.validate",
            Case.Sensitive,
            "and the bucket belongs to the capability the step invokes, so two flows calling it " +
            "share the bound rather than each getting their own.");
    }

    /// <summary>
    /// This flow cannot declare an idempotency window, and the reason is its own contracts.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Written as an assertion because the alternative is a comment nobody re-checks.</strong>
    /// <c>FLOWX1039</c> is a build error, so the day somebody adds
    /// <c>.Idempotency(...)</c> back to <c>Policies.Admission</c> the build stops — but the day
    /// somebody takes the <c>[Sensitive]</c> markers <em>off</em> the input contract, nothing
    /// stops, and this bank starts writing account numbers into every journal row, every emitted
    /// event and every RFC 7807 body. This test is the second half of that pair.
    /// </para>
    /// <para>
    /// It also states the consequence plainly, because the consequence is the whole argument:
    /// a replayed transfer would answer with an IBAN of <c>[redacted]</c> and a <c>200</c>.
    /// </para>
    /// </remarks>
    [Fact]
    public void TheIdempotencyWindowThisFlowCannotDeclare()
    {
        ExecuteTransferFlow.SensitiveMembers.ShouldBe(
            ["CreditorIban", "DebtorIban"],
            "These two are why Policies.Admission carries no Idempotency window. Every payload " +
            "this flow records is handed this array, JournalPayload replaces a matching member " +
            "at every depth with '[redacted]', and a stage-3 replay would hand a later step the " +
            "placeholder as if it were the account number. FLOWX1039 refuses the declaration; " +
            "this asserts the fact the rule reads.");

        typeof(ExecuteTransfer).GetProperty(nameof(ExecuteTransfer.DebtorIban))
            .ShouldNotBeNull()
            .Name.ShouldBe(
                "DebtorIban",
                "and ValidatedTransfer carries a member of the same name, which is what makes " +
                "the redaction reach a contract that never declared the attribute — the match " +
                "is by name, case-insensitively, at every depth.");
    }
}
