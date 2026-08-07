using FlowX;

namespace Crm;

// -------------------------------------------------------------------------------- what is asked

/// <summary>Reads the process that drives one entity kind.</summary>
/// <param name="AppliesTo">Which kind.</param>
/// <remarks>
/// <para>
/// <strong>The claim this sample exists to prove, read back.</strong> The transitions, guards and
/// actions of a sales process live in tables and an administrator rewrites them at run time —
/// and until now nothing could show them. A setup screen listing stages it had been compiled
/// with is the exact contradiction of the claim, on the screen where somebody would go to check
/// it.
/// </para>
/// <para>
/// The active version, and only that one. A superseded definition is history: an opportunity
/// already carries the stage it entered, and a screen offering last quarter's stages beside this
/// quarter's would let somebody move a deal into a stage nothing can leave.
/// </para>
/// </remarks>
public sealed record ReadProcess(EntityKind AppliesTo);

// -------------------------------------------------------------------------------- what comes back

/// <summary>The active process for one entity kind.</summary>
/// <param name="AppliesTo">Which kind it drives.</param>
/// <param name="Version">Which version is active.</param>
/// <param name="Stages">Its stages, in the order they are entered.</param>
/// <param name="Transitions">What may follow what, with what has to hold and what it does.</param>
public sealed record ProcessView(
    string AppliesTo,
    int Version,
    IReadOnlyList<ProcessStageView> Stages,
    IReadOnlyList<ProcessTransitionView> Transitions);

/// <summary>One stage.</summary>
/// <param name="Name">What it is called, which is what an opportunity carries.</param>
/// <param name="Ordinal">Where it sits.</param>
/// <param name="IsTerminal">Whether anything follows it.</param>
/// <param name="Occupants">
/// How many opportunities are sitting in it. <strong>The number that makes the screen worth
/// opening</strong>: a stage nothing has ever entered is either new or a mistake, and a stage
/// holding half the pipeline is where deals go to be forgotten.
/// </param>
public sealed record ProcessStageView(string Name, int Ordinal, bool IsTerminal, int Occupants);

/// <summary>One move between two stages.</summary>
/// <param name="From">The stage left.</param>
/// <param name="To">The stage entered.</param>
/// <param name="Trigger">What causes it.</param>
/// <param name="Guards">
/// What has to hold, all of it. Five operators and no expression language — which is what makes
/// a guard something a screen can render and an administrator can reason about.
/// </param>
/// <param name="Actions">What happens when it is taken, in order.</param>
public sealed record ProcessTransitionView(
    string From,
    string To,
    string Trigger,
    IReadOnlyList<ProcessGuardView> Guards,
    IReadOnlyList<string> Actions);

/// <summary>One condition on a transition.</summary>
/// <param name="Field">What is looked at.</param>
/// <param name="Operator">How it is compared.</param>
/// <param name="Value">What it is compared against.</param>
public sealed record ProcessGuardView(string Field, string Operator, string Value);

// -------------------------------------------------------------------------------- what can go wrong

/// <summary>Refusals the process read can produce.</summary>
public static class ProcessViewErrors
{
    /// <summary>This tenant has published no process for that kind.</summary>
    /// <param name="kind">Which kind was asked for.</param>
    /// <returns>The refusal.</returns>
    /// <remarks>
    /// A distinct answer from "a process with no stages". A tenant that has never published one
    /// has a decision to make; a tenant whose process is empty has a bug to find.
    /// </remarks>
    public static Error NoActiveProcess(EntityKind kind) =>
        new(
            "crm.process_not_published",
            $"No process is active for {kind}. Publish one before anything can move.",
            ErrorCategory.NotFound);
}
