using System.Collections.Immutable;
using FlowX.Compiler.Diagnostics;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;

namespace FlowX.Compiler.Analysis;

/// <summary>
/// Reports a declared policy the runtime will not apply: FLOWX1032 and FLOWX1033.
/// </summary>
/// <remarks>
/// The shell the tests in <c>DeclaredPolicyAnalyzerTests</c> are written against. It
/// registers no action, so every one of them fails, which is the point: the assertions
/// describe the rule before the rule decides what it asserts.
/// </remarks>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class DeclaredPolicyAnalyzer : DiagnosticAnalyzer
{
    /// <inheritdoc />
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } =
        ImmutableArray.Create(
            FlowXDiagnostics.PolicyIsNotExecutedByTheRuntime,
            FlowXDiagnostics.CompensationRetryHasNoCompensation);

    /// <inheritdoc />
    public override void Initialize(AnalysisContext context)
    {
        if (context is null)
        {
            return;
        }

        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
    }
}
