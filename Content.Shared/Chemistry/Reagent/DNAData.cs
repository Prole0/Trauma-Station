using Robust.Shared.Serialization;

namespace Content.Shared.Chemistry.Reagent;

[ImplicitDataDefinitionForInheritors, Serializable, NetSerializable]
public sealed partial class DnaData : ReagentData
{
    [DataField]
    public string DNA = string.Empty;

    /// <summary>
    /// Goobstation - time this DNA was taken at, shown by forensic scanner.
    /// </summary>
    [DataField]
    public TimeSpan Freshness = TimeSpan.Zero;

    public override ReagentData Clone()
    {
        return new DnaData
        {
            DNA = DNA,
            Freshness = Freshness, // Goob
        };
    }

    public override bool Equals(ReagentData? other)
    {
        // <Goob> - only cast once here, check freshness below
        if (other is not DnaData data)
            return false;

        return data.DNA == DNA && data.Freshness == Freshness;
        // </Goob>
    }

    public override int GetHashCode()
    {
        return HashCode.Combine(DNA, Freshness); // Goob - combine Freshness
    }
}
