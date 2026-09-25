using System;
using System.Collections.Generic;
using System.Reflection;
using GHPC.Vehicle;
using HarmonyLib;
using UnityEngine;

namespace CustomFireSupport
{
    /// <summary>
    /// Two flight-behaviour fixes for the mod's own CAS aircraft, both verified against the game's
    /// CASController source.
    ///
    /// ---------------------------------------------------------------------------------------------
    /// FIX 1 - THE AIRCRAFT KEPT SEARCHING AFTER IT ALREADY HAD A TARGET
    ///
    /// CASController.UpdateAI() is called every frame by the AI framework and does this:
    ///
    ///     public void UpdateAI() { _allowVisionUpdate = true; }                       // :386-389
    ///
    /// and the MoveToPoint state consumes that flag:
    ///
    ///     case FlightState.MoveToPoint:
    ///         if (_allowVisionUpdate) { _allowVisionUpdate = false; SearchForTarget(); }   // :789-793
    ///
    /// So for as long as the aircraft is in MoveToPoint it re-runs the whole spotting pass over every
    /// enemy unit, including after FinalTarget has been chosen. The player sees "Searching for targets"
    /// behaviour continue (the plane keeps looking instead of committing) and, worse, the target can be
    /// SWAPPED out from under an attack that is already being set up.
    ///
    /// The fix: once this aircraft has a FinalTarget, decline further search passes. Vanilla's own flow
    /// is untouched for the case that matters (no target yet), and the flag is left alone so nothing
    /// else that reads it changes behaviour.
    ///
    /// ---------------------------------------------------------------------------------------------
    /// FIX 2 - THE AIRCRAFT FLEW ANOTHER PASS INSTEAD OF LEAVING
    ///
    /// EndAttackRun() ends a pass like this:                                             // :1347-1362
    ///
    ///     passes -= 1f;
    ///     if (passes == 0f) { EnterState(FlightState.LeaveArea); return; }
    ///     movePointGoal = orbitPoint;
    ///     EnterState(FlightState.MoveToPoint);          // go around for another pass
    ///
    /// All eight bundled airframe prefabs ship `passes: 0`, so the first decrement makes it -1, the
    /// equality test against 0 never matches, and the aircraft orbits for another pass - forever, since
    /// only an exact 0 stops it. The result is the reported "it attacked once and then kept flying
    /// around" behaviour.
    ///
    /// The fix: set `passes = 1` on the mod's own sorties before the run starts, so the first EndAttackRun
    /// reaches exactly 0 and the aircraft leaves. Only the mod's aircraft are touched (see
    /// CasFireChainRepair.IsOurSortie), so the campaign's own CAS flights keep the game's behaviour.
    /// </summary>
    internal static class CasFlightBehaviourRepair
    {
        /// <summary>Airframes whose remaining passes have been set for this sortie.</summary>
        private static readonly HashSet<int> _passesSet = new HashSet<int>();

        internal static void ResetForScene()
        {
            _passesSet.Clear();
        }

        /// <summary>
        /// True when this controller belongs to one of the mod's sorties. Mirrors the check the rest of
        /// the CAS patches use, so a campaign aircraft is never affected.
        /// </summary>
        private static bool IsOurs(CASController controller)
        {
            return controller != null && CasFireChainRepair.IsOurSortie(controller);
        }

        /// <summary>
        /// FIX 1 + FIX 2, applied on the same method so they cannot interfere.
        ///
        /// FIX 1 (Prefix): refuse a new search pass once this aircraft already has a target. Patching
        /// SearchForTarget rather than UpdateAI is what makes this safe: UpdateAI's flag is left exactly
        /// as vanilla maintains it, and only the duplicate SEARCH is declined. The first search still
        /// runs normally, because FinalTarget is null until it has chosen something.
        ///
        /// FIX 2 (Postfix): give the mod's sorties exactly ONE pass. `passes` is decremented at the END of
        /// each pass, and EndAttackRun leaves the area only when the result is EXACTLY 0 - while all eight
        /// bundled prefabs ship passes = 0, which the decrement turns into -1, so the test never matches
        /// and the aircraft orbits again. Setting it to 1 as the run begins makes that decrement land on
        /// 0. Done here rather than at attack time because this is the start of the aircraft's run.
        /// </summary>
        [HarmonyPatch(typeof(CASController), "SearchForTarget")]
        internal static class CasSearchAndPassPatch
        {
            private static readonly AccessTools.FieldRef<CASController, float> PassesRef =
                AccessTools.FieldRefAccess<CASController, float>("passes");

            private static bool Prefix(CASController __instance)
            {
                try
                {
                    if (!IsOurs(__instance))
                    {
                        return true;   // the campaign's own CAS keeps vanilla behaviour
                    }
                    if (__instance.FinalTarget == null)
                    {
                        return true;   // no target yet: this is the search that matters
                    }

                    // Already committed. Re-running the spotting pass here could only replace the
                    // target the attack is being set up for.
                    return false;
                }
                catch (Exception ex)
                {
                    Log.Error("CAS flight: search gate failed: " + ex);
                    return true;   // never block vanilla on an error
                }
            }

            private static void Postfix(CASController __instance)
            {
                try
                {
                    if (!IsOurs(__instance))
                    {
                        return;
                    }

                    int id = __instance.GetInstanceID();
                    if (!_passesSet.Add(id))
                    {
                        return;   // already set for this aircraft
                    }

                    float before = PassesRef(__instance);
                    PassesRef(__instance) = 1f;

                    Log.Verbose("CAS flight: '" + __instance.gameObject.name + "' passes " + before +
                                " -> 1, so this run ends in LeaveArea instead of another orbit.");
                }
                catch (Exception ex)
                {
                    Log.Error("CAS flight: could not set the single-pass count: " + ex);
                }
            }
        }

        /// <summary>
        /// FIX 2 (belt and braces): the last word on whether the aircraft leaves.
        ///
        /// EndAttackRun is where the leave-or-orbit decision is actually made, so its `passes` is forced
        /// to 1 immediately before the decrement. That makes the method's own `passes == 0f` test take the
        /// LeaveArea branch without this patch having to re-enter any state itself. The check is skipped
        /// when `passes` already equals 1, so the normal path is untouched.
        /// </summary>
        [HarmonyPatch(typeof(CASController), "EndAttackRun")]
        internal static class CasLeaveAfterAttackPatch
        {
            private static readonly AccessTools.FieldRef<CASController, float> PassesRef =
                AccessTools.FieldRefAccess<CASController, float>("passes");

            private static void Prefix(CASController __instance)
            {
                try
                {
                    if (!IsOurs(__instance))
                    {
                        return;   // the campaign's own CAS keeps vanilla behaviour
                    }

                    float before = PassesRef(__instance);
                    if (before != 1f)
                    {
                        PassesRef(__instance) = 1f;
                        Log.Verbose("CAS flight: '" + __instance.gameObject.name + "' passes " + before +
                                    " -> 1 at the end of the attack, so it leaves the area.");
                    }
                }
                catch (Exception ex)
                {
                    Log.Error("CAS flight: could not force the leave-area decision: " + ex);
                }
            }
        }
    }
}
