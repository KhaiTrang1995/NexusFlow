using System.Text.RegularExpressions;
using Shouldly;
using Xunit;

namespace FlowX.Architecture.Tests;

/// <summary>
/// The workflow files are the only gates that cannot report their own failure. A workflow
/// GitHub cannot parse is a <em>startup failure</em>: no job runs, no step logs, and the run
/// carries the file's path where a name would be. Nothing distinguishes that from a gate
/// that ran and passed except opening the run and noticing it has no jobs — which is how
/// <c>quality.yml</c> went a whole set of pushes without evaluating coverage, the mutation
/// score or the debt policy, while the branch read as merely "one job red".
/// </summary>
public sealed class WorkflowFitnessTests
{
    // `key: |`, `key: >`, and the chomping and indentation indicators YAML allows after them.
    private static readonly Regex BlockScalar = new(
        @"^(?<indent>\s*)(?<key>[\w.-]+):\s*[|>][+-]?\d?\s*$",
        RegexOptions.Compiled);

    /// <summary>
    /// A block scalar's body must be indented further than the key that opens it.
    /// </summary>
    /// <remarks>
    /// This is the whole failure, and it is invisible at a glance: a comment pasted at the
    /// key's own indentation ends the block rather than joining it, so the scalar is empty
    /// and the first real line is read as a sibling key. YAML then fails on a mapping it was
    /// never given. Asserted structurally rather than by parsing, because a parser is a
    /// package this project does not carry and this is the shape that has actually broken.
    /// </remarks>
    [Fact]
    public void AWorkflowsBlockScalarsAreIndentedPastTheirKey()
    {
        var offences = Workflows()
            .SelectMany(static file => Offences(file.Name, File.ReadAllLines(file.FullName)))
            .ToList();

        offences.ShouldBeEmpty(
            "a block scalar whose body is not indented past its key is empty, and the line "
            + "after it ends the mapping:\n" + string.Join("\n", offences));
    }

    /// <summary>There is at least one workflow, so the rule above cannot pass vacuously.</summary>
    [Fact]
    public void TheWorkflowSurveyCanStillSeeItsSubject()
    {
        var workflows = Workflows();

        workflows.Count.ShouldBeGreaterThan(3);
        workflows.SelectMany(static f => Bodies(File.ReadAllLines(f.FullName)))
            .Count().ShouldBeGreaterThan(20);
    }

    private static List<FileInfo> Workflows() =>
        RepositoryLayout.Root
            .GetDirectories(".github").SelectMany(static d => d.GetDirectories("workflows"))
            .SelectMany(static d => d.GetFiles("*.yml"))
            .OrderBy(static f => f.Name, StringComparer.Ordinal)
            .ToList();

    private static IEnumerable<string> Offences(string file, string[] lines)
    {
        foreach (var (line, body) in Bodies(lines))
        {
            var opened = BlockScalar.Match(lines[line]);
            var keyIndent = opened.Groups["indent"].Value.Length;

            if (body < 0)
            {
                yield return $"{file}:{line + 1} `{opened.Groups["key"].Value}:` opens a block "
                    + "scalar with nothing after it";
                continue;
            }

            var bodyIndent = lines[body].Length - lines[body].TrimStart().Length;

            if (bodyIndent <= keyIndent)
            {
                yield return $"{file}:{line + 1} `{opened.Groups["key"].Value}:` is indented "
                    + $"{keyIndent} and its first body line ({body + 1}) only {bodyIndent}";
            }
        }
    }

    /// <summary>Each block scalar, as the line that opens it and the first line of its body.</summary>
    private static IEnumerable<(int Line, int Body)> Bodies(string[] lines)
    {
        for (var i = 0; i < lines.Length; i++)
        {
            if (!BlockScalar.IsMatch(lines[i]))
            {
                continue;
            }

            var body = -1;

            for (var j = i + 1; j < lines.Length; j++)
            {
                if (lines[j].Trim().Length == 0)
                {
                    continue;
                }

                body = j;
                break;
            }

            yield return (i, body);
        }
    }
}
