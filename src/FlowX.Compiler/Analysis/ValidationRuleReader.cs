using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using FlowX.Compiler.Model;
using Microsoft.CodeAnalysis;

namespace FlowX.Compiler.Analysis;

/// <summary>
/// Reads the validation rules a contract declares, in the pass that builds the manifest.
/// </summary>
/// <remarks>
/// <para>
/// <strong>The vocabulary is <c>System.ComponentModel.DataAnnotations</c>' and the reading is
/// FlowX's.</strong> Those attributes are in the shared framework — <c>[Required]</c>,
/// <c>[Range]</c>, <c>[StringLength]</c>, <c>[MinLength]</c> and <c>[MaxLength]</c> compile in
/// any <c>net10.0</c> project with no package reference — so declaring one costs a contract
/// nothing and constraint <strong>C6</strong> is untouched. A FlowX-owned <c>[Required]</c>
/// would have been a second spelling of a word every C# author already knows, and the one thing
/// it could have bought — a marker the compiler can find — is not needed, because attributes are
/// matched here by display name and this assembly references neither.
/// </para>
/// <para>
/// <strong>What is deliberately not reused is the enforcement.</strong>
/// <c>Validator.TryValidateObject</c> walks a type with reflection, which constraint
/// <strong>C2</strong> refuses and which no trimmed or NativeAOT build can rely on. So the
/// attribute is read here, at compile time, and what ships is a comparison in generated source.
/// </para>
/// <para>
/// <strong>A rule that cannot be turned into a comparison is not read.</strong>
/// <c>[Range(typeof(DateTime), "…", "…")]</c> parses its bounds with a
/// <see cref="System.ComponentModel.TypeConverter"/> at run time; a length rule on a collection
/// is a different expression from one on a string. Each is skipped rather than guessed at, and
/// the consequence is visible: a step whose contract yields no rule at all is
/// <c>FLOWX1055</c>, so an author who declared <c>Validate</c> and got nothing is told, rather
/// than shipping a policy that checks the members it happened to understand.
/// </para>
/// </remarks>
public static class ValidationRuleReader
{
    private const string Namespace = "System.ComponentModel.DataAnnotations.";
    private const string RequiredAttribute = Namespace + "RequiredAttribute";
    private const string RangeAttribute = Namespace + "RangeAttribute";
    private const string StringLengthAttribute = Namespace + "StringLengthAttribute";
    private const string MinLengthAttribute = Namespace + "MinLengthAttribute";
    private const string MaxLengthAttribute = Namespace + "MaxLengthAttribute";

    /// <summary>The rule name a <c>[Required]</c> produces.</summary>
    public const string RequiredRule = "required";

    /// <summary>The rule name a <c>[Range]</c> produces.</summary>
    public const string RangeRule = "range";

    /// <summary>The rule name the three length attributes produce.</summary>
    public const string LengthRule = "length";

    /// <summary>
    /// Every rule the contract's members declare, in declaration order.
    /// </summary>
    /// <param name="contract">The step's input contract, or <c>null</c> when it has none.</param>
    /// <returns>The rules, or an empty array.</returns>
    public static ValidationRuleModel[] Read(ITypeSymbol? contract)
    {
        if (contract is null)
        {
            return System.Array.Empty<ValidationRuleModel>();
        }

        var rules = new List<ValidationRuleModel>();

        foreach (var member in contract.GetMembers())
        {
            if (member is not IPropertySymbol property ||
                property.IsStatic ||
                property.DeclaredAccessibility != Accessibility.Public ||
                property.GetMethod is null)
            {
                continue;
            }

            foreach (var attribute in Annotations(contract, property))
            {
                var rule = ReadOne(property, attribute);

                if (rule is not null)
                {
                    rules.Add(rule);
                }
            }
        }

        return rules.ToArray();
    }

    /// <summary>
    /// The attributes on a property and on the primary-constructor parameter behind it.
    /// </summary>
    /// <remarks>
    /// Both spellings, for <c>FlowAnalyzer.ReadSensitiveMembers</c>'s reason: a positional
    /// record puts <c>[Required]</c> on the parameter unless the author writes
    /// <c>[property: Required]</c>, and every one of these attributes permits
    /// <c>AttributeTargets.Parameter</c>. A reader blind to one spelling would silently drop the
    /// rules of every record contract written the ordinary way.
    /// </remarks>
    private static IEnumerable<AttributeData> Annotations(ITypeSymbol contract, IPropertySymbol property)
    {
        foreach (var attribute in property.GetAttributes())
        {
            yield return attribute;
        }

        foreach (var constructor in contract.GetMembers(".ctor").OfType<IMethodSymbol>())
        {
            foreach (var parameter in constructor.Parameters)
            {
                if (!string.Equals(parameter.Name, property.Name, System.StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                foreach (var attribute in parameter.GetAttributes())
                {
                    yield return attribute;
                }
            }
        }
    }

    private static ValidationRuleModel? ReadOne(IPropertySymbol property, AttributeData attribute)
    {
        var name = attribute.AttributeClass?.ToDisplayString();

        switch (name)
        {
            case RequiredAttribute:
                return Required(property, AllowsEmptyStrings(attribute));

            case RangeAttribute:
                return Range(property, attribute);

            case StringLengthAttribute:
                return StringLength(property, attribute);

            case MinLengthAttribute:
                return Length(property, Minimum(attribute), maximum: null);

            case MaxLengthAttribute:
                return Length(property, minimum: null, maximum: Minimum(attribute));

            default:
                return null;
        }
    }

    /// <summary>
    /// <c>[Required]</c>, which means "not null" and, for a string, "not blank".
    /// </summary>
    /// <remarks>
    /// <para>
    /// The string case is <c>RequiredAttribute</c>'s own default rather than an embellishment:
    /// <c>AllowEmptyStrings</c> is <c>false</c> unless the author says otherwise, and the
    /// attribute treats a whitespace-only string as missing. An author who sets it to true is
    /// asking for the null check alone, and gets it.
    /// </para>
    /// <para>
    /// <strong>A non-nullable value type yields no rule.</strong> An <c>int</c> is never null,
    /// so the check would be a comparison the compiler folds to <c>false</c> — a rule that
    /// cannot fire, and one that would let a contract satisfy <c>FLOWX1055</c> while validating
    /// nothing.
    /// </para>
    /// </remarks>
    private static ValidationRuleModel? Required(IPropertySymbol property, bool allowsEmpty)
    {
        var access = Access(property);
        var type = property.Type;

        if (IsString(type) && !allowsEmpty)
        {
            return new ValidationRuleModel(
                property.Name,
                RequiredRule,
                "string.IsNullOrWhiteSpace(" + access + ")",
                "'" + property.Name + "' is required.");
        }

        if (type.IsReferenceType ||
            type.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T)
        {
            return new ValidationRuleModel(
                property.Name,
                RequiredRule,
                access + " is null",
                "'" + property.Name + "' is required.");
        }

        return null;
    }

    /// <summary>
    /// <c>[Range(min, max)]</c> over a numeric member.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The lifted comparison is what makes a nullable member work without a second shape: in
    /// C# an <c>int?</c> that is null answers <c>false</c> to both <c>&lt;</c> and <c>&gt;</c>,
    /// so a missing value is out of this rule's business and into <c>[Required]</c>'s — which
    /// is exactly what <c>ValidationAttribute</c> does at run time.
    /// </para>
    /// <para>
    /// The <c>(Type, string, string)</c> constructor is skipped. Its bounds are parsed by a
    /// <see cref="System.ComponentModel.TypeConverter"/> against the culture at run time, and a
    /// generated comparison would have to pick a parse this reader cannot see the result of.
    /// </para>
    /// </remarks>
    private static ValidationRuleModel? Range(IPropertySymbol property, AttributeData attribute)
    {
        if (attribute.ConstructorArguments.Length != 2 || !IsNumeric(property.Type))
        {
            return null;
        }

        if (Numeric(attribute.ConstructorArguments[0]) is not { } minimum ||
            Numeric(attribute.ConstructorArguments[1]) is not { } maximum)
        {
            return null;
        }

        var access = Access(property);

        return new ValidationRuleModel(
            property.Name,
            RangeRule,
            access + " < " + minimum.Code + " || " + access + " > " + maximum.Code,
            "'" + property.Name + "' must be between " + minimum.Text + " and " +
                maximum.Text + ".");
    }

    /// <summary><c>[StringLength(max)]</c>, with an optional <c>MinimumLength</c>.</summary>
    private static ValidationRuleModel? StringLength(IPropertySymbol property, AttributeData attribute)
    {
        if (attribute.ConstructorArguments.Length != 1 ||
            attribute.ConstructorArguments[0].Value is not int maximum)
        {
            return null;
        }

        int? minimum = null;

        foreach (var named in attribute.NamedArguments)
        {
            if (named.Key == "MinimumLength" && named.Value.Value is int declared && declared > 0)
            {
                minimum = declared;
            }
        }

        return Length(property, minimum, maximum);
    }

    /// <summary>
    /// A length bound on a string member, in either direction or both.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Strings only.</strong> <c>[MaxLength]</c> is legal on an array and on a
    /// collection, and each of those is a different expression — <c>.Length</c>, <c>.Count</c>,
    /// or an enumeration for an <c>IEnumerable</c> that has neither, which is a rule that walks
    /// the caller's data before the step runs. Skipped rather than guessed at, per this class's
    /// remarks.
    /// </para>
    /// <para>
    /// A null member is not a length failure, for <see cref="Range"/>'s reason: absence is
    /// <c>[Required]</c>'s question, and a length rule that also refused null would give an
    /// author two ways to say one thing and one of them silently.
    /// </para>
    /// </remarks>
    private static ValidationRuleModel? Length(IPropertySymbol property, int? minimum, int? maximum)
    {
        if (!IsString(property.Type) || (minimum is null && maximum is null))
        {
            return null;
        }

        var access = Access(property);

        if (minimum is { } low && maximum is { } high)
        {
            return new ValidationRuleModel(
                property.Name,
                LengthRule,
                access + " is not null && (" + access + ".Length < " + Literal(low) +
                    " || " + access + ".Length > " + Literal(high) + ")",
                "'" + property.Name + "' must be between " + Literal(low) + " and " +
                    Literal(high) + " characters.");
        }

        if (maximum is { } only)
        {
            return new ValidationRuleModel(
                property.Name,
                LengthRule,
                access + " is { Length: > " + Literal(only) + " }",
                "'" + property.Name + "' must be at most " + Literal(only) + " characters.");
        }

        return new ValidationRuleModel(
            property.Name,
            LengthRule,
            access + " is { Length: < " + Literal(minimum!.Value) + " }",
            "'" + property.Name + "' must be at least " + Literal(minimum.Value) + " characters.");
    }

    /// <summary>Whether the author set <c>AllowEmptyStrings</c> on a <c>[Required]</c>.</summary>
    private static bool AllowsEmptyStrings(AttributeData attribute)
    {
        foreach (var named in attribute.NamedArguments)
        {
            if (named.Key == "AllowEmptyStrings" && named.Value.Value is bool allowed)
            {
                return allowed;
            }
        }

        return false;
    }

    /// <summary>The first constructor argument of a one-argument length attribute.</summary>
    private static int? Minimum(AttributeData attribute) =>
        attribute.ConstructorArguments.Length == 1 && attribute.ConstructorArguments[0].Value is int value
            ? value
            : null;

    /// <summary>How the generated code reaches the member.</summary>
    private static string Access(IPropertySymbol property) =>
        ValidationRuleModel.InputPlaceholder + "." + property.Name;

    private static bool IsString(ITypeSymbol type) => type.SpecialType == SpecialType.System_String;

    /// <summary>
    /// Whether a comparison against a numeric literal is meaningful for this member.
    /// </summary>
    /// <remarks>
    /// The nullable form is unwrapped rather than rejected: <c>int?</c> compares against an
    /// <c>int</c> through the lifted operator, and a rule that skipped it would silently drop
    /// every optional bounded field.
    /// </remarks>
    private static bool IsNumeric(ITypeSymbol type)
    {
        var underlying = type is INamedTypeSymbol named &&
                         named.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T &&
                         named.TypeArguments.Length == 1
            ? named.TypeArguments[0]
            : type;

        switch (underlying.SpecialType)
        {
            case SpecialType.System_Byte:
            case SpecialType.System_SByte:
            case SpecialType.System_Int16:
            case SpecialType.System_UInt16:
            case SpecialType.System_Int32:
            case SpecialType.System_UInt32:
            case SpecialType.System_Int64:
            case SpecialType.System_UInt64:
            case SpecialType.System_Single:
            case SpecialType.System_Double:
            case SpecialType.System_Decimal:
                return true;

            default:
                return false;
        }
    }

    /// <summary>
    /// A bound, twice: as the generated source spells it, and as the message reads it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Two spellings rather than one, because they answer to different readers. The code needs
    /// an invariant literal with a type suffix — a <c>double</c> written under a comma-decimal
    /// culture does not compile, and one written without <c>d</c> can change the comparison's
    /// type — and the caller reading a refusal wants the number, not a C# literal.
    /// </para>
    /// <para>
    /// Only the <c>(int, int)</c> and <c>(double, double)</c> constructors are read. The
    /// <c>(Type, string, string)</c> one parses its bounds at run time; see this class's
    /// remarks.
    /// </para>
    /// </remarks>
    private static (string Code, string Text)? Numeric(TypedConstant argument)
    {
        switch (argument.Value)
        {
            case int value:
                return (Literal(value), Literal(value));

            case double value:
                var text = value.ToString("R", CultureInfo.InvariantCulture);
                return (text + "d", text);

            default:
                return null;
        }
    }

    private static string Literal(int value) => value.ToString(CultureInfo.InvariantCulture);
}
