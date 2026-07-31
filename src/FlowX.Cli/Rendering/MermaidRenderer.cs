using System.Globalization;
using System.Text;
using FlowX.Cli.Manifest;

namespace FlowX.Cli.Rendering;

/// <summary>Renders a manifest as a Mermaid flowchart.</summary>
/// <remarks>
/// <para>
/// Mermaid rather than an image format, because a diagram that lives in a pull request
/// diff is a diagram people actually look at. The architecture stops being a drawing
/// somebody updated once and becomes something a reviewer sees change.
/// </para>
/// <para>
/// Pure: a document in, a string out. The interesting properties — that every edge
/// references a node that exists, that identical input renders identically — are
/// assertable without a renderer, which is what makes them worth asserting.
/// </para>
/// </remarks>
public static class MermaidRenderer
{
    /// <summary>Renders every flow, or one flow when <paramref name="flowId"/> is given.</summary>
    /// <param name="manifest">The parsed manifest.</param>
    /// <param name="flowId">Optional flow to isolate.</param>
    public static string Render(ManifestDocument manifest, string? flowId = null)
    {
        ArgumentNullException.ThrowIfNull(manifest);

        var flows = manifest.Flows
            .Where(f => flowId is null || string.Equals(f.Id, flowId, StringComparison.Ordinal))
            .OrderBy(f => f.Id, StringComparer.Ordinal)
            .ToList();

        var builder = new StringBuilder();
        builder.Append("flowchart TD\n");

        if (flows.Count == 0)
        {
            // A valid diagram saying nothing is found, rather than an empty file that
            // looks like the tool crashed.
            builder.Append("    empty[\"No flows found\"]\n");
            return builder.ToString();
        }

        for (var i = 0; i < flows.Count; i++)
        {
            RenderFlow(builder, flows[i], i, manifest);
        }

        return builder.ToString();
    }

    private static void RenderFlow(StringBuilder builder, ManifestFlow flow, int flowIndex, ManifestDocument manifest)
    {
        var prefix = "f" + flowIndex.ToString(CultureInfo.InvariantCulture);
        var header = $"{flow.Id}@{flow.Version} ({flow.Profile})";

        builder.Append("    subgraph ").Append(prefix).Append('[').Append(Quote(header)).Append("]\n");
        builder.Append("    direction TB\n");

        var steps = flow.Steps.OrderBy(s => s.Id).ToList();

        foreach (var step in Flatten(steps))
        {
            builder.Append("        ").Append(NodeId(prefix, step.Id))
                .Append(Shape(step, Label(step, manifest))).Append('\n');
        }

        Connect(builder, prefix, steps);

        // Compensations are drawn as dashed edges running backwards, because that is
        // what they do: the failure path unwinds in reverse. Drawing them forwards
        // would be tidier and would misrepresent the one thing a saga must get right.
        foreach (var step in Flatten(steps).Where(s => !string.IsNullOrEmpty(s.Compensation)))
        {
            var node = NodeId(prefix, step.Id);
            var compensation = node + "c";

            builder.Append("        ").Append(compensation)
                .Append("[/").Append(Quote("undo " + Strip(step.Compensation!))).Append("/]\n");

            builder.Append("        ").Append(node).Append(" -.-> ").Append(compensation).Append('\n');
        }

        builder.Append("    end\n");
    }

    /// <summary>Every step of a block, steps nested in a conditional included, in id order.</summary>
    private static IEnumerable<ManifestStep> Flatten(IEnumerable<ManifestStep> steps) => steps
        .SelectMany(step => step.Branches.SelectMany(Flatten).Prepend(step))
        .OrderBy(step => step.Id);

    /// <summary>
    /// Draws the edges of one block: each step to the next, and each conditional out to
    /// its branches and back.
    /// </summary>
    /// <remarks>
    /// A conditional has more than one exit — the tail of each branch, plus the
    /// conditional itself when a branch is missing or empty, because the false path then
    /// skips straight to whatever follows. Drawing only one of them would produce a
    /// diagram in which a branch runs off the end, which is exactly the shape a reviewer
    /// is looking at the picture to check.
    /// </remarks>
    private static void Connect(StringBuilder builder, string prefix, List<ManifestStep> steps)
    {
        for (var i = 0; i < steps.Count; i++)
        {
            var step = steps[i];

            for (var branch = 0; branch < step.Branches.Count; branch++)
            {
                var block = step.Branches[branch];

                if (block.Count == 0)
                {
                    continue;
                }

                Edge(builder, NodeId(prefix, step.Id), NodeId(prefix, block[0].Id), BranchLabel(step, branch));
                Connect(builder, prefix, block);
            }

            if (i + 1 >= steps.Count)
            {
                continue;
            }

            var next = NodeId(prefix, steps[i + 1].Id);

            foreach (var exit in Exits(step))
            {
                Edge(builder, NodeId(prefix, exit), next, label: null);
            }
        }
    }

    /// <summary>
    /// The label on the edge into one block, which says <em>which arm</em> and never on
    /// what.
    /// </summary>
    /// <remarks>
    /// Position is all the manifest gives: for a conditional the first block is the
    /// <c>then</c> and the second the <c>Otherwise</c>; for a switch the blocks are the
    /// cases in declaration order and then the <c>Default</c>. The case values are
    /// deliberately not published — they are business data — so the diagram numbers the
    /// arms rather than inventing names for them.
    /// </remarks>
    private static string BranchLabel(ManifestStep step, int branch)
    {
        // A fork's blocks all run, so numbering them is the only true thing to say. "yes"
        // and "no" would read as a choice the flow does not make.
        if (step.Kind == "Parallel")
        {
            return "branch " + branch.ToString(CultureInfo.InvariantCulture);
        }

        // A loop has one block and runs it once per element. "each" is the true thing to
        // say about the edge; how many times is data, and the manifest does not carry it.
        if (step.Kind == "ForEach")
        {
            return "each";
        }

        if (step.Kind != "Switch")
        {
            return branch == 0 ? "yes" : "no";
        }

        return branch == step.Branches.Count - 1
            ? "default"
            : "case " + branch.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>The step ids control can leave <paramref name="step"/> from.</summary>
    /// <remarks>
    /// A branching step has more than one exit — the tail of each populated block, plus
    /// the step itself when some path bypasses it. For a conditional that path is a
    /// missing <c>Otherwise</c>, which the manifest expresses by having only one block;
    /// for a switch it is an empty case or an absent <c>Default</c>, which it expresses by
    /// an empty block. Both reduce to the same question: is there a block that declares
    /// nothing, or fewer than two blocks at all.
    /// </remarks>
    private static IEnumerable<int> Exits(ManifestStep step)
    {
        if (step.Branches.Count == 0)
        {
            yield return step.Id;
            yield break;
        }

        foreach (var block in step.Branches.Where(b => b.Count > 0))
        {
            foreach (var exit in Exits(block[block.Count - 1]))
            {
                yield return exit;
            }
        }

        if (step.Branches.Count < 2 || step.Branches.Any(b => b.Count == 0))
        {
            yield return step.Id;
        }
    }

    private static void Edge(StringBuilder builder, string from, string to, string? label)
    {
        builder.Append("        ").Append(from).Append(" -->");

        if (label is not null)
        {
            builder.Append('|').Append(label).Append('|');
        }

        builder.Append(' ').Append(to).Append('\n');
    }

    private static string Label(ManifestStep step, ManifestDocument manifest)
    {
        if (!string.IsNullOrEmpty(step.Event))
        {
            return "emit " + step.Event;
        }

        if (step.Kind == "Parallel")
        {
            // The merge rule is the one thing about a fork worth reading off a diagram:
            // it says what one branch failing means for the rest. It is structure, so
            // unlike a predicate or a case value the manifest does carry it.
            return string.IsNullOrEmpty(step.Merge) ? "parallel" : "parallel · " + step.Merge;
        }

        if (step.Kind == "ForEach")
        {
            // Not the collection, for the same reason a condition is not its predicate:
            // the manifest has no field for it, and it would be business data if it did.
            // Not the concurrency bound either — the manifest does not carry it.
            return "for each";
        }

        if (step.Kind is "Condition" or "Switch")
        {
            // Not the predicate, and not the selector or the case values: the manifest's
            // step object has no field for any of them, so the diagram can show that the
            // flow branches and where each branch goes, but not on what. Inventing a
            // label would be worse than an honest one.
            return step.Kind == "Switch" ? "switch" : "condition";
        }

        if (string.IsNullOrEmpty(step.Capability))
        {
            return step.Kind ?? "step";
        }

        var capability = manifest.Capabilities
            .FirstOrDefault(c => $"{c.Id}@{c.Version}" == step.Capability);

        var label = Strip(step.Capability);

        // Marks worth seeing at a glance: a step that changes something outside the
        // process, and one that is not safe to retry.
        if (capability is { SideEffects.Count: > 0 })
        {
            label += " ⚡";
        }

        if (capability is { Idempotent: false })
        {
            label += " ⚠";
        }

        return label;
    }

    private static string Shape(ManifestStep step, string label) => step.Kind switch
    {
        "Emit" => $"([{Quote(label)}])",
        "AwaitSignal" => $">{Quote(label)}]",
        "Condition" => $"{{{Quote(label)}}}",
        "Switch" => $"{{{{{Quote(label)}}}}}",
        // A stadium with a doubled border: a fork is not a decision, so it must not wear a
        // decision's diamond.
        "Parallel" => $"[/{Quote(label)}/]",
        // A subroutine box, which is Mermaid's shape for "this runs a block". A loop is
        // not a decision either, and it is not a fork: exactly one block, run repeatedly.
        "ForEach" => $"[[{Quote(label)}]]",
        _ => $"[{Quote(label)}]",
    };

    private static string NodeId(string prefix, int stepId) =>
        prefix + "s" + stepId.ToString(CultureInfo.InvariantCulture);

    private static string Strip(string qualified)
    {
        var at = qualified.IndexOf('@', StringComparison.Ordinal);
        return at < 0 ? qualified : qualified[..at];
    }

    /// <summary>
    /// Quotes a Mermaid label.
    /// </summary>
    /// <remarks>
    /// Mermaid has no escape sequence for a double quote inside a quoted string, so one
    /// is replaced with a typographic quote rather than escaped. The alternative is a
    /// diagram that fails to render, which is strictly worse than one whose label reads
    /// slightly differently.
    /// </remarks>
    private static string Quote(string label) =>
        "\"" + label.Replace("\"", "”", StringComparison.Ordinal) + "\"";
}
