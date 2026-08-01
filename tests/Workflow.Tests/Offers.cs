namespace Workflow.Tests;

/// <summary>
/// The inputs the tests below onboard, named for the route each one takes.
/// </summary>
/// <remarks>
/// Named rather than inlined, because every test in this project is a statement about which
/// path an input takes through the graph, and <c>Offers.Contractor()</c> at a call site says
/// that where a six-argument constructor does not.
/// </remarks>
internal static class Offers
{
    /// <summary>A laptop bag that anyone may have, and a phone that needs signing for.</summary>
    internal static IReadOnlyList<EquipmentRequest> TwoItems { get; } =
    [
        new EquipmentRequest("laptop-bag", NeedsApproval: false),
        new EquipmentRequest("phone", NeedsApproval: true),
    ];

    /// <summary>Takes the <c>Permanent</c> case and the conditional's <c>Otherwise</c> arm.</summary>
    internal static OnboardEmployee Permanent(
        IReadOnlyList<EquipmentRequest>? equipment = null,
        bool requiresBackgroundCheck = false) =>
        new(
            "c-1",
            EmploymentType.Permanent,
            "london",
            equipment ?? TwoItems,
            requiresBackgroundCheck,
            "NI-000-001");

    /// <summary>Takes the <c>Contractor</c> case.</summary>
    internal static OnboardEmployee Contractor() =>
        new("c-2", EmploymentType.Contractor, "leeds", TwoItems, RequiresBackgroundCheck: false, "NI-000-002");

    /// <summary>Matches no case, so it reaches the <c>Default</c> arm and is rejected.</summary>
    internal static OnboardEmployee Intern() =>
        new("c-3", EmploymentType.Intern, "york", TwoItems, RequiresBackgroundCheck: false, "NI-000-003");
}
