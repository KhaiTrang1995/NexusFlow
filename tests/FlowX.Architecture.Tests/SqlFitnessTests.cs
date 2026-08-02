using Microsoft.CodeAnalysis.CSharp.Syntax;
using Shouldly;
using Xunit;

namespace FlowX.Architecture.Tests;

/// <summary>
/// OWASP A03: no statement this platform issues is assembled from a run-time value.
/// </summary>
/// <remarks>
/// The gate that took over from semgrep's <c>csharp-sqli</c> when that rule was excluded by id
/// in <c>.github/workflows/security.yml</c>. Why it is here rather than as a second copy of the
/// rule in shell is the reasoning above the <c>Technical-debt policy</c> job in
/// <c>.github/workflows/quality.yml</c>: two gates for one rule, disagreeing, is worse than
/// one, and a developer meets this one on <c>dotnet test</c> rather than on a red PR.
/// </remarks>
public sealed class SqlFitnessTests
{
    /// <summary>
    /// Files that must appear in the survey for it to be seeing the adapters at all.
    /// </summary>
    /// <remarks>
    /// One per shape the survey has to handle, so a regression in any of them shows up as an
    /// empty scan rather than as a pass: an assignment from a class-scoped <c>const</c>, an
    /// assignment from a <c>const</c> on another type, a nested conditional, a value arriving
    /// through a parameter, a constructor argument, an embedded script, and the CLI — which is
    /// outside <c>plugins/</c> and was equally covered by the rule that was excluded.
    /// </remarks>
    private static readonly string[] MustBeSeen =
    [
        "plugins/FlowX.Postgres/PostgresLeaseStore.cs",
        "plugins/FlowX.Postgres/PostgresFlowJournal.cs",
        "plugins/FlowX.Postgres/PostgresRecoveryIndex.cs",
        "plugins/FlowX.Postgres/PostgresPolicyStores.cs",
        "plugins/FlowX.Postgres/PostgresStreamCheckpointStore.cs",
        "plugins/FlowX.Postgres/Migrations/PostgresMigrator.cs",
        "src/FlowX.Cli/Replay/JournalReader.cs",
    ];

    /// <summary>
    /// Every statement issued by a shipping assembly is fixed when that assembly is built.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The property, not the syntax: a literal, a <c>const</c>, an embedded script, or a
    /// private helper's parameter that every call site fills with one of those. Anything
    /// carrying a run-time value — an interpolation, a <c>+</c>, a <c>string.Format</c>, a
    /// local built from any of them — fails, and it fails whether or not a parameter would have
    /// made it safe. Parameterisation is the fix, but "the value is bound, honestly" is a claim
    /// no scan can check, and a gate resting on it is a gate that passes on the day someone is
    /// wrong.
    /// </para>
    /// <para>
    /// The schema is the one thing that reaches Postgres as an identifier rather than as a
    /// parameter, and it never reaches it as text: it is set on the connection's
    /// <c>search_path</c>, or handed to <c>format('%I', …)</c> as a parameter, having passed
    /// <c>Identifiers.RequireSchemaName</c> first. That is why this gate can be this strict
    /// without anything needing an exemption from it.
    /// </para>
    /// </remarks>
    [Fact]
    public void EveryStatementIsFixedAtBuildTime()
    {
        var assembled = SqlSurvey.Sinks
            .Select(static sink => (sink, why: SqlSurvey.WhyNotFixed(sink.Text, sink.Owner)))
            .Where(static found => found.why is not null)
            .Select(static found => $"{found.sink.Where}: {found.why}")
            .ToArray();

        assembled.ShouldBeEmpty(
            "SQL assembled from a run-time value (OWASP A03). Put the statement in a `const` " +
            "and bind the value as a parameter:" +
            Environment.NewLine + string.Join(Environment.NewLine, assembled));
    }

    /// <summary>
    /// The survey still finds the statements it is written against.
    /// </summary>
    /// <remarks>
    /// Without this, the gate above degrades into a pass the moment the scan stops matching —
    /// a moved adapter, a renamed sink, a parse that returns nothing — because an empty set
    /// satisfies "every statement is fixed at build time" perfectly. This repository has
    /// shipped one test that could not fail; a security gate resting on a source scan is
    /// exactly where the next one comes from.
    /// </remarks>
    [Fact]
    public void TheSqlSurveyStillSeesTheAdapters()
    {
        const string Blind =
            "The SQL survey no longer sees this file, so EveryStatementIsFixedAtBuildTime is " +
            "passing over less than it claims to cover.";

        var seen = SqlSurvey.Sinks.Select(static sink => sink.Where.Split(':')[0]).ToArray();

        foreach (var file in MustBeSeen)
        {
            seen.ShouldContain(file, Blind);
        }
    }

    /// <summary>
    /// Nothing constructs a database command the survey does not know how to read.
    /// </summary>
    /// <remarks>
    /// <c>CommandText</c> was not the only door and the excluded rule only ever watched that
    /// one: <c>PostgresStreamCheckpointStore</c> passes its SQL to <c>new NpgsqlCommand(…)</c>,
    /// and semgrep reported nothing there across every run. A third spelling — a batch command,
    /// a command type from another provider — would be a hole in this gate rather than a
    /// finding, so it fails here instead.
    /// </remarks>
    [Fact]
    public void EverySqlSinkIsOneTheSurveyKnows()
    {
        var unknown = SqlSurvey.Nodes<ObjectCreationExpressionSyntax>()
            .Where(static creation => SqlSurvey.Simple(creation.Type).EndsWith("Command", StringComparison.Ordinal))
            .Where(static creation => !SqlSurvey.CommandTypes.Contains(
                SqlSurvey.Simple(creation.Type), StringComparer.Ordinal))
            .Select(static creation => $"{SqlSurvey.Where(creation)}: {SqlSurvey.Simple(creation.Type)}")
            .ToArray();

        unknown.ShouldBeEmpty(
            "A database command type the SQL survey does not read. Its text is scanned by " +
            "nothing. Add it to SqlSurvey.CommandTypes, or explain in that list why it carries " +
            "no SQL:" + Environment.NewLine + string.Join(Environment.NewLine, unknown));
    }

    /// <summary>
    /// The one method allowed to produce SQL that is not a constant still reads an embedded
    /// resource.
    /// </summary>
    /// <remarks>
    /// <c>SqlSurvey.BuildTimeScriptReaders</c> lets <c>ReadScript</c> through by name, and a
    /// name is not a guarantee. If that method ever starts reading a file off disk, a connection
    /// string, or an argument, the allowance would be laundering a run-time value through a
    /// list written when it did not.
    /// </remarks>
    [Fact]
    public void TheEmbeddedScriptAllowanceStillReadsAnEmbeddedScript()
    {
        foreach (var name in SqlSurvey.BuildTimeScriptReaders)
        {
            var declarations = SqlSurvey.Nodes<MethodDeclarationSyntax>()
                .Where(method => method.Identifier.ValueText == name)
                .ToArray();

            declarations.ShouldNotBeEmpty(
                $"SqlSurvey.BuildTimeScriptReaders allows '{name}', which no longer exists. An " +
                "allowance for a method nobody can find is an allowance nobody can review.");

            foreach (var declaration in declarations)
            {
                declaration.ToString().ShouldContain(
                    "GetManifestResourceStream",
                    customMessage:
                    $"{SqlSurvey.Where(declaration)}: '{name}' is allowed to hand SQL straight to " +
                    "a command because it returns an embedded resource, fixed at build time. It " +
                    "no longer reads one, so the allowance is now covering a run-time value.");
            }
        }
    }
}
