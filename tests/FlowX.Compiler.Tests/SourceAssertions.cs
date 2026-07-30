using System;
using Shouldly;

namespace FlowX.Compiler.Tests;

/// <summary>
/// Substring assertions on generated source that say what they mean.
/// </summary>
/// <remarks>
/// Shouldly's <c>ShouldContain(expected, customMessage)</c> binds to the
/// <c>IEnumerable&lt;T&gt;</c> overload when the subject is a <see cref="string"/>,
/// because a string is a sequence of characters. The compiler then tries to read the
/// message as an <c>Expression&lt;Func&lt;char, bool&gt;&gt;</c> and the error names
/// neither the real problem nor the fix.
///
/// This is the third time that trap has been hit in this repository, which is the
/// point at which working around it in each call site stops being reasonable. These
/// helpers make the intent unambiguous and keep the failure message — which is the
/// only reason to pass one — intact.
/// </remarks>
internal static class SourceAssertions
{
    /// <summary>Asserts the source contains <paramref name="expected"/>.</summary>
    public static void ShouldContainText(this string source, string expected, string because)
        => source.Contains(expected, StringComparison.Ordinal).ShouldBeTrue(
            $"{because}\n\nExpected to find:\n  {expected}");

    /// <summary>Asserts the source does not contain <paramref name="unexpected"/>.</summary>
    public static void ShouldNotContainText(this string source, string unexpected, string because)
        => source.Contains(unexpected, StringComparison.Ordinal).ShouldBeFalse(
            $"{because}\n\nExpected NOT to find:\n  {unexpected}");
}
