namespace FlowX;

/// <summary>
/// Declares, as attribute <em>data</em>, which transport family a trigger attribute
/// belongs to — so the compiler can read the kind of a trigger it has never heard of.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Why this exists.</strong> <see cref="TriggerAttribute.Kind"/> is an abstract
/// property each attribute overrides with an expression
/// (<c>public override TriggerKind Kind =&gt; TriggerKind.Bus;</c>). That is executable
/// code in a property getter, not a value in metadata, and a source generator reads
/// metadata rather than running the assembly it is compiling. Before this marker existed
/// a <see cref="TriggerAttribute"/> subclass shipped by a transport plugin was therefore
/// unreadable by construction, and <c>flowx.manifest.json</c> simply had no entry for the
/// trigger it declared. An enum passed to a constructor <em>is</em> attribute data, and is
/// readable across an assembly boundary from metadata alone.
/// </para>
/// <para>
/// <strong>The alternative that does not work</strong>, recorded so it is not tried again:
/// moving the kind onto a base constructor parameter — <c>protected
/// TriggerAttribute(TriggerKind kind)</c> — reads only when the attribute's own source is
/// in the compilation. The <c>base(TriggerKind.Bus)</c> call is IL inside the derived
/// attribute's constructor, not part of the applied attribute's constructor arguments, so
/// it is invisible in exactly the case that matters: a plugin referenced as a compiled
/// assembly.
/// </para>
/// <para>
/// <strong>Put it on the attribute class, not on the flow.</strong> The declaration
/// belongs to whoever authored the trigger attribute; a flow author should not have to
/// restate a fact about a package they consume. The compiler walks the attribute's base
/// chain looking for this marker, so a plugin that factors shared members into its own
/// intermediate base declares the kind once, on the base.
/// </para>
/// <para>
/// <strong>Two declarations that can disagree.</strong> <see cref="TriggerAttribute.Kind"/>
/// is what the runtime reads; this marker is what the manifest publishes. Keep them equal.
/// When the attribute's source is in the compilation the disagreement is at least
/// detectable; when it arrives as a compiled reference it is not, because the property
/// getter's value cannot be observed without running it. The attributes
/// <c>FlowX.Abstractions</c> ships are held to agreement by a fitness function.
/// </para>
/// <example>
/// A transport plugin declaring its own trigger:
/// <code>
/// [TriggerKind(TriggerKind.Bus)]
/// [AttributeUsage(AttributeTargets.Class, AllowMultiple = true, Inherited = false)]
/// public sealed class MqttTriggerAttribute(string topic) : TriggerAttribute
/// {
///     public override TriggerKind Kind => TriggerKind.Bus;
///
///     public string Topic { get; } = topic;
/// }
/// </code>
/// </example>
/// </remarks>
/// <param name="kind">The transport family the annotated trigger attribute declares.</param>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = true)]
public sealed class TriggerKindAttribute(TriggerKind kind) : Attribute
{
    /// <summary>The transport family the annotated trigger attribute declares.</summary>
    public TriggerKind Kind { get; } = kind;
}
