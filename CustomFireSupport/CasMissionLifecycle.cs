using System;
using System.Collections.Generic;
using GHPC;
using HarmonyLib;
using UnityEngine;

namespace CustomFireSupport
{
    /// <summary>Base-scene transitions end missions; additive terrain/UI loads do not.</summary>
    internal static class CasMissionLifecycle
    {
        private static readonly HashSet<UnityEngine.Object> Retired = new HashSet<UnityEngine.Object>();

        internal static void Retire(UnityEngine.Object asset)
        {
            if (!ReferenceEquals(asset, null)) Retired.Add(asset);
        }

        [HarmonyPatch(typeof(SceneController), "LoadSceneByIndex")]
        private static class BeginTransitionPatch
        {
            private static void Prefix()
            {
                CustomSupportRegistry.ResetForScene();
                CasCallReadinessRepair.ResetForScene();
                CasMissileAttackRun.ResetForScene();
                CasLaserRunHold.ResetForScene();
                CasAttackLibrary.Reset();
                CustomSlotBuilder.ResetLoadouts();
                CasPayloadFactory.ResetForMission();
                CasPrewarmer.BeginMission();
            }
        }

        // The game invokes AfterSceneLoad only after awaiting every old scene's unload.
        // Retire only our generated assets; bundle assets remain pinned for the session.
        [HarmonyPatch(typeof(SceneController), "AfterSceneLoad")]
        private static class FinishTransitionPatch
        {
            private static void Prefix()
            {
                foreach (UnityEngine.Object asset in Retired)
                {
                    if (asset != null) UnityEngine.Object.Destroy(asset);
                }
                Retired.Clear();
            }
        }
    }
}
