using GHPC.Vehicle;
using HarmonyLib;

namespace CustomFireSupport
{
    /// <summary>Restores the mod's CAS aircraft to one attack pass.</summary>
    internal static class CasFlightBehaviourRepair
    {
        private static bool IsOurs(CASController controller)
        {
            return controller != null && CasFireChainRepair.IsOurSortie(controller);
        }

        [HarmonyPatch(typeof(CASController), "SearchForTarget")]
        internal static class CasSearchAndPassPatch
        {
            private static bool Prefix(CASController __instance)
            {
                if (!IsOurs(__instance)) return true;
                return __instance.FinalTarget == null;
            }

            private static void Postfix(CASController __instance)
            {
                if (IsOurs(__instance)) CasMissileAttackRun.InitializeSortie(__instance);
            }
        }

        [HarmonyPatch(typeof(CASController), "EndAttackRun")]
        internal static class CasLeaveAfterAttackPatch
        {
            private static readonly AccessTools.FieldRef<CASController, float> PassesRef =
                AccessTools.FieldRefAccess<CASController, float>("passes");

            [HarmonyPriority(Priority.Last)]
            private static void Prefix(CASController __instance)
            {
                if (IsOurs(__instance) && !CasLaserRunHold.IsHolding(__instance))
                    PassesRef(__instance) = 1f;
            }
        }
    }
}
