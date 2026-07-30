// netstandard2.0 predates several attributes the C# compiler emits references to.
// Declaring them here is the standard workaround and costs nothing at run time —
// they exist only so the compiler can bind.

namespace System.Runtime.CompilerServices
{
    /// <summary>Enables <c>init</c> accessors and records on netstandard2.0.</summary>
    internal static class IsExternalInit
    {
    }
}

namespace System.Diagnostics.CodeAnalysis
{
    /// <summary>Marks an out parameter that is non-null when the method returns true.</summary>
    [AttributeUsage(AttributeTargets.Parameter, AllowMultiple = false)]
    internal sealed class NotNullWhenAttribute : Attribute
    {
        /// <summary>Creates the attribute.</summary>
        public NotNullWhenAttribute(bool returnValue) => ReturnValue = returnValue;

        /// <summary>The return value that guarantees non-nullness.</summary>
        public bool ReturnValue { get; }
    }
}
