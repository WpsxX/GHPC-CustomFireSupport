using System;
using System.Collections.Generic;
using System.Reflection;
using GHPC.Vehicle;
using HarmonyLib;
using UnityEngine;

namespace CustomFireSupport
{
    /// <summary>
    /// The air-to-ground missile's ATTACK RUN.
    ///
    /// Scope is deliberately narrow, as asked: this touches ONLY the mod's own sorties and ONLY while the
    /// attack type the aircraft is flying is <c>CASAttackType.AirToGroundMissile</c>. Bombs, rockets and gun
    /// runs - and every campaign CAS flight - go through the game's own logic untouched.
    ///
    /// Two things were wrong with a missile call before this:
    ///
    /// ---------------------------------------------------------------------------------------------
    /// 1. THE AIRCRAFT AIMED LIKE IT WAS DROPPING A BOMB
    ///
    /// CASController.GetAimPosition(AmmoType, bool)  (:1373-1420)
    ///
    ///     if (ammo.Guidance != Unguided) return finalTargetPosition;              // guided: straight at it
    ///     ... a fall-of-shot solve through the BALLISTIC COMPUTER ...            // unguided: bomb aim
    ///
    /// The mod fires BOMB data (see CasPayloadFactory.BuildMissileHardpoint), so the ammo is Unguided and the
    /// aircraft flew the missile run with a BOMB's aiming solution - a lofted release point computed for a
    /// gravity weapon. For a missile the aircraft should simply point at the target, which is what the game
    /// itself does for a guided round: the seeker/designator is on the target at release, and the missile
    /// (flown by CasMissileGuidance) does the rest.
    ///
    /// ---------------------------------------------------------------------------------------------
    /// 2. IT FIRED INSIDE THE MISSILE'S MINIMUM RANGE
    ///
    /// CASController.Fire() (:1310-1340) releases as soon as the range is under the loadout's
    /// ReleaseDistance, whatever the geometry is, and then EndAttackRun() (:1343-1358) decrements `passes`.
    /// When the target is very close at the moment of the call the aircraft arrives already inside the
    /// missile's launch envelope, fires anyway, and the round leaves with the target nearly under it - the
    /// "close-range flight and hit are bizarre" case. A real crew in that position does not fire: it flies
    /// out and comes round again.
    ///
    /// The fix is here: block that release, give the run its pass back, and let the aircraft re-attack. The
    /// retry is bounded (two go-arounds) so a target that can never be reached from the required range
    /// still ends the sortie instead of orbiting forever.
    ///
    /// Only the release is gated - the aircraft's approach, the laser run hold (CasLaserRunHold) and the
    /// guidance are unchanged.
    /// </summary>
    internal static class CasMissileAttackRun
    {
        /// <summary>How many times a run may go around because the launch geometry was not there.</summary>
        private const int MaxGoArounds = 2;

        /// <summary>Go-arounds used per controller, so one sortie cannot orbit for ever.</summary>
        private static readonly Dictionary<int, int> _goArounds = new Dictionary<int, int>();

        /// <summary>Controllers still allowed to fire after using their go-arounds (one log line each).</summary>
        private static readonly HashSet<int> _reportedForce = new HashSet<int>();

        private static readonly AccessTools.FieldRef<CASController, bool> LastKnownRef =
            AccessTools.FieldRefAccess<CASController, bool>("_targetIsLastKnownPosition");

        private static readonly AccessTools.FieldRef<CASController, Vector3> TargetPositionRef =
            AccessTools.FieldRefAccess<CASController, Vector3>("_targetPosition");

        /// <summary>
        /// The game's own "leave the battlefield" state, and the private method that enters it. The flight
        /// state enum is private, so its LeaveArea member is looked up by name - the same trick
        /// CasTargetSpreadPatch already uses to reach TurnTowardTarget.
        /// </summary>
        private static readonly MethodInfo EnterStateMethod = AccessTools.Method(typeof(CASController), "EnterState");
        private static readonly object LeaveAreaState = ResolveLeaveAreaState();
        private static readonly HashSet<int> Departed = new HashSet<int>();

        private static object ResolveLeaveAreaState()
        {
            Type state = typeof(CASController).GetNestedType("FlightState",
                BindingFlags.Public | BindingFlags.NonPublic);
            return state != null ? Enum.Parse(state, "LeaveArea") : null;
        }

        /// <summary>
        /// SEARCH, SHOOT, GET OUT. A fire-and-forget missile does not need its carrier any more, so the
        /// aircraft breaks off once the salvo is away and heads for the exit, instead of flying the rest of
        /// the pass over the target. A laser round is deliberately NOT sent home here: its beam only exists
        /// while the carrier keeps its nose on the target, which is what CasLaserRunHold enforces, and the
        /// game enters LeaveArea on its own once that run is finished.
        /// </summary>
        internal static void DepartNow(CASController controller)
        {
            if (controller == null || EnterStateMethod == null || LeaveAreaState == null)
            {
                return;
            }
            if (!Departed.Add(controller.GetInstanceID()))
            {
                return; // already on the way out
            }
            try
            {
                EnterStateMethod.Invoke(controller, new object[] { LeaveAreaState });
            }
            catch (Exception ex)
            {
                Log.Warn("CAS missile: could not order the break-off: " + ex.Message);
            }
        }

        /// <summary>
        /// Seconds after the FIRST missile leaves the rail before the aircraft turns for home. The missiles
        /// leave one pull apart (about 1.5 s), so turning away on the first one would mean never firing the
        /// second: this covers the whole salvo.
        /// </summary>
        private const float SalvoDepartSeconds = 3f;

        private static readonly Dictionary<int, ReturnOrder> PendingDepartures = new Dictionary<int, ReturnOrder>();

        private struct ReturnOrder
        {
            internal CASController Controller;
            internal float NotBefore;
        }

        /// <summary>Arms the break-off once the salvo has had time to leave the rails.</summary>
        internal static void ScheduleDepart(CASController controller)
        {
            if (controller == null)
            {
                return;
            }
            int id = controller.GetInstanceID();
            if (Departed.Contains(id) || PendingDepartures.ContainsKey(id))
            {
                return; // already leaving, or already counting down
            }
            ReturnOrder order;
            order.Controller = controller;
            order.NotBefore = Time.time + SalvoDepartSeconds;
            PendingDepartures[id] = order;
        }

        /// <summary>Called every frame: carries out any break-off whose salvo has finished.</summary>
        internal static void Tick()
        {
            if (PendingDepartures.Count == 0 || Time.timeScale <= 0f)
            {
                return;
            }
            List<int> due = null;
            foreach (KeyValuePair<int, ReturnOrder> entry in PendingDepartures)
            {
                if (Time.time >= entry.Value.NotBefore)
                {
                    if (due == null)
                    {
                        due = new List<int>();
                    }
                    due.Add(entry.Key);
                }
            }
            if (due == null)
            {
                return;
            }
            for (int i = 0; i < due.Count; i++)
            {
                ReturnOrder order;
                if (PendingDepartures.TryGetValue(due[i], out order))
                {
                    PendingDepartures.Remove(due[i]);
                    DepartNow(order.Controller);
                }
            }
        }

        internal static void ResetForScene()
        {
            _goArounds.Clear();
            _reportedForce.Clear();
            Departed.Clear();
            PendingDepartures.Clear();
        }

        /// <summary>
        /// Where the run's target is: the live unit's centre, or the position it was last seen at. Mirrors
        /// CASController.GetFinalTargetPosition() (:1254), which is private.
        /// </summary>
        internal static bool TryGetTargetPosition(CASController controller, out Vector3 position)
        {
            position = Vector3.zero;
            if (controller == null)
            {
                return false;
            }
            if (controller.FinalTarget != null && controller.FinalTarget.Center != null &&
                !LastKnownRef(controller))
            {
                position = controller.FinalTarget.Center.position;
                return true;
            }
            position = TargetPositionRef(controller);
            return position != Vector3.zero;
        }

        /// <summary>
        /// The launch geometry of this moment: horizontal range to the target and the angle between the
        /// aircraft's own nose and the target. `why` is null when the launch is inside the envelope.
        /// </summary>
        internal static bool InLaunchEnvelope(CASController controller,
            CasAirframeCatalog.MissileProfile profile, out float range, out float offAxis, out string why)
        {
            range = -1f;
            offAxis = -1f;
            why = null;

            Vector3 target;
            if (!TryGetTargetPosition(controller, out target))
            {
                return true; // no target to judge: leave the game's own decision alone
            }

            Vector3 toTarget = target - controller.transform.position;
            Vector3 flat = new Vector3(toTarget.x, 0f, toTarget.z);
            range = flat.magnitude;
            offAxis = toTarget.sqrMagnitude > 0.01f ? Vector3.Angle(controller.transform.forward, toTarget) : 0f;

            if (profile == null)
            {
                return true;
            }

            if (range < profile.MinimumLaunchRangeMeters)
            {
                why = "the target is " + range.ToString("0") + " m away and this round needs at least " +
                      profile.MinimumLaunchRangeMeters.ToString("0") + " m to be delivered";
                return false;
            }
            if (offAxis > profile.MaxLaunchOffAxisDegrees)
            {
                why = "the target is " + offAxis.ToString("0") + " deg off the aircraft's nose (limit " +
                      profile.MaxLaunchOffAxisDegrees.ToString("0") + " deg)";
                return false;
            }
            return true;
        }

        /// <summary>
        /// True when the release about to happen should be held back, and books the go-around. Returns false
        /// once the sortie has used its go-arounds (the shot is then taken as it stands).
        /// </summary>
        internal static bool ShouldGoAround(CASController controller, string why, float range)
        {
            int used;
            _goArounds.TryGetValue(controller.GetInstanceID(), out used);
            if (used >= MaxGoArounds)
            {
                if (_reportedForce.Add(controller.GetInstanceID()))
                {
                    Log.Warn("CAS missile attack: '" + controller.gameObject.name + "' has already gone " +
                             "around " + used + " time(s) for this launch and still cannot get the range (" +
                             why + "). Firing anyway - the round will have to turn hard after release.");
                }
                return false;
            }

            _goArounds[controller.GetInstanceID()] = used + 1;
            // passes = 2 makes the game's own EndAttackRun decrement to 1, i.e. orbit for another pass
            // instead of leaving (the mod sets passes = 1 elsewhere so a normal sortie leaves after one).
            controller.passes = 2f;
            return true;
        }

        /// <summary>
        /// The release went ahead on a re-attack: make the following EndAttackRun end the sortie instead of
        /// sending the aircraft round a third time.
        /// </summary>
        internal static void NoteReleasedAfterGoAround(CASController controller)
        {
            if (controller == null)
            {
                return;
            }
            int used;
            if (_goArounds.TryGetValue(controller.GetInstanceID(), out used) && used > 0)
            {
                controller.passes = 1f;
                _goArounds.Remove(controller.GetInstanceID());
            }
        }
    }

    /// <summary>
    /// Holds back an air-to-ground missile release that is outside the round's launch envelope. Bypassed
    /// entirely for every other attack type and for every aircraft that is not one of the mod's sorties.
    ///
    /// The patch names the (CASAttackType) overload explicitly: CASHardpointManager has a second, private
    /// Fire(CASAttackMeta), and leaving the target as just "Fire" makes it ambiguous - PatchCheck fails it and
    /// Harmony cannot bind it.
    /// </summary>
    [HarmonyPatch(typeof(CASHardpointManager), "Fire", new Type[] { typeof(CASAttackType) })]
    internal static class CasMissileReleaseGatePatch
    {
        private static bool Prefix(CASHardpointManager __instance, CASAttackType type)
        {
            try
            {
                if (type != CASAttackType.AirToGroundMissile || __instance == null)
                {
                    return true; // bombs, rockets, gun runs: the game's own logic, untouched
                }

                CASController controller = __instance.GetComponentInParent<CASController>();
                if (controller == null || !CasFireChainRepair.IsOurSortie(controller))
                {
                    return true; // the campaign's own flights keep vanilla behaviour
                }

                AmmoType ammo = __instance.GetAmmoType(type);
                CasAirframeCatalog.MissileProfile profile = CasPayloadFactory.ProfileFor(ammo);
                if (profile == null)
                {
                    return true;
                }

                float range;
                float offAxis;
                string why;
                if (CasMissileAttackRun.InLaunchEnvelope(controller, profile, out range, out offAxis, out why))
                {
                    CasMissileAttackRun.NoteReleasedAfterGoAround(controller);
                    // FIRE AND LEAVE. With a fire-and-forget round the aircraft has nothing left to do here:
                    // it breaks off and leaves the area the instant the missiles are away. (Laser rounds stay
                    // until their beam run is over - see DepartNow.)
                    if (profile.Guidance == CasAirframeCatalog.GuidanceKind.FireAndForget)
                    {
                        CasMissileAttackRun.ScheduleDepart(controller);
                    }
                    return true;
                }

                if (CasMissileAttackRun.ShouldGoAround(controller, why, range))
                {
                    return false; // no release this pass
                }
                return true;
            }
            catch (Exception ex)
            {
                Log.Error("CAS missile release gate failed (the shot goes ahead): " + ex);
                return true;
            }
        }
    }

    /// <summary>
    /// Flies the missile run the way a missile run is flown: the aim point IS the target, not a bomb's
    /// fall-of-shot solution. Only the mod's own missile ammunition is redirected; every other round keeps
    /// the ballistic computer's answer.
    /// </summary>
    [HarmonyPatch(typeof(CASController), "GetAimPosition")]
    internal static class CasMissileAimPatch
    {
        private static void Postfix(CASController __instance, AmmoType ammo, ref Vector3 __result)
        {
            try
            {
                if (ammo == null || !CasPayloadFactory.IsOurMissile(ammo))
                {
                    return;
                }
                Vector3 target;
                if (CasMissileAttackRun.TryGetTargetPosition(__instance, out target))
                {
                    __result = target;
                }
            }
            catch (Exception ex)
            {
                Log.Error("CAS missile aim patch failed (the game's own aim is kept): " + ex);
            }
        }
    }
}



