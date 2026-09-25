using GHPC.Vehicle;

namespace CustomFireSupport
{
    /// <summary>
    /// The two rules the CAS firing-chain repair is built on, kept free of Unity so they can be
    /// regression-tested without the game (see tests/FireChainTests).
    ///
    /// Both come straight out of the shipped assembly:
    ///
    ///   1. CASHardpointManager.SetUpHardpoints instantiates HardpointPrefabs[i] on EVERY attach point
    ///      (reusing entry 0 when the list holds a single entry), and HasCriticalConfigError() refuses
    ///      the loadout - after which DoConfig() never sets _configDone, so every Fire() is refused for
    ///      the whole sortie - when the list is neither one entry nor at least one entry per attach point.
    ///
    ///   2. CASController picks the attack type from the TARGET and the mounted pylons, and the type is
    ///      only usable when BOTH a non-empty pylon of it exists (CASHardpointManager.CanDoAttackType)
    ///      AND the loadout declares an attack entry for it (GetAttackMetaByType). The game checks the
    ///      first and then dereferences the second without a null check, so a disagreement between the
    ///      two is a silent dry pass.
    /// </summary>
    internal static class CasFireChainRules
    {
        /// <summary>The attack types the repair considers, in the order the fallback walks them.</summary>
        internal static readonly CASAttackType[] AttackTypes =
        {
            CASAttackType.Bombs,
            CASAttackType.Rockets,
            CASAttackType.AirToGroundMissile,
            CASAttackType.GunRun
        };

        internal static bool HardpointListFits(int prefabCount, int attachPoints)
        {
            if (prefabCount <= 0 || attachPoints <= 0)
            {
                return false;
            }
            return prefabCount == 1 || prefabCount >= attachPoints;
        }

        /// <summary>Index of an attack type in <see cref="AttackTypes"/> (0 when it is not one of them).</summary>
        internal static int TypeIndex(CASAttackType type)
        {
            for (int i = 0; i < AttackTypes.Length; i++)
            {
                if (AttackTypes[i] == type)
                {
                    return i;
                }
            }
            return 0;
        }

        /// <summary>
        /// Which type to fire: the one the game picked when it is fireable, otherwise the first fireable
        /// one in <see cref="AttackTypes"/> order (bombs and rockets first - they are what a bomb or
        /// rocket slot exists for). A pick outside the table (an air-to-air missile or the game's own
        /// "Inert") is treated as "no preference". False when nothing at all is fireable, and then
        /// <paramref name="chosenIndex"/> is still a usable index so the caller can report it.
        /// </summary>
        internal static bool ChooseFireableType(bool[] fireable, int wantedIndex, out int chosenIndex)
        {
            chosenIndex = wantedIndex >= 0 && wantedIndex < (fireable == null ? 0 : fireable.Length)
                ? wantedIndex
                : 0;
            if (fireable == null || fireable.Length == 0)
            {
                return false;
            }
            if (fireable[chosenIndex])
            {
                return true;
            }
            for (int i = 0; i < fireable.Length; i++)
            {
                if (fireable[i])
                {
                    chosenIndex = i;
                    return true;
                }
            }
            return false;
        }
    }
}
