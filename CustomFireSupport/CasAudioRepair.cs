using System;
using System.Collections.Generic;
using FMODUnity;
using GHPC.Vehicle;
using HarmonyLib;
using MelonLoader;
using UnityEngine;

namespace CustomFireSupport
{
    // Older cas_assets stores StudioEventEmitter.Event, not EventReference.
    // Current FMODUnity.Play returns immediately for an empty EventReference.
    internal static class CasAudioRepair
    {
        private static readonly HashSet<string> reported = new HashSet<string>();

        internal static void RepairBundleAudio()
        {
            foreach (GameObject prefab in CasPrewarmer.BundlePrefabs)
            {
                if (prefab == null) continue;
                foreach (StudioEventEmitter emitter in prefab.GetComponentsInChildren<StudioEventEmitter>(true))
                    Migrate(emitter);
            }
        }

        internal static bool Migrate(StudioEventEmitter emitter)
        {
            if (emitter == null || !emitter.EventReference.IsNull) return false;
#pragma warning disable 618
            string path = emitter.Event;
#pragma warning restore 618
            if (string.IsNullOrEmpty(path)) return false;
            try
            {
                var description = RuntimeManager.GetEventDescription(path);
                if (!description.isValid()) return false;
                emitter.EventReference = new EventReference { Guid = RuntimeManager.PathToGUID(path) };
                if (reported.Add(path)) MelonLogger.Msg("[CAS audio] Migrated legacy event: " + path);
                return !emitter.EventReference.IsNull;
            }
            catch (Exception)
            {
                // Banks may still be loading during menu prewarm. Retry before Play.
                return false;
            }
        }

        [HarmonyPatch(typeof(StudioEventEmitter), "Play")]
        private static class LegacyPlayPatch
        {
            private static void Prefix(StudioEventEmitter __instance)
            {
                if (__instance == null || !__instance.EventReference.IsNull) return;
                CASController plane = __instance.GetComponentInParent<CASController>();
                if (CasPrewarmer.IsFromOurBundle(__instance.gameObject) ||
                    (plane != null && CasFireChainRepair.IsOurSortie(plane))) Migrate(__instance);
            }
        }
    }
}
