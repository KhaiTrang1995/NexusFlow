using System.Text;

namespace FlowX.Compiler.Emit;

/// <summary>An indent-aware string builder for emitted C#.</summary>
/// <remarks>
/// <para>
/// Trivial by design. The alternative — building syntax trees with
/// <c>SyntaxFactory</c> — produces code that is correct by construction but
/// unreadable to write and unreadable when it comes out. Risk R1 says the emitted
/// code must be something a developer can set a breakpoint in and follow, so it is
/// written the way a human would write it.
/// </para>
/// <para>
/// Line endings are normalised to <c>\n</c> so a snapshot test produces the same
/// bytes on every platform. A generator whose golden files differ between Windows
/// and Linux is a generator whose tests get disabled.
/// </para>
/// </remarks>
public sealed class SourceWriter
{
    private const string IndentUnit = "    ";

    private readonly StringBuilder _builder = new StringBuilder();
    private int _indent;

    /// <summary>Writes a line at the current indent. An empty string writes a blank line.</summary>
    public SourceWriter Line(string text = "")
    {
        if (text is null)
        {
            throw new System.ArgumentNullException(nameof(text));
        }

        if (text.Length > 0)
        {
            for (var i = 0; i < _indent; i++)
            {
                _builder.Append(IndentUnit);
            }

            _builder.Append(text);
        }

        _builder.Append('\n');
        return this;
    }

    /// <summary>Writes <c>{</c> and increases the indent.</summary>
    public SourceWriter OpenBrace()
    {
        Line("{");
        _indent++;
        return this;
    }

    /// <summary>Decreases the indent and writes <c>}</c>.</summary>
    public SourceWriter CloseBrace(string suffix = "")
    {
        _indent--;
        Line("}" + suffix);
        return this;
    }

    /// <summary>The emitted source.</summary>
    public override string ToString() => _builder.ToString();
}
