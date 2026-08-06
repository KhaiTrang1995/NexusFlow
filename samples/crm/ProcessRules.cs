using System.Globalization;

namespace Crm;

/// <summary>
/// The fields a guard may name, and the only ones it may name.
/// </summary>
/// <remarks>
/// <para>
/// <strong>A whitelist rather than an expression language, and
/// <c>docs/26-CRM-Sample.md</c> §7.4 is the argument.</strong> A general expression evaluator
/// over the entity graph would be a second execution engine: untyped, unbounded, invisible to
/// the manifest, unreachable by <c>FLOWX1011</c>'s ambient-read analysis, and impossible to
/// authorise at build time. Five operators over a closed set of fields is what can be
/// <em>checked</em>.
/// </para>
/// <para>
/// <strong>The point of the list being a compile-time constant is when it fires.</strong> A
/// guard naming a field that is not here is refused when the administrator publishes the
/// definition, not at three in the morning when an opportunity happens to reach that stage.
/// </para>
/// <para>
/// Adding a field is a code change and a deployment. That is the price §7.3 states, and it is
/// paid deliberately: a field nothing in this file names is a field no reader of the process
/// can reason about.
/// </para>
/// </remarks>
public static class ProcessFields
{
    /// <summary>The opportunity's amount, in its own currency.</summary>
    public const string Amount = "amount";

    /// <summary>Its currency.</summary>
    public const string Currency = "currency";

    /// <summary>0 to 100.</summary>
    public const string Probability = "probability";

    /// <summary>The account's region.</summary>
    public const string Region = "region";

    /// <summary>The account's industry.</summary>
    public const string Industry = "industry";

    /// <summary>Who holds it.</summary>
    public const string Owner = "owner";

    /// <summary>Every field a guard may name without an administrator having declared it.</summary>
    public static IReadOnlySet<string> All { get; } =
        new HashSet<string>(StringComparer.Ordinal)
        {
            Amount, Currency, Probability, Region, Industry, Owner,
        };

    /// <summary>Every field a guard may name, given what this tenant has declared.</summary>
    /// <param name="declared">The custom fields declared for the entity, by name.</param>
    /// <returns>The built-in fields and the declared ones.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="declared"/> is null.</exception>
    /// <remarks>
    /// <para>
    /// <strong>This is the one place the closed list above became open, and what it gave up is
    /// narrower than it looks.</strong> The remarks on this class argue against an expression
    /// language, and every word of that still holds: the operators are still five, the
    /// comparison is still text or number, guard evaluation is still a pure function of a
    /// snapshot, and nothing here evaluates anything. What is now data is the <em>catalogue</em>
    /// — which names are legal — and not the language.
    /// </para>
    /// <para>
    /// <strong>The property that mattered is kept, which is when a bad guard is caught.</strong>
    /// A guard naming a field nobody declared is still refused by
    /// <see cref="ProcessPublishing.Validate"/> when the administrator publishes, not at three
    /// in the morning when an opportunity happens to reach that stage. The difference is that
    /// the set it is checked against is now read from <c>custom_field</c> instead of compiled in
    /// — so adding a field stopped being a deployment, and naming one that does not exist did
    /// not stop being an error.
    /// </para>
    /// </remarks>
    public static IReadOnlySet<string> Including(IReadOnlyDictionary<string, CustomFieldRow> declared)
    {
        ArgumentNullException.ThrowIfNull(declared);

        var fields = new HashSet<string>(All, StringComparer.Ordinal);

        foreach (var name in declared.Keys)
        {
            fields.Add(name);
        }

        return fields;
    }
}

/// <summary>
/// What a guard is evaluated against: the entity's whitelisted fields, and nothing else.
/// </summary>
/// <remarks>
/// <strong>A snapshot rather than a live reader.</strong> The transition engine reads the
/// entity once and evaluates every guard of every candidate transition against the same values,
/// so two guards on one transition cannot disagree about what the amount was. It also means
/// guard evaluation is a pure function, which is why the whole of <see cref="ProcessRules"/> is
/// testable without a database.
/// </remarks>
/// <param name="Amount">The opportunity's amount, or null when it has none.</param>
/// <param name="Currency">Its currency.</param>
/// <param name="Probability">0 to 100.</param>
/// <param name="Region">The account's region.</param>
/// <param name="Industry">The account's industry.</param>
/// <param name="Owner">Who holds it.</param>
/// <param name="Custom">
/// The opportunity's custom values, by field name, or null when it has none. Read last, so a
/// declared field can never shadow a built-in one — the same precedence
/// <see cref="ProcessFields.Including"/> gives, stated in the two places it has to hold.
/// </param>
public sealed record ProcessFacts(
    decimal? Amount,
    string? Currency,
    int? Probability,
    string? Region,
    string? Industry,
    Guid? Owner,
    IReadOnlyDictionary<string, string?>? Custom = null)
{
    /// <summary>Reads one whitelisted field, or null when it is not set.</summary>
    /// <param name="field">One of <see cref="ProcessFields.All"/>.</param>
    /// <returns>The value as text, or null.</returns>
    /// <remarks>
    /// Text, because a guard's <c>value</c> column is text and comparing like with like is what
    /// keeps <see cref="GuardOperator.Equals"/> from meaning three different things. The two
    /// ordering operators parse both sides as numbers and refuse when either will not parse —
    /// see <see cref="ProcessRules.Holds"/>.
    /// </remarks>
    public string? Read(string field) => field switch
    {
        ProcessFields.Amount => Amount?.ToString(CultureInfo.InvariantCulture),
        ProcessFields.Currency => Currency,
        ProcessFields.Probability => Probability?.ToString(CultureInfo.InvariantCulture),
        ProcessFields.Region => Region,
        ProcessFields.Industry => Industry,
        ProcessFields.Owner => Owner?.ToString(),
        _ => Custom is not null && Custom.TryGetValue(field, out var value) ? value : null,
    };
}

/// <summary>A transition and everything needed to decide whether it may be taken.</summary>
/// <param name="Transition">The transition itself.</param>
/// <param name="Guards">Its guards. All must hold.</param>
/// <param name="Actions">What it does, in order.</param>
public sealed record TransitionCandidate(
    ProcessTransition Transition,
    IReadOnlyList<TransitionGuard> Guards,
    IReadOnlyList<TransitionAction> Actions);

/// <summary>Why a definition cannot be published.</summary>
/// <param name="TransitionId">Which transition the fault is on, or null for a whole-definition fault.</param>
/// <param name="Reason">What is wrong, in a sentence an administrator can act on.</param>
public sealed record ProcessFault(Guid? TransitionId, string Reason);

/// <summary>
/// Matching a transition and evaluating its guards — the whole of the configurable part's
/// decision, as a pure function.
/// </summary>
/// <remarks>
/// <strong>Nothing here reads a clock, a database or a random source.</strong> That is what
/// makes the configured behaviour reproducible on a replay, and what lets the tests below it
/// cover every operator without standing anything up.
/// </remarks>
public static class ProcessRules
{
    /// <summary>
    /// The first transition leaving <paramref name="fromStage"/> on <paramref name="trigger"/>
    /// whose guards all hold.
    /// </summary>
    /// <param name="candidates">Every transition the definition holds.</param>
    /// <param name="fromStage">Where the entity is now.</param>
    /// <param name="trigger">What happened.</param>
    /// <param name="facts">The entity's whitelisted fields.</param>
    /// <returns>The transition to take, or null when none is allowed.</returns>
    /// <remarks>
    /// <para>
    /// <strong>First by ordinal, not best.</strong> Two transitions whose guards both hold are
    /// not an ambiguity to resolve at run time — the administrator ordered them and the order
    /// is the answer. Scoring them instead would make the outcome depend on a rule nobody
    /// wrote down.
    /// </para>
    /// <para>
    /// <strong>No match is not an error.</strong> An opportunity moved to a stage its process
    /// has no transition out of has simply not triggered anything, and §7.3's diagram records
    /// that as a recorded outcome rather than a failure.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentNullException">Any argument is null.</exception>
    public static TransitionCandidate? Match(
        IReadOnlyList<TransitionCandidate> candidates,
        Guid fromStage,
        string trigger,
        ProcessFacts facts)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(trigger);
        ArgumentNullException.ThrowIfNull(facts);

        TransitionCandidate? best = null;

        for (var i = 0; i < candidates.Count; i++)
        {
            var candidate = candidates[i];

            if (candidate.Transition.From != fromStage ||
                !string.Equals(candidate.Transition.Trigger, trigger, StringComparison.Ordinal))
            {
                continue;
            }

            if (!AllHold(candidate.Guards, facts))
            {
                continue;
            }

            if (best is null || candidate.Transition.Ordinal < best.Transition.Ordinal)
            {
                best = candidate;
            }
        }

        return best;
    }

    /// <summary>Whether every guard holds.</summary>
    /// <param name="guards">The transition's guards.</param>
    /// <param name="facts">The entity's whitelisted fields.</param>
    /// <returns><c>true</c> when all of them hold, including when there are none.</returns>
    /// <exception cref="ArgumentNullException">Any argument is null.</exception>
    public static bool AllHold(IReadOnlyList<TransitionGuard> guards, ProcessFacts facts)
    {
        ArgumentNullException.ThrowIfNull(guards);
        ArgumentNullException.ThrowIfNull(facts);

        for (var i = 0; i < guards.Count; i++)
        {
            if (!Holds(guards[i], facts))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Whether one guard holds.</summary>
    /// <param name="guard">The guard.</param>
    /// <param name="facts">The entity's whitelisted fields.</param>
    /// <returns><c>true</c> when it holds.</returns>
    /// <remarks>
    /// <para>
    /// <strong>An unset field fails every operator but <see cref="GuardOperator.NotEquals"/>
    /// and a negative <see cref="GuardOperator.IsSet"/>.</strong> "Amount is greater than
    /// 50 000" is not true of an opportunity with no amount, and neither is "amount equals
    /// 50 000" — the guard is a claim about a value, and there is no value.
    /// </para>
    /// <para>
    /// <strong>The two ordering operators refuse text.</strong> A guard comparing a region to
    /// "greater than EU-WEST" is a mistake nobody meant to write, and answering it with a
    /// string comparison would give it a plausible answer instead of none. It is false, and
    /// <see cref="ProcessPublishing.Validate"/> refuses it at publish time so it never gets
    /// this far.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentNullException">Any argument is null.</exception>
    public static bool Holds(TransitionGuard guard, ProcessFacts facts)
    {
        ArgumentNullException.ThrowIfNull(guard);
        ArgumentNullException.ThrowIfNull(facts);

        var actual = facts.Read(guard.Field);

        return guard.Operator switch
        {
            GuardOperator.IsSet => IsTrue(guard.Value) == (actual is not null),
            GuardOperator.Equals => actual is not null && string.Equals(actual, guard.Value, StringComparison.Ordinal),
            GuardOperator.NotEquals => !string.Equals(actual, guard.Value, StringComparison.Ordinal),
            GuardOperator.GreaterThan => Compare(actual, guard.Value) > 0,
            GuardOperator.LessThan => Compare(actual, guard.Value) < 0,
            _ => false,
        };
    }

    /// <summary>Whether an operator compares numbers rather than text.</summary>
    /// <param name="op">The operator.</param>
    /// <returns><c>true</c> for the two ordering operators.</returns>
    public static bool IsNumeric(GuardOperator op) =>
        op is GuardOperator.GreaterThan or GuardOperator.LessThan;

    private static int Compare(string? actual, string expected)
    {
        // Both sides, or no answer. An unset field and an unparseable bound both give 0, which
        // makes GreaterThan and LessThan alike false — the guard did not hold rather than
        // holding by accident.
        if (actual is null ||
            !decimal.TryParse(actual, NumberStyles.Number, CultureInfo.InvariantCulture, out var left) ||
            !decimal.TryParse(expected, NumberStyles.Number, CultureInfo.InvariantCulture, out var right))
        {
            return 0;
        }

        return left.CompareTo(right);
    }

    private static bool IsTrue(string value) =>
        string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// What a definition must satisfy before it may be published.
/// </summary>
/// <remarks>
/// <strong>Checked at publish, which is the whole point.</strong> An administrator who names a
/// field that does not exist finds out when they press publish, with the transition named. The
/// alternative is that the mistake sits in the table until an opportunity reaches that stage,
/// and the first person to learn of it is whoever is on call.
/// </remarks>
public static class ProcessPublishing
{
    /// <summary>Every reason this definition cannot be published.</summary>
    /// <param name="stages">Its stages.</param>
    /// <param name="candidates">Its transitions, with their guards and actions.</param>
    /// <param name="fields">
    /// Every field a guard may name. Defaults to the built-in ones; pass
    /// <see cref="ProcessFields.Including"/> to let guards name what this tenant declared.
    /// </param>
    /// <returns>The faults, empty when it may be published.</returns>
    /// <exception cref="ArgumentNullException">Any argument is null.</exception>
    /// <remarks>
    /// <strong>The catalogue is a parameter and not a lookup, which is what keeps this a pure
    /// function.</strong> Reading <c>custom_field</c> in here would put a database call inside
    /// the one part of the configurable process that has never needed one, and would make the
    /// answer depend on when it was asked.
    /// </remarks>
    public static IReadOnlyList<ProcessFault> Validate(
        IReadOnlyList<ProcessStage> stages,
        IReadOnlyList<TransitionCandidate> candidates,
        IReadOnlySet<string>? fields = null)
    {
        ArgumentNullException.ThrowIfNull(stages);
        ArgumentNullException.ThrowIfNull(candidates);

        var nameable = fields ?? ProcessFields.All;
        var faults = new List<ProcessFault>();
        var known = new HashSet<Guid>(stages.Select(static stage => stage.Id));

        if (stages.Count == 0)
        {
            faults.Add(new ProcessFault(null, "A process must have at least one stage."));
        }

        foreach (var candidate in candidates)
        {
            var id = candidate.Transition.Id;

            if (!known.Contains(candidate.Transition.From) || !known.Contains(candidate.Transition.To))
            {
                faults.Add(new ProcessFault(id, "A transition must join two stages of this process."));
            }

            if (candidate.Transition.Trigger.Length == 0)
            {
                faults.Add(new ProcessFault(id, "A transition must name the trigger it answers."));
            }

            foreach (var guard in candidate.Guards)
            {
                if (!nameable.Contains(guard.Field))
                {
                    faults.Add(new ProcessFault(
                        id,
                        $"'{guard.Field}' is not a field a guard may name. The fields are: " +
                        string.Join(", ", nameable.Order(StringComparer.Ordinal)) + "."));
                }

                if (!Enum.IsDefined(guard.Operator))
                {
                    faults.Add(new ProcessFault(id, $"'{guard.Operator}' is not an operator."));
                }
                else if (ProcessRules.IsNumeric(guard.Operator) &&
                         !decimal.TryParse(guard.Value, NumberStyles.Number, CultureInfo.InvariantCulture, out _))
                {
                    faults.Add(new ProcessFault(
                        id,
                        $"'{guard.Operator}' compares numbers, and '{guard.Value}' is not one."));
                }
            }

            foreach (var action in candidate.Actions)
            {
                if (!Enum.IsDefined(action.Kind))
                {
                    faults.Add(new ProcessFault(
                        id,
                        $"'{action.Kind}' is not an action this build can run. Adding one is a " +
                        "code change — see docs/26-CRM-Sample.md §7.3."));
                }
            }
        }

        return faults;
    }
}
