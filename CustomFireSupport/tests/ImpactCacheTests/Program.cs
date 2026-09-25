using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using CustomFireSupport;
using GHPC.Effects;

// Models the game's public database contract and runs the PRODUCTION lookup patches against it.
// The point of the suite: a bundled round carries the export's CachedIndex (FFAR 116 / S-5K 119 /
// S-8K 121), and the live tables do not use it. The patch has to answer from the round's effect
// descriptor, never dereference that number, and never write into the game's tables.
public class AmmoType
{
    public string Name;
    public bool HasImpactEffect = true;
    public bool HasImpactDecal = true;
    public int EffectSize = 6;           // Rocket
    public int ImpactCategory;
    public int DecalCategory = 3;        // Explosion
    public int CachedIndex = -1;
}

namespace HarmonyLib
{
    public sealed class HarmonyPatch : Attribute { public HarmonyPatch(Type type, string name) { } }
}

namespace CustomFireSupport
{
    internal static class CasPrewarmer
    {
        internal static readonly HashSet<AmmoType> Owned = new HashSet<AmmoType>(AmmoReferenceComparer.Instance);
        internal static bool IsBundledAmmo(AmmoType ammo) { return ammo != null && Owned.Contains(ammo); }
    }

    internal static class CasPayloadFactory
    {
        internal static readonly WeakIdentitySet<AmmoType> Missiles = new WeakIdentitySet<AmmoType>();
        internal static readonly WeakIdentitySet<AmmoType> Rounds = new WeakIdentitySet<AmmoType>();
        internal static bool IsOurRound(AmmoType ammo) { return Rounds.Contains(ammo); }
        internal static bool IsOurMissile(AmmoType ammo) { return Missiles.Contains(ammo); }
    }

    internal static class Log
    {
        internal static readonly List<string> Lines = new List<string>();
        internal static readonly List<string> Errors = new List<string>();
        internal static void Info(string message) { Lines.Add(message); }
        internal static void Error(string message) { Errors.Add(message); }
        internal static void Verbose(string message) { }
    }
}

namespace GHPC.Effects
{
    public class ImpactEffectDataScriptable
    {
        public string name;
        public GameObjectStub EffectPrefab;
        public int CachedPoolIndex;

        public ImpactEffectDataScriptable(string name, bool withPrefab = true)
        {
            this.name = name;
            EffectPrefab = withPrefab ? new GameObjectStub() : null;
        }
    }

    /// <summary>Stand-in for UnityEngine.GameObject: only reference identity is used.</summary>
    public sealed class GameObjectStub { }

    public class ImpactDecalDataScriptable
    {
        public string name;
        public GameObjectStub DecalPrefab;

        public ImpactDecalDataScriptable(string name, bool withPrefab = true)
        {
            this.name = name;
            DecalPrefab = withPrefab ? new GameObjectStub() : null;
        }
    }

    public static class ParticleEffectsManager
    {
        public enum SurfaceMaterial { Other, Default, Steel, Wood, Flesh, Dirt, Concrete, RedBrick, ThinMetal, ThinWood, Glass, Water, None, COUNT }
        public enum FusedStatus { Unfuzed, Fuzed, Other, COUNT }
        public enum FilterStrictness { VeryLow, Low, Medium, High, Exact }
        public enum Category { HighExplosive, Heat, Kinetic, Other, Ricochet, COUNT }

        public class ImpactEffectDatabaseEntry
        {
            public string EffectName;
            public Category Category;
            public int EffectSize;
            public ImpactEffectDataScriptable ParticleEffectData;
        }

        /// <summary>Everything the database would return, keyed by category + effect size.</summary>
        public static readonly Dictionary<string, ImpactEffectDatabaseEntry> Table =
            new Dictionary<string, ImpactEffectDatabaseEntry>();

        /// <summary>How many matches were asked for, so the memoization can be asserted.</summary>
        public static int MatchCalls;

        /// <summary>Set to make the filter blow up, so the patch's fallback can be asserted.</summary>
        public static bool ThrowOnMatch;

        public static ImpactEffectDatabaseEntry GetBestImpactEffectMatch(
            ImpactEffectsDatabaseScriptable database, AmmoType ammoType,
            FusedStatus fusedStatus, SurfaceMaterial surfaceMaterial,
            out FilterStrictness finalFilterStrictness, bool isRicochet)
        {
            MatchCalls++;
            finalFilterStrictness = FilterStrictness.Exact;
            if (ThrowOnMatch) throw new InvalidOperationException("filter exploded");
            if (!ammoType.HasImpactEffect) return null;
            ImpactEffectDatabaseEntry entry;
            return Table.TryGetValue(ammoType.ImpactCategory + "/" + ammoType.EffectSize, out entry) ? entry : null;
        }
    }

    public class ImpactEffectsDatabaseScriptable
    {
        public class BulletImpactCacheData { public AmmoType Ammo; }
        public Dictionary<int, BulletImpactCacheData> CachedData = new Dictionary<int, BulletImpactCacheData>();
    }

    public static class ImpactDecalsManager
    {
        public enum DecalType { Dent, Gouge, Penetration, COUNT }
        public enum DecalImpactAngle { High, Medium, Low, COUNT }
        public enum DecalFilterStrictness { VeryLow, Low, Medium, High, Exact }

        public class ImpactDecalDatabaseEntry
        {
            public string DecalName;
            public ImpactDecalDataScriptable ImpactDecalData;
        }

        public static readonly Dictionary<string, ImpactDecalDatabaseEntry> Table =
            new Dictionary<string, ImpactDecalDatabaseEntry>();
        public static int MatchCalls;

        public static ImpactDecalDatabaseEntry GetBestImpactDecalMatch(
            ImpactDecalsDatabaseScriptable database, AmmoType ammoType,
            ParticleEffectsManager.FusedStatus fusedStatus, ParticleEffectsManager.SurfaceMaterial surfaceMaterial,
            out DecalFilterStrictness finalFilterStrictness,
            DecalType decalType = DecalType.Dent, DecalImpactAngle impactAngle = DecalImpactAngle.High)
        {
            MatchCalls++;
            finalFilterStrictness = DecalFilterStrictness.Exact;
            if (!ammoType.HasImpactDecal) return null;
            ImpactDecalDatabaseEntry entry;
            return Table.TryGetValue(ammoType.DecalCategory + "/" + decalType + "/" + impactAngle, out entry)
                ? entry : null;
        }
    }

    public class ImpactDecalsDatabaseScriptable
    {
        public class ImpactDecalCacheData { public AmmoType Ammo; }
        public Dictionary<int, ImpactDecalCacheData> CachedData = new Dictionary<int, ImpactDecalCacheData>();
    }
}

internal static class Program
{
    private static int passed;

    private static void Check(bool condition, string name)
    {
        if (!condition) throw new Exception("FAILED: " + name);
        passed++;
        Console.WriteLine("PASS " + name);
    }

    /// <summary>Runs the production prefix and returns whether it let the vanilla method run.</summary>
    private static bool EffectPrefix(object database, AmmoType ammo, out object prefab, out bool result)
    {
        Type patch = typeof(CasImpactCacheRepair).GetNestedType("EffectLookup", BindingFlags.NonPublic);
        object[] args = { database, ammo, ParticleEffectsManager.FusedStatus.Fuzed,
            ParticleEffectsManager.SurfaceMaterial.Dirt, false, null, false };
        bool runOriginal = (bool)patch.GetMethod("Prefix", BindingFlags.Static | BindingFlags.NonPublic)
            .Invoke(null, args);
        prefab = args[5];
        result = (bool)args[6];
        return runOriginal;
    }

    private static bool DecalPrefix(object database, AmmoType ammo, out object prefab, out bool result)
    {
        Type patch = typeof(CasImpactCacheRepair).GetNestedType("DecalLookup", BindingFlags.NonPublic);
        object[] args = { database, ammo, ParticleEffectsManager.FusedStatus.Fuzed,
            ParticleEffectsManager.SurfaceMaterial.Dirt, null,
            ImpactDecalsManager.DecalType.Dent, ImpactDecalsManager.DecalImpactAngle.High, false };
        bool runOriginal = (bool)patch.GetMethod("Prefix", BindingFlags.Static | BindingFlags.NonPublic)
            .Invoke(null, args);
        prefab = args[4];
        result = (bool)args[7];
        return runOriginal;
    }

    private static int Main()
    {
        // The effect/decal tables the "session" offers: a rocket explosion, and nothing for other sizes.
        ParticleEffectsManager.Table["0/6"] = new ParticleEffectsManager.ImpactEffectDatabaseEntry
        {
            EffectName = "RocketSmallImpact_Dirt",
            EffectSize = 6,
            ParticleEffectData = new ImpactEffectDataScriptable("RocketSmallImpact_Dirt")
        };
        ImpactDecalsManager.Table["3/Dent/High"] = new ImpactDecalsManager.ImpactDecalDatabaseEntry
        {
            DecalName = "RocketScorch",
            ImpactDecalData = new ImpactDecalDataScriptable("RocketScorch")
        };

        AmmoType rocket = new AmmoType { Name = "S-8K", CachedIndex = 121, ImpactCategory = 0, EffectSize = 6 };
        CasPrewarmer.Owned.Add(rocket);
        ImpactEffectsDatabaseScriptable effects = new ImpactEffectsDatabaseScriptable();
        ImpactDecalsDatabaseScriptable decals = new ImpactDecalsDatabaseScriptable();

        // A live table that happens to be big enough for index 121 to exist: the dangerous case, where
        // the vanilla lookup would silently return ANOTHER round's entry instead of throwing.
        for (int i = 0; i <= 130; i++)
            effects.CachedData[i] = new ImpactEffectsDatabaseScriptable.BulletImpactCacheData();
        for (int i = 0; i <= 130; i++)
            decals.CachedData[i] = new ImpactDecalsDatabaseScriptable.ImpactDecalCacheData();
        int tableSizeBefore = effects.CachedData.Count;

        object prefab;
        bool result;
        bool runOriginal = EffectPrefix(effects, rocket, out prefab, out result);
        Check(!runOriginal, "exported index in range: the vanilla indexer is NOT allowed to run");
        Check(result && prefab is ImpactEffectDataScriptable &&
              ((ImpactEffectDataScriptable)prefab).name == "RocketSmallImpact_Dirt",
              "rocket resolves the explosion its detector describes");
        Check(rocket.CachedIndex == 121, "the exported cache index is left exactly as the asset shipped it");
        Check(effects.CachedData.Count == tableSizeBefore, "nothing is written into the effects table");

        runOriginal = DecalPrefix(decals, rocket, out prefab, out result);
        Check(!runOriginal && result && ((ImpactDecalDataScriptable)prefab).name == "RocketScorch",
              "the decal table answers independently of the effects table");
        Check(rocket.CachedIndex == 121, "a nested decal query leaves the shared index alone");
        Check(decals.CachedData.Count == tableSizeBefore, "nothing is written into the decal table");

        // Memoization: the same combination must not re-run the filter, but a different one must.
        int before = ParticleEffectsManager.MatchCalls;
        EffectPrefix(effects, rocket, out prefab, out result);
        EffectPrefix(effects, rocket, out prefab, out result);
        Check(ParticleEffectsManager.MatchCalls == before, "a salvo reuses the resolved entry instead of rescanning");
        Type patch = typeof(CasImpactCacheRepair).GetNestedType("EffectLookup", BindingFlags.NonPublic);
        object[] otherSurface = { effects, rocket, ParticleEffectsManager.FusedStatus.Unfuzed,
            ParticleEffectsManager.SurfaceMaterial.Steel, true, null, false };
        patch.GetMethod("Prefix", BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, otherSurface);
        Check(ParticleEffectsManager.MatchCalls == before + 1, "a new combination resolves once and is memoized");

        // An index outside the table: the vanilla path would throw KeyNotFoundException, so the patch
        // has to cover that too - for any ammo, not just the mod's own.
        AmmoType foreign = new AmmoType { Name = "another mod's rocket", CachedIndex = 999, EffectSize = 6 };
        runOriginal = EffectPrefix(effects, foreign, out prefab, out result);
        Check(!runOriginal && result, "an ammo whose index is not in the table is resolved instead of throwing");
        Check(foreign.CachedIndex == 999, "its index is untouched too");

        // Native ammo with a valid index keeps the game's own authoritative table.
        AmmoType native = new AmmoType { Name = "M829A4", CachedIndex = 3 };
        effects.CachedData[3] = new ImpactEffectsDatabaseScriptable.BulletImpactCacheData();
        runOriginal = EffectPrefix(effects, native, out prefab, out result);
        Check(runOriginal, "native ammo with a valid index still uses the game's own lookup");
        Check(prefab == null && !result, "the vanilla result is left to the original method");

        // A generator swap (new mission) must not answer from the old table.
        effects.CachedData = new Dictionary<int, ImpactEffectsDatabaseScriptable.BulletImpactCacheData>();
        for (int i = 0; i <= 130; i++)
            effects.CachedData[i] = new ImpactEffectsDatabaseScriptable.BulletImpactCacheData();
        runOriginal = EffectPrefix(effects, rocket, out prefab, out result);
        Check(!runOriginal && result && ((ImpactEffectDataScriptable)prefab).name == "RocketSmallImpact_Dirt",
              "a rebuilt database is re-resolved rather than answered from the previous one");

        // An ammo whose descriptor matches nothing reports "no effect" exactly like the game would.
        AmmoType inert = new AmmoType { Name = "inert practice", CachedIndex = 5, EffectSize = 12 };
        CasPrewarmer.Owned.Add(inert);
        runOriginal = EffectPrefix(effects, inert, out prefab, out result);
        Check(!runOriginal && !result && prefab == null, "a descriptor matching nothing yields no effect, not a throw");

        // Ammo with no decal descriptor at all (the FFAR is exported that way).
        AmmoType noDecal = new AmmoType { Name = "FFAR", CachedIndex = 116, HasImpactDecal = false };
        CasPrewarmer.Owned.Add(noDecal);
        runOriginal = DecalPrefix(decals, noDecal, out prefab, out result);
        Check(!runOriginal && !result && prefab == null, "an ammo with HasImpactDecal = 0 resolves to no decal");

        // Identity is by reference, so a same-named native round is never mistaken for ours.
        AmmoType twin = new AmmoType { Name = "S-8K", CachedIndex = 121 };
        runOriginal = EffectPrefix(effects, twin, out prefab, out result);
        Check(runOriginal, "a same-named native round is left to the game");

        // Runtime rounds (gun belt, missile) are covered by identity, not by name.
        AmmoType missile = new AmmoType { Name = "AGM-65", CachedIndex = 128, ImpactCategory = 0, EffectSize = 6 };
        CasPayloadFactory.Missiles.Add(missile);
        CasPayloadFactory.Missiles.Add(missile);
        runOriginal = EffectPrefix(effects, missile, out prefab, out result);
        Check(!runOriginal && result, "a runtime missile is resolved directly, whatever its donor index was");
        Check(missile.CachedIndex == 128, "the donor index is preserved for the missile");
        runOriginal = DecalPrefix(decals, missile, out prefab, out result);
        Check(!runOriginal && result, "the runtime missile's decal is resolved directly too");
        missile.Name = "renamed AGM";
        Check(CasPayloadFactory.IsOurMissile(missile), "weak identity survives renames and duplicate Add");
        Check(!CasPayloadFactory.IsOurMissile(new AmmoType { Name = "renamed AGM" }),
              "a same-name round does not inherit ownership");
        Check(!CasPayloadFactory.IsOurMissile(null), "null ammo is not owned");

        AmmoType gun = new AmmoType { Name = "PGU-13", CachedIndex = 17, ImpactCategory = 0, EffectSize = 6 };
        CasPayloadFactory.Rounds.Add(gun);
        runOriginal = EffectPrefix(effects, gun, out prefab, out result);
        Check(!runOriginal && result, "a runtime gun round stays covered");

        WeakReference discarded = AddDiscardedAmmo();
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        Check(!discarded.IsAlive, "the identity registry does not retain discarded ammo");

        // If the direct resolution itself fails, the game's own method is handed back control: the patch
        // can never leave an impact worse off than it was before the patch existed.
        AmmoType fragile = new AmmoType { Name = "fragile rocket", CachedIndex = 4, EffectSize = 6 };
        CasPrewarmer.Owned.Add(fragile);
        effects.CachedData[4] = new ImpactEffectsDatabaseScriptable.BulletImpactCacheData();
        ParticleEffectsManager.ThrowOnMatch = true;
        runOriginal = EffectPrefix(effects, fragile, out prefab, out result);
        Check(runOriginal, "a failing resolution falls back to the game's own lookup");
        Check(prefab == null, "the fallback does not hand out a half-resolved prefab");
        Check(Log.Errors.Count == 1, "the fallback is reported instead of being swallowed");
        ParticleEffectsManager.ThrowOnMatch = false;

        Check(Log.Lines.Count > 0, "the fix reports what it did, so a game log can prove it");
        Console.WriteLine("sample log: " + Log.Lines[0]);
        Console.WriteLine("passed: " + passed + ", failed: 0");
        return 0;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference AddDiscardedAmmo()
    {
        AmmoType ammo = new AmmoType { Name = "discarded" };
        CasPayloadFactory.Rounds.Add(ammo);
        return new WeakReference(ammo);
    }
}
