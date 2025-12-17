using Content.Shared.Chemistry;
using Content.Shared.EntityEffects;
using Robust.Shared.GameObjects;
using Robust.Shared.Serialization.Manager.Attributes;

namespace Content.Goobstation.Shared.Disease.Components;

/// <summary>
/// A disease effect that executes entity effects.
/// Severity from DiseaseEffectComponent automatically scales the effect strength.
/// </summary>
[RegisterComponent]
public sealed partial class DiseaseEntityEffectComponent : ScalingDiseaseEffect
{
    /// <summary>
    /// The entity effects to execute when this disease effect triggers
    /// </summary>
    [DataField(required: true)]
    public EntityEffect[] Effects = default!;

    /// <summary>
    /// Base quantity to pass to entity effects (gets multiplied by Severity from DiseaseEffectComponent)
    /// </summary>
    [DataField]
    public float BaseQuantity = 1.0f;

    /// <summary>
    /// Additional multiplier on top of severity scaling
    /// Use this to tune how strongly severity affects this particular effect
    /// </summary>
    [DataField]
    public float SeverityMultiplier = 1.0f;

    /// <summary>
    /// Whether to use the effect scale or not, some entity effects do not scale.
    /// </summary>
    [DataField]
    public bool Scale = true;
}
