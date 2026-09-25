using System;
using System.Collections.Generic;
using System.Text;
using GHPC;
using GHPC.Vehicle;
using GHPC.Weaponry.CAS;
using HarmonyLib;
using UnityEngine;

namespace CustomFireSupport
{
    /// <summary>
    /// Finds aircraft / loadout templates for CAS slots.
    ///
    /// A mission scene without CAS has no CasAirframeUnit to clone, so this provider falls back to
    /// everything the game already has in memory:
    ///
    ///   1. the current mission's CasSupportManager airframes (best source, any faction),
    ///   2. the template cached from an earlier mission in this session,
    ///   3. every loaded CASController / CASHardpointManager (aircraft prefabs live in the bootstrap and
    ///      menu shared asset files, which GHPC loads additively and never unloads) paired with every
    ///      loaded CASLoadoutScriptable.
    ///
    /// Whatever is found in (1) is cached in static fields for later missions - Unity's
    /// Resources.UnloadUnusedAssets also walks static variables, so the prefab and its hardpoint
    /// references are kept alive instead of being unloaded with the scene.
    /// </summary>
    internal static class CasDonorProvider
    {
        private static readonly AccessTools.FieldRef<CASHardpointManager, CASLoadoutScriptable> HardpointManagerLoadoutRef =
            AccessTools.FieldRefAccess<CASHardpointManager, CASLoadoutScriptable>("Loadout");

        /// <summary>
        /// Airframe names worth trying when nothing else is available (GHPC's CAS aircraft). These
        /// match the real prefab assets shipped with the game (verified against the Unity project:
        /// GameObject/A10, F104, F4_LW, F4_USAF, MiG17, MiG21, MiG23BN, SU22).
        /// </summary>
        private static readonly string[] AirframeNameHints =
        {
            "a10", "a-10", "f104", "f-104", "f4", "f-4", "f15", "f-15", "su22", "su-22", "su25", "su-25",
            "mig17", "mig21", "mig23", "mig"
        };

        /// <summary>
        /// false while the fit check is being bypassed (see <see cref="Collect"/>: when no candidate
        /// survives it, the list is rebuilt with the check off rather than dropping the slot).
        /// </summary>
        private static bool _enforceFit = true;

        internal static List<CasTemplate> Collect(CasSupportManager manager, Faction playerFaction)
        {
            // Keep the hardpoint weapon library in sync with what this mission actually has in memory
            // (the mission's airframes + every loaded loadout). Slots that request an attack type the
            // chosen airframe does not ship with draw their hardpoints from it.
            CasAttackLibrary.Refresh(manager);

            List<CasTemplate> templates = Build(manager, playerFaction, true);

            if (templates.Count == 0)
            {
                // Every candidate was rejected by the hardpoint-fit check (see FitsAirframe). Retry
                // relaxed: an ill-fitting loadout makes the game skip DoConfig(), so the aircraft
                // cannot fire - bad, but still better than dropping the slot entirely.
                Log.Warn("no CAS template survived the airframe/loadout fit check; retrying without it " +
                         "(the aircraft may then fly its pass without firing).");
                templates = Build(manager, playerFaction, false);
            }

            if (templates.Count == 0)
            {
                Log.Warn("no CAS airframe template found: this mission has none and no aircraft prefab / loadout " +
                         "is currently loaded. Play any mission that has CAS once and the template is cached for the rest of the session.");
            }
            else
            {
                Log.Info("CAS templates: " + templates.Count + " candidate(s) collected (turn on VerboseLogging for the full list).");
                Log.Verbose("CAS templates: " + Describe(templates));
            }
            return templates;
        }

        private static List<CasTemplate> Build(CasSupportManager manager, Faction playerFaction, bool enforceFit)
        {
            _enforceFit = enforceFit;
            try
            {
                List<CasTemplate> templates = new List<CasTemplate>();

                // ============================ OPTION B ROSTER RULE ============================
                //
                // THE AIRFRAME (the prefab) MAY ONLY EVER COME FROM THE BUNDLE. The PAYLOAD (the loadout
                // asset) may additionally come from the mission scene, so slots keep the variety of
                // mounting something the bundle does not ship.
                //
                // Why the split: the failure that made calls "sometimes spawn nothing" was in the PREFAB,
                // not the loadout. An airframe taken from the scene is a live object; instantiating one
                // clones something whose CASController.Start() never runs, so the aircraft appears (or
                // not) but never engages. A loadout is a ScriptableObject describing pylons - it is read,
                // never instantiated as the aircraft - so a scene loadout is safe to use.
                //
                // The previous version called AddBundleAirframes() and then ALSO added the scene
                // airframes, so the bundle merely enlarged the pool instead of defining it. Scene
                // prefabs stayed selectable and were still drawn occasionally - which is exactly the
                // "small chance of no aircraft" that this replaces. The scene is now consulted for
                // LOADOUTS ONLY.
                // =============================================================================
                int fromBundle = AddBundleAirframes(templates);

                if (fromBundle == 0)
                {
                    // No bundle roster. The old scene path is the only thing left that can fill a slot,
                    // and it is known to be unreliable, so it is used (a slot that might send nothing
                    // beats a slot that definitely sends nothing) but reported loudly.
                    Log.Error("CAS roster: the cas_assets catalogue is empty, so this mission falls back to " +
                              "the SCENE scan. Aircraft from the scene may be cloned objects whose " +
                              "CASController.Start() never runs, which is the known cause of a CAS call " +
                              "that spawns nothing. Check the 'CAS airframe catalogue' / 'CAS pre-warm' " +
                              "lines above.");
                    if (manager != null)
                    {
                        AddSceneAirframes(templates, manager.BlueCasAirframes, Faction.Blue);
                        AddSceneAirframes(templates, manager.RedCasAirframes, Faction.Red);
                    }
                    ScanLoadedObjects(templates);
                    return templates;
                }

                // Supplement with the mission's OWN loadouts, paired onto the bundle's airframes only.
                int before = templates.Count;
                int sceneLoadouts = AddSceneLoadouts(templates);
                if (sceneLoadouts > 0)
                {
                    Log.Verbose("CAS roster: " + sceneLoadouts + " extra loadout pairing(s) from the mission " +
                                "scene added onto the bundle airframes (" + (templates.Count - before) +
                                " template(s)).");
                }

                Log.Verbose("CAS roster: " + fromBundle + " bundle template(s), " + templates.Count +
                            " in total; every AIRFRAME is a bundled prefab asset.");
                return templates;
            }
            finally
            {
                _enforceFit = true;
            }
        }

        /// <summary>
        /// Pares each loadout the mission itself has in memory onto the BUNDLE's airframes.
        ///
        /// Only loadouts cross over - the airframe loop is driven by the bundle catalogue, so nothing
        /// here can introduce a scene prefab. This is what preserves "mount a payload the bundle does
        /// not ship" without reintroducing the unreliable scene airframe.
        ///
        /// Returns the number of extra templates contributed.
        /// </summary>
        private static int AddSceneLoadouts(List<CasTemplate> templates)
        {
            List<CASLoadoutScriptable> sceneLoadouts = new List<CASLoadoutScriptable>();

            try
            {
                CASLoadoutScriptable[] loaded = Resources.FindObjectsOfTypeAll<CASLoadoutScriptable>();
                for (int i = 0; i < loaded.Length; i++)
                {
                    CASLoadoutScriptable loadout = loaded[i];
                    if (loadout == null || loadout.Loadout == null || loadout.Loadout.HardpointPrefabs == null ||
                        loadout.Loadout.HardpointPrefabs.Length == 0)
                    {
                        continue;
                    }
                    bool known = false;
                    for (int j = 0; j < sceneLoadouts.Count; j++)
                    {
                        if (ReferenceEquals(sceneLoadouts[j], loadout))
                        {
                            known = true;
                            break;
                        }
                    }
                    if (!known)
                    {
                        sceneLoadouts.Add(loadout);
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Warn("CAS roster: could not read the mission's loadouts: " + ex.Message);
                return 0;
            }

            if (sceneLoadouts.Count == 0)
            {
                return 0;
            }

            List<GameObject> airframes = CasPrewarmer.BundleAirframePrefabs();
            int added = 0;

            for (int i = 0; i < airframes.Count; i++)
            {
                GameObject prefab = airframes[i];
                if (prefab == null)
                {
                    continue;
                }
                CASHardpointManager manager = prefab.GetComponentInChildren<CASHardpointManager>(true);
                if (manager == null)
                {
                    continue;
                }

                for (int j = 0; j < sceneLoadouts.Count; j++)
                {
                    // AddCandidate drops anything that does not fit this airframe, and skips pairs that
                    // are already in the list, so re-adding a bundled loadout here is harmless.
                    added += AddCandidate(templates, prefab, sceneLoadouts[j], Faction.Neutral,
                        "bundle airframe + mission loadout", true, manager);
                }
            }
            return added;
        }

        /// <summary>
        /// Adds one template per bundled airframe x bundled loadout that fits it, straight from the
        /// name-keyed catalogue (CasPrewarmer.BundleAirframeNames / AirframePrefab / LoadoutAsset).
        ///
        /// These are guaranteed to be prefab ASSETS rather than scene instances, which is what makes the
        /// aircraft actually reach CASController.Start(). Returns how many templates were added.
        /// </summary>
        private static int AddBundleAirframes(List<CasTemplate> templates)
        {
            if (!CasPrewarmer.HasBundleAirframes)
            {
                // The bundle has not been pre-warmed yet (or failed to load). Fall back to the scene,
                // which is the old - less reliable - path. Reported because it is the state in which
                // "the aircraft sometimes does not appear" can still happen.
                Log.Warn("CAS roster: the cas_assets catalogue is not loaded yet; falling back to the scene " +
                         "scan for this mission. If CAS calls spawn nothing, this is why - the bundle failed " +
                         "to pre-warm (see the 'CAS pre-warm' lines above).");
                return 0;
            }

            List<GameObject> airframes = CasPrewarmer.BundleAirframePrefabs();
            List<CASLoadoutScriptable> loadouts = CasPrewarmer.BundleLoadouts();
            int added = 0;

            for (int i = 0; i < airframes.Count; i++)
            {
                GameObject prefab = airframes[i];
                if (prefab == null)
                {
                    continue;
                }

                CASHardpointManager manager = prefab.GetComponentInChildren<CASHardpointManager>(true);
                if (manager == null)
                {
                    // Without a hardpoint manager the aircraft cannot mount or fire anything, and
                    // CASController.Start() would fail on it. The bundle builder already rejects this,
                    // so reaching here means a hand-edited bundle.
                    Log.Error("CAS roster: bundled airframe '" + prefab.name + "' has no CASHardpointManager; " +
                              "it cannot fire and is skipped. Rebuild the bundle with CasBundleRebuild.");
                    continue;
                }

                // The airframe's own loadout first, so the slot can fly the model's real pylons.
                CASLoadoutScriptable own = null;
                try
                {
                    own = HardpointManagerLoadoutRef(manager);
                }
                catch (Exception ex)
                {
                    Log.Verbose("could not read '" + prefab.name + "'s own loadout: " + ex.Message);
                }

                if (own != null && own.Loadout != null && own.Loadout.HardpointPrefabs != null &&
                    own.Loadout.HardpointPrefabs.Length > 0)
                {
                    added += AddCandidate(templates, prefab, own, Faction.Neutral, "bundled airframe", true, manager);
                }

                // Cross-pairs stay on offer: mounting a type the model does not ship with is how a rocket
                // slot finds a loadout when the airframe has none of its own. FitsAirframe still has to
                // accept the pair, and an airframe flying its own loadout is preferred.
                for (int j = 0; j < loadouts.Count; j++)
                {
                    CASLoadoutScriptable loadout = loadouts[j];
                    if (loadout == null || ReferenceEquals(loadout, own) || loadout.Loadout == null ||
                        loadout.Loadout.HardpointPrefabs == null || loadout.Loadout.HardpointPrefabs.Length == 0)
                    {
                        continue;
                    }
                    added += AddCandidate(templates, prefab, loadout, Faction.Neutral, "bundled airframe", true, manager);
                }
            }

            return added;
        }

        // ------------------------------------------------------------------
        // Emergency scene fallback (only used when the bundle roster is empty)
        // ------------------------------------------------------------------

        private static void AddSceneAirframes(List<CasTemplate> templates, CasAirframeUnit[] airframes, Faction faction)
        {
            if (airframes == null)
            {
                return;
            }

            for (int i = 0; i < airframes.Length; i++)
            {
                CasAirframeUnit airframe = airframes[i];
                if (airframe == null || airframe.airframePrefab == null || airframe.Loadout == null || airframe.Loadout.Loadout == null)
                {
                    continue;
                }

                AddCandidate(templates, airframe.airframePrefab, airframe.Loadout, faction,
                    "mission scene", true);
            }
        }

        // ------------------------------------------------------------------
        // 3. Everything currently loaded
        // ------------------------------------------------------------------

        private static void ScanLoadedObjects(List<CasTemplate> templates)
        {
            List<CASLoadoutScriptable> loadouts = new List<CASLoadoutScriptable>();
            CASLoadoutScriptable[] loadedLoadouts = Resources.FindObjectsOfTypeAll<CASLoadoutScriptable>();
            for (int i = 0; i < loadedLoadouts.Length; i++)
            {
                CASLoadoutScriptable loadout = loadedLoadouts[i];
                if (loadout != null && loadout.Loadout != null && loadout.Loadout.HardpointPrefabs != null &&
                    loadout.Loadout.HardpointPrefabs.Length > 0)
                {
                    loadouts.Add(loadout);
                }
            }

            List<GameObject> donors = new List<GameObject>();
            List<bool> donorIsAsset = new List<bool>();

            CASController[] controllers = Resources.FindObjectsOfTypeAll<CASController>();
            for (int i = 0; i < controllers.Length; i++)
            {
                CASController controller = controllers[i];
                if (controller == null)
                {
                    continue;
                }
                AddDonor(donors, donorIsAsset, controller.gameObject);
            }

            CASHardpointManager[] managers = Resources.FindObjectsOfTypeAll<CASHardpointManager>();
            for (int i = 0; i < managers.Length; i++)
            {
                CASHardpointManager manager = managers[i];
                if (manager == null)
                {
                    continue;
                }
                // Only take managers that belong to a real aircraft. The scene also contains bare
                // hardpoint test objects ("HardpointHolder", "Hardpoint manager", ...) whose manager
                // has no CASController and no airframe name; pairing every loaded loadout with those
                // produced a huge list of bogus templates in the mission log.
                if (!BelongsToAirframe(manager.gameObject))
                {
                    continue;
                }
                AddDonor(donors, donorIsAsset, manager.gameObject);
            }

            // There used to be a name-matched "safety net" here that walked
            // Resources.FindObjectsOfTypeAll<GameObject>() whenever the typed scans above found nothing.
            // It is gone on purpose: from the second mission of a session that array also holds wrappers
            // whose native object is already gone, and reading .name / GetComponentInChildren on one of
            // them is a native access violation that kills the process - see
            // FireSupportTemplates.FindArtilleryEffectPrefab. It was also redundant: the airframe prefabs
            // come from the pinned cas_assets bundle and are already covered by the typed scans above.

            Log.Verbose("CAS donor scan: " + donors.Count + " airframe object(s), " + loadouts.Count + " loadout asset(s) loaded.");


            for (int i = 0; i < donors.Count; i++)
            {
                GameObject donor = donors[i];
                CASHardpointManager manager = donor.GetComponentInChildren<CASHardpointManager>(true);

                CASLoadoutScriptable own = null;
                if (manager != null)
                {
                    try
                    {
                        own = HardpointManagerLoadoutRef(manager);
                    }
                    catch (Exception ex)
                    {
                        Log.Verbose("could not read the airframe's own loadout: " + ex.Message);
                    }
                }

                if (own != null && own.Loadout != null && own.Loadout.HardpointPrefabs != null && own.Loadout.HardpointPrefabs.Length > 0)
                {
                    AddCandidate(templates, donor, own, Faction.Neutral,
                        "loaded airframe" + (donorIsAsset[i] ? " prefab" : string.Empty), donorIsAsset[i], manager);
                }

                // Cross-pairs (this airframe + some OTHER airframe's loadout) stay on offer: that is how a
                // slot can mount a type the airframe does not ship with. FitsAirframe still has to accept
                // the pair, and the draw pool prefers an airframe flying its OWN loadout, so a cross-pair
                // is only ever reached when nothing better exists.
                for (int j = 0; j < loadouts.Count; j++)
                {
                    if (ReferenceEquals(loadouts[j], own))
                    {
                        continue;
                    }
                    AddCandidate(templates, donor, loadouts[j], Faction.Neutral,
                        "loaded airframe" + (donorIsAsset[i] ? " prefab" : string.Empty), donorIsAsset[i], manager);
                }

                if (own == null && loadouts.Count == 0)
                {
                    Log.Verbose("loaded airframe '" + donor.name + "' has no usable loadout asset - skipped.");
                }
            }
        }
        /// <summary>
        /// True when the object (or an ancestor) is a real CAS aircraft: it either carries a
        /// CASController somewhere in its hierarchy or its own / an ancestor's name matches a known
        /// airframe (name hints or the side catalog). This keeps bare hardpoint test objects out of
        /// the donor list.
        /// </summary>
        private static bool BelongsToAirframe(GameObject candidate)
        {
            if (candidate == null)
            {
                return false;
            }
            if (candidate.GetComponentInChildren<CASController>(true) != null)
            {
                return true;
            }

            Transform t = candidate.transform;
            while (t != null)
            {
                if (NameLooksLikeAirframe(t.name) ||
                    CasAirframeCatalog.GuessSide(t.name) != AirframeSide.Unknown)
                {
                    return true;
                }
                t = t.parent;
            }
            return false;
        }

        /// <summary>
        /// Adds a donor after resolving it to the full aircraft root.
        ///
        /// A donor reached through CASHardpointManager is usually a child node: in the shipped
        /// prefabs the CASController sits on the aircraft root while the manager sits on a child
        /// ("A10/Hardpoint manager", "SU22/HardpointHolder"). Vanilla CasSupportManager.SendCasSupport
        /// instantiates the prefab and immediately does GetComponent&lt;CASController&gt;() on the clone,
        /// so a child node is unusable twice over: there is no controller at its root
        /// (NullReferenceException - the call fails) and its HardpointAttachPoints reference transforms
        /// that live outside the clone (the airframe can never mount anything).
        ///
        /// Only objects whose own root (or an ancestor) owns a CASController are kept; bare hardpoint
        /// rigs and loadout holders are dropped here instead of producing a template that cannot fly.
        /// </summary>
        private static void AddDonor(List<GameObject> donors, List<bool> donorIsAsset, GameObject candidate)
        {
            GameObject root = ResolveAirframeRoot(candidate);
            if (root == null)
            {
                return;
            }
            for (int i = 0; i < donors.Count; i++)
            {
                if (ReferenceEquals(donors[i], root))
                {
                    return;
                }
            }
            donors.Add(root);
            // Asset or live instance? Two native tests have now proven fatal here or nearby, so neither is
            // used any more: gameObject.scene.IsValid() hard-crashed the game (GameObject.get_scene), and
            // root.transform.parent did the same on the first map open after a mission restart
            // (Transform.get_parent - see FireSupportTemplates.FindArtilleryEffectPrefab). The bundle
            // membership test is a pure managed HashSet lookup and it answers the only question that
            // matters: "will this object still be alive next mission?" - which is exactly what this flag
            // gates (the session cache). It only ever breaks a tie otherwise, never correctness.
            donorIsAsset.Add(CasPrewarmer.IsFromOurBundle(root));
        }

        /// <summary>
        /// The aircraft root for a scanned object: the GameObject that owns the CASController, which is
        /// what vanilla SendCasSupport requires on the instantiated prefab. Returns null for objects
        /// that are not part of a CAS aircraft (bare hardpoint test rigs, loadout holders, ...).
        /// </summary>
        private static GameObject ResolveAirframeRoot(GameObject candidate)
        {
            if (candidate == null)
            {
                return null;
            }

            CASController controller = candidate.GetComponentInParent<CASController>(true);
            if (controller != null)
            {
                return controller.gameObject;
            }

            return candidate.GetComponent<CASController>() != null ? candidate : null;
        }

        private static bool NameLooksLikeAirframe(string name)
        {
            if (string.IsNullOrEmpty(name))
            {
                return false;
            }
            string lower = name.ToLowerInvariant();
            for (int i = 0; i < AirframeNameHints.Length; i++)
            {
                if (lower.Contains(AirframeNameHints[i]))
                {
                    return true;
                }
            }
            return false;
        }

        // ------------------------------------------------------------------
        // Candidate list helpers
        // ------------------------------------------------------------------

        private static int AddCandidate(
            List<CasTemplate> templates,
            GameObject prefab,
            CASLoadoutScriptable loadout,
            Faction faction,
            string source,
            bool isAsset)
        {
            return AddCandidate(templates, prefab, loadout, faction, source, isAsset,
                prefab != null ? prefab.GetComponentInChildren<CASHardpointManager>(true) : null);
        }

        private static int AddCandidate(
            List<CasTemplate> templates,
            GameObject prefab,
            CASLoadoutScriptable loadout,
            Faction faction,
            string source,
            bool isAsset,
            CASHardpointManager manager)
        {
            if (prefab == null || loadout == null || loadout.Loadout == null)
            {
                return 0;
            }

            // HARD ROSTER GUARD (option B): an airframe that is not one of the bundle's own prefabs may
            // not enter the pool at all.
            //
            // Every candidate is checked here rather than at each call site, because this is the single
            // door into the template list - so no future path can quietly reintroduce a scene airframe.
            // A scene airframe is a LIVE object: Instantiating it clones something whose
            // CASController.Start() does not run, which is the known cause of a CAS call that sends
            // nothing. Loadouts are deliberately NOT restricted this way (a loadout is only read).
            //
            // The bundle may exist without the catalogue being built (an older cas_assets), in which
            // case IsBundledAirframe cannot answer and the guard stays out of the way rather than
            // emptying every slot.
            if (CasPrewarmer.HasBundleAirframes && !CasPrewarmer.IsBundledAirframe(prefab))
            {
                Log.Verbose("CAS template skipped: airframe '" + prefab.name + "' (" + source +
                            ") is not one of the bundle's prefabs, so it is not eligible (" +
                            CasPrewarmer.BundleAirframeNames.Length + " bundled airframe(s) only).");
                return 0;
            }

            // An airframe without a CASHardpointManager can never be configured: CASController.SetLoadout
            // logs "no CAS Hardpoint Manager was found" and returns before DoConfig(), so the aircraft
            // flies its pass and never fires. Drop it instead of offering a dead sortie.
            if (manager == null)
            {
                Log.Verbose("CAS template skipped: airframe '" + prefab.name +
                            "' carries no CASHardpointManager, so it could never fire.");
                return 0;
            }

            // Reject airframe/loadout pairs the game cannot configure.
            //
            // CASHardpointManager.HasCriticalConfigError() rejects a loadout whose HardpointPrefabs
            // list is neither a single entry nor one entry per attach point. DoConfig() then returns
            // before SetUpHardpoints(), so _configDone stays false and every Fire() call is refused
            // with "aborted attack type ... due to being unconfigured" - the aircraft flies its pass
            // and never drops anything. That is exactly what happens when a scanned aircraft prefab
            // is paired with an unrelated loadout asset, so the fit is checked here instead.
            if (_enforceFit && !FitsAirframe(manager, loadout.Loadout))
            {
                Log.Verbose("CAS template skipped: loadout '" + loadout.name + "' (" +
                            Count(loadout.Loadout.HardpointPrefabs) + " hardpoint prefab(s)) does not fit airframe '" +
                            prefab.name + "' (" + AttachCount(manager) + " attach point(s)).");
                return 0;
            }

            for (int i = 0; i < templates.Count; i++)
            {
                if (ReferenceEquals(templates[i].Prefab, prefab) && ReferenceEquals(templates[i].Loadout, loadout))
                {
                    return 0;
                }
            }

            string name = string.IsNullOrEmpty(prefab.name) ? "(unnamed airframe)" : prefab.name;

            // A mission airframe knows its faction; a prefab found by scanning the loaded assets does not,
            // so fall back to the name table extracted from the game's aircraft assets.
            Faction effectiveFaction = faction;
            if (effectiveFaction == Faction.Neutral)
            {
                effectiveFaction = FireSupportTemplates.ToFaction(CasAirframeCatalog.GuessSide(name));
            }

            templates.Add(new CasTemplate
            {
                Prefab = prefab,
                Loadout = loadout,
                Name = name,
                LoadoutName = string.IsNullOrEmpty(loadout.name) ? "(unnamed loadout)" : loadout.name,
                Faction = effectiveFaction,
                AvailableAttacks = FireSupportTemplates.CollectAttackTypes(loadout.Loadout),
                MountedAttacks = CollectMountedAttacks(loadout.Loadout),
                HardpointCount = Count(loadout.Loadout.HardpointPrefabs),
                AttachPointCount = AttachCount(manager),
                Source = source,
                IsAsset = isAsset
            });
            return 1;
        }

        /// <summary>
        /// True when the game can actually mount this loadout on this airframe. Mirrors
        /// CASHardpointManager.HasCriticalConfigError(): the prefab list must be non-empty and must be
        /// either a single entry (reused on every attach point) or at least one entry per attach point.
        /// </summary>
        private static bool FitsAirframe(CASHardpointManager manager, CASLoadout loadout)
        {
            if (loadout == null || loadout.HardpointPrefabs == null || loadout.HardpointPrefabs.Length == 0)
            {
                return false;
            }

            // No measurable attach points on the donor: the fit CANNOT be decided, and an undecidable
            // pair is exactly the one that spawns nothing. The game's own check is
            // "HardpointPrefabs.Length < HardpointAttachPoints.Length && Length > 1", and with an
            // unknown attach-point count that comparison silently passes here while the real
            // CASHardpointManager may still refuse to configure the sortie. Reject it instead: the
            // caller falls back to the unvalidated list and reports it, rather than shipping a pair
            // that looks fine and comes back empty.
            if (manager == null || manager.HardpointAttachPoints == null ||
                manager.HardpointAttachPoints.Length == 0)
            {
                return false;
            }

            int prefabs = loadout.HardpointPrefabs.Length;
            int attachPoints = manager.HardpointAttachPoints.Length;
            return prefabs == 1 || prefabs >= attachPoints;
        }

        /// <summary>
        /// Returns only attack types physically mounted by the loadout.  CASAttackMeta is intentionally
        /// kept separate: GHPC uses it to choose an attack, but CASHardpointManager.CanDoAttackType()
        /// ultimately checks the instantiated hardpoints, so a metadata-only entry cannot fire.
        /// </summary>
        private static AttackKind[] CollectMountedAttacks(CASLoadout loadout)
        {
            List<AttackKind> mounted = new List<AttackKind>();
            if (loadout == null || loadout.HardpointPrefabs == null)
            {
                return mounted.ToArray();
            }

            for (int i = 0; i < loadout.HardpointPrefabs.Length; i++)
            {
                GameObject prefab = loadout.HardpointPrefabs[i];
                CASHardpoint hardpoint = prefab == null
                    ? null
                    : prefab.GetComponentInChildren<CASHardpoint>(true);
                if (!FireSupportTemplates.IsUsableHardpoint(hardpoint))
                {
                    continue;
                }

                AttackKind kind;
                if (FireSupportTemplates.TryFromGameAttack(hardpoint.Type, out kind) && !mounted.Contains(kind))
                {
                    mounted.Add(kind);
                }
            }
            return mounted.ToArray();
        }

        private static int Count(GameObject[] array)
        {
            return array == null ? 0 : array.Length;
        }

        private static int AttachCount(CASHardpointManager manager)
        {
            return manager == null || manager.HardpointAttachPoints == null ? 0 : manager.HardpointAttachPoints.Length;
        }

        private static string Describe(List<CasTemplate> templates)
        {
            StringBuilder builder = new StringBuilder();
            for (int i = 0; i < templates.Count; i++)
            {
                if (i > 0)
                {
                    builder.Append("; ");
                }
                CasTemplate template = templates[i];
                AirframeInfo catalogInfo = CasAirframeCatalog.Match(template.Name);
                builder.Append('\'').Append(template.Name).Append("' + loadout '").Append(template.LoadoutName)
                    .Append("' [").Append(template.Faction).Append(", ")
                    .Append(template.IsAsset ? "prefab" : "instance").Append(", ")
                    .Append(template.Source).Append(", attacks=")
                    .Append(FireSupportTemplates.DescribeAttacks(template.AvailableAttacks));
                if (catalogInfo != null)
                {
                    builder.Append(", catalog=").Append(catalogInfo.Side).Append('/').Append(catalogInfo.Flyover)
                        .Append('/').Append(FireSupportTemplates.DescribeAttacks(catalogInfo.TypicalAttacks));
                }
                builder.Append(']');
            }
            return builder.ToString();
        }
    }
}
