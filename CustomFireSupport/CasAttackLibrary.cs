using System.Collections.Generic;
using GHPC.Vehicle;
using GHPC.Weaponry.CAS;
using UnityEngine;

namespace CustomFireSupport
{
    /// <summary>
    /// Mission index of valid bundled CASHardpoint prefabs.
    /// A CAS aircraft can only perform attack types whose hardpoints are physically mounted
    /// (CASHardpointManager.CanDoAttackType consults the mounted CASHardpoint.Type flags), so a
    /// requested type like GunRun needs a gun hardpoint on the pylons - it is not enough to append a
    /// CASAttackMeta entry. This library is what lets a slot mount a type the chosen airframe's own
    /// loadout does not ship with.
    ///
    /// Runtime factory templates are used directly by the slot builder, never indexed as donors.
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
        /// Indexes valid hardpoint prefabs from bundled CASLoadoutScriptable assets.
        /// Only runs once per mission (see Reset).
        /// </summary>
        internal static void Refresh(CasSupportManager manager)
        {
            if (_refreshed)
            {
                return;
            }
            _refreshed = true;

            List<CASLoadoutScriptable> loadouts = CasPrewarmer.BundleLoadouts();
            for (int i = 0; i < loadouts.Count; i++)
            {
                IndexLoadout(loadouts[i]);
            }

        }

        /// <summary>True when at least one valid bundled hardpoint prefab can deliver the attack type.</summary>
        internal static bool CanSupply(CASAttackType type)
        {
            List<GameObject> list;
            return TryGetValid(type, out list) && list.Count > 0;
        }

        /// <summary>The first prefab able to deliver the attack type, or null.</summary>
        internal static GameObject FirstFor(CASAttackType type)
        {
            List<GameObject> list;
            if (TryGetValid(type, out list) && list.Count > 0)
            {
                return list[0];
            }
            return null;
        }

        /// <summary>Every valid bundled prefab able to deliver the attack type (empty when there is none).</summary>
        internal static List<GameObject> AllFor(CASAttackType type)
        {
            List<GameObject> list;
            if (TryGetValid(type, out list))
            {
                return new List<GameObject>(list);
            }
            return new List<GameObject>();
        }

        private static bool TryGetValid(CASAttackType type, out List<GameObject> list)
        {
            if (!_hardpoints.TryGetValue(type, out list)) return false;
            for (int i = list.Count - 1; i >= 0; i--)
            {
                GameObject prefab = list[i];
                CASHardpoint hp = prefab != null ? prefab.GetComponentInChildren<CASHardpoint>(true) : null;
                if (!CasPrewarmer.IsFromOurBundle(prefab) || !FireSupportTemplates.IsUsableHardpoint(hp) || hp.Type != type)
                    list.RemoveAt(i);
            }
            return true;
        }

        private static void IndexLoadout(CASLoadoutScriptable loadout)
        {
            if (!CasPrewarmer.IsFromOurBundle(loadout) || loadout.Loadout == null || loadout.Loadout.HardpointPrefabs == null)
            {
                return;
            }
            GameObject[] prefabs = loadout.Loadout.HardpointPrefabs;
            for (int i = 0; i < prefabs.Length; i++)
            {
                GameObject prefab = prefabs[i];
                if (!CasPrewarmer.IsFromOurBundle(prefab))
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
            // at all: nothing may ever mount one, so leaving them out keeps the index limited to
            // attack types a slot can actually be given.
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

