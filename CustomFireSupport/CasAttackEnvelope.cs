using System;
using System.Collections;
using System.Reflection;
using GHPC;
using GHPC.Vehicle;
using HarmonyLib;
using UnityEngine;

namespace CustomFireSupport
{
    /// <summary>Revalidate the release envelope at transition, after pre-delay and at each trigger.</summary>
    internal static class CasAttackEnvelope
    {
        private static readonly FieldInfo State = AccessTools.Field(typeof(CASController), "_currentState");
        private static readonly AccessTools.FieldRef<CASController, CASAttackType> Attack =
            AccessTools.FieldRefAccess<CASController, CASAttackType>("_finalAttackType");
        private static readonly AccessTools.FieldRef<CASController, float> ReleaseDistance =
            AccessTools.FieldRefAccess<CASController, float>("_releaseDistance");
        private static readonly AccessTools.FieldRef<CASController, CASHardpointManager> Manager =
            AccessTools.FieldRefAccess<CASController, CASHardpointManager>("_hardpointManager");
        private static readonly Func<CASController, Vector3> TargetPosition =
            CachedDelegate.Create<Func<CASController, Vector3>>(AccessTools.Method(typeof(CASController), "GetFinalTargetPosition"));
        private static readonly MethodInfo TargetPositionMethod = AccessTools.Method(typeof(CASController), "GetFinalTargetPosition");
        private static readonly MethodInfo EndRunMethod = AccessTools.Method(typeof(CASController), "EndAttackRun");
        private static readonly Action<CASController> EndRun =
            CachedDelegate.Create<Action<CASController>>(AccessTools.Method(typeof(CASController), "EndAttackRun"));

        internal static bool IsFiring(CASController plane)
        {
            return plane != null && Convert.ToInt32(State.GetValue(plane)) == 3;
        }

        internal static bool Inside(CASController plane, bool starting)
        {
            if (plane == null) return false;
            if (CasAirTargets.IsAirUnit(plane.FinalTarget))
                return InsideAirTarget(plane, plane.FinalTarget, Attack(plane));
            Vector3 target = TargetPosition != null ? TargetPosition(plane) :
                (Vector3)TargetPositionMethod.Invoke(plane, null);
            Vector3 offset = target - plane.transform.position;
            Vector3 forward = plane.transform.forward;
            // Bomb release uses horizontal alignment: its computed aim is above the ground target.
            // Guns and rockets also have to finish pitching toward the ballistic aim point.
            Vector3 aim = plane.movePointGoal - plane.transform.position;
            float aimAngle = Attack(plane) == CASAttackType.Bombs ? 0f : Vector3.Angle(forward, aim);
            offset.y = 0f;
            forward.y = 0f;
            float heading = Vector3.Angle(forward, offset);
            float range = Vector3.Distance(target, plane.transform.position);
            return Accept(range, ReleaseDistance(plane), heading, aimAngle, starting);
        }

        internal static bool InsideAirTarget(CASController plane, Unit target, CASAttackType type)
        {
            if (plane == null || target == null || target.Neutralized || target.Center == null ||
                (type != CASAttackType.GunRun && type != CASAttackType.AirToGroundMissile &&
                 type != CASAttackType.AirToAirMissile)) return false;
            CASHardpointManager manager = Manager(plane);
            CASAttackMeta meta = manager != null ? manager.GetAttackMetaByType(type) : null;
            if (meta == null) return false;
            Vector3 offset = target.Center.position - plane.transform.position;
            // Do not flatten altitude or use the previous aim point as the target direction. A
            // moving aircraft (or AGM target B) can be outside the cone while that old point is ahead.
            float targetAngle = Vector3.Angle(plane.transform.forward, offset);
            float aimAngle = type == CASAttackType.GunRun
                ? Vector3.Angle(plane.transform.forward, plane.movePointGoal - plane.transform.position)
                : targetAngle;
            return CasAirAttackRules.Accept(offset.magnitude, meta.ReleaseDistance, targetAngle, aimAngle);
        }

        internal static bool CanReleaseAt(CASController plane, Unit target, CASAttackType type)
        {
            return !CasAirTargets.IsAirUnit(target) ||
                (IsFiring(plane) && InsideAirTarget(plane, target, type));
        }

        // Scalar rule is exercised by the headless regression check.
        internal static bool Accept(float range, float release, float heading, float aimAngle, bool starting)
        {
            return !float.IsNaN(range) && !float.IsInfinity(range) && release > 0f &&
                range > 1f && range <= release && heading <= (starting ? 5f : 10f) && aimAngle <= 10f;
        }

        [HarmonyPatch(typeof(CASController), "EnterState")]
        private static class TransitionPatch
        {
            private static void Postfix(CASController __instance, object newState)
            {
                CustomCasMarker marker = __instance.GetComponent<CustomCasMarker>();
                if (marker == null || newState == null) return;
                string state = newState.ToString();
                if (state == "TurnTowardTarget" || state == "MoveToPoint" || state == "LeaveArea")
                    marker.AttackRevision++;
            }

            private static bool Prefix(CASController __instance, object newState)
            {
                if (!CasFireChainRepair.IsOurSortie(__instance) || newState == null) return true;
                string state = newState.ToString();
                // Stay in the turn/approach state until the native firing table is complete.
                if (state == "OnFinal") return CasBallisticsCache.ReadyForFinal(__instance);
                if (state == "FiringWeapons")
                    return Convert.ToInt32(State.GetValue(__instance)) == 2 && Inside(__instance, true);
                return true;
            }
        }

        [HarmonyPatch(typeof(CASController), "Fire")]
        private static class DelayedReleasePatch
        {
            private static void Postfix(CASController __instance, ref IEnumerator __result)
            {
                if (CasFireChainRepair.IsOurSortie(__instance))
                {
                    CustomCasMarker marker = __instance.GetComponent<CustomCasMarker>();
                    __result = Guard(__instance, __result, marker, marker != null ? marker.AttackRevision : 0);
                }
            }

            private static IEnumerator Guard(CASController plane, IEnumerator original, CustomCasMarker marker, int revision)
            {
                try
                {
                    // Preserve vanilla's pre-delay. The next MoveNext is where it releases the attack.
                    if (!original.MoveNext()) yield break;
                    yield return original.Current;
                    float deadline = Time.time + 8f;
                    while (IsCurrent(plane, marker, revision) && !Inside(plane, true) && Time.time < deadline)
                        yield return null;
                    if (!IsCurrent(plane, marker, revision)) yield break;
                    if (!Inside(plane, true))
                    {
                        if (EndRun != null) EndRun(plane);
                        else EndRunMethod.Invoke(plane, null);
                        yield break;
                    }
                    while (IsCurrent(plane, marker, revision) && original.MoveNext()) yield return original.Current;
                }
                finally
                {
                    IDisposable disposable = original as IDisposable;
                    if (disposable != null) disposable.Dispose();
                }
            }

            private static bool IsCurrent(CASController plane, CustomCasMarker marker, int revision)
            {
                return IsFiring(plane) && (marker == null || marker.AttackRevision == revision);
            }
        }

        [HarmonyPatch(typeof(CASHardpointManager), "Fire", new[] { typeof(CASAttackMeta) })]
        private static class TriggerPatch
        {
            private static bool Prefix(CASHardpointManager __instance)
            {
                CASController plane = __instance.GetComponentInParent<CASController>();
                return !CasFireChainRepair.IsOurSortie(plane) || (IsFiring(plane) && Inside(plane, false));
            }
        }
    }
}
