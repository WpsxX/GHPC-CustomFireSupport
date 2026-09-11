using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using GHPC.Vehicle;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.ResourceManagement.AsyncOperations;

namespace CustomFireSupport
{
    /// <summary>
    /// Pre-loads CAS content so every aircraft and hardpoint is available in every mission.
    ///
    /// Two sources, tried in order at the earliest safe point (the first scene load of the session,
    /// and again defensively when a mission is prepared):
    ///
    ///   1. the optional "cas_assets" AssetBundle shipped next to the mod DLL. It is built from the
    ///      game's own extracted assets and carries all 8 CAS airframes, the 13 loadouts and the 9
    ///      hardpoints, so loading it makes the donor scan and the hardpoint library see everything
    ///      from the very first mission. This is the only way to pre-load the fixed-wing jets, which
    ///      have no addressable key and otherwise only exist while a terrain scene is loaded.
    ///   2. the addressable keys from CasPrewarmKeys (empty by default: the game's aircraft have no
    ///      addressable keys of their own, and the bundled pack above covers every CAS asset).
    ///
    /// Everything loaded here is pinned for the session (bundle and asset references are never
    /// released), mirroring how the game itself keeps Addressables-loaded units alive.
    /// </summary>
    internal static class CasPrewarmer
    {
        /// <summary>The known addressable keys, used when the config value is the special word "auto".</summary>
        private static readonly string[] AutoKeys = new string[0];

        /// <summary>Pins the loaded handles so Addressables never releases the assets.</summary>
        private static readonly List<AsyncOperationHandle<GameObject>> _keptHandles =
            new List<AsyncOperationHandle<GameObject>>();

        /// <summary>Strong references as a second guard against garbage collection.</summary>
        private static readonly List<GameObject> _keptPrefabs = new List<GameObject>();

        /// <summary>The optional cas_assets bundle and its assets, pinned for the whole session.</summary>
        private static AssetBundle _casBundle;
        private static readonly List<UnityEngine.Object> _bundleAssets = new List<UnityEngine.Object>();

        /// <summary>The prefabs the bundle was built from (used to repair their materials at runtime).</summary>
        internal static List<GameObject> BundlePrefabs
        {
            get { return _bundlePrefabs; }
        }

        private static readonly List<GameObject> _bundlePrefabs = new List<GameObject>();

        /// <summary>
        /// Every GameObject inside the bundled prefabs, roots AND children. LoadAllAssets() only hands back
        /// the 33 assets the bundle was built from, so without this a child object (for example the
        /// "WP Smoke Billowing Cloud Big (2)" particle system inside the smoke prefab) looks like an asset
        /// the game owns - which once made the smoke fallback pick that single particle system as its
        /// "shell" instead of the real projectile prefab.
        /// </summary>
        private static readonly HashSet<GameObject> _bundleObjects = new HashSet<GameObject>();

        private static bool _done;

        /// <summary>
        /// True when an asset came out of the mod's own "cas_assets" bundle rather than from the game.
        ///
        /// The bundle is built from an exported copy of the game's assets, and an exporter cannot recover
        /// compiled shaders, so its materials are approximations (see the bundle build notes in the
        /// README). Whenever the game itself has the same asset in memory - a mission whose artillery
        /// batteries offer a smoke shell, or a scene that references the flare prefab - that original is
        /// the better choice, so callers use this as a tie-breaker between equally suitable candidates.
        /// </summary>
        internal static bool IsFromOurBundle(UnityEngine.Object asset)
        {
            if (asset == null)
            {
                return false;
            }

            GameObject go = asset as GameObject;
            if (go == null)
            {
                Component component = asset as Component;
                if (component != null)
                {
                    go = component.gameObject;
                }
            }
            if (go != null)
            {
                return _bundleObjects.Contains(go);
            }

            for (int i = 0; i < _bundleAssets.Count; i++)
            {
                if (ReferenceEquals(_bundleAssets[i], asset))
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>Records a bundled prefab and everything under it (see <see cref="_bundleObjects"/>).</summary>
        private static void TrackBundlePrefab(GameObject prefab)
        {
            if (prefab == null)
            {
                return;
            }
            Transform[] transforms = prefab.GetComponentsInChildren<Transform>(true);
            for (int i = 0; i < transforms.Length; i++)
            {
                if (transforms[i] != null)
                {
                    _bundleObjects.Add(transforms[i].gameObject);
                }
            }
        }

        /// <summary>
        /// Runs once per session. A missing bundle or empty key list disables that source quietly; an
        /// explicit key that cannot be loaded is reported and never takes the mission down.
        /// </summary>
        internal static void EnsurePrewarmed()
        {
            if (_done)
            {
                return;
            }
            _done = true; // attempt exactly once per session; failures are logged, not retried

            // 1. The bundled CAS asset pack (all airframes / loadouts / hardpoints), if installed.
            LoadExtraBundle();

            // 1b. Rebuild the bundled materials with the game's own shaders (see the class comment): the
            // export cannot carry compiled shaders, and this is what makes the bundled smoke shells, flares
            // and aircraft look exactly like the game's own instead of like white boxes.
            CasBundleMaterialRepair.RepairBundleMaterials();

            // 2. Addressable keys from the config. Read straight from the parsed config:
            // CustomSupportRegistry.Global is only populated once a mission starts, so the early
            // OnSceneWasLoaded call (main menu) would otherwise see the empty default and a configured
            // key list would never be used at all.
            string configured = ConfigSchema.ReadGlobal().CasPrewarmKeys;
            string[] keys = ParseKeys(configured);
            if (keys == null || keys.Length == 0)
            {
                Log.Verbose("CAS pre-warm: no extra addressable keys configured ('" + configured + "').");
                return;
            }

            int loaded = 0;
            int failed = 0;
            int casCapable = 0;
            for (int i = 0; i < keys.Length; i++)
            {
                string key = keys[i];
                try
                {
                    AsyncOperationHandle<GameObject> handle = Addressables.LoadAssetAsync<GameObject>(key);
                    GameObject prefab = handle.WaitForCompletion();
                    if (prefab == null)
                    {
                        failed++;
                        Log.Warn("CAS pre-warm: '" + key + "' resolved to null (wrong type or missing address).");
                        Addressables.Release(handle);
                        continue;
                    }
                    _keptHandles.Add(handle);
                    _keptPrefabs.Add(prefab);
                    loaded++;

                    // A prefab only feeds the CAS pipeline if it actually carries CAS components. GHPC's
                    // aircraft units have neither, so pre-warming them cannot supply a sortie - say so
                    // instead of silently reporting success.
                    bool capable = prefab.GetComponentInChildren<CASController>(true) != null ||
                                   prefab.GetComponentInChildren<CASHardpointManager>(true) != null;
                    if (capable)
                    {
                        casCapable++;
                        Log.Info("CAS pre-warm: loaded '" + key + "' -> " + prefab.name + " (CAS-capable).");
                    }
                    else
                    {
                        Log.Verbose("CAS pre-warm: loaded '" + key + "' -> " + prefab.name +
                                    " but it carries no CASController/CASHardpointManager, so it cannot feed " +
                                    "the CAS donor scan.");
                    }
                }
                catch (Exception ex)
                {
                    failed++;
                    Log.Warn("CAS pre-warm: '" + key + "' failed (" + ex.GetType().Name + ": " + ex.Message + ").");
                }
            }

            Log.Info("CAS pre-warm finished: " + loaded + " asset(s) kept (" + casCapable + " CAS-capable), " +
                     failed + " failed" +
                     (casCapable > 0
                         ? "; these now feed the CAS donor scan and the hardpoint library."
                         : "; none of them can feed CAS, so CAS in a mission without its own airframes still " +
                           "needs the session cache from an earlier CAS mission."));
        }

        /// <summary>
        /// Loads the optional "cas_assets" AssetBundle that ships next to the mod DLL. It carries every
        /// CAS airframe, loadout and hardpoint, so once it is loaded the donor scan and the hardpoint
        /// library see them in every mission - this is what makes all aircraft available from the very
        /// first mission instead of only after a mission that happens to offer them.
        /// </summary>
        private static void LoadExtraBundle()
        {
            string path = FindBundlePath();
            if (path == null)
            {
                Log.Info("CAS pre-warm: no 'cas_assets' bundle next to the mod DLL (optional; " +
                         "fixed-wing CAS airframes then only load with a terrain scene).");
                return;
            }

            try
            {
                AssetBundle bundle = AssetBundle.LoadFromFile(path);
                if (bundle == null)
                {
                    Log.Warn("CAS pre-warm: could not load bundle '" + path + "' (wrong Unity version?).");
                    return;
                }

                UnityEngine.Object[] assets = bundle.LoadAllAssets();
                _casBundle = bundle;

                int prefabs = 0;
                int loadouts = 0;
                int hardpoints = 0;
                for (int i = 0; i < assets.Length; i++)
                {
                    UnityEngine.Object asset = assets[i];
                    if (asset == null)
                    {
                        continue;
                    }
                    _bundleAssets.Add(asset);

                    GameObject go = asset as GameObject;
                    if (go != null)
                    {
                        prefabs++;
                        _bundlePrefabs.Add(go);
                        TrackBundlePrefab(go);
                        if (go.GetComponentInChildren<CASHardpoint>(true) != null)
                        {
                            hardpoints++;
                        }
                    }
                    else if (asset is CASLoadoutScriptable)
                    {
                        loadouts++;
                    }
                }

                Log.Info("CAS pre-warm: loaded bundle '" + Path.GetFileName(path) + "' (" + DescribeBundleFile(path) +
                         ") -> " + assets.Length + " asset(s), " + prefabs + " prefab(s) (" + hardpoints +
                         " hardpoint(s)), " + loadouts +
                         " loadout(s); all CAS aircraft and hardpoints are now available in every mission.");
            }
            catch (Exception ex)
            {
                Log.Error("CAS pre-warm: bundle load failed: " + ex);
            }
        }

        /// <summary>
        /// Size and write time of the bundle file, so a log tells WHICH build of cas_assets is installed.
        /// A stale bundle is otherwise invisible: an old one with placeholder shaders renders smoke and
        /// flares as opaque white blocks (see the bundle notes in the README).
        /// </summary>
        private static string DescribeBundleFile(string path)
        {
            try
            {
                FileInfo info = new FileInfo(path);
                return (info.Length / (1024f * 1024f)).ToString("0.0") + " MB, " +
                       info.LastWriteTime.ToString("yyyy-MM-dd HH:mm");
            }
            catch (Exception)
            {
                return "size unknown";
            }
        }

        /// <summary>
        /// Looks for the bundle next to the mod DLL (both flat and in a CustomFireSupport subfolder,
        /// with or without a .bundle extension).
        /// </summary>
        private static string FindBundlePath()
        {
            string modDir;
            try
            {
                modDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            }
            catch (Exception)
            {
                return null;
            }
            if (string.IsNullOrEmpty(modDir))
            {
                return null;
            }

            string nested = Path.Combine(modDir, "CustomFireSupport");
            string[] candidates =
            {
                Path.Combine(modDir, "cas_assets"),
                Path.Combine(modDir, "cas_assets.bundle"),
                Path.Combine(nested, "cas_assets"),
                Path.Combine(nested, "cas_assets.bundle")
            };
            for (int i = 0; i < candidates.Length; i++)
            {
                if (File.Exists(candidates[i]))
                {
                    return candidates[i];
                }
            }
            return null;
        }

        /// <summary>
        /// "auto" expands to the known pre-warm keys (none at the moment: every CAS airframe, loadout and
        /// hardpoint comes from the bundled cas_assets pack, so "auto" simply means "nothing extra");
        /// anything else is split on commas / semicolons. Empty / null / whitespace returns null
        /// (feature off).
        /// </summary>
        private static string[] ParseKeys(string configured)
        {
            if (string.IsNullOrWhiteSpace(configured))
            {
                return null;
            }
            if (configured.Trim().ToLowerInvariant() == "auto")
            {
                return AutoKeys;
            }

            List<string> keys = new List<string>();
            string[] parts = configured.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < parts.Length; i++)
            {
                string key = parts[i].Trim();
                if (key.Length > 0)
                {
                    keys.Add(key);
                }
            }
            return keys.Count == 0 ? null : keys.ToArray();
        }
    }
}
