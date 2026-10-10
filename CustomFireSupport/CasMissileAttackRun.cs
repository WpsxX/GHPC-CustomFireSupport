using System.Collections.Generic;
using System.Reflection;
using GHPC;
using GHPC.Vehicle;
using HarmonyLib;
using UnityEngine;

namespace CustomFireSupport
{
    /// <summary>Single-pass missile support and the target-position override used by custom missiles.</summary>
    internal static class CasMissileAttackRun
    {
        private static readonly AccessTools.FieldRef<CASController, bool> LastKnownRef =
            AccessTools.FieldRefAccess<CASController, bool>("_targetIsLastKnownPosition");
        private static readonly AccessTools.FieldRef<CASController, Vector3> TargetPositionRef =
            AccessTools.FieldRefAccess<CASController, Vector3>("_targetPosition");

        // AGM-65 is the only missile that gets an A/B salvo plan.  Kh-25 remains a
        // laser-beam-rider pair on the controller's one native FinalTarget.
        private sealed class AgmPlan
        {
            internal Unit Primary;
            internal Unit Secondary;
            internal int Committed;
            internal Unit Pending;
        }

        private static readonly Dictionary<CASController, AgmPlan> AgmPlans =
            new Dictionary<CASController, AgmPlan>();

        // SearchForTarget and the firing coroutine can be separated by one frame. Keep the last
        // target accepted by the native search so a transient FinalTarget clear cannot turn the
        // first AGM trigger pull into a dry pass.
        private static readonly Dictionary<CASController, Unit> LastPrimaryTargets =
            new Dictionary<CASController, Unit>();

        // The second Maverick may spread only inside the first target's local battle group.
        // If no suitable enemy is found in this circle, both rounds deliberately use Primary.
        internal const float SecondaryTargetRadiusMeters = 800f;

        internal static void InitializeSortie(CASController controller)
        {
            if (controller == null || !CasFireChainRepair.IsOurSortie(controller)) return;
            controller.passes = 1f;
            // SearchForTarget has a postfix even when another prefix skips the native search.
            // Do not erase an A/B plan that has already been used by this controller; doing so
            // would make a later search callback rebuild the second shot from FinalTarget.
        }

        internal static void BeginSortie(CASController controller)
        {
            if (controller == null || !CasFireChainRepair.IsOurSortie(controller)) return;
            AgmPlans.Remove(controller);
            LastPrimaryTargets.Remove(controller);
            controller.passes = 1f;
        }

        internal static void ResetForScene()
        {
            AgmPlans.Clear();
            LastPrimaryTargets.Clear();
        }

        internal static void EndSortie(CASController controller)
        {
            // A destroyed Unity wrapper still identifies its dictionary entry.
            if (!ReferenceEquals(controller, null))
            {
                AgmPlans.Remove(controller);
                LastPrimaryTargets.Remove(controller);
            }
        }

        internal static void RememberPrimaryTarget(CASController controller, Unit target)
        {
            if (CasAirTargets.IsOurMissileSortie(controller) &&
                target != null && !target.Neutralized && target.Center != null)
            {
                LastPrimaryTargets[controller] = target;
            }
        }

        internal static Unit RecoverLaunchTarget(CASController controller, AmmoType ammo)
        {
            if (controller == null) return null;

            AgmPlan plan;
            if (IsAgm65(ammo) && AgmPlans.TryGetValue(controller, out plan) && plan != null)
            {
                Unit planned = plan.Pending;
                if (planned == null) planned = plan.Committed == 0 ? plan.Primary : plan.Secondary;
                if (IsBasicAgmTargetValid(planned)) return planned;
            }

            if (IsBasicAgmTargetValid(controller.FinalTarget)) return controller.FinalTarget;
            Unit remembered;
            return LastPrimaryTargets.TryGetValue(controller, out remembered) &&
                   IsBasicAgmTargetValid(remembered) ? remembered : null;
        }

        [HarmonyPatch(typeof(CASController), "EnterState")]
        private static class LeaveAreaPatch
        {
            // FlightState is private in the shipped assembly; avoid a direct reference to its type.
            private static void Postfix(CASController __instance, object newState)
            {
                if (newState != null && newState.ToString() == "LeaveArea") EndSortie(__instance);
            }
        }

        internal static bool IsAgm65(AmmoType ammo)
        {
            CasAirframeCatalog.MissileProfile profile = CasPayloadFactory.ProfileFor(ammo);
            return profile != null && string.Equals(profile.MissileId, "AGM-65 Maverick",
                System.StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Chooses the target for the next AGM-65 launch.  The first launch is the aircraft's
        /// native target; the second is a separately validated enemy when one exists, with the
        /// first target as the deliberate fallback.  The ordinal is committed only by the Init
        /// postfix after a real round has been bound.
        /// </summary>
        internal static Unit SelectAgmTarget(CASController controller, CASHardpoint hardpoint,
            Unit nativeTarget = null)
        {
            if (controller == null || hardpoint == null || !IsAgm65(hardpoint.Ammo))
            {
                return controller != null ? controller.FinalTarget : null;
            }

            AgmPlan plan;
            if (!AgmPlans.TryGetValue(controller, out plan) || plan == null)
            {
                plan = new AgmPlan();
                AgmPlans[controller] = plan;
            }

            if (plan.Committed == 0 && plan.Pending == null)
            {
                plan.Primary = nativeTarget != null ? nativeTarget : controller.FinalTarget;
                if (!IsBasicAgmTargetValid(plan.Primary))
                {
                    Unit remembered;
                    if (LastPrimaryTargets.TryGetValue(controller, out remembered))
                    {
                        plan.Primary = remembered;
                    }
                }
                // Do not query mission visibility services before the first missile exists. A
                // failure to find B must never cancel a valid launch at A. Search on shot two.
                plan.Secondary = null;
            }

            // The native target is already the game's accepted lock. Do not make the first
            // trigger pull depend on a second visibility/attackability query that can race the
            // controller's target-search update and cause the whole first call to leave empty.
            Unit target = plan.Committed == 0 ? plan.Primary : plan.Secondary;
            if (plan.Committed == 0)
            {
                if (!IsBasicAgmTargetValid(target))
                {
                    plan.Pending = null;
                    return null;
                }
            }
            else if (!IsAgmTargetValid(controller, target, plan.Primary))
            {
                // Re-scan immediately before the second trigger pull. A target may have died,
                // left visibility, or become unreachable during the 1.5 second interval.
                try
                {
                    target = FireSupportPatches.CasTargetSpreadPatch.FindAlternativeTargetForAgm(
                        controller, plan.Primary);
                }
                catch (System.Exception)
                {
                    target = null;
                }
                // No valid target inside the 800 m circle means both missiles stay on A.
                if (!IsAgmTargetValid(controller, target, plan.Primary)) target = plan.Primary;
                if (!IsBasicAgmTargetValid(target)) target = null;
            }

            if (plan.Committed > 0) plan.Secondary = target;
            plan.Pending = target;
            return target;
        }

        private static bool IsBasicAgmTargetValid(Unit target)
        {
            return target != null && !target.Neutralized && target.Center != null;
        }

        private static bool IsAgmTargetValid(CASController controller, Unit target, Unit exclude)
        {
            try
            {
                return controller != null && target != null && target != exclude &&
                       !target.Neutralized && target.Center != null && exclude != null &&
                       exclude.Center != null &&
                       (target.Center.position - exclude.Center.position).sqrMagnitude <=
                           SecondaryTargetRadiusMeters * SecondaryTargetRadiusMeters &&
                       FireSupportPatches.CasTargetSpreadPatch.CanPlaneAttackForAgm(controller, target) &&
                       CasAirTargets.IsVisibleFrom(controller, target);
            }
            catch (System.Exception) { return false; }
        }

        /// <summary>Advances the AGM plan only after guidance has received this exact target.</summary>
        internal static void CommitAgmLaunch(CASController controller, Unit target)
        {
            if (controller == null || target == null) return;
            AgmPlan plan;
            if (!AgmPlans.TryGetValue(controller, out plan) || plan == null) return;
            if (plan.Pending == target)
            {
                plan.Committed = Mathf.Min(2, plan.Committed + 1);
                plan.Pending = null;
            }
        }

        internal static long NoteMissileFired(CASController controller,
            CasAirframeCatalog.MissileProfile profile, Unit launchTarget, int shotId)
        {
            if (controller == null || profile == null || !CasFireChainRepair.IsOurSortie(controller)) return 0;
            float range = launchTarget != null && launchTarget.Center != null
                ? Vector3.Distance(controller.transform.position, launchTarget.Center.position) : 0f;
            // Only laser beam riding needs to keep the carrier on the attack run.
            return CasLaserRunHold.Begin(controller, profile, range);
        }

        internal static bool TryGetTargetPosition(CASController controller, out Vector3 position)
        {
            position = Vector3.zero;
            if (controller == null) return false;
            if (controller.FinalTarget != null && controller.FinalTarget.Center != null && !LastKnownRef(controller))
            {
                position = controller.FinalTarget.Center.position;
                return true;
            }
            position = TargetPositionRef(controller);
            return position != Vector3.zero;
        }
    }

    [HarmonyPatch(typeof(CASController), "GetAimPosition")]
    internal static class CasMissileAimPatch
    {
        private static void Postfix(CASController __instance, AmmoType ammo, ref Vector3 __result)
        {
            try
            {
                if (ammo == null || !CasPayloadFactory.IsOurMissile(ammo)) return;
                Vector3 target;
                if (CasMissileAttackRun.TryGetTargetPosition(__instance, out target)) __result = target;
            }
            catch (System.Exception)
            {
            }
        }
    }
}
