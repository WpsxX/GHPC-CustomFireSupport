using System;
using CustomFireSupport;
using GHPC.Vehicle;

// The game's CASAttackType, in its shipped order (GHPC.Vehicle.CASAttackType):
//   Bombs = 0, Rockets = 1, AirToGroundMissile = 2, AirToAirMissile = 3, GunRun = 4, Inert = 5
namespace GHPC.Vehicle
{
    public enum CASAttackType
    {
        Bombs,
        Rockets,
        AirToGroundMissile,
        AirToAirMissile,
        GunRun,
        Inert
    }
}

/// <summary>
/// Regression tests for the two rules the CAS firing-chain repair depends on. Both are read out of the
/// shipped assembly, and both are the difference between "the aircraft fires" and "the aircraft flies its
/// pass and drops nothing, with no log the player can see":
///
///   * the hardpoint-prefab list rule (CASHardpointManager.HasCriticalConfigError), and
///   * the attack-type fallback order (used because CASController dereferences a missing attack entry).
/// </summary>
internal static class Program
{
    private static int passed;
    private static int failed;

    private static void Check(bool condition, string name)
    {
        if (condition)
        {
            passed++;
            Console.WriteLine("PASS " + name);
        }
        else
        {
            failed++;
            Console.WriteLine("FAIL " + name);
        }
    }

    private static int Main()
    {
        // ---- the hardpoint list rule ---------------------------------------------------------
        // "must have either one entry total or one entry per attach point"
        Check(CasFireChainRules.HardpointListFits(1, 1), "one prefab fits one attach point");
        Check(CasFireChainRules.HardpointListFits(1, 11), "one prefab is reused on every attach point (A-10)");
        Check(CasFireChainRules.HardpointListFits(4, 4), "one prefab per attach point fits (MiG-23BN)");
        Check(CasFireChainRules.HardpointListFits(5, 4), "more prefabs than attach points still fits");
        Check(!CasFireChainRules.HardpointListFits(2, 4),
            "2 prefabs for 4 attach points is the case the game refuses to configure");
        Check(!CasFireChainRules.HardpointListFits(3, 11),
            "3 prefabs for 11 attach points is refused as well");
        Check(!CasFireChainRules.HardpointListFits(0, 4), "an empty list is refused");
        Check(!CasFireChainRules.HardpointListFits(2, 0),
            "an unknown attach-point count can only be satisfied by the single-entry form");

        // The bug this closes, stated as a case: an exported loadout sized for a different airframe.
        // MiG-23BN has 4 attach points and one of its FAB-250 loadouts carries 2 prefabs; handing that
        // pair to the game leaves _configDone false, so every Fire() is refused for the whole sortie.
        Check(!CasFireChainRules.HardpointListFits(2, 4) && CasFireChainRules.HardpointListFits(1, 4),
            "collapsing the refused 2-of-4 list to one prefab is what makes that sortie fireable");

        // ---- the attack-type fallback order --------------------------------------------------
        int index;
        bool[] rocketsOnly = { false, true, false, false };
        Check(CasFireChainRules.ChooseFireableType(rocketsOnly, CasFireChainRules.TypeIndex(CASAttackType.Rockets), out index) &&
              index == CasFireChainRules.TypeIndex(CASAttackType.Rockets),
            "a fireable type is kept as-is");
        Check(CasFireChainRules.ChooseFireableType(rocketsOnly, CasFireChainRules.TypeIndex(CASAttackType.Bombs), out index) &&
              index == CasFireChainRules.TypeIndex(CASAttackType.Rockets),
            "a rocket-only sortie asked to bomb fires its rockets instead of going silent");

        bool[] bombsOnly = { true, false, false, false };
        Check(CasFireChainRules.ChooseFireableType(bombsOnly, CasFireChainRules.TypeIndex(CASAttackType.Rockets), out index) &&
              index == CasFireChainRules.TypeIndex(CASAttackType.Bombs),
            "a bomb-only sortie asked to rocketeer drops its bombs instead of going silent");

        bool[] gunOnly = { false, false, false, true };
        Check(CasFireChainRules.ChooseFireableType(gunOnly, CasFireChainRules.TypeIndex(CASAttackType.Bombs), out index) &&
              index == CasFireChainRules.TypeIndex(CASAttackType.GunRun),
            "a gun-only sortie falls back to the gun run");

        bool[] nothing = { false, false, false, false };
        Check(!CasFireChainRules.ChooseFireableType(nothing, 0, out index),
            "nothing fireable is reported, so the sortie says so instead of pretending to attack");

        bool[] both = { true, true, false, false };
        Check(CasFireChainRules.ChooseFireableType(both, CasFireChainRules.TypeIndex(CASAttackType.Rockets), out index) &&
              index == CasFireChainRules.TypeIndex(CASAttackType.Rockets),
            "the game's own pick wins when it is fireable, even with another type available");
        Check(CasFireChainRules.ChooseFireableType(both, 99, out index) &&
              index == CasFireChainRules.TypeIndex(CASAttackType.Bombs),
            "an out-of-range pick falls back to the first fireable type");

        Check(CasFireChainRules.TypeIndex(CASAttackType.Bombs) == 0 &&
              CasFireChainRules.TypeIndex(CASAttackType.Rockets) == 1 &&
              CasFireChainRules.TypeIndex(CASAttackType.GunRun) == 3,
            "the walk order is bombs, rockets, air-to-ground missile, gun run");
        Check(CasFireChainRules.TypeIndex(CASAttackType.Inert) == 0,
            "a type outside the walkable set cannot index past the array");

        Console.WriteLine("passed: " + passed + ", failed: " + failed);
        return failed == 0 ? 0 : 1;
    }
}
