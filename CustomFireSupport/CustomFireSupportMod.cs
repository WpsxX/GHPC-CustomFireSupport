using System;
using MelonLoader;

[assembly: MelonInfo(typeof(CustomFireSupport.CustomFireSupportMod), "CustomFireSupport", "1.0.4", "WpsxX")]
[assembly: MelonGame("Radian Simulations LLC", "GHPC")]

namespace CustomFireSupport
{
    /// <summary>
    /// MelonLoader entry point.
    ///
    /// The mod gives the player up to six fully configurable fire-support slots on the mission map.
    /// Everything is driven by UserData/MelonPreferences.cfg:
    ///
    ///   [CustomFireSupport]            global switches (master, hide vanilla buttons, reload key, ...)
    ///   Slot1..6                     one support slot each (type, count, shells, timing, CAS loadout)
    ///
    /// Nothing is hard-coded: see ConfigSchema.cs for every key, its default and its valid range.
    /// </summary>
    public class CustomFireSupportMod : MelonMod
    {
        /// <summary>Initializes the configuration and Harmony patches.</summary>
        public override void OnInitializeMelon()
        {
            try
            {
                ConfigSchema.Initialize();
                HarmonyInstance.PatchAll();
            }
            catch (Exception)
            {
            }
        }

        public override void OnSceneWasLoaded(int buildIndex, string sceneName)
        {
            CasBundleMaterialRepair.ClearSceneIndex();
            // Mission cleanup is hooked to SceneController.LoadSceneByIndex. Loading an additive
            // scene must not erase live sortie targets or rebuild mission inventories.
            // Earliest safe moment of a session to pull the configured addressable CAS assets into
            // memory (main menu / bootstrap scenes load first). No-op once done or when unconfigured.
            CasPrewarmer.EnsurePrewarmed();
        }

        public override void OnSceneWasInitialized(int buildIndex, string sceneName)
        {
            // MelonLoader dispatches this after scene loading; native effect assets can now be bound.
            CasBundleMaterialRepair.RefreshForScene();
        }

        public override void OnUpdate()
        {
            try
            {
                FireSupportPatches.CasTargetSpreadPatch.Tick();
                CustomSupportRegistry.Tick();
            }
            catch (Exception)
            {
            }

            // Advance any gun-sound stop tail that is still fading out (see CasGunAudio.End): the tail
            // is detached from the aircraft, so nothing else drives its fade.
            try
            {
                CasGunAudio.PumpTails();
            }
            catch (Exception)
            {
            }
        }
    }
}


