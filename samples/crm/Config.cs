using FlowX;

namespace Crm;

// -------------------------------------------------------------------------------- what is asked

/// <summary>What an administrator has declared, and can be shown.</summary>
/// <remarks>
/// <para>
/// <strong>Twelve things that could be declared and not read.</strong> Every one of these had a
/// route that writes it and none that lists it, so a setup screen could create a validation rule
/// and never show it again. That is the shape of a configuration surface nobody trusts: the only
/// way to find out what a tenant has is to try something and see what refuses it.
/// </para>
/// <para>
/// <strong>One vocabulary and one route, not twelve.</strong> The same argument
/// <see cref="ReadableEntity"/> makes. A read per table is twelve endpoints, twelve contracts,
/// twelve places for the permission to be spelt differently, and the thirteenth is written by
/// somebody copying the twelfth. What varies between them is a statement, and a statement is a
/// constant chosen by a switch.
/// </para>
/// </remarks>
public enum ConfigKind
{
    /// <summary>A named query saved over a custom object.</summary>
    ListView = 0,

    /// <summary>A rule that refuses a write when it holds.</summary>
    ValidationRule = 1,

    /// <summary>An aggregate over a parent's children.</summary>
    RollUp = 2,

    /// <summary>A field computed from the same record.</summary>
    Formula = 3,

    /// <summary>A grouped count or sum over one source.</summary>
    Report = 4,

    /// <summary>A set of tiles, each naming a report.</summary>
    Dashboard = 5,

    /// <summary>An address this server will later send to.</summary>
    Connector = 6,

    /// <summary>What this tenant calls an entity or one of its columns.</summary>
    Label = 7,

    /// <summary>Who has to approve what, and in what order.</summary>
    ApprovalProcess = 8,

    /// <summary>What a case of one priority is promised.</summary>
    SlaPolicy = 9,

    /// <summary>When the desk is open.</summary>
    BusinessHours = 10,

    /// <summary>A patch of the map.</summary>
    Territory = 11,
}

/// <summary>Lists what a tenant has declared of one kind.</summary>
/// <param name="Kind">Which kind.</param>
/// <param name="Limit">How many at most.</param>
public sealed record ReadConfig(ConfigKind Kind, int Limit);

// -------------------------------------------------------------------------------- what comes back

/// <summary>One declared thing, as a setup screen needs to list it.</summary>
/// <param name="Id">
/// Its identifier, or null for the two kinds that have none — a label and a day's opening hours
/// are keyed by what they describe rather than by an id of their own.
/// </param>
/// <param name="Name">The identifier an administrator refers to it by.</param>
/// <param name="Label">What a person sees.</param>
/// <param name="Summary">
/// <strong>What it does, in a sentence, composed by the server.</strong> A row of columns is not
/// what an administrator is looking for on a setup screen; "refuses when amount is greater than
/// 100000" is. Composing it here rather than on the client is what stops five clients writing
/// five different sentences about one rule — and what lets the sentence use columns the client
/// was never given.
/// </param>
/// <param name="IsActive">
/// Whether it is in force. Kinds that cannot be switched off report <c>true</c>.
/// </param>
public sealed record ConfigItem(
    Guid? Id,
    string Name,
    string Label,
    string Summary,
    bool IsActive);

/// <summary>What a tenant has declared of one kind.</summary>
/// <param name="Kind">Which kind was asked for.</param>
/// <param name="Items">What there is, oldest first.</param>
public sealed record ConfigList(ConfigKind Kind, IReadOnlyList<ConfigItem> Items);

// -------------------------------------------------------------------------------- what it accepts

/// <summary>What the configuration read accepts.</summary>
public static class ConfigLimits
{
    /// <summary>The most items one read answers with.</summary>
    public const int MaxItems = 200;
}
