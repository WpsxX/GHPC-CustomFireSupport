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
        /// Airframe + loadout pairs seen in earlier missions of this session. Prefab assets referenced
        /// by a mission's CasAirframeUnit (and the ones found by the loaded-object scan) survive scene
        /// unloads, so they stay usable for the rest of the session - this is what makes every aircraft
        /// that any played mission offered available in later missions too.
        /// </summary>
        private sealed class CachedTemplate
        {
            internal GameObject Prefab;
            internal CASLoadoutScriptable Loadout;
            internal string Source;
        }

        private static readonly List<CachedTemplate> _cache = new List<CachedTemplate>();

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

                if (manager != null)
                {
                    AddSceneAirframes(templates, manager.BlueCasAirframes, Faction.Blue);
                    AddSceneAirframes(templates, manager.RedCasAirframes, Faction.Red);
                }

                // Remember every scene airframe (prefab asset + its loadout) for later missions.
                for (int i = 0; i < templates.Count; i++)
                {
                    if (templates[i].Loadout != null)
                    {
                        Cache(templates[i].Prefab, templates[i].Loadout, "scene '" + templates[i].Name + "'");
                    }
                }

                // Always supplement from the session cache and the loaded assets, not just when the
                // player's own faction is missing. This keeps every airframe (including the GunRun
                // preference A-10 / MiG-23BN) available even when the mission only carries other
                // aircraft, and keeps the template list complete for the auto-by-faction selection.
                {
                    int before = templates.Count;

                    for (int i = 0; i < _cache.Count; i++)
                    {
                        CachedTemplate cached = _cache[i];
                        if (cached.Prefab == null || cached.Loadout == null)
                        {
                            continue; // destroyed (a scene object that was unloaded): skip
                        }
                        AddCandidate(templates, cached.Prefab, cached.Loadout, Faction.Neutral,
                            "session cache from " + cached.Source, false);
                    }

                    ScanLoadedObjects(templates);

                    if (templates.Count > before)
                    {
                        Log.Verbose("CAS donor scan added " + (templates.Count - before) + " template(s).");
                    }
                }

                return templates;
            }
            finally
            {
                _enforceFit = true;
            }
        }

        private static void Cache(GameObject prefab, CASLoadoutScriptable loadout, string source)
        {
            if (prefab == null || loadout == null)
            {
                return;
            }
            for (int i = 0; i < _cache.Count; i++)
            {
                if (ReferenceEquals(_cache[i].Prefab, prefab) && ReferenceEquals(_cache[i].Loadout, loadout))
                {
                    return;
                }
            }
            _cache.Add(new CachedTemplate { Prefab = prefab, Loadout = loadout, Source = source });
            Log.Verbose("CAS template cached for later missions: '" + prefab.name + "' + loadout '" + loadout.name +
                        "' (" + source + "; " + _cache.Count + " cached)");
        }

        // ------------------------------------------------------------------
        // 1. Mission scene airframes
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

        private static void AddCandidate(
            List<CasTemplate> templates,
            GameObject prefab,
            CASLoadoutScriptable loadout,
            Faction faction,
            string source,
            bool isAsset)
        {
            AddCandidate(templates, prefab, loadout, faction, source, isAsset,
                prefab != null ? prefab.GetComponentInChildren<CASHardpointManager>(true) : null);
        }

        private static void AddCandidate(
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
                return;
            }

            // An airframe without a CASHardpointManager can never be configured: CASController.SetLoadout
            // logs "no CAS Hardpoint Manager was found" and returns before DoConfig(), so the aircraft
            // flies its pass and never fires. Drop it instead of offering a dead sortie.
            if (manager == null)
            {
                Log.Verbose("CAS template skipped: airframe '" + prefab.name +
                            "' carries no CASHardpointManager, so it could never fire.");
                return;
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
                return;
            }

            // A prefab asset that fits is worth keeping for the rest of the session (scene instances
            // are not: they are destroyed when their scene unloads).
            if (isAsset)
            {
                Cache(prefab, loadout, source);
            }

            for (int i = 0; i < templates.Count; i++)
            {
                if (ReferenceEquals(templates[i].Prefab, prefab) && ReferenceEquals(templates[i].Loadout, loadout))
                {
                    return;
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
                HardpointCount = Count(loadout.Loadout.HardpointPrefabs),
                AttachPointCount = AttachCount(manager),
                Source = source,
                IsAsset = isAsset
            });
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

            // No hardpoint manager on the donor (e.g. a partially loaded prefab): nothing to check.
            if (manager == null || manager.HardpointAttachPoints == null || manager.HardpointAttachPoints.Length == 0)
            {
                return true;
            }

            int prefabs = loadout.HardpointPrefabs.Length;
            int attachPoints = manager.HardpointAttachPoints.Length;
            return prefabs == 1 || prefabs >= attachPoints;
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
