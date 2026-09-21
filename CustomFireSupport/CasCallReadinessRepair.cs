using System;
using System.Collections.Generic;
using System.Reflection;
using GHPC.UI;
using GHPC.UI.Map;
using GHPC.Weaponry.CAS;
using HarmonyLib;
using UnityEngine;

namespace CustomFireSupport
{
    /// <summary>Prevents duplicate CAS click subscriptions and restores custom slot readiness before vanilla checks it.</summary>
    internal static class CasCallReadinessRepair
    {
        private static readonly AccessTools.FieldRef<CasAirframeUnit, int> MissionsRef =
            AccessTools.FieldRefAccess<CasAirframeUnit, int>("_missionsAvailable");
        private static readonly AccessTools.FieldRef<CasAirframeUnit, float> CooldownRef =
            AccessTools.FieldRefAccess<CasAirframeUnit, float>("<RemainingCooldown>k__BackingField");
        private static readonly HashSet<int> ReportedDuplicates = new HashSet<int>();

        internal static void ResetForScene()
        {
            ReportedDuplicates.Clear();
        }

        internal static bool EnsureReadyForCall(CasAirframeUnit airframe, int slotIndex,
            int configuredMissions, float configuredCooldownSeconds, out string reason)
        {
            reason = null;
            if (airframe == null)
            {
                return false;
            }

            int missions = MissionsRef(airframe);
            float cooldown = CooldownRef(airframe);
            float wantedCooldown = configuredCooldownSeconds <= 0f ? 0f : configuredCooldownSeconds;
            int wantedMissions = configuredMissions < 0 ? CustomSlotBuilder.InfiniteMissionsDisplay : configuredMissions;
            if (missions > 0 && cooldown <= 0f)
            {
                return true;
            }

            reason = "sorties=" + missions + ", cooldown=" + cooldown.ToString("0.#") + "s";
            if (missions <= 0)
            {
                MissionsRef(airframe) = wantedMissions;
            }
            if (cooldown > wantedCooldown)
            {
                CooldownRef(airframe) = wantedCooldown;
            }
            Log.Warn("CAS call: slot " + slotIndex + " was not ready (" + reason + "); restored sorties=" +
                     wantedMissions + ", cooldown=" + wantedCooldown.ToString("0.#") + "s.");
            return true;
        }

        [HarmonyPatch(typeof(TopoMapCamera), "add_MapClick")]
        internal static class CasMapClickDedupePatch
        {
            private static readonly MethodInfo TryCallCasMethod =
                AccessTools.Method(typeof(MapController), "TryCallCAS", new[] { typeof(MapInteractionEventArgs) });
            private static readonly FieldInfo MapClickField =
                AccessTools.Field(typeof(TopoMapCamera), "MapClick");

            private static bool Prefix(TopoMapCamera __instance, Action<MapInteractionEventArgs> value)
            {
                try
                {
                    if (__instance == null || value == null || TryCallCasMethod == null || MapClickField == null)
                    {
                        return true;
                    }
                    Delegate[] incoming = value.GetInvocationList();
                    bool isCasHandler = false;
                    for (int i = 0; i < incoming.Length; i++)
                    {
                        if (incoming[i] != null && incoming[i].Method == TryCallCasMethod)
                        {
                            isCasHandler = true;
                            break;
                        }
                    }
                    if (!isCasHandler)
                    {
                        return true;
                    }

                    Action<MapInteractionEventArgs> existing =
                        MapClickField.GetValue(__instance) as Action<MapInteractionEventArgs>;
                    if (existing == null)
                    {
                        return true;
                    }
                    Delegate[] current = existing.GetInvocationList();
                    for (int i = 0; i < current.Length; i++)
                    {
                        if (current[i] != null && current[i].Method == TryCallCasMethod)
                        {
                            if (ReportedDuplicates.Add(__instance.GetInstanceID()))
                            {
                                Log.Warn("CAS map click handler was already subscribed on '" + __instance.name +
                                         "'; dropped the duplicate subscription.");
                            }
                            return false;
                        }
                    }
                }
                catch (Exception ex)
                {
                    Log.Error("CAS map click de-duplication failed: " + ex);
                }
                return true;
            }
        }
    }
}
