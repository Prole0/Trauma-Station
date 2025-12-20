// SPDX-FileCopyrightText: 2025 GoobBot <uristmchands@proton.me>
// SPDX-FileCopyrightText: 2025 SolsticeOfTheWinter <solsticeofthewinter@gmail.com>
// SPDX-FileCopyrightText: 2025 TheBorzoiMustConsume <197824988+TheBorzoiMustConsume@users.noreply.github.com>
//
// SPDX-License-Identifier: AGPL-3.0-or-later

using Content.Shared.Humanoid;
using Content.Shared.EntityEffects;
using Content.Shared.Humanoid.Prototypes;
using Robust.Shared.Prototypes;
using Content.Shared.Polymorph;

namespace Content.Goobstation.Shared.EntityEffects.Effects;

public sealed partial class SpeciesChange : EntityEffectBase<SpeciesChange>
{
    [DataField(required: true)]
    public ProtoId<SpeciesPrototype> NewSpecies;

    [DataField]
    public bool Polymorph = true;

    public override string? EntityEffectGuidebookText(IPrototypeManager prototype, IEntitySystemManager entSys)
        => Loc.GetString("reagent-effect-guidebook-change-species", ("species", NewSpecies));
}

public abstract class SharedSpeciesChangeEffectSystem : EntityEffectSystem<HumanoidAppearanceComponent, SpeciesChange>
{
    [Dependency] private readonly SharedHumanoidAppearanceSystem _humanoid = default!;

    protected override void Effect(Entity<HumanoidAppearanceComponent> ent, ref EntityEffectEvent<SpeciesChange> args)
    {
        Change(ent, args.Effect.NewSpecies, args.Effect.Polymorph);
    }

    public void Change(Entity<HumanoidAppearanceComponent> ent, ProtoId<SpeciesPrototype> id, bool polymorph)
    {
        if (polymorph)
            Polymorph(ent, id);
        else
            _humanoid.SetSpecies(ent, id, humanoid: ent.Comp);
    }

    protected virtual void Polymorph(EntityUid target, ProtoId<SpeciesPrototype> id)
    {
    }
}
