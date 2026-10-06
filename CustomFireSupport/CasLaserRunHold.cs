using System;
using System.Collections.Generic;
using System.Reflection;
using GHPC.Vehicle;
using HarmonyLib;
using UnityEngine;

namespace CustomFireSupport
{
    /// <summary>
    /// Keeps the launching aircraft on its run for as long as one of its LASER-GUIDED missiles still needs
    /// the beam. This is the carrier half of the Kh-25's defining trait - the brief's "发射后飞行员绝对不能
    /// 大幅机动 ... 载机必须保持机头大致对准目标（通常偏角不能超过30度~35度）" - and without it the weapon
    /// could not work at all: the game's own CAS run ends (and the aircraft banks away to its exfil goal)
    /// about two seconds after the shot, while a Kh-25 released at 2.6 km needs almost seven seconds to
    /// arrive. The missile's guidance (CasMissileGuidance) is what enforces the 35 degree limit; this is
    /// what stops the AI aircraft from breaking it by accident.
    ///
    /// WHY IT IS A PATCH AND NOT A TIMING TWEAK. The obvious way to hold the run is to make the missile
    /// attack's own firing duration as long as the flight - the game's Fire coroutine waits out
    /// `_pendingAttackDuration + postDelay` while staying in FlightState.FiringWeapons, and that state keeps
    /// aiming at the target. But the same figure is fed to the aim computer as
    /// (`_releaseDistance - _pendingAttackDuration * TrueAirSpeed * 0.7`), i.e. as a RANGE, so a six second
    /// duration moves the aim point 840 m and points the nose at the wrong elevation - and the nose is the
    /// laser. So the run is held the other way round: EndAttackRun - the one method that ends the pass - is
    /// declined while the beam is needed, and invoked again once it is not.
    ///
    /// State per aircraft: Holding (the missile is still in the air) -> Released (the missile is done, or the
    /// hold's cap expired) -> the run is ended for real, and vanilla takes it from there (the aircraft leaves
    /// the area, as CasFlightBehaviourRepair's single-pass fix intends).
    ///
    /// Only our own sorties are ever touched.
    /// </summary>
    internal static class CasLaserRunHold
    {
        /// <summary>Seconds of flight time allowed per metre of slant range, plus a margin, when sizing a hold.</summary>
        private const float FlightTimeMargin = 1.35f;
        private const float HoldExtraSeconds = 0.8f;

        /// <summary>A hold shorter than this is pointless (the run already lasts that long).</summary>
        private const float MinimumHoldSeconds = 2.5f;

        private sealed class Hold
        {
            internal Transform Carrier;
            internal float Until;
            internal bool Released;
            internal string Missile;
        }

        private static readonly Dictionary<int, Hold> _holds = new Dictionary<int, Hold>();

        internal static void ResetForScene()
        {
            _holds.Clear();
        }

        /// <summary>
        /// A laser-guided missile has just left one of our aircraft: keep that aircraft on its run until the
        /// round is done with it. `slantRange` is the range to the impact point at launch, which is what the
        /// flight time is estimated from.
        /// </summary>
        internal static void Begin(CASController controller, CasAirframeCatalog.MissileProfile profile,
            float slantRange)
        {
            if (controller == null || profile == null ||
                profile.Guidance != CasAirframeCatalog.GuidanceKind.LaserBeamRider)
            {
                return;   // a fire-and-forget round frees the aircraft at the rail
            }

            try
            {
                float speed = Mathf.Max(1f, profile.CruiseSpeedMeters);
                float seconds = Mathf.Clamp(slantRange / speed * FlightTimeMargin + HoldExtraSeconds,
                    MinimumHoldSeconds, Mathf.Max(MinimumHoldSeconds, profile.CarrierHoldMaxSeconds));

                int id = controller.transform.GetInstanceID();
                Hold hold;
                if (!_holds.TryGetValue(id, out hold))
                {
                    hold = new Hold();
                    _holds[id] = hold;
                }
                hold.Carrier = controller.transform;
                hold.Until = Mathf.Max(hold.Until, Time.time + seconds);
                hold.Released = false;
                hold.Missile = profile.MissileId;
            }
            catch (Exception ex)
            {
                Log.Error("CAS laser run: could not start the hold: " + ex);
            }
        }

        /// <summary>
        /// The missile no longer needs the carrier (it hit, or it lost the beam). Called by
        /// CasMissileGuidance on hand-over, on a lost beam and on the round's destruction.
        /// </summary>
        internal static void End(Transform carrier)
        {
            if (carrier == null)
            {
                return;
            }
            Hold hold;
            if (_holds.TryGetValue(carrier.GetInstanceID(), out hold))
            {
                hold.Released = true;
            }
        }

        /// <summary>True while this aircraft must stay pointed at the target for its missile.</summary>
        private static bool IsHolding(CASController controller)
        {
            if (controller == null)
            {
                return false;
            }
            Hold hold;
            if (!_holds.TryGetValue(controller.transform.GetInstanceID(), out hold))
            {
                return false;
            }
            if (hold.Released)
            {
                return false;
            }
            if (Time.time > hold.Until)
            {
                // The round is late (or never arrived): the carrier stops being a laser designator and the
                // run ends normally. The missile's own guidance takes it from here.
                hold.Released = true;
                Log.Warn("CAS laser run: the hold for '" + controller.gameObject.name + "' hit its " +
                         "cap, so the aircraft resumes its own flight; the " + hold.Missile +
                         " fires on what it can still see.");
                return false;
            }
            return true;
        }

        /// <summary>The game's own method for ending the pass, which is what releases the aircraft.</summary>
        private static readonly MethodInfo EndAttackRunMethod =
            AccessTools.Method(typeof(CASController), "EndAttackRun");

        /// <summary>
        /// The run may not end while the beam is needed. Declining the original leaves the aircraft in
        /// FlightState.FiringWeapons, whose own state handler keeps re-aiming it at the target
        /// (`movePointGoal = GetAimPosition(...)` every 0.2 s), so the nose stays on the spot.
        /// </summary>
        [HarmonyPatch(typeof(CASController), "EndAttackRun")]
        internal static class CasHoldTheRunPatch
        {
            private static bool Prefix(CASController __instance)
            {
                try
                {
                    if (__instance == null || !CasFireChainRepair.IsOurSortie(__instance) ||
                        !IsHolding(__instance))
                    {
                        return true;   // not ours, or nothing is riding a beam: vanilla ends the pass
                    }
                    return false;
                }
                catch (Exception ex)
                {
                    Log.Error("CAS laser run: hold check failed: " + ex);
                    return true;   // never trap an aircraft because of an error
                }
            }
        }

        /// <summary>
        /// The other half: once the beam is no longer needed, the run has to be ended for real. Nothing else
        /// will call EndAttackRun again - the game's Fire coroutine already did, and its call was declined.
        ///
        /// The hold's own entry is removed here on the first pass, which is what makes this a one-shot: the
        /// aircraft cannot be sent to LeaveArea twice, and a hold cannot be released twice.
        /// </summary>
        [HarmonyPatch(typeof(CASController), "Update")]
        internal static class CasReleaseTheRunPatch
        {
            private static void Postfix(CASController __instance)
            {
                try
                {
                    if (__instance == null || !CasFireChainRepair.IsOurSortie(__instance))
                    {
                        return;
                    }

                    Hold hold;
                    int id = __instance.transform.GetInstanceID();
                    if (!_holds.TryGetValue(id, out hold))
                    {
                        return;
                    }

                    // Due either because the missile is done with the aircraft (the guidance said so) or
                    // because the hold's cap has passed - which is the only way out if the round was never
                    // heard from again. Either way the cap is checked HERE as well as in IsHolding, because
                    // nothing calls IsHolding once the aircraft is already sitting in its firing state.
                    if (!hold.Released && Time.time <= hold.Until)
                    {
                        return;
                    }
                    _holds.Remove(id);

                    if (EndAttackRunMethod == null)
                    {
                        return;
                    }
                    if (__instance.FinalTarget == null)
                    {
                        // It is already off its attack run (the game ended it some other way): nothing to do,
                        // and calling EndAttackRun again would only re-enter LeaveArea.
                        return;
                    }
                    EndAttackRunMethod.Invoke(__instance, null);
                }
                catch (Exception ex)
                {
                    Log.Error("CAS laser run: releasing the aircraft failed: " + ex);
                }
            }
        }
    }
}

