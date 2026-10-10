using System.Collections.Generic;
using System.Reflection;
using GHPC.Vehicle;
using HarmonyLib;
using UnityEngine;

namespace CustomFireSupport
{
    /// <summary>Holds a laser-designating carrier only while a laser missile still needs the beam.</summary>
    internal static class CasLaserRunHold
    {
        private const float FlightTimeMargin = 1.35f;
        private const float HoldExtraSeconds = 0.8f;
        private const float MinimumHoldSeconds = 2.5f;

        private sealed class ShotHold
        {
            internal float BeamUntil;
            internal bool BeamReleased;
        }

        private sealed class Hold
        {
            internal bool EndRequested;
            internal readonly Dictionary<long, ShotHold> Shots = new Dictionary<long, ShotHold>();
        }

        private static readonly Dictionary<int, Hold> Holds = new Dictionary<int, Hold>();
        private static long NextToken;
        private static readonly MethodInfo EndAttackRunMethod = AccessTools.Method(typeof(CASController), "EndAttackRun");

        internal static void ResetForScene() { Holds.Clear(); }

        internal static void Remove(CASController controller)
        {
            if (controller != null) Holds.Remove(controller.GetInstanceID());
        }

        internal static long Begin(CASController controller, CasAirframeCatalog.MissileProfile profile, float slantRange)
        {
            if (controller == null || profile == null ||
                profile.Guidance != CasAirframeCatalog.GuidanceKind.LaserBeamRider) return 0;
            float estimated = slantRange / Mathf.Max(1f, profile.CruiseSpeedMeters) * FlightTimeMargin + HoldExtraSeconds;
            Hold hold;
            int id = controller.GetInstanceID();
            if (!Holds.TryGetValue(id, out hold))
            {
                hold = new Hold();
                Holds[id] = hold;
            }
            long token = ++NextToken;
            hold.Shots[token] = new ShotHold
            {
                BeamUntil = Time.time + Mathf.Clamp(estimated, MinimumHoldSeconds,
                    Mathf.Max(MinimumHoldSeconds, profile.CarrierHoldMaxSeconds))
            };
            return token;
        }

        internal static void End(Transform carrier, long token, bool beamOnly = false)
        {
            if (carrier == null || token == 0) return;
            CASController controller = carrier.GetComponentInParent<CASController>();
            Hold hold;
            ShotHold shot;
            if (controller == null || !Holds.TryGetValue(controller.GetInstanceID(), out hold) ||
                !hold.Shots.TryGetValue(token, out shot)) return;
            shot.BeamReleased = true;
        }

        internal static bool IsHolding(CASController controller)
        {
            Hold hold;
            if (controller == null || !Holds.TryGetValue(controller.GetInstanceID(), out hold)) return false;
            foreach (ShotHold shot in hold.Shots.Values)
                if (!shot.BeamReleased && shot.BeamUntil >= Time.time) return true;
            return false;
        }

        [HarmonyPatch(typeof(CASController), "EndAttackRun")]
        internal static class CasHoldTheRunPatch
        {
            [HarmonyPriority(Priority.First)]
            private static bool Prefix(CASController __instance)
            {
                if (__instance == null || !CasFireChainRepair.IsOurSortie(__instance)) return true;
                Hold hold;
                if (!Holds.TryGetValue(__instance.GetInstanceID(), out hold)) return true;
                if (IsHolding(__instance))
                {
                    hold.EndRequested = true;
                    return false;
                }
                Holds.Remove(__instance.GetInstanceID());
                return true;
            }
        }

        [HarmonyPatch(typeof(CASController), "Update")]
        internal static class CasReleaseTheRunPatch
        {
            private static void Postfix(CASController __instance)
            {
                if (__instance == null || !CasFireChainRepair.IsOurSortie(__instance)) return;
                Hold hold;
                if (!Holds.TryGetValue(__instance.GetInstanceID(), out hold) ||
                    !hold.EndRequested || IsHolding(__instance)) return;
                Holds.Remove(__instance.GetInstanceID());
                if (EndAttackRunMethod != null) EndAttackRunMethod.Invoke(__instance, null);
            }
        }
    }
}
