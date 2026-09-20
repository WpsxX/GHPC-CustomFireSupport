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
    /// Exported ammo indexes belong to the export's databases, not this game session. Effects and
    /// decals also share AmmoType.CachedIndex despite owning separate caches. Select a valid index
    /// only during each lookup, then restore the caller's value so the two databases cannot collide.
    /// </summary>
    internal static class CasImpactCacheRepair
    {
        private sealed class Entry
        {
            internal int Index;
            internal object Data;
        }

        private sealed class Cache
        {
            public Cache() { }
            internal object Data;
            internal readonly Dictionary<AmmoType, Entry> Ammo =
                new Dictionary<AmmoType, Entry>(AmmoReferenceComparer.Instance);

            internal void CheckGeneration(object data)
            {
                if (ReferenceEquals(Data, data)) return;
                Ammo.Clear();
                Data = data;
            }
        }

        private static readonly ConditionalWeakTable<ImpactEffectsDatabaseScriptable, Cache> Effects =
            new ConditionalWeakTable<ImpactEffectsDatabaseScriptable, Cache>();
        private static readonly ConditionalWeakTable<ImpactDecalsDatabaseScriptable, Cache> Decals =
            new ConditionalWeakTable<ImpactDecalsDatabaseScriptable, Cache>();

        private static bool Owns(AmmoType ammo)
        {
            return ammo != null && (CasPrewarmer.IsBundledAmmo(ammo) || CasPayloadFactory.IsOurRound(ammo) ||
                CasPayloadFactory.IsOurMissile(ammo));
        }

        [HarmonyPatch(typeof(ImpactEffectsDatabaseScriptable), "TryGetCachedImpactEffectData")]
        private static class EffectLookup
        {
            private static void Prefix(ImpactEffectsDatabaseScriptable __instance, AmmoType ammoType, out int? __state)
            {
                __state = null;
                if (!Owns(ammoType)) return;
                __state = ammoType.CachedIndex;
                Cache cache = Effects.GetOrCreateValue(__instance);
                cache.CheckGeneration(__instance.CachedData);
                Entry entry;
                ImpactEffectsDatabaseScriptable.BulletImpactCacheData data;
                if (!cache.Ammo.TryGetValue(ammoType, out entry) ||
                    !__instance.CachedData.TryGetValue(entry.Index, out data) || !ReferenceEquals(entry.Data, data))
                {
                    int index;
                    __instance.CacheNewData(ammoType, out index);
                    entry = new Entry { Index = index, Data = __instance.CachedData[index] };
                    cache.Ammo[ammoType] = entry;
                    Log.Verbose("CAS impact cache: registered '" + ammoType.Name + "' at " + index + ".");
                }
                ammoType.CachedIndex = entry.Index;
            }

            private static void Postfix(AmmoType ammoType, int? __state)
            {
                if (__state.HasValue) ammoType.CachedIndex = __state.Value;
            }

            private static Exception Finalizer(Exception __exception, AmmoType ammoType, int? __state)
            {
                if (__state.HasValue) ammoType.CachedIndex = __state.Value;
                return __exception;
            }
        }

        [HarmonyPatch(typeof(ImpactDecalsDatabaseScriptable), "TryGetCachedImpactDecalData")]
        private static class DecalLookup
        {
            private static void Prefix(ImpactDecalsDatabaseScriptable __instance, AmmoType ammoType, out int? __state)
            {
                __state = null;
                if (!Owns(ammoType)) return;
                __state = ammoType.CachedIndex;
                Cache cache = Decals.GetOrCreateValue(__instance);
                cache.CheckGeneration(__instance.CachedData);
                Entry entry;
                ImpactDecalsDatabaseScriptable.ImpactDecalCacheData data;
                if (!cache.Ammo.TryGetValue(ammoType, out entry) ||
                    !__instance.CachedData.TryGetValue(entry.Index, out data) || !ReferenceEquals(entry.Data, data))
                {
                    int index;
                    __instance.CacheNewData(ammoType, out index);
                    entry = new Entry { Index = index, Data = __instance.CachedData[index] };
                    cache.Ammo[ammoType] = entry;
                }
                ammoType.CachedIndex = entry.Index;
            }

            private static void Postfix(AmmoType ammoType, int? __state)
            {
                if (__state.HasValue) ammoType.CachedIndex = __state.Value;
            }

            private static Exception Finalizer(Exception __exception, AmmoType ammoType, int? __state)
            {
                if (__state.HasValue) ammoType.CachedIndex = __state.Value;
                return __exception;
            }
        }
    }
}
