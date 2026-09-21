using System.Collections.Generic;
using System.Text;
using GHPC.Vehicle;
using GHPC.Weaponry.CAS;
using UnityEngine;

namespace CustomFireSupport
{
    /// <summary>
    /// Session-wide index of every CASHardpoint prefab reachable from the loadouts that are currently
    /// loaded. A CAS aircraft can only perform attack types whose hardpoints are physically mounted
    /// (CASHardpointManager.CanDoAttackType consults the mounted CASHardpoint.Type flags), so a
    /// requested type like GunRun needs a gun hardpoint on the pylons - it is not enough to append a
    /// CASAttackMeta entry. This library is what lets a slot mount a type the chosen airframe's own
    /// loadout does not ship with.
    ///
    /// GHPC has no global asset database, so the index is rebuilt from whatever is in memory at
    /// mission-build time: the mission's own airframe loadouts first, then every CASLoadoutScriptable
    /// asset that happens to be loaded (the same sources CasDonorProvider uses).
    /// </summary>
    internal static class CasAttackLibrary
    {
        private static readonly Dictionary<CASAttackType, List<GameObject>> _hardpoints =
            new Dictionary<CASAttackType, List<GameObject>>();

        private static bool _refreshed;

        /// <summary>Drops the previous index. Called when a new mission starts building its slots.</summary>
        internal static void Reset()
        {
            _hardpoints.Clear();
            _refreshed = false;
        }

        /// <summary>
        /// Indexes hardpoint prefabs from the given CAS manager's airframes and from all loaded
        /// CASLoadoutScriptable assets. Only runs once per mission (see Reset).
        /// </summary>
        internal static void Refresh(CasSupportManager manager)
        {
            if (_refreshed)
            {
                return;
            }
            _refreshed = true;

            if (manager != null)
            {
                IndexAirframes(manager.BlueCasAirframes);
                IndexAirframes(manager.RedCasAirframes);
            }

            CASLoadoutScriptable[] loadouts = Resources.FindObjectsOfTypeAll<CASLoadoutScriptable>();
            for (int i = 0; i < loadouts.Length; i++)
            {
                IndexLoadout(loadouts[i]);
            }

            StringBuilder builder = new StringBuilder();
            foreach (KeyValuePair<CASAttackType, List<GameObject>> pair in _hardpoints)
            {
                if (builder.Length > 0)
                {
                    builder.Append("; ");
                }
                builder.Append(pair.Key).Append(" x").Append(pair.Value.Count);
            }
            Log.Verbose("CAS hardpoint library: " + (builder.Length == 0 ? "empty" : builder.ToString()));

            LogSessionSurveyOnce();
        }

        /// <summary>
        /// Once per session, prints what attack types the game's own content can actually deliver and
        /// which loaded AssetBundles could be pinned to keep scene-only assets alive across missions.
        /// This is the evidence that decides between "harvest and pin bundles" and "construct gun
        /// hardpoints at runtime": if no GunRun hardpoint is ever seen in loaded content, harvesting
        /// scenes can never produce a gun run and only runtime construction can.
        /// </summary>
        private static bool _surveyLogged;

        private static void LogSessionSurveyOnce()
        {
            if (_surveyLogged)
            {
                return;
            }
            _surveyLogged = true;

            bool gunRun = false;
            StringBuilder types = new StringBuilder();
            foreach (KeyValuePair<CASAttackType, List<GameObject>> pair in _hardpoints)
            {
                if (pair.Key == CASAttackType.GunRun && pair.Value.Count > 0)
                {
                    gunRun = true;
                }
                if (types.Length > 0)
                {
                    types.Append(", ");
                }
                types.Append(pair.Key).Append(" x").Append(pair.Value.Count);
                if (pair.Value.Count > 0)
                {
                    types.Append(" [").Append(pair.Value[0].name);
                    if (pair.Value.Count > 1)
                    {
                        types.Append(" ...");
                    }
                    types.Append(']');
                }
            }

            Log.Info("CAS hardpoint survey: " + (types.Length == 0
                ? "no hardpoints in any loaded content yet"
                : "loaded content can deliver " + types));

            // Which AssetBundles are alive right now - pinning one of these is the only way to keep
            // scene-referenced (non-addressable) CAS assets alive after the mission unloads.
            StringBuilder bundles = new StringBuilder();
            int bundleCount = 0;
            IEnumerable<AssetBundle> loaded = AssetBundle.GetAllLoadedAssetBundles();
            foreach (AssetBundle bundle in loaded)
            {
                if (bundle == null)
                {
                    continue;
                }
                bundleCount++;
                if (bundles.Length > 0)
                {
                    bundles.Append(", ");
                }
                bundles.Append(bundle.name);
            }
            Log.Info("CAS hardpoint survey: " + bundleCount + " AssetBundle(s) loaded" +
                     (bundles.Length > 0 ? ": " + bundles : string.Empty));

            Log.Info("CAS hardpoint survey: GunRun hardpoint " +
                     (gunRun
                         ? "IS present in loaded content - harvesting + pinning can deliver gun runs."
                         : "NOT found in any loaded content this session; a gun run needs a scene/bundle that carries a gun hardpoint, or a runtime-constructed gun hardpoint."));
        }

        /// <summary>True when at least one loaded hardpoint prefab can deliver the attack type.</summary>
        internal static bool CanSupply(CASAttackType type)
        {
            List<GameObject> list;
            return _hardpoints.TryGetValue(type, out list) && list.Count > 0;
        }

        /// <summary>The first prefab able to deliver the attack type, or null.</summary>
        internal static GameObject FirstFor(CASAttackType type)
        {
            List<GameObject> list;
            if (_hardpoints.TryGetValue(type, out list) && list.Count > 0)
            {
                return list[0];
            }
            return null;
        }

        /// <summary>Every loaded prefab able to deliver the attack type (empty when there is none).</summary>
        internal static List<GameObject> AllFor(CASAttackType type)
        {
            List<GameObject> list;
            if (_hardpoints.TryGetValue(type, out list))
            {
                return list;
            }
            return new List<GameObject>();
        }

        private static void IndexAirframes(CasAirframeUnit[] airframes)
        {
            if (airframes == null)
            {
                return;
            }
            for (int i = 0; i < airframes.Length; i++)
            {
                CasAirframeUnit airframe = airframes[i];
                if (airframe != null)
                {
                    IndexLoadout(airframe.Loadout);
                }
            }
        }

        private static void IndexLoadout(CASLoadoutScriptable loadout)
        {
            if (loadout == null || loadout.Loadout == null || loadout.Loadout.HardpointPrefabs == null)
            {
                return;
            }
            GameObject[] prefabs = loadout.Loadout.HardpointPrefabs;
            for (int i = 0; i < prefabs.Length; i++)
            {
                GameObject prefab = prefabs[i];
                if (prefab == null)
                {
                    continue;
                }
                CASHardpoint hardpoint = prefab.GetComponentInChildren<CASHardpoint>(true);
                if (!FireSupportTemplates.IsUsableHardpoint(hardpoint))
                {
                    continue;
                }
                Add(hardpoint.Type, prefab);
            }
        }

        private static void Add(CASAttackType type, GameObject prefab)
        {
            // Attack types the mod no longer offers (air-to-air missile, training round) are not indexed
            // at all: nothing may ever mount one, and leaving them out also keeps the survey log honest
            // about what a slot can actually be given.
            AttackKind supported;
            if (!FireSupportTemplates.TryFromGameAttack(type, out supported))
            {
                return;
            }

            List<GameObject> list;
            if (!_hardpoints.TryGetValue(type, out list))
            {
                list = new List<GameObject>();
                _hardpoints.Add(type, list);
            }
            for (int i = 0; i < list.Count; i++)
            {
                if (ReferenceEquals(list[i], prefab))
                {
                    return;
                }
            }
            list.Add(prefab);
        }
    }
}
