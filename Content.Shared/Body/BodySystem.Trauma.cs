using Content.Medical.Common.Body;
using Content.Medical.Common.Targeting;
using Content.Shared.Body;
using Robust.Shared.Prototypes;

namespace Content.Shared.Body;

/// <summary>
/// Trauma - organ enabled logic, extra helpers for working with body stuff
/// </summary>
public sealed partial class BodySystem
{
    [Dependency] private readonly CommonBodyCacheSystem _cache = default!;

    /// <summary>
    /// Body parts' organ categories.
    /// </summary>
    public static readonly ProtoId<OrganCategoryPrototype>[] BodyParts =
    [
        "Head",
        "Torso",
        "ArmLeft",
        "LegLeft",
        "HandLeft",
        "FootLeft",
        "ArmRight",
        "LegRight",
        "HandRight",
        "FootRight"
    ];

    /// <summary>
    /// Vital body parts' organ categories.
    /// </summary>
    public static readonly ProtoId<OrganCategoryPrototype>[] VitalParts =
    [
        "Head",
        "Torso"
    ];
    // TODO: vital internal organs???

    /// <summary>
    /// Tries to enable a given organ, letting systems run logic.
    /// Returns true if it is valid and now enabled.
    /// </summary>
    public bool EnableOrgan(Entity<OrganComponent?> organ)
    {
        if (!_organQuery.Resolve(organ, ref organ.Comp) || organ.Comp.Body is not {} body)
            return false;

        if (HasComp<EnabledOrganComponent>(organ))
            return true; // already enabled

        var attemptEv = new OrganEnableAttemptEvent(body);
        RaiseLocalEvent(organ, ref attemptEv);
        if (attemptEv.Cancelled)
            return false;

        EnsureComp<EnabledOrganComponent>(organ);

        // let other systems do their logic now
        var ev = new OrganEnabledEvent(body);
        RaiseLocalEvent(organ, ref ev);
        return true;
    }

    /// <summary>
    /// Disabled a given organ, letting systems run logic.
    /// Returns true if it is valid and now disabled.
    /// </summary>
    public bool DisableOrgan(Entity<OrganComponent?> organ)
    {
        if (!_organQuery.Resolve(organ, ref organ.Comp) || organ.Comp.Body is not {} body)
            return false;

        if (!TryComp<EnabledOrganComponent>(organ, out var enabled))
            return true; // already disabled

        // no attempt event that wouldn't make any sense

        RemComp(organ, enabled);

        var ev = new OrganDisabledEvent(body);
        RaiseLocalEvent(organ, ref ev);
        return true;
    }

    /// <summary>
    /// Non-dogshit version of TryGetOrgansWithComponent
    /// </summary>
    public List<Entity<T>> GetOrgans<T>(Entity<BodyComponent?> body, bool logMissing = true) where T : Component
    {
        if (!_bodyQuery.Resolve(body, ref body.Comp, logMissing))
            return [];

        TryGetOrgansWithComponent<T>(body, out var organs);
        return organs;
    }

    /// <summary>
    /// Get all organs in a body, both internal and external.
    /// </summary>
    public List<Entity<OrganComponent>> GetOrgans(Entity<BodyComponent?> body, bool logMissing = true)
        => GetOrgans<OrganComponent>(body, logMissing);

    /// <summary>
    /// Get all internal organs in a body.
    /// </summary>
    public List<Entity<InternalOrganComponent>> GetInternalOrgans(Entity<BodyComponent?> body, bool logMissing = true)
        => GetOrgans<InternalOrganComponent>(body, logMissing);

    /// <summary>
    /// Get all external organs in a body.
    /// </summary>
    public List<Entity<OrganComponent>> GetExternalOrgans(Entity<BodyComponent?> body, bool logMissing = true)
    {
        var organs = GetOrgans(body, logMissing);
        organs.RemoveAll(organ => HasComp<InternalOrganComponent>(organ));
        return organs;
    }

    /// <summary>
    /// Get a list of vital bodyparts, which contribute to vital damage.
    /// </summary>
    public List<Entity<OrganComponent>> GetVitalParts(EntityUid body)
    {
        // doing this to use cache instead of looping every organ and checking them
        var vital = new List<Entity<OrganComponent>>(VitalParts.Length);
        foreach (var category in VitalParts)
        {
            if (_cache.GetOrgan(body, category) is {} organ)
                vital.Add(organ);
        }

        return vital;
    }

    /// <summary>
    /// Gets the fraction of bodyparts that are vital.
    /// For a torso or torso+head this is 1, for invalid bodies this is 0.
    /// </summary>
    public float GetVitalBodyPartRatio(Entity<BodyComponent?> body)
    {
        if (!_bodyQuery.Resolve(body, ref body.Comp) || body.Comp.Organs?.ContainedEntities is not {} organs)
            return 0f;

        // TODO SHITMED: change vital to just be a bool on OrganCategoryPrototype
        int total = 0;
        int vital = 0;
        foreach (var organ in organs)
        {
            if (GetCategory(organ) is not {} category || !BodyParts.Contains(category))
                continue;

            total++;
            if (VitalParts.Contains(category))
                vital++;
        }

        return total == 0
            ? 0f // no dividing by zero incase a body somehow has no parts?!
            : (float) vital / total;
    }

    /// <summary>
    /// Converts Enums from BodyPartType to their Targeting system equivalent.
    /// </summary>
    public TargetBodyPart GetTargetBodyPart(BodyPartType type, BodyPartSymmetry symmetry)
    {
        return (type, symmetry) switch
        {
            (BodyPartType.Head, _) => TargetBodyPart.Head,
            (BodyPartType.Torso, _) => TargetBodyPart.Chest,
            (BodyPartType.Arm, BodyPartSymmetry.Left) => TargetBodyPart.LeftArm,
            (BodyPartType.Arm, BodyPartSymmetry.Right) => TargetBodyPart.RightArm,
            (BodyPartType.Hand, BodyPartSymmetry.Left) => TargetBodyPart.LeftHand,
            (BodyPartType.Hand, BodyPartSymmetry.Right) => TargetBodyPart.RightHand,
            (BodyPartType.Leg, BodyPartSymmetry.Left) => TargetBodyPart.LeftLeg,
            (BodyPartType.Leg, BodyPartSymmetry.Right) => TargetBodyPart.RightLeg,
            (BodyPartType.Foot, BodyPartSymmetry.Left) => TargetBodyPart.LeftFoot,
            (BodyPartType.Foot, BodyPartSymmetry.Right) => TargetBodyPart.RightFoot,
            _ => TargetBodyPart.Chest,
        };
    }

    /// <summary>
    /// Converts Enums from Targeting system to their BodyPartType equivalent.
    /// </summary>
    public (BodyPartType Type, BodyPartSymmetry Symmetry) ConvertTargetBodyPart(TargetBodyPart? targetPart)
    {
        return targetPart switch
        {
            TargetBodyPart.Head => (BodyPartType.Head, BodyPartSymmetry.None),
            TargetBodyPart.Chest => (BodyPartType.Torso, BodyPartSymmetry.None),
            TargetBodyPart.Groin => (BodyPartType.Torso, BodyPartSymmetry.None),
            TargetBodyPart.LeftArm => (BodyPartType.Arm, BodyPartSymmetry.Left),
            TargetBodyPart.LeftHand => (BodyPartType.Hand, BodyPartSymmetry.Left),
            TargetBodyPart.RightArm => (BodyPartType.Arm, BodyPartSymmetry.Right),
            TargetBodyPart.RightHand => (BodyPartType.Hand, BodyPartSymmetry.Right),
            TargetBodyPart.LeftLeg => (BodyPartType.Leg, BodyPartSymmetry.Left),
            TargetBodyPart.LeftFoot => (BodyPartType.Foot, BodyPartSymmetry.Left),
            TargetBodyPart.RightLeg => (BodyPartType.Leg, BodyPartSymmetry.Right),
            TargetBodyPart.RightFoot => (BodyPartType.Foot, BodyPartSymmetry.Right),
            _ => (BodyPartType.Torso, BodyPartSymmetry.None)
        };
    }

    /// <summary>
    /// Returns an entity's organ category, or null if it isn't an organ.
    /// </summary>
    public ProtoId<OrganCategoryPrototype>? GetCategory(Entity<OrganComponent?> organ)
        => _organQuery.Resolve(organ, ref organ.Comp) ? organ.Comp.Category : null;

    /// <summary>
    /// Gets an organ in a certain slot of the body, or null if it's missing.
    /// Helper to use body cache system.
    /// </summary>
    public EntityUid? GetOrgan(EntityUid body, ProtoId<OrganCategoryPrototype> category)
        => _cache.GetOrgan(body, category);

    /// <summary>
    /// Gets the body of an organ, returning null if it isn't an organ or is detached.
    /// </summary>
    public EntityUid? GetBody(EntityUid organ)
        => _organQuery.CompOrNull(organ)?.Body;

    /// <summary>
    /// Tries to insert an organ into a body.
    /// Returns true if it is now in the body.
    /// </summary>
    public bool InsertOrgan(Entity<BodyComponent?> body, Entity<OrganComponent?> organ)
    {
        if (!_bodyQuery.Resolve(body, ref body.Comp) ||
            !_organQuery.Resolve(organ, ref organ.Comp) ||
            body.Comp.Organs is not {} container)
            return false;

        if (container.Contains(organ))
            return true; // it was already inserted

        var ev = new OrganInsertAttemptEvent(body, organ);
        RaiseLocalEvent(body, ref ev);
        if (!ev.Cancelled)
            RaiseLocalEvent(organ, ref ev);
        if (ev.Cancelled)
            return false;

        return _container.Insert(organ.Owner, container);
    }

    /// <summary>
    /// Tries to remove an organ from a body.
    /// Returns true if it is no longer in the body.
    /// </summary>
    public bool RemoveOrgan(Entity<BodyComponent?> body, Entity<OrganComponent?> organ)
    {
        if (!_bodyQuery.Resolve(body, ref body.Comp) ||
            !_organQuery.Resolve(organ, ref organ.Comp) ||
            body.Comp.Organs is not {} container)
            return false;

        if (!container.Contains(organ))
            return true; // it was already removed

        var ev = new OrganRemoveAttemptEvent(body, organ);
        RaiseLocalEvent(body, ref ev);
        if (!ev.Cancelled)
            RaiseLocalEvent(organ, ref ev);
        if (ev.Cancelled)
            return false;

        return _container.Remove(organ.Owner, container);
    }

    public bool ReplaceOrgan(Entity<BodyComponent?> body, Entity<OrganComponent?> organ)
    {
        if (!_bodyQuery.Resolve(body, ref body.Comp) ||
            !_organQuery.Resolve(organ, ref organ.Comp) ||
            organ.Comp.Category is not {} category)
            return false;

        // if an organ is already there try to remove it
        // it will be dropped on the floor
        if (GetOrgan(body, category) is {} old && !RemoveOrgan(body, old))
            return false;

        return InsertOrgan(body, organ);
    }

    /// <summary>
    /// Tries to decapitate a mob, returning true if it succeeded.
    /// </summary>
    public bool TryDecapitate(EntityUid uid, EntityUid? user = null)
    {
        var ev = new DecapitateEvent(user);
        RaiseLocalEvent(uid, ref ev);
        return ev.Handled;
    }
}
