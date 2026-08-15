namespace FlowX.Compiler.Model;

/// <summary>
/// One check the generator emits for one annotated member of a step's input contract.
/// </summary>
/// <remarks>
/// <para>
/// <strong>The condition and the message are both decided here, and both are closed.</strong>
/// <see cref="Condition"/> is a C# boolean expression that is <em>true when the rule is
/// broken</em>, written against a placeholder for the input; <see cref="Message"/> is the
/// sentence a caller reads. Deciding them where the symbols are — in
/// <c>ValidationRuleReader</c> — rather than in the emitter is what keeps the emitter free of
/// type questions, and it is the same split every other model in this folder makes.
/// </para>
/// <para>
/// <strong>No member of this type can carry a value.</strong> The message is built from the
/// rule's own declared bounds, which are literals in the author's source, and the condition
/// reads the member rather than describing it. There is therefore no expression through which a
/// <c>[Sensitive]</c> member's value could reach a refusal — which is the property
/// <c>docs/10 §3</c>'s <c>Validate</c> row is worth nothing without.
/// </para>
/// </remarks>
public sealed class ValidationRuleModel
{
    /// <summary>Creates a rule.</summary>
    /// <param name="field">The contract member's name, as the caller will see it.</param>
    /// <param name="rule">The rule's stable machine-readable name.</param>
    /// <param name="condition">
    /// A boolean C# expression, true when the rule is broken, in which
    /// <see cref="InputPlaceholder"/> stands for the step's input.
    /// </param>
    /// <param name="message">What was expected. Never what was supplied.</param>
    public ValidationRuleModel(string field, string rule, string condition, string message)
    {
        Field = field;
        Rule = rule;
        Condition = condition;
        Message = message;
    }

    /// <summary>What the condition writes where the step's input goes.</summary>
    /// <remarks>
    /// A token no C# expression can contain, so the emitter's substitution cannot collide with
    /// anything the reader produced.
    /// </remarks>
    public const string InputPlaceholder = "$input";

    /// <summary>The contract member's name.</summary>
    public string Field { get; }

    /// <summary><c>required</c>, <c>range</c> or <c>length</c>.</summary>
    public string Rule { get; }

    /// <summary>The C# expression that is true when the rule is broken.</summary>
    public string Condition { get; }

    /// <summary>The sentence the caller reads.</summary>
    public string Message { get; }
}
