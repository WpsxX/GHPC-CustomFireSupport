using GHPC;
using GHPC.AI;
using GHPC.Vehicle;
using UnityEngine;

namespace CustomFireSupport
{
    /// <summary>
    /// WHAT AN "AIR TARGET" IS, and the one gate that may never be skipped for one.
    ///
    /// The player's request is that the air-to-ground missile be able to lock onto and attack a
    /// helicopter. The game already has the fall-through for it - CASController.GetIdealAttackType's
    /// TargetShortNameUs.Chopper case reaches CASAttackType.AirToGroundMissile when the sortie carries
    /// no air-to-air missile, no gun and no rockets (CASController.cs:1193-1210) - and the mod's own
    /// flight (CasMissileGuidance) already tracks a moving target every frame. What was missing is
    /// ACQUISITION: a map click supplies a ground point only (MapController.cs:1390-1401,
    /// CASController.SetInterestPoint flattens y to the terrain, :903-908) and CheatTargetUnit is null
    /// on the player path, so a helicopter is only engageable if it happens to fall inside the
    /// aircraft's 45 degree / 1000 m spot cone and out-scores every ground unit. This class holds the
    /// two answers the three call sites (acquisition in CasTargetSpreadPatch, the call-time target in
    /// CasCallReadinessRepair, the flight in CasMissileGuidance / CasMissileAttackRun) all need, so
    /// "air" means exactly one thing in the whole mod:
    ///
    ///   1. <see cref="IsAirUnit"/> - is this unit an aircraft? The game's own two answers are used and
    ///      nothing is invented: the unit's TargetShortNameUs is Chopper or FastMover (that enum IS what
    ///      CASController.GetIdealAttackType switches on), or the unit carries an IAircraft whose
    ///      IsHelicopter is true (HelicopterController.cs:221 - and Unit.Aircraft is the same
    ///      GetComponentInChildren&lt;IAircraft&gt;() reference Unit.Awake already cached, so this is a
    ///      field read, not a hierarchy walk per frame).
    ///
    ///   2. <see cref="IsVisibleFrom"/> - THE VANILLA VISIBILITY TEST, replicated call-for-call from
    ///      CASController.SearchForTarget (:934). This is the single biggest regression risk in the whole
    ///      feature: CASController honours CheatTargetUnit at :959-962 with NO visibility test and NO
    ///      cone test of its own, so a cheat target written without this check makes CAS engage through
    ///      terrain, smoke and trees. Any code path in this mod that is about to make an aircraft treat
    ///      an air unit as its target must pass through here first, from the AIRCRAFT'S CURRENT POSITION
    ///      (which is why the search-time check, not the call-time one, is authoritative - see
    ///      CasTargetSpreadPatch.Prefix).
    /// </summary>
    internal static class CasAirTargets
    {
        /// <summary>
        /// True for an aircraft, by the game's own two classifications. Ground vehicles, infantry,
        /// bunkers and static weapons all answer false, so every behavioural branch behind this test is
        /// unreachable for a ground target - which is what keeps ordinary ground attack unchanged.
        /// </summary>
        internal static bool IsAirUnit(Unit unit)
        {
            if (unit == null)
            {
                return false;
            }

            TargetShortNameUs shortName = unit.ShortNameUs;
            if (shortName == TargetShortNameUs.Chopper || shortName == TargetShortNameUs.FastMover)
            {
                return true;
            }

            // The game's own helicopter answer: Unit.Awake caches GetComponentInChildren<IAircraft>()
            // and HelicopterController (the only helicopter implementation) returns true here.
            IAircraft aircraft = unit.Aircraft;
            return aircraft != null && aircraft.IsHelicopter;
        }

        /// <summary>
        /// The unit a target transform belongs to. The mod's missiles are aimed at `Unit.Center`
        /// (CasPayloadFactory.SetPendingImpact is handed launchTarget.Center, FireSupportPatches.cs:496),
        /// which is a child of the unit, so the parent lookup is the normal path.
        /// </summary>
        internal static Unit UnitOf(Transform target)
        {
            return target == null ? null : target.GetComponentInParent<Unit>();
        }

        /// <summary>
        /// True when this controller is one of the mod's own sorties AND its loadout can actually
        /// deliver CASAttackType.AirToGroundMissile. This is the ownership + capability half of the air
        /// gate; the air-target half is IsAirUnit / IsVisibleFrom. A bomb- or rocket-only slot answers
        /// false here, so nothing in this feature can re-route it.
        ///
        /// The hardpoint manager is the authoritative answer (its _hasAirToGroundMissile flag is set by
        /// the pylons the loadout really mounted) and it is already configured by the time any of the
        /// call sites runs: CasSupportManager.SendCasSupport calls SetLoadout -> DoConfig ->
        /// SetUpHardpoints on the frame the aircraft spawns (:76), before the aircraft's first search.
        /// </summary>
        internal static bool CanDeliverAirToGroundMissile(CASController controller)
        {
            if (controller == null)
            {
                return false;
            }
            CASHardpointManager manager = controller.GetComponentInChildren<CASHardpointManager>(true);
            return manager != null && manager.CanDoAttackType(CASAttackType.AirToGroundMissile);
        }

        /// <summary>
        /// OUR SORTIE + AIR-TO-GROUND MISSILE CAPABILITY. Every new behaviour of this feature sits
        /// behind this one test, so vanilla aircraft, enemy aircraft, campaign CAS and every slot that
        /// flies bombs / rockets / guns are excluded by construction.
        /// </summary>
        internal static bool IsOurMissileSortie(CASController controller)
        {
            return CasFireChainRepair.IsOurSortie(controller) && CanDeliverAirToGroundMissile(controller);
        }

        /// <summary>
        /// THE VISIBILITY GATE, replicated exactly from CASController.SearchForTarget (:926-934):
        ///
        ///     Vector3 vector2 = unit.Center.position - this._transform.position;
        ///     float num  = vector2.magnitude + 50f;
        ///     float num2 = num * num;
        ///     VisibilityManager.IsPositionVisible(null, this._transform.position, unit.Owner,
        ///         unit.Center.position, num, num2, out num3, out unit2,
        ///         grassBlocks: true, terrainCheckOnly: false,
        ///         skipTreesAndSmoke: false, treatTreesAsOpaque: true);
        ///
        /// The same origin (the aircraft's own position), the same target point (the unit's centre), the
        /// same +50 m slack on the distance, and the same four flags - so a target this accepts is one the
        /// vanilla spot scan would have accepted from where the aircraft is standing, and a target it
        /// rejects is one the game itself would not have spotted (terrain, forest, or smoke without
        /// thermals: VisibilityManager.cs:38-152).
        /// </summary>
        internal static bool IsVisibleFrom(CASController controller, Unit unit)
        {
            if (controller == null || unit == null)
            {
                return false;
            }

            Transform centre = unit.Center;
            if (centre == null)
            {
                return false;
            }

            Vector3 origin = controller.transform.position;
            Vector3 target = centre.position;
            float maxDistance = Vector3.Distance(origin, target) + 50f;
            float sqrMaxDistance = maxDistance * maxDistance;

            float obstructionPenalty;
            Unit struck;
            return VisibilityManager.IsPositionVisible(null, origin, unit.Owner, target,
                maxDistance, sqrMaxDistance, out obstructionPenalty, out struck,
                true, false, false, true);
        }
    }
}
