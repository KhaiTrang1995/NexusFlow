using System.Security.Claims;

namespace FlowX.Testing;

/// <summary>
/// Callers to run a flow as, for the stances a capability can declare.
/// </summary>
/// <remarks>
/// <para>
/// A flow whose capabilities declare <see cref="Authorization.Authenticated"/> or
/// <see cref="Authorization.Permission"/> is refused when nobody is calling it, so a test of
/// such a flow has to say who is. These are the two shapes that answer that, and they exist
/// so the answer is one call rather than four lines of <see cref="ClaimsIdentity"/>
/// construction repeated in every file.
/// </para>
/// <para>
/// <strong>There is deliberately no <c>Anyone</c> or <c>Admin</c>.</strong> A principal that
/// satisfied every stance would let a test pass over a capability whose permission it never
/// held, which is the same failure as not checking at all — and it would make the permission
/// named in the flow's manifest untested in the one place it is easiest to test.
/// </para>
/// </remarks>
public static class TestPrincipal
{
    /// <summary>The authentication type test principals carry.</summary>
    /// <remarks>
    /// Any non-empty string makes <see cref="ClaimsIdentity.IsAuthenticated"/> true, which is
    /// the property <see cref="Authorization.Authenticated"/> is decided against. Named
    /// rather than inlined so a test asserting on it does not repeat a literal.
    /// </remarks>
    public const string AuthenticationType = "FlowX.Testing";

    /// <summary>
    /// A caller who is signed in and holds no permission.
    /// </summary>
    /// <param name="name">
    /// The subject id, if a capability or an assertion reads one. Optional, because most do
    /// not.
    /// </param>
    /// <remarks>
    /// Satisfies <see cref="Authorization.Authenticated"/> and fails every
    /// <see cref="Authorization.Permission"/>. That combination is what proves a permission
    /// stance is doing something a plain authentication check would not.
    /// </remarks>
    public static ClaimsPrincipal SignedIn(string? name = null)
    {
        var claims = name is null
            ? Array.Empty<Claim>()
            : new[] { new Claim(ClaimTypes.NameIdentifier, name) };

        return new ClaimsPrincipal(new ClaimsIdentity(claims, AuthenticationType));
    }

    /// <summary>
    /// A caller who is signed in and holds <paramref name="permissions"/>.
    /// </summary>
    /// <param name="permissions">The permission names, as the capabilities spell them.</param>
    /// <exception cref="ArgumentNullException"><paramref name="permissions"/> is <c>null</c>.</exception>
    /// <remarks>
    /// Emitted as one space-delimited <c>scope</c> claim, which is the shape an OAuth 2.0
    /// access token carries — so a test principal exercises the same reading path a real
    /// token does rather than a convenient one that only tests take.
    /// </remarks>
    public static ClaimsPrincipal Holding(params string[] permissions)
    {
        ArgumentNullException.ThrowIfNull(permissions);

        return new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim("scope", string.Join(" ", permissions))],
            AuthenticationType));
    }
}
