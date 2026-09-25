using System;
using System.Collections.Generic;
using System.Reflection;
using GHPC;
using GHPC.UI;
using GHPC.UI.Map;
using GHPC.Weaponry;
using GHPC.Weaponry.CAS;
using GHPC.Weapons;
using HarmonyLib;
using UnityEngine;

namespace CustomFireSupport
{
    /// <summary>
    /// Why a CAS call sometimes produces no aircraft at all.
    ///
    /// One map click runs MapController.TryCallCAS once per entry in <c>TopoMapCamera.MapClick</c>. That
    /// event is a plain C# multicast delegate and the game never de-duplicates it: MapController adds
    /// <c>MapClick += TryCallCAS</c> in InitControlState but its OnDestroy only removes
    /// <c>TryCallFireMission</c>, so the CAS handler leaks and one click dispatches the handler once per
    /// surviving subscriber. Each run re-rolls the slot and spawns its own aircraft.
    ///
    /// HOW THE MOD HANDLES IT: the duplication is NOT intercepted. Two attempts at blocking it (a flag in
    /// TryCallCAS, then a subscription rebuild plus a position-keyed guard on SendCasSupport) both made CAS
    /// stop responding, because TryCallCAS is invoked for every map click in ANY map mode and simply
    /// returns early when the map is not in CAS mode - so a gate cannot tell a no-op invocation from the
    /// real one and ends up suppressing the call that mattered. Both were removed.
    ///
    /// Instead the duplication is made HARMLESS where the divergence actually came from: the airframe draw.
    /// CustomSupportRegistry.TryRerollAirframeForCall gives one map click ONE draw (first draw in a frame
    /// wins, per slot), so two dispatches of the same click send the same aircraft with the same loadout,
    /// while a genuine later click still re-rolls. See that method for the reasoning.
    ///
    /// This file's remaining job is the readiness repair: making the airframe a call is routed to genuinely
    /// ready, using the game's OWN fields (see <see cref="EnsureReadyForCall"/>) instead of bypassing the
    /// readiness gate with SendCasSupport's "yayFreePlane" parameter - the bypass also skips the game's
    /// mission bookkeeping, which is what made an earlier attempt at this break other things.
    ///
    /// Everything is logged: one line per call, naming the state the game's gate will see.
    /// </summary>
    internal static class CasCallReadinessRepair
    {
        private static readonly AccessTools.FieldRef<CasAirframeUnit, int> MissionsRef =
            AccessTools.FieldRefAccess<CasAirframeUnit, int>("_missionsAvailable");
        private static readonly AccessTools.FieldRef<CasAirframeUnit, float> CooldownRef =
            AccessTools.FieldRefAccess<CasAirframeUnit, float>("<RemainingCooldown>k__BackingField");

        /// <summary>Invocations of MapController.TryCallCAS seen in the current frame, per map click.</summary>
        private static int _tryCallCasThisFrame = -1;
        private static int _lastFrame = -1;

        internal static void ResetForScene()
        {
            _tryCallCasThisFrame = -1;
            _lastFrame = -1;
        }


        /// <summary>
        /// Counts this frame's TryCallCAS invocations. More than one means the click handler is subscribed
        /// more than once, which is the double dispatch described in the class comment.
        /// </summary>
        internal static int NoteTryCallCas()
        {
            int frame = Time.frameCount;
            if (frame != _lastFrame)
            {
                _lastFrame = frame;
                _tryCallCasThisFrame = 0;
            }
            return ++_tryCallCasThisFrame;
        }

        /// <summary>
        /// Gives the airframe the readiness the slot's configuration promises, through the game's own
        /// fields, so SendCasSupport's own gate (array[casIndex].IsReady) accepts it for the right reason.
        ///
        /// IsReady is "_missionsAvailable > 0 && RemainingCooldown &lt;= 0". A slot configured for infinite
        /// sorties stores the display value 99, and one configured with CooldownSeconds = 0 wants no
        /// recharge; both can be true in the config and still fail here, because the game lowers them on
        /// its own (ReduceMissionsAvailable on every send, ResetCooldown to the 120 s RechargeTime when the
        /// aircraft returns). When that has happened the call silently returns MapMissionResult.Empty and
        /// no aircraft appears.
        ///
        /// Only the mod's own airframes are touched, and only towards what the slot's config already says.
        /// </summary>
        internal static bool EnsureReadyForCall(CasAirframeUnit airframe, int slotIndex, int configuredMissions,
            float configuredCooldownSeconds, out string reason)
        {
            reason = null;
            if (airframe == null)
            {
                return false;
            }

            int missions = MissionsRef == null ? 1 : MissionsRef(airframe);
            float cooldown = CooldownRef == null ? 0f : CooldownRef(airframe);
            if (missions > 0 && cooldown <= 0f)
            {
                return true; // ready as the game wants it
            }

            int wanted = configuredMissions < 0 ? CustomSlotBuilder.InfiniteMissionsDisplay : configuredMissions;
            float wantedCooldown = configuredCooldownSeconds <= 0f ? 0f : configuredCooldownSeconds;

            reason = "the airframe reported sorties=" + missions + ", cooldown=" + cooldown.ToString("0.#") +
                     "s, so the game's IsReady gate would refuse the call and no aircraft would be sent";
            if (MissionsRef != null && missions <= 0)
            {
                MissionsRef(airframe) = wanted;
            }
            if (CooldownRef != null && cooldown > wantedCooldown)
            {
                CooldownRef(airframe) = wantedCooldown;
            }

            Log.Warn("CAS call: slot " + slotIndex + ": " + reason + ". Restored it to sorties=" + wanted +
                     ", cooldown=" + wantedCooldown.ToString("0.#") + "s (what the slot is configured for) " +
                     "instead of bypassing the gate, so the game's own mission bookkeeping stays intact.");
            return true;
        }


        /// <summary>
        /// Reports what the game's readiness gate is about to see, and counts this frame's TryCallCAS
        /// invocations so a duplicate click dispatch is visible in the log instead of having to be
        /// inferred from the number of re-rolls.
        /// </summary>
        [HarmonyPatch(typeof(CasSupportManager), "SendCasSupport")]
        internal static class SendCasSupportOutcomePatch
        {
            private static int _lastFrame = -1;
            private static int _invocationsThisFrame;

            private static void Prefix(CasSupportManager __instance, Faction unitFaction, int casIndex)
            {
                try
                {
                    int frame = Time.frameCount;
                    if (frame != _lastFrame)
                    {
                        _lastFrame = frame;
                        _invocationsThisFrame = 0;
                    }
                    _invocationsThisFrame++;
                    if (_invocationsThisFrame < 2)
                    {
                        return;
                    }

                    Log.Warn("CAS call: SendCasSupport was called " + _invocationsThisFrame +
                             " times in one frame (one map click). Each call re-rolls the airframe and " +
                             "consumes the airframe's readiness, so only one of them can actually send an " +
                             "aircraft - " + CasCallReadinessRepair.DescribeGate(__instance, unitFaction, casIndex) + ".");
                }
                catch (Exception ex)
                {
                    Log.Error("CAS call: reporting the duplicate sends failed: " + ex);
                }
            }

            private static void Postfix(CasSupportManager __instance, Faction unitFaction, int casIndex,
                ref MapMissionResult __result)
            {
                try
                {
                    string outcome = __result.IsSuccess
                        ? "sent " + (__result.SupportInfo == null ? "(no airframe reported)" : __result.SupportInfo.ToString())
                        : "REFUSED (no aircraft)";
                    Log.Info("CAS call outcome: " + outcome + "; " +
                             CasCallReadinessRepair.DescribeGate(__instance, unitFaction, casIndex) + ".");
                }
                catch (Exception ex)
                {
                    Log.Error("CAS call: reporting the outcome failed: " + ex);
                }
            }
        }

        /// <summary>The state the game's own readiness gate looks at, for one SendCasSupport call.</summary>
        internal static string DescribeGate(CasSupportManager manager, Faction faction, int casIndex)
        {
            try
            {
                CasAirframeUnit[] array = manager == null
                    ? null
                    : (faction == Faction.Blue ? manager.BlueCasAirframes : manager.RedCasAirframes);
                if (array == null)
                {
                    return "the " + faction + " airframe array is null";
                }
                if (casIndex < 0 || casIndex >= array.Length)
                {
                    return "index " + casIndex + " is outside the " + faction + " array (" + array.Length + " entries)";
                }

                CasAirframeUnit unit = array[casIndex];
                if (unit == null)
                {
                    return "index " + casIndex + " holds no airframe";
                }
                int missions = MissionsRef == null ? -1 : MissionsRef(unit);
                float cooldown = CooldownRef == null ? -1f : CooldownRef(unit);
                return "index " + casIndex + " '" + (unit.airframePrefab == null ? "?" : unit.airframePrefab.name) +
                       "': sorties=" + missions + ", cooldown=" + cooldown.ToString("0.#") + "s, IsReady=" +
                       unit.IsReady;
            }
            catch (Exception ex)
            {
                return "the readiness state could not be read (" + ex.GetType().Name + ")";
            }
        }
    }
}
