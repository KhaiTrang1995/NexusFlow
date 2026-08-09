using System.Text.RegularExpressions;

namespace FlowX.Architecture.Tests;

/// <summary>
/// Reads the shipped SQL migrations and reports what tables they declare, which of those are
/// tenant-scoped, and which are protected.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Row-level security is the kind of control that is absent rather than wrong.</strong>
/// A table that forgot <c>ENABLE ROW LEVEL SECURITY</c> has no failing query and no error in a
/// log — every tenant simply reads every row, and the deployment looks healthy right up until
/// somebody notices. Nothing else in the build can see the omission: the C# compiles, the
/// analyzers pass, and the integration tests run as a single tenant and get the answer they
/// expected. The migration is the only artefact that records the decision, so the migration is
/// where the gate has to read.
/// </para>
/// <para>
/// <strong>Regular expressions, and the reason that is acceptable here when it is not in
/// <see cref="SourceSurvey"/>.</strong> There is no SQL parser in this build and adding one to
/// read a dozen DDL files would be a large dependency for a small question. What makes the
/// trade safe is the direction of the failure: every pattern below is anchored on the literal
/// keywords PostgreSQL requires, so a statement written in an unexpected style is not matched,
/// and an unmatched <c>CREATE TABLE</c> means a table the gate does not know about rather than
/// a table it wrongly clears. <see cref="TenancyFitnessTests.TheMigrationSurveyStillSeesTheSchema"/>
/// is what stops that degrading into a silent empty set.
/// </para>
/// </remarks>
internal static partial class MigrationSurvey
{
    /// <summary>The directories holding shipped migrations, relative to the repository root.</summary>
    public static readonly string[] MigrationDirectories =
    [
        "plugins/FlowX.Postgres/Migrations",
        "samples/crm/Migrations",
    ];

    /// <summary>The role a tenant-scoped connection assumes, as migration <c>0002</c> creates it.</summary>
    public const string TenantRole = "flowx_tenant";

    private static readonly Lazy<IReadOnlyList<TableDeclaration>> TablesLazy = new(FindTables);
    private static readonly Lazy<HashSet<string>> SecuredLazy = new(FindRowLevelSecured);
    private static readonly Lazy<HashSet<string>> GrantedLazy = new(FindGrantedToTenantRole);

    /// <summary>Every table any shipped migration creates.</summary>
    public static IReadOnlyList<TableDeclaration> Tables => TablesLazy.Value;

    /// <summary>Tables that turn row-level security on.</summary>
    public static HashSet<string> RowLevelSecured => SecuredLazy.Value;

    /// <summary>Tables the tenant role is granted access to.</summary>
    public static HashSet<string> GrantedToTenantRole => GrantedLazy.Value;

    /// <summary>
    /// A migration's text with <c>--</c> comments removed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Stripping comments is a correctness requirement, not tidiness.</strong> Read
    /// raw, <c>-- ALTER TABLE account ENABLE ROW LEVEL SECURITY;</c> matches the pattern for
    /// enabling row-level security, so commenting a policy out during an incident and forgetting
    /// to restore it would leave the gate green on a table that is no longer protected — the
    /// exact failure this survey exists to catch, defeated by two characters.
    /// </para>
    /// <para>
    /// It also protects the parenthesis balancing in <see cref="BodyAfter"/>: prose in a column
    /// comment routinely contains a single unmatched bracket, and these migrations already
    /// contain two such lines. Inside a table body one would truncate the column list early and
    /// hide a <c>tenant_id</c> declared after it, which fails silently in the unsafe direction.
    /// </para>
    /// </remarks>
    private static string TextOf(FileInfo file)
    {
        var lines = File.ReadAllLines(file.FullName);

        for (var i = 0; i < lines.Length; i++)
        {
            var comment = lines[i].IndexOf("--", StringComparison.Ordinal);

            if (comment >= 0)
            {
                lines[i] = lines[i][..comment];
            }
        }

        return string.Join('\n', lines);
    }

    /// <summary>Every <c>.sql</c> file in a migration directory.</summary>
    public static IEnumerable<FileInfo> MigrationFiles()
    {
        foreach (var relative in MigrationDirectories)
        {
            var directory = new DirectoryInfo(Path.Combine(RepositoryLayout.Root.FullName, relative));

            if (!directory.Exists)
            {
                continue;
            }

            foreach (var file in directory.EnumerateFiles("*.sql", SearchOption.AllDirectories)
                         .OrderBy(static f => f.Name, StringComparer.Ordinal))
            {
                yield return file;
            }
        }
    }

    private static List<TableDeclaration> FindTables()
    {
        var tables = new List<TableDeclaration>();

        foreach (var file in MigrationFiles())
        {
            var text = TextOf(file);

            foreach (Match match in CreateTable().Matches(text))
            {
                var name = match.Groups["name"].Value;
                var body = BodyAfter(text, match.Index + match.Length);

                tables.Add(new TableDeclaration(
                    Name: name,
                    File: SourceSurvey.RelativePath(file),
                    Line: LineOf(text, match.Index),
                    HasTenantColumn: TenantColumn().IsMatch(body)));
            }
        }

        return tables;
    }

    /// <summary>
    /// The column list of a <c>CREATE TABLE</c>, read by balancing parentheses from the opening one.
    /// </summary>
    /// <remarks>
    /// Balanced rather than "up to the first <c>);</c>", because a column can carry a
    /// <c>CHECK (... IN (...))</c> and a table can carry a composite <c>PRIMARY KEY (a, b)</c> —
    /// both of which close a parenthesis in the middle of the body. Stopping at the first one
    /// would truncate the body and could hide a <c>tenant_id</c> declared after it.
    /// </remarks>
    private static string BodyAfter(string text, int openParenthesis)
    {
        var depth = 0;

        for (var i = openParenthesis - 1; i < text.Length; i++)
        {
            depth += text[i] switch
            {
                '(' => 1,
                ')' => -1,
                _ => 0,
            };

            if (depth == 0 && text[i] == ')')
            {
                return text[openParenthesis..i];
            }
        }

        return text[(openParenthesis - 1)..];
    }

    private static HashSet<string> FindRowLevelSecured() =>
        Collect(EnableRowLevelSecurity());

    private static HashSet<string> FindGrantedToTenantRole()
    {
        var granted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var file in MigrationFiles())
        {
            foreach (Match match in GrantToTenant().Matches(TextOf(file)))
            {
                // `GRANT ... ON a, b, c TO flowx_tenant` is one statement naming three tables.
                foreach (var name in match.Groups["names"].Value.Split(','))
                {
                    granted.Add(Unqualified(name.Trim()));
                }
            }
        }

        return granted;
    }

    private static HashSet<string> Collect(Regex pattern)
    {
        var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var file in MigrationFiles())
        {
            foreach (Match match in pattern.Matches(TextOf(file)))
            {
                found.Add(Unqualified(match.Groups["name"].Value));
            }
        }

        return found;
    }

    /// <summary><c>public.account</c> and <c>account</c> are the same table.</summary>
    private static string Unqualified(string name)
    {
        var lastDot = name.LastIndexOf('.');

        return (lastDot < 0 ? name : name[(lastDot + 1)..]).Trim('"');
    }

    private static int LineOf(string text, int index) =>
        text.AsSpan(0, index).Count('\n') + 1;

    [GeneratedRegex(
        @"CREATE\s+TABLE\s+(?:IF\s+NOT\s+EXISTS\s+)?(?<name>[A-Za-z_.""][A-Za-z0-9_.""]*)\s*\(",
        RegexOptions.IgnoreCase)]
    private static partial Regex CreateTable();

    /// <summary>A <c>tenant_id</c> column declaration, anchored at the start of a body line.</summary>
    /// <remarks>
    /// Anchored so that a foreign key or a <c>CHECK</c> merely <em>mentioning</em> another
    /// table's <c>tenant_id</c> does not read as this table declaring one.
    /// </remarks>
    [GeneratedRegex(
        @"^\s*""?tenant_id""?\s+\w",
        RegexOptions.IgnoreCase | RegexOptions.Multiline)]
    private static partial Regex TenantColumn();

    [GeneratedRegex(
        @"ALTER\s+TABLE\s+(?:ONLY\s+)?(?<name>[A-Za-z_.""][A-Za-z0-9_.""]*)\s+ENABLE\s+ROW\s+LEVEL\s+SECURITY",
        RegexOptions.IgnoreCase)]
    private static partial Regex EnableRowLevelSecurity();

    [GeneratedRegex(
        @"GRANT\s+[A-Za-z, ]+?\s+ON\s+(?<names>[A-Za-z0-9_.""][A-Za-z0-9_.,""\s]*?)\s+TO\s+" + TenantRole,
        RegexOptions.IgnoreCase)]
    private static partial Regex GrantToTenant();
}

/// <summary>One table, as a migration declares it.</summary>
/// <param name="Name">The unqualified table name.</param>
/// <param name="File">Repository-relative path of the migration that creates it.</param>
/// <param name="Line">One-based line of the <c>CREATE TABLE</c>.</param>
/// <param name="HasTenantColumn">Whether the table declares its own <c>tenant_id</c> column.</param>
internal sealed record TableDeclaration(string Name, string File, int Line, bool HasTenantColumn)
{
    /// <summary>Where this table is declared, in a form a failure message can print.</summary>
    public string Where => $"{File}:{Line} ({Name})";
}
