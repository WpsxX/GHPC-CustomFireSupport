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
        internal static readonly HashSet<Material> BundleMaterials = new HashSet<Material>();
        internal static readonly HashSet<Shader> BundleShaders = new HashSet<Shader>();
        // Prefabs are pinned for the session; their renderer hierarchy does not need rescanning.
        internal static readonly HashSet<Renderer> BundleRenderers = new HashSet<Renderer>();
        private static readonly HashSet<AmmoType> _bundleAmmo = new HashSet<AmmoType>(AmmoReferenceComparer.Instance);

        // ------------------------------------------------------------------
        // Name-keyed catalogue of the bundle's prefabs and loadouts.
        //
        // WHY THIS EXISTS (the authoritative source of CAS aircraft)
        //
        // The mod used to decide what an aircraft was by scanning whatever the current scene had in
        // memory (Resources.FindObjectsOfTypeAll<CASController>, the mission's CasAirframeUnit arrays)
        // and then checking "did this come from our bundle?" by reference against _bundleObjects. That
        // made the choice depend on what a particular scan happened to collect: the SAME airframe was
        // treated as a bundled prefab in one mission and as a foreign object in the next, and a slot
        // that drew the second case sent an aircraft whose CASController.Start() never ran - the call
        // produced nothing and the HUD never showed "Searching for targets".
        //
        // The bundle is deterministic: it is built from 8 airframe prefabs and 13 loadout assets and
        // every one of them is a real .prefab / .asset (verified at build time by CasBundleRebuild).
        // So the aircraft roster is read from HERE, by name, and never from the scene. "Is it ours?" is
        // then answered by lookup rather than by reference, which cannot vary between missions.
        // ------------------------------------------------------------------

        /// <summary>
        /// The 8 CAS airframes the bundle ships, keyed by the prefab name used inside the bundle.
        /// These are the models the mod may summon; nothing else is eligible.
        /// </summary>
        internal static readonly string[] BundleAirframeNames =
        {
            "A10", "F104", "F4_LW", "F4_USAF", "MiG17", "MiG21", "MiG23BN", "SU22"
        };

        private static readonly Dictionary<string, GameObject> _airframesByName =
            new Dictionary<string, GameObject>(StringComparer.OrdinalIgnoreCase);

        private static readonly Dictionary<string, CASLoadoutScriptable> _loadoutsByName =
            new Dictionary<string, CASLoadoutScriptable>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// True when this GameObject is one of the bundle's own airframe prefabs.
        ///
        /// This is the option-B roster rule in one predicate: the AIRFRAME must be a bundled prefab,
        /// while a LOADOUT may come from anywhere. A scene airframe is a live object whose clone never
        /// runs CASController.Start(), so it must never be selectable; a loadout is only ever read.
        ///
        /// Answers false when the catalogue is empty, so callers must gate on
        /// <see cref="HasBundleAirframes"/> first and treat "no catalogue" as a separate case.
        /// </summary>
        internal static bool IsBundledAirframe(GameObject prefab)
        {
            if (prefab == null || _airframesByName.Count == 0)
            {
                return false;
            }
            for (int i = 0; i < BundleAirframeNames.Length; i++)
            {
                GameObject bundled = AirframePrefab(BundleAirframeNames[i]);
                if (bundled != null && ReferenceEquals(bundled, prefab))
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// The bundled airframe prefab with this name, or null when the bundle does not ship it.
        /// Names are matched case-insensitively, so "F104" and "f104" are the same aircraft.
        /// </summary>
        internal static GameObject AirframePrefab(string name)
        {
            if (string.IsNullOrEmpty(name))
            {
                return null;
            }
            GameObject found;
            return _airframesByName.TryGetValue(name.Trim(), out found) ? found : null;
        }

        /// <summary>The bundled loadout asset with this name, or null when the bundle does not ship it.</summary>
        internal static CASLoadoutScriptable LoadoutAsset(string name)
        {
            if (string.IsNullOrEmpty(name))
            {
                return null;
            }
            CASLoadoutScriptable found;
            return _loadoutsByName.TryGetValue(name.Trim(), out found) ? found : null;
        }

        /// <summary>
        /// GameObjects read out of the mod's own bundle, resolved by name.
        /// Empty until the bundle has been pre-warmed, so callers must treat "not found yet" as
        /// "the bundle is not loaded", not as "the bundle does not have it".
        /// </summary>
        internal static bool HasBundleAirframes
        {
            get { return _airframesByName.Count > 0; }
        }

        /// <summary>Every airframe found in the bundle, in BundleAirframeNames order.</summary>
        internal static List<GameObject> BundleAirframePrefabs()
        {
            List<GameObject> result = new List<GameObject>();
            for (int i = 0; i < BundleAirframeNames.Length; i++)
            {
                GameObject prefab = AirframePrefab(BundleAirframeNames[i]);
                if (prefab != null)
                {
                    result.Add(prefab);
                }
            }
            return result;
        }

        /// <summary>Every loadout found in the bundle.</summary>
        internal static List<CASLoadoutScriptable> BundleLoadouts()
        {
            return new List<CASLoadoutScriptable>(_loadoutsByName.Values);
        }

        private static void IndexBundleAsset(UnityEngine.Object asset)
        {
            GameObject go = asset as GameObject;
            if (go != null)
            {
                // Only the 8 airframes are indexed under their own name; hardpoint/munition prefabs are
                // not aircraft and must never be selectable as one.
                for (int i = 0; i < BundleAirframeNames.Length; i++)
                {
                    if (string.Equals(go.name, BundleAirframeNames[i], StringComparison.OrdinalIgnoreCase))
                    {
                        _airframesByName[BundleAirframeNames[i]] = go;
                        break;
                    }
                }
                return;
            }

            CASLoadoutScriptable loadout = asset as CASLoadoutScriptable;
            if (loadout != null && !string.IsNullOrEmpty(loadout.name))
            {
                _loadoutsByName[loadout.name] = loadout;
            }
        }

        /// <summary>Reports which airframes the bundle actually delivered, and names any that are absent.</summary>
        private static void ReportAirframeCatalogue()
        {
            List<string> present = new List<string>();
            List<string> absent = new List<string>();
            for (int i = 0; i < BundleAirframeNames.Length; i++)
            {
                if (AirframePrefab(BundleAirframeNames[i]) != null)
                {
                    present.Add(BundleAirframeNames[i]);
                }
                else
                {
                    absent.Add(BundleAirframeNames[i]);
                }
            }

            Log.Info("CAS airframe catalogue: " + present.Count + "/" + BundleAirframeNames.Length +
                     " airframe(s) in the bundle [" + string.Join(", ", present.ToArray()) + "]; " +
                     _loadoutsByName.Count + " loadout(s).");

            if (absent.Count > 0)
            {
                // Loud, because a missing airframe means a slot can never be filled by that model and
                // the slot would otherwise look like a random failure.
                Log.Error("CAS airframe catalogue: MISSING from the bundle: " + string.Join(", ", absent.ToArray()) +
                          ". The bundle is incomplete - rebuild it with CasBundleRebuild (Unity CLI) before " +
                          "reporting a summon failure, because no mission can supply these models.");
            }
        }


        internal static bool IsBundledAmmo(AmmoType ammo)
        {
            return ammo != null && _bundleAmmo.Contains(ammo);
        }

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

            Material material = asset as Material;
            if (material != null) return BundleMaterials.Contains(material);
            Shader shader = asset as Shader;
            if (shader != null) return BundleShaders.Contains(shader);

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

            // Only renderers here: they are the thing no other pass can find. A prefab the bundle did not
            // list itself (the hardpoint -> AmmoCodexScriptable -> AmmoType.ShotVisual chain) contributes
            // its renderers to nothing else, while its MATERIALS are already covered by the before/after
            // scan in LoadExtraBundle - see the verification pass there, which reports whether the two
            // sources ever disagree.
            Renderer[] renderers = prefab.GetComponentsInChildren<Renderer>(true);
            for (int r = 0; r < renderers.Length; r++)
            {
                if (renderers[r] != null)
                {
                    BundleRenderers.Add(renderers[r]);
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
        /// Tracks one prefab an ammunition asset points at (its flight visual, shown model or one of its
        /// detonation prefabs). Idempotent: a prefab the bundle already listed, or one shared by several
        /// hardpoints, is counted only once. <paramref name="counter"/> is bumped for every prefab that was
        /// NOT already known, i.e. for the indirect dependencies this pass exists for.
        /// </summary>
        private static void TrackAmmoPrefab(GameObject prefab, ref int counter)
        {
            if (prefab == null || _bundleObjects.Contains(prefab))
            {
                return;
            }
            _bundlePrefabs.Add(prefab);
            TrackBundlePrefab(prefab);
            counter++;
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
                // Dependencies are not returned by LoadAllAssets. Record materials/shaders introduced
                // by this synchronous load, including the hardpoints' indirect ShotVisual prefabs.
                HashSet<Material> previousMaterials = new HashSet<Material>(Resources.FindObjectsOfTypeAll<Material>());
                HashSet<Shader> previousShaders = new HashSet<Shader>(Resources.FindObjectsOfTypeAll<Shader>());
                AssetBundle bundle = AssetBundle.LoadFromFile(path);
                if (bundle == null)
                {
                    Log.Warn("CAS pre-warm: could not load bundle '" + path + "' (wrong Unity version?).");
                    return;
                }

                UnityEngine.Object[] assets = bundle.LoadAllAssets();
                _casBundle = bundle;
                foreach (Material material in Resources.FindObjectsOfTypeAll<Material>())
                    if (material != null && !previousMaterials.Contains(material)) BundleMaterials.Add(material);
                foreach (Shader shader in Resources.FindObjectsOfTypeAll<Shader>())
                    if (shader != null && !previousShaders.Contains(shader)) BundleShaders.Add(shader);

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

                    // Index it for the name-keyed catalogue that the CAS slot builder reads its aircraft
                    // roster from (see BundleAirframeNames). This is what makes the roster independent of
                    // whatever the scene scan happens to find.
                    IndexBundleAsset(asset);

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

                // CAS munitions live outside the hardpoint transform hierarchy: a hardpoint only
                // references its AmmoCodexScriptable, and the round's objects hang off the AmmoType
                // inside it. LoadAllAssets() returns neither - the codex and everything it points at are
                // indirect dependencies of the hardpoint prefab - so the graph is walked explicitly here.
                //
                // All four references matter and none of them is optional:
                //   * ShotVisual          - the object CASHardpoint.SpawnMunition instantiates: the flying
                //                           rocket, with its motor flame, backblast and smoke trail. This is
                //                           the one the earlier version of this pass already followed.
                //   * VisualModel         - the round shown on the pylon of a visible-munitions hardpoint.
                //   * DetonateEffect      - the prefab the game instantiates when the round detonates.
                //   * TerrainImpactEffect - the same for a ground hit.
                // Reaching them is what puts their RENDERERS into BundleRenderers: that set is what the
                // TVE / heat-distortion handling walks, and a detonation prefab's distortion quad is not a
                // renderer any other pass would ever hand over. The materials come from the load scan
                // above, which - measured against the shipped cas_assets - already covers all 83 materials
                // of the full dependency graph while this traversal alone reaches 72 of them, so the scan
                // stays the authority and the pass below only reports a disagreement.
                int rootCount = _bundlePrefabs.Count;
                int airborne = 0;
                int impact = 0;
                for (int p = 0; p < rootCount; p++)
                {
                    foreach (CASHardpoint hardpoint in _bundlePrefabs[p].GetComponentsInChildren<CASHardpoint>(true))
                    {
                        // CASHardpoint._ammo is private in the shipped game assembly.  The
                        // publicized reference used at build time exposes it, but that does not
                        // change the runtime access check, so touching it here throws a
                        // FieldAccessException and aborts the entire CAS pre-warm.  Use the public
                        // Ammo property and treat an uninitialised hardpoint as an empty one.
                        AmmoType ammo;
                        try
                        {
                            ammo = hardpoint == null ? null : hardpoint.Ammo;
                        }
                        catch (Exception)
                        {
                            Log.Warn("CAS pre-warm: skipping hardpoint '" +
                                     (hardpoint == null ? "<null>" : hardpoint.name) +
                                     "' whose ammo could not be read.");
                            continue;
                        }
                        if (ammo == null) continue;
                        _bundleAmmo.Add(ammo);
                        TrackAmmoPrefab(ammo.ShotVisual, ref airborne);
                        TrackAmmoPrefab(ammo.VisualModel, ref airborne);
                        TrackAmmoPrefab(ammo.DetonateEffect, ref impact);
                        TrackAmmoPrefab(ammo.TerrainImpactEffect, ref impact);
                    }
                }
                // Verification pass. The before/after scan above is the authority on WHICH materials and
                // shaders are the bundle's (they are the objects that did not exist before the load); a
                // renderer the traversal reached can add nothing to it - but if it ever does, the two
                // sources disagree and the material would render with its exported placeholder shader. So
                // pick the stragglers up with the same criterion, and say so in the log.
                int lateMaterials = 0;
                int lateShaders = 0;
                foreach (Renderer renderer in BundleRenderers)
                {
                    if (renderer == null) continue;
                    Material[] materials = renderer.sharedMaterials;
                    for (int m = 0; m < materials.Length; m++)
                    {
                        Material material = materials[m];
                        if (material == null || previousMaterials.Contains(material)) continue;
                        if (BundleMaterials.Add(material)) lateMaterials++;
                        if (material.shader != null && !previousShaders.Contains(material.shader) &&
                            BundleShaders.Add(material.shader)) lateShaders++;
                    }
                }

                Log.Info("CAS bundle graph: " + _bundlePrefabs.Count + " prefab(s) tracked (" + prefabs +
                         " returned by the bundle, " + airborne + " round visual(s) and " + impact +
                         " detonation prefab(s) reached through " + _bundleAmmo.Count + " ammunition asset(s)); " +
                         BundleRenderers.Count + " renderer(s), " + BundleMaterials.Count + " material(s), " +
                         BundleShaders.Count + " shader(s) covered by the shader repair" +
                         (lateMaterials + lateShaders > 0
                             ? " (" + lateMaterials + " material(s) and " + lateShaders +
                               " shader(s) only the traversal reached)."
                             : " (the load scan and the traversal agree)."));

                // The aircraft roster is taken from this catalogue, so say up front what the bundle
                // actually delivered - a missing model would otherwise surface much later as a slot that
                // "randomly" sends nothing.
                ReportAirframeCatalogue();

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
