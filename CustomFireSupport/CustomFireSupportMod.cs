using System;
using MelonLoader;
//using UnityEngine;

[assembly: MelonInfo(typeof(CustomFireSupport.CustomFireSupportMod), "CustomFireSupport", "1.0.2", "WpsxX")]
[assembly: MelonGame("Radian Simulations LLC", "GHPC")]

namespace CustomFireSupport
{
    /// <summary>
    /// MelonLoader entry point.
    ///
    /// The mod gives the player up to five fully configurable fire-support slots on the mission map.
    /// Everything is driven by UserData/MelonPreferences.cfg:
    ///
    ///   [CustomFireSupport]            global switches (master, hide vanilla buttons, reload key, ...)
    ///   [CustomFireSupport.Slot1..5]   one support slot each (type, count, shells, timing, CAS loadout)
    ///
    /// Nothing is hard-coded: see ConfigSchema.cs for every key, its default and its valid range.
    /// </summary>
    public class CustomFireSupportMod : MelonMod
    {
        internal static CustomFireSupportMod Instance;

        /// <summary>Forwarded to the log helper; refreshed every time the config is read.</summary>
        internal static bool VerboseLogging
        {
            get { return CustomSupportRegistry.Global != null && CustomSupportRegistry.Global.VerboseLogging; }
        }

        public override void OnInitializeMelon()
        {
            Instance = this;

            try
            {
                ConfigSchema.Initialize();
                HarmonyInstance.PatchAll();
                Log.Info("loaded (v1.0.2). Config file: Bin\\UserData\\MelonPreferences.cfg -> [CustomFireSupport] (keys Slot1_* .. Slot6_*)");
                Log.Info("The slots are built at the start of every mission; edit the cfg and restart the mission to apply changes.");
            }
            catch (Exception ex)
            {
                Log.Error("failed to initialise: " + ex);
            }
        }

        public override void OnSceneWasLoaded(int buildIndex, string sceneName)
        {
            CasBundleMaterialRepair.ClearSceneIndex();
            // Drop all references to the previous mission's objects; the scene destroyed them already.
            CustomSupportRegistry.ResetForScene();
            // Earliest safe moment of a session to pull the configured addressable CAS assets into
            // memory (main menu / bootstrap scenes load first). No-op once done or when unconfigured.
            CasPrewarmer.EnsurePrewarmed();
            Log.Verbose("scene loaded: " + sceneName);
        }

        public override void OnSceneWasInitialized(int buildIndex, string sceneName)
        {
            // Native effect shaders are guaranteed to be available after scene initialization. Retry
            // the bundle material binding here so rocket/smoke/illumination particles do not stay on
            // the placeholder shader chosen during the early menu load.
            CasBundleMaterialRepair.RefreshForScene();
        }

        public override void OnUpdate()
        {
            try
            {
                CustomSupportRegistry.Tick();
            }
            catch (Exception ex)
            {
                Log.Error("update failed: " + ex);
            }
        }
    }
}
