using System;
using System.Collections.Generic;
using GHPC;
using GHPC.Vehicle;
using GHPC.Weaponry.CAS;
using HarmonyLib;
using UnityEngine;

namespace CustomFireSupport
{
    /// <summary>Aircraft, loadouts and donor hardpoints are taken only from the pinned cas_assets bundle.</summary>
    internal static class CasDonorProvider
    {
        private static readonly AccessTools.FieldRef<CASHardpointManager, CASLoadoutScriptable> HardpointManagerLoadoutRef =
            AccessTools.FieldRefAccess<CASHardpointManager, CASLoadoutScriptable>("Loadout");

        internal static List<CasTemplate> Collect(CasSupportManager manager, Faction playerFaction)
        {
            CasAttackLibrary.Refresh(manager);
            List<CasTemplate> templates = new List<CasTemplate>();
            AddBundleAirframes(templates);
            return templates;
        }

        private static int AddBundleAirframes(List<CasTemplate> templates)
        {
            if (!CasPrewarmer.HasBundleAirframes)
            {
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
                    continue;
                }

                // The airframe's own loadout first, so the slot can fly the model's real pylons.
                CASLoadoutScriptable own = null;
                try
                {
                    own = HardpointManagerLoadoutRef(manager);
                }
                catch
                {
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

        private static int AddCandidate(List<CasTemplate> templates, GameObject prefab,
            CASLoadoutScriptable loadout, Faction faction, string source, bool isAsset,
            CASHardpointManager manager)
        {
            if (!CasPrewarmer.IsBundledAirframe(prefab) || !CasPrewarmer.IsFromOurBundle(loadout) ||
                loadout.Loadout == null || !FitsAirframe(manager, loadout.Loadout)) return 0;
            CASController controller = prefab.GetComponent<CASController>();
            if (!prefab.activeSelf || controller == null || !controller.enabled ||
                prefab.GetComponent<Rigidbody>() == null || !manager.enabled) return 0;
            for (int i = 0; i < manager.HardpointAttachPoints.Length; i++)
            {
                Transform point = manager.HardpointAttachPoints[i];
                if (point == null || !point.IsChildOf(prefab.transform)) return 0;
            }
            GameObject[] hardpoints = loadout.Loadout.HardpointPrefabs;
            bool usable = false;
            for (int i = 0; i < hardpoints.Length; i++)
            {
                GameObject hardpoint = hardpoints[i];
                if (ReferenceEquals(hardpoint, null)) continue; // Empty stations are valid.
                if (!CasPrewarmer.IsFromOurBundle(hardpoint)) return 0;
                if (FireSupportTemplates.IsUsableHardpoint(hardpoint.GetComponentInChildren<CASHardpoint>(true)))
                    usable = true;
            }
            if (!usable) return 0;
            for (int i = 0; i < templates.Count; i++)
                if (ReferenceEquals(templates[i].Prefab, prefab) && ReferenceEquals(templates[i].Loadout, loadout))
                    return 0;
            string name = prefab.name;
            templates.Add(new CasTemplate
            {
                Prefab = prefab,
                Loadout = loadout,
                Name = name,
                LoadoutName = loadout.name,
                Faction = FireSupportTemplates.ToFaction(CasAirframeCatalog.GuessSide(name)),
                AvailableAttacks = FireSupportTemplates.CollectAttackTypes(loadout.Loadout),
                MountedAttacks = CollectMountedAttacks(loadout.Loadout),
                HardpointCount = hardpoints.Length,
                AttachPointCount = AttachCount(manager),
                Source = source,
                IsAsset = true
            });
            return 1;
        }

        private static bool FitsAirframe(CASHardpointManager manager, CASLoadout loadout)
        {
            if (loadout == null || loadout.HardpointPrefabs == null || loadout.HardpointPrefabs.Length == 0)
            {
                return false;
            }

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
    }
}


