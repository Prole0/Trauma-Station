using System.Diagnostics.CodeAnalysis;
using Content.Goobstation.Shared.Disease.Components;
using Content.Shared.Chat;
using Content.Shared.Damage;
using Content.Shared.EntityEffects;
using Content.Shared.Flash;
using Content.Shared.Humanoid;
using Content.Shared.Maps;
using Content.Shared.Movement.Systems;
using Content.Shared.Popups;
using Content.Shared.Random.Helpers;
using Content.Shared.StatusEffectNew;
using Content.Shared.StatusIcon.Components;
using Content.Shared.Stunnable;
using Content.Shared.Weapons.Melee;
using Robust.Shared.Audio.Systems;
using Robust.Shared.Map;
using Robust.Shared.Prototypes;
using Robust.Shared.Random;

namespace Content.Goobstation.Shared.Disease.Systems;

public partial class SharedDiseaseSystem
{
    [Dependency] private readonly EntityLookupSystem _lookup = default!;
    [Dependency] private readonly MovementModStatusSystem _movementMod = default!;
    [Dependency] private readonly SharedAudioSystem _audio = default!;
    [Dependency] private readonly SharedChatSystem _chat = default!;
    [Dependency] private readonly SharedEntityEffectsSystem _effects = default!;
    [Dependency] private readonly SharedFlashSystem _flash = default!;
    [Dependency] private readonly SharedMapSystem _map = default!;
    [Dependency] private readonly SharedMeleeWeaponSystem _melee = default!;
    [Dependency] private readonly SharedPopupSystem _popup = default!;
    [Dependency] private readonly StatusEffectsSystem _status = default!;
    [Dependency] private readonly SharedStunSystem _stun = default!;
    [Dependency] private readonly IMapManager _mapMan = default!;
    [Dependency] private readonly TileSystem _tile = default!;

    public const float MaxEffectSeverity = 1f; // magic numbers are EVIL and BAD

    protected virtual void InitializeEffects()
    {
        SubscribeLocalEvent<DiseaseAudioEffectComponent, DiseaseEffectEvent>(OnAudioEffect);
        SubscribeLocalEvent<DiseaseEmoteEffectComponent, DiseaseEffectEvent>(OnEmoteEffect);
        SubscribeLocalEvent<DiseaseSpreadEffectComponent, DiseaseEffectEvent>(OnDiseaseSpreadEffect);
        SubscribeLocalEvent<DiseaseForceSpreadEffectComponent, DiseaseEffectEvent>(OnDiseaseForceSpreadEffect);
        SubscribeLocalEvent<DiseaseFightImmunityEffectComponent, DiseaseEffectEvent>(OnFightImmunityEffect);
        SubscribeLocalEvent<DiseaseFlashEffectComponent, DiseaseEffectEvent>(OnFlashEffect);
        SubscribeLocalEvent<DiseasePopupEffectComponent, DiseaseEffectEvent>(OnPopupEffect);
        SubscribeLocalEvent<DiseasePryTileEffectComponent, DiseaseEffectEvent>(OnPryTileEffect);
        SubscribeLocalEvent<DiseaseEntityEffectComponent, DiseaseEffectEvent>(OnEntityEffect);
    }

    private void OnAudioEffect(EntityUid uid, DiseaseAudioEffectComponent effect, DiseaseEffectEvent args)
    {
        if (_net.IsClient)
            return;

        var sound = effect.Sound;
        if (effect.SoundFemale != null && TryComp<HumanoidAppearanceComponent>(args.Ent, out var humanoid) && humanoid.Sex == Sex.Female)
            sound = effect.SoundFemale;

        _audio.PlayPvs(sound, args.Ent);
    }

    private void OnEmoteEffect(EntityUid uid, DiseaseEmoteEffectComponent effect, DiseaseEffectEvent args)
    {
        if (effect.WithChat)
            _chat.TryEmoteWithChat(args.Ent, effect.Emote);
        else
            _chat.TryEmoteWithoutChat(args.Ent, effect.Emote);
    }

    private void OnDiseaseSpreadEffect(EntityUid uid, DiseaseSpreadEffectComponent effect, DiseaseEffectEvent args)
    {
        // for gear that makes you less(/more?) infective to others
        var ev = new DiseaseOutgoingSpreadAttemptEvent(
            effect.SpreadParams.Power,
            effect.SpreadParams.Chance,
            effect.SpreadParams.Type
        );
        RaiseLocalEvent(args.Ent, ref ev);

        if (ev.Power < 0 || ev.Chance < 0)
            return;

        var xform = Transform(args.Ent);
        var (selfPos, selfRot) = _transform.GetWorldPositionRotation(xform);

        var targets = _melee.ArcRayCast(selfPos, selfRot, effect.Arc, effect.Range, xform.MapID, args.Ent);

        foreach (var target in targets)
        {
            DoInfectionAttempt(target, args.Disease, ev.Power, ev.Chance * GetScale(args, effect), effect.SpreadParams.Type);
        }
    }

    private void OnDiseaseForceSpreadEffect(EntityUid uid, DiseaseForceSpreadEffectComponent effect, DiseaseEffectEvent args)
    {
        var transform = _transform.GetMapCoordinates(args.Ent);
        var targets = _lookup.GetEntitiesInRange<DamageableComponent>(transform, effect.Range);

        foreach (var target in targets)
        {
            if (!_random.Prob(effect.Chance * GetScale(args, effect)))
                continue;
            if (HasDisease(target.Owner, args.Disease.Comp.Genotype))
                continue;

            var newDisease = TryClone(args.Disease);
            if (newDisease == null)
                continue;

            MutateDisease(newDisease.Value);
            if (!TryInfect(target.Owner, newDisease.Value, null, true))
                QueueDel(newDisease);
            else if (effect.AddIcon)
                EnsureComp<StatusIconComponent>(target.Owner);
        }
    }

    private void OnFightImmunityEffect(EntityUid uid, DiseaseFightImmunityEffectComponent effect, DiseaseEffectEvent args)
    {
        ChangeImmunityProgress(args.Disease.Owner, effect.Amount * GetScale(args, effect), args.Disease.Comp);
    }

    private void OnFlashEffect(EntityUid uid, DiseaseFlashEffectComponent effect, DiseaseEffectEvent args)
    {
        if (_net.IsClient) // flashes twice if ran on both server and client
            return;

        var scale = GetScale(args, effect);
        var duration = effect.Duration * scale;
        _status.TryAddStatusEffect(args.Ent, _flash.FlashedKey, out _, duration);
        _movementMod.TryUpdateMovementSpeedModDuration(args.Ent, MovementModStatusSystem.FlashSlowdown, duration, effect.SlowTo);

        if (effect.StunDuration is {} stun)
            _stun.TryUpdateKnockdownDuration(args.Ent, stun * scale);
    }

    private void OnPopupEffect(EntityUid uid, DiseasePopupEffectComponent effect, DiseaseEffectEvent args)
    {
        if (_net.IsClient)
            return;

        if (effect.HostOnly)
            _popup.PopupEntity(Loc.GetString(effect.String, ("source", args.Ent)), args.Ent, args.Ent, effect.Type);
        else
            _popup.PopupEntity(Loc.GetString(effect.String, ("source", args.Ent)), args.Ent, effect.Type);
    }

    private void OnPryTileEffect(EntityUid uid, DiseasePryTileEffectComponent effect, DiseaseEffectEvent args)
    {
        if (_net.IsClient)
            return;

        var xform = Transform(args.Ent);
        var mapPos = _transform.GetMapCoordinates(xform);
        if (_mapMan.TryFindGridAt(mapPos, out var gridUid, out var grid))
        {
            for (int i = 0; i < effect.Attempts; i++)
            {
                var distance = effect.Range * MathF.Sqrt(_random.NextFloat());
                var tileCoordinates = mapPos.Offset(_random.NextAngle().ToVec() * distance);
                var tile = _map.GetTileRef((gridUid, grid), tileCoordinates);
                if (_tile.DeconstructTile(tile))
                    break;
            }
        }
    }

    private void OnEntityEffect(Entity<DiseaseEntityEffectComponent> ent, ref DiseaseEffectEvent args)
    {
        var scale = ent.Comp.Scale ? GetScale(args, ent) : 1f;
        _effects.TryApplyEffects(args.Ent, ent.Comp.Effects, scale);
    }

    protected float GetScale(DiseaseEffectEvent args, ScalingDiseaseEffect effect)
    {
        return (effect.SeverityScale ? args.Comp.Severity : 1f)
            * (effect.TimeScale ? (float)_updateInterval.TotalSeconds : 1f)
            * (effect.ProgressScale ? args.Disease.Comp.InfectionProgress : 1f);
    }

    private Entity<DiseaseEffectComponent>? RemoveRandomEffect(EntityUid uid, DiseaseComponent disease)
    {
        if (disease.Effects.Count < 1)
        {
            Log.Error($"Disease {ToPrettyString(uid)} attempted to remove a random effect, but had no effects left.");
            return null;
        }
        var index = _random.Next(disease.Effects.Count - 1);
        var effectUid = disease.Effects[index];
        disease.Effects.RemoveAt(index);

        Dirty(uid, disease);
        return TryComp<DiseaseEffectComponent>(effectUid, out var comp) ? (effectUid, comp) : null;
    }

    private Entity<DiseaseEffectComponent>? AddRandomEffect(EntityUid uid, DiseaseComponent disease)
    {
        if (!_proto.TryIndex(disease.AvailableEffects, out var effects))
        {
            Log.Error($"Disease {ToPrettyString(uid)} attempted to mutate to add an effect, but there are no valid effects for its type.");
            return null;
        }

        var weights = new Dictionary<string, float>(effects.Weights);
        foreach (var diseaseEffect in disease.Effects) // no rolling effects we have
        {
            if (Prototype(diseaseEffect) is {} proto)
                weights.Remove(proto.ID);
        }

        if (weights.Count == 0)
        {
            Log.Error($"Disease {ToPrettyString(uid)} attempted to mutate to add an effect, but it has all available effects.");
            return null;
        }

        var protoId = new EntProtoId(_random.Pick(weights));
        var proto = _proto.Index(protoId);
        Entity<DiseaseEffectComponent>? effect = null;
        if (proto.TryGetComponent<DiseaseEffectComponent>(out var effectComp, Factory))
            TryAdjustEffect((uid, disease), proto, out effect, _random.NextFloat(effectComp.MinSeverity, 1f));

        Dirty(uid, disease);
        return effect;
    }

    #region public API

    /// <summary>
    /// Finds an effect of specified prototype, if any
    /// </summary>
    public Entity<DiseaseEffectComponent>? FindEffect(Entity<DiseaseComponent?> ent, EntProtoId effectId)
    {
        if (!Resolve(ent, ref ent.Comp))
            return null;

        var effectProto = _proto.Index(effectId);
        foreach (var effectUid in ent.Comp.Effects)
        {
            if (effectProto != Prototype(effectUid))
                continue;

            if (!TryComp<DiseaseEffectComponent>(effectUid, out var diseaseEffect))
            {
                Log.Error($"Found disease effect {ToPrettyString(effectUid)} without DiseaseEffectComponent");
                return null;
            }

            return (effectUid, diseaseEffect);
        }

        return null;
    }

    /// <summary>
    /// Checks if the disease has an effect of specified prototype
    /// </summary>
    public bool HasEffect(EntityUid uid, EntProtoId effectId, DiseaseComponent? comp = null)
        => FindEffect(uid, effectId, comp) == null;

    /// <summary>
    /// Removes the specified disease effect from this disease
    /// </summary>
    public bool TryRemoveEffect(EntityUid uid, EntityUid effect, DiseaseComponent? comp = null)
    {
        if (!Resolve(uid, ref comp))
            return false;

        if (!comp.Effects.Remove(effect))
            return false;

        PredictedQueueDel(effect);

        Dirty(uid, comp);
        return true;
    }

    /// <summary>
    /// Removes the disease effect of specified prototype from this disease
    /// </summary>
    public bool TryRemoveEffect(Entity<DiseaseComponent?> ent, EntProtoId effectId)
    {
        if (!Resolve(ent, ref ent.Comp) || FindEffect(ent) is not {} effect)
            return false;

        return TryRemoveEffect(ent, effect);
    }

    /// <summary>
    /// Removes the specified disease effect from this disease
    /// </summary>
    public bool TryAddEffect(EntityUid uid, EntityUid effectUid, [NotNullWhen(true)] out Entity<DiseaseEffectComponent>? effect, DiseaseComponent? comp = null)
    {
        effect = null;
        if (!Resolve(uid, ref comp, false))
            return false;

        if (!TryComp<DiseaseEffectComponent>(effectUid, out var diseaseEffect))
        {
            Log.Error($"Tried to add disease effect {ToPrettyString(effect)}, but it had no DiseaseEffectComponent");
            return false;
        }
        effect = (effectUid, diseaseEffect);
        comp.Effects.Add(effectUid);

        Dirty(uid, comp);
        return true;
    }

    /// <summary>
    /// Adds an effect of given prototype to the specified disease
    /// </summary>
    public bool TryAddEffect(Entity<DiseaseComponent?> ent, EntProtoId effectId, [NotNullWhen(true)] out Entity<DiseaseEffectComponent>? effect)
    {
        effect = null;
        if (!Resolve(ent, ref ent.Comp) || HasEffect(ent, effectId))
            return false;

        var effectUid = PredictedSpawnAtPosition(effectId, new EntityCoordinates(ent, Vector2.Zero));
        if (TryAddEffect(ent, effectUid, out effect))
            return true;

        PredictedDel(effectUid);
        return false;
    }

    /// <summary>
    /// Tries to adjust the strength of the effect of given prototype, creating or removing it as needed
    /// Non-present effects are assumed to have severity 0 regardless of the prototype's specified severity
    /// </summary>
    public bool TryAdjustEffect(Entity<DiseaseComponent>? ent, EntProtoId effectId, [NotNullWhen(true)] out Entity<DiseaseEffectComponent>? effect, float delta)
    {
        effect = null;
        if (!Resolve(ent, ref ent.Comp))
            return false;

        bool spawned = false;
        FindEffect(ent, effectId, out effect);
        if (effect == null)
        {
            spawned = true;
            if (!TryAddEffect(ent, effectId, out effect))
                return false;
        }

        if (spawned)
            effect.Value.Comp.Severity = 0f;

        effect.Value.Comp.Severity += delta;
        if (effect.Value.Comp.Severity <= 0f)
        {
            if (!TryRemoveEffect(ent, effect.Value.Owner))
                return false;
        }

        Dirty(effect.Value);
        Dirty(uid, comp);
        return true;
    }

    #endregion
}
