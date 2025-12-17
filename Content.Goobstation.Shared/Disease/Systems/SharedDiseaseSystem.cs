using System.Diagnostics.CodeAnalysis;
using System.Numerics;
using Content.Goobstation.Shared.Disease.Components;
using Content.Shared.Mobs.Systems;
using Content.Shared.Rejuvenate;
using Robust.Shared.Map;
using Robust.Shared.Network;
using Robust.Shared.Prototypes;
using Robust.Shared.Random;
using Robust.Shared.Timing;

namespace Content.Goobstation.Shared.Disease.Systems;

public abstract partial class SharedDiseaseSystem : EntitySystem
{
    [Dependency] private readonly IGameTiming _timing = default!;
    [Dependency] private readonly INetManager _net = default!;
    [Dependency] private readonly IPrototypeManager _proto = default!;
    [Dependency] protected readonly IRobustRandom _random = default!;
    [Dependency] private readonly MobStateSystem _mobState = default!;
    [Dependency] private readonly SharedTransformSystem _transform = default!;

    private TimeSpan _lastUpdated = TimeSpan.FromSeconds(0);

    protected EntProtoId BaseDisease = "DiseaseBase";

    /// <summary>
    /// The interval between updates of disease and disease effect entities
    /// </summary>
    private readonly TimeSpan _updateInterval = TimeSpan.FromSeconds(0.5f); // update every half-second to not lag the game

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<DiseaseCarrierComponent, MapInitEvent>(OnDiseaseCarrierInit);
        SubscribeLocalEvent<DiseaseCarrierComponent, DiseaseCuredEvent>(OnDiseaseCured);
        SubscribeLocalEvent<DiseaseCarrierComponent, RejuvenateEvent>(OnRejuvenate);

        SubscribeLocalEvent<DiseaseComponent, MapInitEvent>(OnDiseaseInit);
        SubscribeLocalEvent<DiseaseComponent, DiseaseUpdateEvent>(OnUpdateDisease);
        SubscribeLocalEvent<DiseaseComponent, DiseaseCloneEvent>(OnClonedInto);

        InitializeConditions();
        InitializeEffects();
        InitializeImmunity();
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);


        if (_timing.CurTime < _lastUpdated + _updateInterval)
            return;

        _lastUpdated += _updateInterval;

        if (!_timing.IsFirstTimePredicted)
            return;

        var diseaseCarriers = EntityQueryEnumerator<DiseaseCarrierComponent>();
        // so that we can EnsureComp disease carriers while we're looping over them without erroring
        List<Entity<DiseaseCarrierComponent>> carriers = new();
        while (diseaseCarriers.MoveNext(out var uid, out var diseaseCarrier))
        {
            carriers.Add((uid, diseaseCarrier));
        }
        for (var i = 0; i < carriers.Count; i++)
        {
            UpdateDiseases(carriers[i].Owner, carriers[i].Comp);
        }
    }

    private void UpdateDiseases(EntityUid uid, DiseaseCarrierComponent diseaseCarrier)
    {
        // not foreach since it can be cured and deleted from the list while inside the loop
        foreach (var diseaseUid in diseaseCarrier.Diseases)
        {
            var ev = new DiseaseUpdateEvent((uid, diseaseCarrier));
            RaiseLocalEvent(diseaseUid, ev);
        }
    }

    private void OnDiseaseCarrierInit(EntityUid uid, DiseaseCarrierComponent diseaseCarrier, MapInitEvent args)
    {
        foreach (var diseaseId in diseaseCarrier.StartingDiseases)
        {
            TryInfect((uid, diseaseCarrier), diseaseId, out _);
        }
    }

    private void OnDiseaseInit(EntityUid uid, DiseaseComponent disease, MapInitEvent args)
    {
        // check if disease is a preset
        if (disease.StartingEffects.Count == 0)
            return;

        var complexity = 0f;
        foreach (var effectSpecifier in disease.StartingEffects)
        {
            if (TryAdjustEffect((uid, disease), effectSpecifier.Key, out var effect, effectSpecifier.Value))
                complexity += effect.Value.Comp.GetComplexity();
        }
        // disease is a preset so set the complexity
        disease.Complexity = complexity;

        Dirty(uid, disease);
    }

    private void OnDiseaseCured(Entity<DiseaseCarrierComponent> ent, ref DiseaseCuredEvent args)
    {
        TryCure(ent.AsNullable(), args.DiseaseCured);
    }

    private void OnRejuvenate(Entity<DiseaseCarrierComponent> ent, ref RejuvenateEvent args)
    {
        var curing = ent.AsNullable();
        while (ent.Comp.Diseases.Count != 0)
        {
            if (!TryCure(curing, ent.Comp.Diseases[0]))
                break;
        }
    }

    private void OnUpdateDisease(EntityUid uid, DiseaseComponent disease, DiseaseUpdateEvent args)
    {
        var timeDelta = (float)_updateInterval.TotalSeconds;
        var alive = !_mobState.IsDead(args.Ent.Owner) || disease.AffectsDead;

        if (alive && !args.Ent.Comp.EffectImmune)
        {
            foreach (var effectUid in disease.Effects)
            {
                if (!TryComp<DiseaseEffectComponent>(effectUid, out var effect))
                    continue;

                var conditionsEv = new DiseaseCheckConditionsEvent(args.Ent.Owner, (uid, disease), effect);
                RaiseLocalEvent(effectUid, ref conditionsEv);
                if (!conditionsEv.DoEffect)
                    continue;
                var effectEv = new DiseaseEffectEvent(args.Ent.Owner, (uid, disease), effect);
                RaiseLocalEvent(effectUid, effectEv);
            }
        }

        var ev = new GetImmunityEvent((uid, disease));
        // don't even check immunity if we can't affect this disease
        if (CanImmunityAffect(args.Ent.Owner, disease))
            RaiseLocalEvent(args.Ent.Owner, ref ev);

        // infection progression
        if (alive)
            ChangeInfectionProgress(uid, timeDelta * disease.InfectionRate, disease);
        else
            ChangeInfectionProgress(uid, timeDelta * disease.DeadInfectionRate, disease);

        // immunity
        ChangeInfectionProgress(uid, -timeDelta * ev.ImmunityStrength * disease.ImmunityProgress, disease);
        ChangeImmunityProgress(uid, timeDelta * (ev.ImmunityGainRate * disease.ImmunityGainRate), disease);

        if (!(disease.InfectionProgress <= 0f))
            return;
        var curedEv = new DiseaseCuredEvent((uid, disease));
        RaiseLocalEvent(args.Ent.Owner, curedEv);
    }

    private void OnClonedInto(EntityUid uid, DiseaseComponent disease, DiseaseCloneEvent args)
    {
        foreach (var effectUid in args.Source.Comp.Effects)
        {
            if (!TryComp<DiseaseEffectComponent>(effectUid, out var effectComp) || Prototype(effectUid) is not {} proto)
                continue;

            TryAdjustEffect((uid, disease), proto, out _, effectComp.Severity);
        }
        // no idea how to do this better
        disease.InfectionRate = args.Source.Comp.InfectionRate;
        disease.MutationRate = args.Source.Comp.MutationRate;
        disease.ImmunityGainRate = args.Source.Comp.ImmunityGainRate;
        disease.MutationMutationCoefficient = args.Source.Comp.MutationMutationCoefficient;
        disease.ImmunityGainMutationCoefficient = args.Source.Comp.ImmunityGainMutationCoefficient;
        disease.InfectionRateMutationCoefficient = args.Source.Comp.InfectionRateMutationCoefficient;
        disease.ComplexityMutationCoefficient = args.Source.Comp.ComplexityMutationCoefficient;
        disease.SeverityMutationCoefficient = args.Source.Comp.SeverityMutationCoefficient;
        disease.EffectMutationCoefficient = args.Source.Comp.EffectMutationCoefficient;
        disease.GenotypeMutationCoefficient = args.Source.Comp.GenotypeMutationCoefficient;
        disease.Complexity = args.Source.Comp.Complexity;
        disease.Genotype = args.Source.Comp.Genotype;
        disease.CanGainImmunity = args.Source.Comp.CanGainImmunity;
        disease.AffectsDead = args.Source.Comp.AffectsDead;
        disease.DeadInfectionRate = args.Source.Comp.DeadInfectionRate;
        disease.AvailableEffects = args.Source.Comp.AvailableEffects;
        disease.DiseaseType = args.Source.Comp.DiseaseType;
    }

    #region public API

    #region disease

    /// <summary>
    /// Changes infection progress for given disease
    /// </summary>
    public void ChangeInfectionProgress(EntityUid uid, float amount, DiseaseComponent? comp = null)
    {
        if (!Resolve(uid, ref comp))
            return;

        comp.InfectionProgress = Math.Min(comp.InfectionProgress + amount, 1f);
        Dirty(uid, comp);
    }

    /// <summary>
    /// Changes immunity progress for given disease
    /// </summary>
    public void ChangeImmunityProgress(EntityUid uid, float amount, DiseaseComponent? comp = null)
    {
        if (!Resolve(uid, ref comp))
            return;

        comp.ImmunityProgress = Math.Clamp(comp.ImmunityProgress + amount, 0f, 1f);
        Dirty(uid, comp);
    }

    #endregion

    #region disease carriers

    public bool HasAnyDisease(EntityUid uid, DiseaseCarrierComponent? comp = null)
    {
        if (!Resolve(uid, ref comp, false))
            return false;

        return comp.Diseases.Count != 0;
    }

    /// <summary>
    /// Finds a disease of specified genotype, if any
    /// </summary>
    private bool FindDisease(EntityUid uid, int genotype, [NotNullWhen(true)] out EntityUid? disease, DiseaseCarrierComponent? comp = null)
    {
        disease = null;
        if (!Resolve(uid, ref comp, false))
            return false;

        foreach (var diseaseUid in comp.Diseases)
        {
            if (!TryComp<DiseaseComponent>(diseaseUid, out var diseaseComp))
                continue;

            if (genotype != diseaseComp.Genotype)
                continue;
            disease = diseaseUid;
            return true;
        }
        return false;
    }

    /// <summary>
    /// Checks if the entity has a disease of specified genotype
    /// </summary>
    private bool HasDisease(EntityUid uid, int genotype, DiseaseCarrierComponent? comp = null)
    {
        return FindDisease(uid, genotype, out _, comp);
    }

    /// <summary>
    /// Tries to cure the entity of the given disease entity
    /// </summary>
    public bool TryCure(Entity<DiseaseCarrierComponent?> ent, EntityUid disease)
    {
        if (!Resolve(ent, ref ent.Comp))
            return false;

        if (!ent.Comp.Diseases.Remove(disease))
            return false;

        PredictedQueueDel(disease);
        Dirty(ent, ent.Comp);
        return true;
    }

    /// <summary>
    /// Tries to infect the entity with the given disease entity
    /// Does not clone the provided disease entity, use <see cref="TryClone"/> for that
    /// </summary>
    public bool TryInfect(Entity<DiseaseCarrierComponent?> ent, EntityUid disease, bool force = false)
    {
        if (force)
            ent.Comp ??= EnsureComp<DiseaseCarrierComponent>(ent);

        if (!Resolve(ent, ref ent.Comp, false))
            return false;

        if (!TryComp<DiseaseComponent>(disease, out var diseaseComp))
        {
            Log.Error($"Attempted to infect {ToPrettyString(ent)} with disease ToPrettyString{disease}, but it had no DiseaseComponent");
            return false;
        }

        var checkEv = new DiseaseInfectAttemptEvent((disease, diseaseComp));
        RaiseLocalEvent(ent, ref checkEv);
        // check immunity
        if (!force && (HasDisease(ent, diseaseComp.Genotype) || !checkEv.CanInfect))
            return false;

        _transform.SetCoordinates(disease, new EntityCoordinates(ent, Vector2.Zero));
        ent.Comp.Diseases.Add(disease);
        var ev = new DiseaseGainedEvent((disease, diseaseComp));
        RaiseLocalEvent(uid, ev);
        Dirty(ent, ent.Comp);
        return true;
    }

    /// <summary>
    /// Tries to infect the entity with a given disease prototype
    /// </summary>
    public bool TryInfect(Entity<DiseaseCarrierComponent?> ent, EntProtoId diseaseId, [NotNullWhen(true)] out EntityUid? disease, bool force = false)
    {
        disease = null;

        if (force)
            ent.Comp ??= EnsureComp<DiseaseCarrierComponent>(ent);

        if (!Resolve(ent, ref ent.Comp, false))
            return false;

        var spawned = PredictedSpawnAtPosition(diseaseId, new EntityCoordinates(ent, Vector2.Zero));
        if (!TryInfect(ent, spawned, force))
        {
            PredictedDel(spawned);
            return false;
        }
        disease = spawned;
        return true;
    }

    #endregion

    #endregion
}
