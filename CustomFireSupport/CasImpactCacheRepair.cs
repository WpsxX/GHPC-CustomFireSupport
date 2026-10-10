using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using GHPC.Effects;
using HarmonyLib;

namespace CustomFireSupport
{
    internal sealed class AmmoReferenceComparer : IEqualityComparer<AmmoType>
    {
        internal static readonly AmmoReferenceComparer Instance = new AmmoReferenceComparer();
        public bool Equals(AmmoType x, AmmoType y) { return ReferenceEquals(x, y); }
        public int GetHashCode(AmmoType ammo) { return RuntimeHelpers.GetHashCode(ammo); }
    }

    /// <summary>
    /// Answers the impact-effect and impact-decal lookups for the mod's own ammunition WITHOUT going
    /// through the exported cache index.
    ///
    /// Why this is needed. Both databases resolve an effect with
    ///
    ///     CachedData[ammoType.CachedIndex]
    ///
    /// i.e. a raw dictionary indexer with no TryGetValue, and <c>AmmoType.CachedIndex</c> is a field the
    /// ASSET carries. For the ammunition the cas_assets bundle was exported from, that number belongs to
    /// the export's own database: FFAR = 116, S-5K = 119 and S-8K = 121 are recorded right in the .asset
    /// files and they arrive here unchanged. This session's database is built from whatever ammunition the
    /// loaded content actually has, so that number means nothing here: it either points at a DIFFERENT
    /// round's entry (the impact silently shows that round's effect - for a rocket, no explosion) or it is
    /// outside the table altogether (KeyNotFoundException inside the physics step, and no effect at all).
    /// Both symptoms are "the rocket hit something and nothing happened".
    ///
    /// Why not register our ammo in the live table instead (what this file did before). Because effects
    /// and decals are TWO databases that share the ONE <c>CachedIndex</c> field: caching an ammo in the
    /// effects table moves the index out from under the decals table and vice versa, so the lookup had to
    /// save and restore the field around every single query - correct only as long as every caller is
    /// covered, and it still wrote into the game's own serialized dictionaries.
    ///
    /// What this does instead. It resolves the entry directly, with the game's own filter logic
    /// (<c>ParticleEffectsManager.GetBestImpactEffectMatch</c> /
    /// <c>ImpactDecalsManager.GetBestImpactDecalMatch</c>) and then skips the original method - the same
    /// idea as the smoke and illumination shells, which hand the bundled prefab over directly instead of
    /// looking it up through export-time data (see FireSupportTemplates.FindArtilleryEffectPrefab).
    /// Nothing is written to <c>CachedData</c> or to <c>CachedIndex</c>, so the two databases cannot
    /// collide, the stored asset is never mutated, and another mod's exported ammunition is covered by the
    /// same rule.
    ///
    /// The resolution is memoized per database generation, per ammo and per combination - the same
    /// 3x13+1 combinations the game's own CacheNewData precomputes eagerly, but only the ones a round
    /// actually asks for, and without touching the shared tables.
    /// </summary>
    internal static class CasImpactCacheRepair
    {
        /// <summary>Room for the 13 surface materials the game enumerates.</summary>
        private const int SurfaceSlots = 16;

        private sealed class EffectTable
        {
            internal object Generation;
            internal readonly Dictionary<AmmoType, Dictionary<int, ImpactEffectDataScriptable>> Ammo =
                new Dictionary<AmmoType, Dictionary<int, ImpactEffectDataScriptable>>(AmmoReferenceComparer.Instance);

            internal void CheckGeneration(object data)
            {
                if (ReferenceEquals(Generation, data)) return;
                Ammo.Clear();
                Generation = data;
            }
        }

        private sealed class DecalTable
        {
            internal object Generation;
            internal readonly Dictionary<AmmoType, Dictionary<int, ImpactDecalDataScriptable>> Ammo =
                new Dictionary<AmmoType, Dictionary<int, ImpactDecalDataScriptable>>(AmmoReferenceComparer.Instance);

            internal void CheckGeneration(object data)
            {
                if (ReferenceEquals(Generation, data)) return;
                Ammo.Clear();
                Generation = data;
            }
        }

        private static readonly ConditionalWeakTable<ImpactEffectsDatabaseScriptable, EffectTable> Effects =
            new ConditionalWeakTable<ImpactEffectsDatabaseScriptable, EffectTable>();
        private static readonly ConditionalWeakTable<ImpactDecalsDatabaseScriptable, DecalTable> Decals =
            new ConditionalWeakTable<ImpactDecalsDatabaseScriptable, DecalTable>();

        private static bool Owns(AmmoType ammo)
        {
            return ammo != null && (CasPrewarmer.IsBundledAmmo(ammo) || CasPayloadFactory.IsOurRound(ammo) ||
                CasPayloadFactory.IsOurMissile(ammo));
        }

        // ------------------------------------------------------------------
        // Pre-warm repair: a ROCKET with no impact decal switched on
        // ------------------------------------------------------------------

        /// <summary>
        /// Turns an impact decal ON for a bundled ROCKET whose asset says it has none, once, at pre-warm.
        ///
        /// WHY THIS IS NEEDED. The shipped <c>ammo_FFAR_10lb</c> - the round the mod's Lau 32 (FFAR) pods
        /// fire - carries <c>HasImpactDecal = 0</c>. That flag is read FIRST by the game's own matcher
        /// (<c>ImpactDecalsManager.GetBestImpactDecalMatch</c>, which returns null on it before any database
        /// search), so no amount of searching finds a mark for that round and it can never stamp one on any
        /// surface. Every other rocket the mod ships - the S-5K (UB16 / UB32 / MARS-2) and the S-8K (B8) -
        /// already carries <c>HasImpactDecal = 1</c> and craters normally, and the FFAR's descriptor is
        /// otherwise IDENTICAL to the S-5K's (DecalCategory HEAT, DecalType Dent, DecalImpactAngle High,
        /// Flags 4, MinFilterStrictness Low), so the one flag is the whole difference.
        ///
        /// WHY THE DESCRIPTOR IS REWRITTEN RATHER THAN THE LOOKUP PATCHED. This file's two Harmony patches
        /// only change HOW an entry is found; they cannot invent one. The FFAR's own request never reaches
        /// the database, so clearing the flag on the descriptor is the only place the decision can be
        /// changed - and it is a plain struct field on the mod's own bundled asset, reached here as a local
        /// copy and written straight back.
        ///
        /// GUARDED ON EffectSize == Rocket, deliberately. Bullet and autocannon rounds are refused the Dirt
        /// crater by the game itself (<c>CreateImpactDecalOfType</c> returns early for those sizes), and the
        /// bombs and missiles are already correctly described - clearing their flag would be a change with
        /// no purpose. Only a rocket that has the flag CLEARED is touched, so the S-5K, the S-8K and the
        /// Hydra (all already set) return false here and are not even written.
        ///
        /// NOTHING ELSE IS TOUCHED: <c>DecalCategory</c>, <c>DecalType</c>, <c>DecalImpactAngle</c>,
        /// <c>Flags</c>, <c>MinFilterStrictness</c> and <c>CachedIndex</c> keep the asset's own values, so
        /// the round resolves through this file's existing direct-decal path to exactly the entry the S-5K
        /// gets. The ammo is also deliberately NOT added to <c>_ourRounds</c> and no
        /// <c>CachedDecalAmmo</c>-style EffectSize-raising clone is involved - that clone exists because a
        /// GUN round's own descriptor has to keep its EffectSize for its explosion lookups, and applying it
        /// to a rocket would change which effect the FFAR's detonation resolves to.
        ///
        /// ORDERING IS LOAD-BEARING. This runs from the bundle's hardpoint ammo scan, i.e. at pre-warm and
        /// before any shot is fired. <see cref="DecalLookup"/> memoizes a null result for a combination -
        /// including this round's Dirt key - so a repair applied after the first impact would be answered
        /// from that memo and silently ignored for the rest of the session.
        /// </summary>
        /// <returns>true when the descriptor was rewritten.</returns>
        internal static bool RepairRocketImpactDecal(AmmoType ammo)
        {
            if (ammo == null)
            {
                return false;
            }

            if (ammo.ImpactEffectDescriptor.EffectSize != ParticleEffectsManager.EffectSize.Rocket)
            {
                return false;
            }

            if (ammo.ImpactDecalDescriptor.HasImpactDecal)
            {
                return false;
            }

            // The descriptor is a struct on the ammo asset: read it, set the flag on the copy, write the
            // whole descriptor back.
            ImpactDecalsManager.ImpactDecalDescriptor descriptor = ammo.ImpactDecalDescriptor;
            descriptor.HasImpactDecal = true;
            ammo.ImpactDecalDescriptor = descriptor;

            return true;
        }

        /// <summary>
        /// True when the vanilla lookup must not be used for this ammo. Either the ammo is the mod's own
        /// (its exported index belongs to another session's database and may still be IN range here, which
        /// is the dangerous case), or that index is not in this session's table at all - then the vanilla
        /// indexer would throw instead of reporting "no effect".
        /// </summary>
        private static bool NeedsDirectLookup(AmmoType ammo, bool indexPresentInTable)
        {
            if (ammo == null)
            {
                return false;
            }
            return Owns(ammo) || !indexPresentInTable;
        }

        [HarmonyPatch(typeof(ImpactEffectsDatabaseScriptable), "TryGetCachedImpactEffectData")]
        private static class EffectLookup
        {
            private static bool Prefix(
                ImpactEffectsDatabaseScriptable __instance,
                AmmoType ammoType,
                ParticleEffectsManager.FusedStatus fusedStatus,
                ParticleEffectsManager.SurfaceMaterial surfaceMaterial,
                bool isRicochet,
                out ImpactEffectDataScriptable prefab,
                ref bool __result)
            {
                prefab = null;
                bool indexPresent = __instance != null && __instance.CachedData != null &&
                                    __instance.CachedData.ContainsKey(ammoType.CachedIndex);
                if (__instance == null || __instance.CachedData == null || !NeedsDirectLookup(ammoType, indexPresent))
                {
                    return true; // untouched: the game's own table is authoritative for this ammo.
                }

                try
                {
                    prefab = ResolveEffect(__instance, ammoType, fusedStatus, surfaceMaterial, isRicochet);
                    __result = prefab != null;
                    return false; // the exported index must never be dereferenced.
                }
                catch (Exception)
                {
                    // Nothing was written anywhere, so falling back to the game's own method cannot make
                    // things worse than they were before this patch existed.
                    prefab = null;
                    return true;
                }
            }

            private static ImpactEffectDataScriptable ResolveEffect(
                ImpactEffectsDatabaseScriptable database,
                AmmoType ammoType,
                ParticleEffectsManager.FusedStatus fusedStatus,
                ParticleEffectsManager.SurfaceMaterial surfaceMaterial,
                bool isRicochet)
            {
                EffectTable table = Effects.GetOrCreateValue(database);
                table.CheckGeneration(database.CachedData);

                Dictionary<int, ImpactEffectDataScriptable> perAmmo;
                if (!table.Ammo.TryGetValue(ammoType, out perAmmo))
                {
                    perAmmo = new Dictionary<int, ImpactEffectDataScriptable>();
                    table.Ammo.Add(ammoType, perAmmo);
                }

                int key = ((int)fusedStatus * SurfaceSlots + (int)surfaceMaterial) * 2 + (isRicochet ? 1 : 0);
                ImpactEffectDataScriptable resolved;
                if (perAmmo.TryGetValue(key, out resolved))
                {
                    return resolved; // a memoized "no effect for this combination" is an answer too.
                }

                ParticleEffectsManager.FilterStrictness strictness;
                ParticleEffectsManager.ImpactEffectDatabaseEntry entry = ParticleEffectsManager.GetBestImpactEffectMatch(
                    database, ammoType, fusedStatus, surfaceMaterial, out strictness, isRicochet);
                resolved = entry == null ? null : entry.ParticleEffectData;
                if (resolved == null || resolved.EffectPrefab == null)
                {
                    resolved = null; // the game's own return conditions, reproduced exactly.
                }
                perAmmo.Add(key, resolved);
                return resolved;
            }

        }

        [HarmonyPatch(typeof(ImpactDecalsDatabaseScriptable), "TryGetCachedImpactDecalData")]
        private static class DecalLookup
        {
            private static bool Prefix(
                ImpactDecalsDatabaseScriptable __instance,
                AmmoType ammoType,
                ParticleEffectsManager.FusedStatus fusedStatus,
                ParticleEffectsManager.SurfaceMaterial surfaceMaterial,
                out ImpactDecalDataScriptable prefab,
                ImpactDecalsManager.DecalType decalType,
                ImpactDecalsManager.DecalImpactAngle impactAngle,
                ref bool __result)
            {
                prefab = null;
                bool indexPresent = __instance != null && __instance.CachedData != null &&
                                    __instance.CachedData.ContainsKey(ammoType.CachedIndex);
                if (__instance == null || __instance.CachedData == null || !NeedsDirectLookup(ammoType, indexPresent))
                {
                    return true;
                }

                try
                {
                    prefab = ResolveDecal(__instance, ammoType, fusedStatus, surfaceMaterial, decalType, impactAngle);
                    __result = prefab != null;
                    return false;
                }
                catch (Exception)
                {
                    prefab = null;
                    return true;
                }
            }

            private static ImpactDecalDataScriptable ResolveDecal(
                ImpactDecalsDatabaseScriptable database,
                AmmoType ammoType,
                ParticleEffectsManager.FusedStatus fusedStatus,
                ParticleEffectsManager.SurfaceMaterial surfaceMaterial,
                ImpactDecalsManager.DecalType decalType,
                ImpactDecalsManager.DecalImpactAngle impactAngle)
            {
                DecalTable table = Decals.GetOrCreateValue(database);
                table.CheckGeneration(database.CachedData);

                Dictionary<int, ImpactDecalDataScriptable> perAmmo;
                if (!table.Ammo.TryGetValue(ammoType, out perAmmo))
                {
                    perAmmo = new Dictionary<int, ImpactDecalDataScriptable>();
                    table.Ammo.Add(ammoType, perAmmo);
                }

                // 3 fused states x 13 surfaces x 3 decal types x 3 impact angles, in the game's own order.
                int key = (((int)fusedStatus * SurfaceSlots + (int)surfaceMaterial) * 4 + (int)decalType) * 4 +
                          (int)impactAngle;
                ImpactDecalDataScriptable resolved;
                if (perAmmo.TryGetValue(key, out resolved))
                {
                    return resolved;
                }

                ImpactDecalsManager.DecalFilterStrictness strictness;
                ImpactDecalsManager.ImpactDecalDatabaseEntry entry = ImpactDecalsManager.GetBestImpactDecalMatch(
                    database, ammoType, fusedStatus, surfaceMaterial, out strictness, decalType, impactAngle);
                resolved = entry == null ? null : entry.ImpactDecalData;
                if (resolved == null || resolved.DecalPrefab == null)
                {
                    resolved = null;
                }
                perAmmo.Add(key, resolved);
                return resolved;
            }

        }
    }
}

