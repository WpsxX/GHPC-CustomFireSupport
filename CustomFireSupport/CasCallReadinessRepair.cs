using System;
using System.Collections.Generic;
using System.Reflection;
using GHPC;
using GHPC.Utility;
using GHPC.Vehicle;
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
    /// </summary>
    internal static class CasCallReadinessRepair
    {
        private static readonly AccessTools.FieldRef<CasAirframeUnit, int> MissionsRef =
            AccessTools.FieldRefAccess<CasAirframeUnit, int>("_missionsAvailable");
        private static readonly AccessTools.FieldRef<CasAirframeUnit, float> CooldownRef =
            AccessTools.FieldRefAccess<CasAirframeUnit, float>("<RemainingCooldown>k__BackingField");

        internal static void ResetForScene()
        {
            SendCasSupportOutcomePatch.ResetForScene();
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
        ///
        /// THE POSTFIX ALSO GIVES AN AIR CALL ITS TARGET. A map click against a helicopter supplies a
        /// ground point only (MapController.cs:1390-1401 flattens it, CASController.SetInterestPoint does
        /// it again at :903-908) and CheatTargetUnit is null on the player path, so the game's own
        /// acquisition has to find the helicopter inside a 45 degree / 1000 m spot cone and out-score every
        /// ground unit in it. For a sortie carrying an air-to-ground missile - the only sorties this mod
        /// flies that can attack an aircraft at all, and the only ones CasMissileGuidance flies as an
        /// anti-aircraft round - the aircraft can simply be TOLD, through the game's own field:
        ///
        ///     if (CheatTargetUnit != null &amp;&amp; !CheatTargetUnit.Neutralized) unit3 = CheatTargetUnit;
        ///     (CASController.SearchForTarget :959-962)
        ///
        /// THE VISIBILITY GATE IS NOT OPTIONAL. The game honours that field with NO cone test and NO
        /// visibility test of its own, so a value written here without one is an aircraft that attacks
        /// through terrain, smoke and trees. The check is CasAirTargets.IsVisibleFrom - vanilla's own call
        /// from CASController.SearchForTarget :934, replicated flag for flag - and it is taken from the
        /// aircraft's position AT THIS MOMENT, which for a freshly summoned sortie is its deploy point.
        /// That is why CasTargetSpreadPatch.Prefix re-decides the same question on every search
        /// frame (from where the aircraft actually is by then) and CLEARS this field when the answer is no:
        /// the field set here is provisional, and the search-time gate is the authority. A call the gate
        /// refuses is not a failure - the aircraft simply acquires the target the game's own way, and if it
        /// finds nothing the sortie is exactly the sortie it would have been.
        /// </summary>
        [HarmonyPatch(typeof(CasSupportManager), "SendCasSupport")]
        internal static class SendCasSupportOutcomePatch
        {
            /// <summary>The CASControllers that already existed when the call started, so the one the call
            /// spawns can be told apart from every sortie already in the air.</summary>
            private static readonly HashSet<int> KnownControllers = new HashSet<int>();

            internal static void ResetForScene()
            {
                KnownControllers.Clear();
            }

            /// <summary>
            /// How near the clicked point an enemy aircraft has to be for a call to be aimed at it. The
            /// same figure and the same three-dimensional measure as the search-time gate
            /// (CasTargetSpreadPatch.AirSearchRadiusMeters), so the two halves of the feature agree on what
            /// "the player clicked the helicopter" means.
            /// </summary>
            private const float AirSearchRadiusMeters = 800f;

            private static void Prefix()
            {
                try
                {
                    SnapshotControllers();
                }
                catch (Exception ex)
                {
                    Log.Error("CAS call: could not snapshot existing aircraft: " + ex);
                }
            }

            private static void Postfix(CasSupportManager __instance, Vector3 supportPosition,
                ref MapMissionResult __result)
            {
                try
                {
                    if (__result.IsSuccess)
                    {
                        GiveAirTargetToSpawnedSortie(__instance, supportPosition);
                    }
                }
                catch (Exception ex)
                {
                    Log.Error("CAS call: could not assign an air target: " + ex);
                }
            }

            private static void SnapshotControllers()
            {
                KnownControllers.Clear();
                CASController[] planes = UnityEngine.Object.FindObjectsOfType<CASController>();
                for (int i = 0; i < planes.Length; i++)
                {
                    if (planes[i] != null)
                    {
                        KnownControllers.Add(planes[i].GetInstanceID());
                    }
                }
            }

            /// <summary>
            /// Hands the aircraft this call just spawned the enemy aircraft nearest the clicked point, if
            /// there is one AND the aircraft can see it right now. Everything is behind the ownership and
            /// capability test (CasAirTargets.IsOurMissileSortie), so no other call - vanilla, campaign,
            /// scripted, or any of the mod's bomb / rocket / gun slots - can be affected.
            /// </summary>
            private static void GiveAirTargetToSpawnedSortie(CasSupportManager manager, Vector3 supportPosition)
            {
                CASController spawned = FindSpawnedController(manager);
                if (spawned == null)
                {
                    return;
                }
                if (!CasAirTargets.IsOurMissileSortie(spawned))
                {
                    return; // not one of our air-to-ground-missile sorties: nothing to do.
                }
                if (spawned.CheatTargetUnit != null)
                {
                    return; // the game (a scripted call) already named a target: never overwrite it.
                }

                Vector3 point = new Vector3(supportPosition.x, 0f, supportPosition.z);
                // The clicked point is flattened to the terrain height the game itself used, so the 3-D
                // distance below is measured from the ground the player clicked on.
                bool flag;
                point.y = CodeUtils.GetTerrainHeightAtPosition(point, out flag, 2000f);

                Unit air = FireSupportPatches.CasTargetSpreadPatch.FindNearbyEnemyAir(
                    spawned, point, AirSearchRadiusMeters);
                if (air == null)
                {
                    return;
                }

                if (!CasAirTargets.CanDeliverAirToGroundMissile(spawned))
                {
                    return;
                }

                if (!CasAirTargets.IsVisibleFrom(spawned, air))
                {
                    return;
                }

                spawned.CheatTargetUnit = air;
            }

            /// <summary>
            /// The controller this SendCasSupport call created: the one that was not in the air when the
            /// call started and that belongs to the manager that just sent it.
            /// </summary>
            private static CASController FindSpawnedController(CasSupportManager manager)
            {
                CASController[] planes = UnityEngine.Object.FindObjectsOfType<CASController>();
                for (int i = 0; i < planes.Length; i++)
                {
                    CASController plane = planes[i];
                    if (plane == null || KnownControllers.Contains(plane.GetInstanceID()))
                    {
                        continue;
                    }
                    if (plane.casManager == manager)
                    {
                        return plane;
                    }
                }
                return null;
            }

        }
    }
}

