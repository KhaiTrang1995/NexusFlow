using Mono.Cecil;
using Mono.Cecil.Cil;

namespace FlowX.Architecture.Tests;

/// <summary>
/// Walks a compiled module and reports which types each member actually touches.
/// </summary>
/// <remarks>
/// <para>
/// Shared by the gates that ask "does this assembly reach that one" — P4's reflection ban,
/// P3's transport isolation, P2's rule that capabilities form a set rather than a graph.
/// All three are the same walk over signatures and method bodies, differing only in what
/// they consider a violation, and writing the walk three times is how three rules end up
/// with three subtly different ideas of what "references" means.
/// </para>
/// <para>
/// Type <em>references</em>, not resolved definitions. Resolution needs every dependency on
/// disk and throws when one is missing, which would turn a rule about this repository into
/// a rule about whoever restored the NuGet cache. A reference carries the namespace and the
/// name, and that is the whole question being asked.
/// </para>
/// </remarks>
internal static class IlSurvey
{
    /// <summary>Every type declared in a module, including nested ones.</summary>
    public static IEnumerable<TypeDefinition> AllTypes(ModuleDefinition module)
    {
        foreach (var type in module.Types)
        {
            foreach (var nested in WithNested(type))
            {
                yield return nested;
            }
        }
    }

    /// <summary>
    /// Everything a type mentions, including from inside the types the compiler nested in it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the form nearly every rule wants, and getting it wrong is silent. An
    /// <c>async</c> method's body is not in the method: the compiler moves it into a nested
    /// state machine and leaves behind a stub that starts it. A rule reading only the type's
    /// own methods therefore sees the signature of every async method and the body of none —
    /// so a capability that awaits another capability, which is the only way one capability
    /// can call another in this codebase, reads as clean.
    /// </para>
    /// <para>
    /// Found by writing the violation and watching the gate stay green. The same applies to
    /// lambdas, iterators and local functions, all of which land in nested types.
    /// </para>
    /// </remarks>
    public static IEnumerable<Usage> UsagesDeep(TypeDefinition type) =>
        WithNested(type).SelectMany(Usages);

    /// <summary>
    /// Every type a member's signature or body mentions, with the member to blame.
    /// </summary>
    /// <remarks>
    /// Signature <em>and</em> body. A dependency held in a field is a reference the
    /// declaring type cannot execute without; a type constructed inside one method is a
    /// reference the compiler recorded and the reader will not see from the outside. Both
    /// are how a rule gets broken.
    /// </remarks>
    public static IEnumerable<Usage> Usages(TypeDefinition type)
    {
        foreach (var @interface in type.Interfaces)
        {
            foreach (var used in Flatten(@interface.InterfaceType))
            {
                yield return new Usage(type.FullName, used);
            }
        }

        if (type.BaseType is not null)
        {
            foreach (var used in Flatten(type.BaseType))
            {
                yield return new Usage(type.FullName, used);
            }
        }

        foreach (var field in type.Fields)
        {
            foreach (var used in Flatten(field.FieldType))
            {
                yield return new Usage($"{type.FullName}.{field.Name}", used);
            }
        }

        foreach (var method in type.Methods)
        {
            var where = $"{type.FullName}.{method.Name}";

            foreach (var used in FromSignature(method))
            {
                yield return new Usage(where, used);
            }

            foreach (var (used, via) in FromBody(method))
            {
                yield return new Usage(where, used, via);
            }
        }
    }

    /// <summary>
    /// Whether a member was written by the compiler rather than by a person.
    /// </summary>
    /// <remarks>
    /// A lambda cache, an async state machine and a closure display class are all static
    /// mutable state that no one typed and no one can remove. Judging them by a rule about
    /// how humans should write code produces failures whose only fix is to stop using
    /// <c>async</c>. The exemption is narrow: it applies to what the compiler emitted, not
    /// to a generator's output that a developer reads and debugs.
    /// </remarks>
    public static bool IsCompilerGenerated(IMemberDefinition member) =>
        HasCompilerGeneratedAttribute(member)
        || (member.DeclaringType is not null && IsCompilerGenerated(member.DeclaringType))
        || member.Name.StartsWith('<');

    private static bool HasCompilerGeneratedAttribute(ICustomAttributeProvider member) =>
        member.HasCustomAttributes
        && member.CustomAttributes.Any(static a =>
            a.AttributeType.FullName is "System.Runtime.CompilerServices.CompilerGeneratedAttribute");

    private static IEnumerable<TypeDefinition> WithNested(TypeDefinition type)
    {
        yield return type;

        foreach (var nested in type.NestedTypes)
        {
            foreach (var inner in WithNested(nested))
            {
                yield return inner;
            }
        }
    }

    private static IEnumerable<TypeReference> FromSignature(MethodDefinition method)
    {
        foreach (var used in Flatten(method.ReturnType))
        {
            yield return used;
        }

        foreach (var parameter in method.Parameters)
        {
            foreach (var used in Flatten(parameter.ParameterType))
            {
                yield return used;
            }
        }
    }

    private static IEnumerable<(TypeReference Used, string? Via)> FromBody(MethodDefinition method)
    {
        if (!method.HasBody)
        {
            yield break;
        }

        foreach (var variable in method.Body.Variables)
        {
            foreach (var used in Flatten(variable.VariableType))
            {
                yield return (used, null);
            }
        }

        foreach (var instruction in method.Body.Instructions)
        {
            foreach (var used in FromOperand(instruction))
            {
                yield return used;
            }
        }
    }

    private static IEnumerable<(TypeReference Used, string? Via)> FromOperand(Instruction instruction)
    {
        switch (instruction.Operand)
        {
            case MethodReference call:
                foreach (var used in Flatten(call.DeclaringType).Concat(Flatten(call.ReturnType)))
                {
                    yield return (used, call.Name);
                }

                foreach (var parameter in call.Parameters)
                {
                    foreach (var used in Flatten(parameter.ParameterType))
                    {
                        yield return (used, call.Name);
                    }
                }

                if (call is GenericInstanceMethod generic)
                {
                    foreach (var argument in generic.GenericArguments)
                    {
                        foreach (var used in Flatten(argument))
                        {
                            yield return (used, call.Name);
                        }
                    }
                }

                break;

            case FieldReference field:
                foreach (var used in Flatten(field.DeclaringType).Concat(Flatten(field.FieldType)))
                {
                    yield return (used, field.Name);
                }

                break;

            case TypeReference type:
                foreach (var used in Flatten(type))
                {
                    yield return (used, null);
                }

                break;

            default:
                break;
        }
    }

    /// <summary>
    /// Unwraps a type reference to the named types inside it.
    /// </summary>
    /// <remarks>
    /// <c>Lazy&lt;HttpContext&gt;[]</c> is a reference to <c>HttpContext</c>, and a rule that
    /// only looked at the outermost name would miss it — which is the same hole
    /// <c>docs/diagnostics/FLOWX1003.md</c> records the analyzer closing by unwrapping
    /// generic wrappers.
    /// </remarks>
    private static IEnumerable<TypeReference> Flatten(TypeReference? type)
    {
        if (type is null)
        {
            yield break;
        }

        if (type is GenericInstanceType instance)
        {
            foreach (var used in Flatten(instance.ElementType))
            {
                yield return used;
            }

            foreach (var argument in instance.GenericArguments)
            {
                foreach (var used in Flatten(argument))
                {
                    yield return used;
                }
            }

            yield break;
        }

        if (type is TypeSpecification specification)
        {
            foreach (var used in Flatten(specification.ElementType))
            {
                yield return used;
            }

            yield break;
        }

        if (type.IsGenericParameter)
        {
            yield break;
        }

        yield return type;
    }
}

/// <summary>One type mentioned by one member.</summary>
/// <param name="Member">The member to name in a failure message.</param>
/// <param name="Used">The type it mentions.</param>
/// <param name="Via">
/// The referenced member the mention came through — a called method or a read field — or
/// <c>null</c> when the type appears in a signature, a local or a token. It is what lets a
/// rule distinguish <c>typeof(T).Name</c> from <c>typeof(T).GetMethods()</c>, which mention
/// the same namespace for entirely different reasons.
/// </param>
internal readonly record struct Usage(string Member, TypeReference Used, string? Via = null)
{
    /// <summary>The used type's namespace-qualified name, without generic arity noise.</summary>
    public string UsedName => Used.FullName;

    /// <summary>The namespace the used type lives in, walking out of any nesting.</summary>
    public string UsedNamespace
    {
        get
        {
            var type = Used;

            while (type.DeclaringType is not null)
            {
                type = type.DeclaringType;
            }

            return type.Namespace ?? string.Empty;
        }
    }
}
