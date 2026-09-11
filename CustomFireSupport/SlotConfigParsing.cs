using System;
using System.Collections.Generic;
using System.Text;

namespace CustomFireSupport
{
    /// <summary>What kind of map-panel support button a slot represents.</summary>
    public enum SlotKind
    {
        Artillery,
        ArtillerySmoke,
        ArtilleryIllumination,
        CasFixedWing
    }

    /// <summary>Indirect-fire shell type (mirrors GHPC's IndirectFireMunitionType).</summary>
    public enum MunitionKind
    {
        AntiPersonnel,
        AntiArmor,
        Smoke,
        Illumination
    }

    /// <summary>Indirect-fire weapon type (mirrors GHPC's IndirectFireWeaponType).</summary>
    public enum WeaponKind
    {
        Guns,
        Mortars,
        Rockets,
        Any
    }

    /// <summary>CAS flyover profile (mirrors CasAirframeUnit.FlyoverType).</summary>
    public enum FlyoverKind
    {
        SinglePass,
        Linger
    }

    /// <summary>
    /// CAS attack type (mirrors GHPC's CASAttackType, minus the two the mod no longer offers: the
    /// air-to-air missile and the training / "Inert" entry - see IsRemovedAttackType).
    /// </summary>
    public enum AttackKind
    {
        Bombs,
        Rockets,
        AirToGroundMissile,
        GunRun
    }

    /// <summary>
    /// Pure, dependency-free config parsing / validation. It deliberately knows nothing about
    /// MelonLoader or the game assemblies, which is what makes it unit-testable outside the game
    /// (see tests/ConfigParsingTests). Every parse method is total: unknown input never throws,
    /// it falls back to a documented default and reports the problem through an out parameter.
    /// </summary>
    public static class SlotConfigParsing
    {
        public const int MaxMissions = 999;
        public const int MaxRoundsPerCall = 999;
        public const float MaxHeadingDegrees = 360f;

        /// <summary>
        /// Radius in metres of a slot's `CasAccuracy` impact circle at value 1.0. The knob is the circle's
        /// radius as a PERCENTAGE of this: `0` (or the mod's "off" spelling -1) = radius 0 (every round is
        /// flown into the target's own centre), `0.25` = 25 % = 3.75 m, `0.5` = 7.5 m, `1` = 15 m,
        /// `&gt; 1` = wider still.
        ///
        /// Pure math, so it lives here where the headless tests can reach it; the game-side
        /// CasPayloadFactory uses it for the sampling and the diagnostics.
        /// </summary>
        public const float CasAccuracyRadiusAtOne = 15f;

        /// <summary>Impact-circle radius of a slot's CasAccuracy, in metres (never negative).</summary>
        public static float CasAccuracyRadius(float accuracy)
        {
            if (float.IsNaN(accuracy))
            {
                return CasAccuracyRadiusAtOne;
            }
            return accuracy <= 0f ? 0f : accuracy * CasAccuracyRadiusAtOne;
        }

        // ------------------------------------------------------------------
        // Enum parsing (case / separator / alias tolerant)
        // ------------------------------------------------------------------

        public static bool TryParseSlotKind(string raw, out SlotKind kind)
        {
            switch (Normalize(raw))
            {
                case "artillery":
                case "arty":
                case "he":
                case "gun":
                case "guns":
                case "firemission":
                    kind = SlotKind.Artillery;
                    return true;

                case "artillerysmoke":
                case "smoke":
                case "smk":
                case "wp":
                case "whitephosphorus":
                    kind = SlotKind.ArtillerySmoke;
                    return true;

                case "artilleryillumination":
                case "artilleryillum":
                case "illum":
                case "illumination":
                case "flare":
                    kind = SlotKind.ArtilleryIllumination;
                    return true;

                case "cassupport":
                case "cas":
                case "fixedwing":
                case "plane":
                case "aircraft":
                case "air":
                    kind = SlotKind.CasFixedWing;
                    return true;

                default:
                    kind = SlotKind.Artillery;
                    return false;
            }
        }

        /// <summary>
        /// True for the helicopter CAS type names the mod used to accept. The helicopter support was
        /// removed again, so these are no longer valid - the test only exists to explain the removal in
        /// the log instead of reporting a generic "unknown Type".
        /// </summary>
        public static bool IsRemovedHelicopterType(string raw)
        {
            switch (Normalize(raw))
            {
                case "cashelisupport":
                case "casheli":
                case "heli":
                case "helicopter":
                case "rotary":
                case "rotarywing":
                case "chopper":
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>
        /// True for the CAS attack type names the mod used to accept and no longer offers:
        ///
        ///   * AirToAirMissile - a strike sortie has nothing to shoot at in the air, so the mod no longer
        ///     mounts or offers one;
        ///   * Inert - the game's training round / "no weapon" entry.
        ///
        /// Both values still exist in GHPC's own CASAttackType enum, and a mission loadout may still
        /// declare them; this only keeps them out of the config surface and out of every payload the mod
        /// builds. The test exists so the log can explain the removal instead of reporting a bare
        /// "unknown attack type".
        /// </summary>
        public static bool IsRemovedAttackType(string raw)
        {
            switch (Normalize(raw))
            {
                case "airtoairmissile":
                case "airtoair":
                case "airair":
                case "aam":
                case "aim9":
                case "aim-9":
                case "r60":
                case "r-60":
                case "inert":
                case "training":
                case "trainer":
                case "dummy":
                case "practice":
                    return true;
                default:
                    return false;
            }
        }

        public static bool TryParseMunition(string raw, out MunitionKind munition)
        {
            switch (Normalize(raw))
            {
                case "antipersonnel":
                case "ap":
                case "he":
                case "personnel":
                case "frag":
                case "highexplosive":
                    munition = MunitionKind.AntiPersonnel;
                    return true;

                case "antiarmor":
                case "at":
                case "antitank":
                case "heat":
                case "apfsds":
                case "sabot":
                    munition = MunitionKind.AntiArmor;
                    return true;

                case "smoke":
                case "smk":
                case "wp":
                case "whitephosphorus":
                    munition = MunitionKind.Smoke;
                    return true;

                case "illumination":
                case "illum":
                case "flare":
                case "star":
                    munition = MunitionKind.Illumination;
                    return true;

                default:
                    munition = MunitionKind.AntiPersonnel;
                    return false;
            }
        }

        public static bool TryParseWeapon(string raw, out WeaponKind weapon)
        {
            switch (Normalize(raw))
            {
                case "guns":
                case "gun":
                case "howitzer":
                case "cannon":
                    weapon = WeaponKind.Guns;
                    return true;

                case "mortars":
                case "mortar":
                    weapon = WeaponKind.Mortars;
                    return true;

                case "rockets":
                case "rocket":
                case "mlrs":
                    weapon = WeaponKind.Rockets;
                    return true;

                case "":
                case "any":
                case "all":
                    weapon = WeaponKind.Any;
                    return true;

                default:
                    weapon = WeaponKind.Any;
                    return false;
            }
        }

        public static bool TryParseFlyover(string raw, out FlyoverKind flyover)
        {
            switch (Normalize(raw))
            {
                case "singlepass":
                case "single":
                case "pass":
                case "one":
                    flyover = FlyoverKind.SinglePass;
                    return true;

                case "linger":
                case "loiter":
                case "orbit":
                case "stay":
                    flyover = FlyoverKind.Linger;
                    return true;

                default:
                    flyover = FlyoverKind.SinglePass;
                    return false;
            }
        }

        /// <summary>
        /// Parses a comma / semicolon separated attack-type list. An empty list, "Any" or "*" means
        /// "keep every attack type the template aircraft supports" (the safe default). Unknown tokens
        /// are skipped and reported through <paramref name="problems"/>; so are the names of attack
        /// types the mod no longer offers (air-to-air missile, training round), which get an explicit
        /// "no longer supported" note instead of a bare "unknown". A list that consists only of
        /// rejected tokens is treated as "Any" so a typo can never produce an unfireable aircraft.
        /// </summary>
        public static AttackKind[] ParseAttackList(string raw, out string problems)
        {
            problems = null;
            if (string.IsNullOrEmpty(raw) || Normalize(raw) == "any" || raw.Trim() == "*")
            {
                return new AttackKind[0];
            }

            List<AttackKind> parsed = new List<AttackKind>();
            List<string> unknown = new List<string>();
            List<string> removed = new List<string>();
            string[] tokens = raw.Split(new char[] { ',', ';', '|' }, StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < tokens.Length; i++)
            {
                string token = tokens[i].Trim();
                if (token.Length == 0)
                {
                    continue;
                }
                if (Normalize(token) == "any" || token == "*")
                {
                    return new AttackKind[0];
                }

                AttackKind attack;
                if (TryParseAttack(token, out attack))
                {
                    if (!parsed.Contains(attack))
                    {
                        parsed.Add(attack);
                    }
                }
                else if (IsRemovedAttackType(token))
                {
                    removed.Add(token);
                }
                else
                {
                    unknown.Add(token);
                }
            }

            if (removed.Count > 0 || unknown.Count > 0)
            {
                StringBuilder builder = new StringBuilder();
                if (removed.Count > 0)
                {
                    builder.Append("no longer supported (the mod dropped air-to-air missiles and training " +
                                   "rounds), ignored: ");
                    Join(builder, removed);
                }
                if (unknown.Count > 0)
                {
                    if (builder.Length > 0)
                    {
                        builder.Append("; ");
                    }
                    builder.Append("unknown, ignored: ");
                    Join(builder, unknown);
                }
                problems = builder.ToString();
            }

            if (parsed.Count == 0)
            {
                // Everything was unknown / removed: fall back to "Any" instead of building an aircraft
                // that can never fire.
                return new AttackKind[0];
            }
            return parsed.ToArray();
        }

        private static void Join(StringBuilder builder, List<string> items)
        {
            for (int i = 0; i < items.Count; i++)
            {
                if (i > 0)
                {
                    builder.Append(", ");
                }
                builder.Append(items[i]);
            }
        }

        public static bool TryParseAttack(string raw, out AttackKind attack)
        {
            switch (Normalize(raw))
            {
                case "bombs":
                case "bomb":
                    attack = AttackKind.Bombs;
                    return true;

                case "rockets":
                case "rocket":
                    attack = AttackKind.Rockets;
                    return true;

                case "airtogroundmissile":
                case "atgm":
                case "agm":
                case "airground":
                case "missile":
                case "airtoground":
                    attack = AttackKind.AirToGroundMissile;
                    return true;

                case "gunrun":
                case "gun":
                case "strafe":
                case "cannon":
                    attack = AttackKind.GunRun;
                    return true;

                default:
                    // The air-to-air missile and the training round used to be accepted here; they are
                    // deliberately gone (see IsRemovedAttackType). The value returned on failure is
                    // irrelevant - every caller checks the return value - so it is the neutral member.
                    attack = AttackKind.Bombs;
                    return false;
            }
        }

        // ------------------------------------------------------------------
        // Cross-field helpers
        // ------------------------------------------------------------------

        public static bool IsCasSlot(SlotKind kind)
        {
            return kind == SlotKind.CasFixedWing;
        }

        /// <summary>Shell type a slot uses when "Munition" is left empty.</summary>
        public static MunitionKind DefaultMunitionFor(SlotKind kind)
        {
            switch (kind)
            {
                case SlotKind.ArtillerySmoke:
                    return MunitionKind.Smoke;
                case SlotKind.ArtilleryIllumination:
                    return MunitionKind.Illumination;
                default:
                    return MunitionKind.AntiPersonnel;
            }
        }

        /// <summary>
        /// True when the configured shell type is consistent with the configured button type. A smoke
        /// or illumination button firing HE is almost certainly a config mistake, so the caller logs a
        /// warning (the value is still honoured - the user may have a reason).
        /// </summary>
        public static bool MunitionMatchesSlotKind(SlotKind kind, MunitionKind munition)
        {
            switch (kind)
            {
                case SlotKind.ArtillerySmoke:
                    return munition == MunitionKind.Smoke;
                case SlotKind.ArtilleryIllumination:
                    return munition == MunitionKind.Illumination;
                case SlotKind.Artillery:
                    return munition != MunitionKind.Smoke && munition != MunitionKind.Illumination;
                default:
                    return true;
            }
        }

        // ------------------------------------------------------------------
        // Range clamping
        // ------------------------------------------------------------------

        /// <summary>Mission count: -1 (any negative) = unlimited, otherwise 0..999.</summary>
        public static int ClampMissions(int value)
        {
            if (value < 0)
            {
                return -1;
            }
            return value > MaxMissions ? MaxMissions : value;
        }

        /// <summary>Rounds per call: -1 (anything below 1) = the vanilla battery's own count, else 1..999.</summary>
        public static int ClampRoundsPerCall(int value)
        {
            if (value < 1)
            {
                return -1;
            }
            return value > MaxRoundsPerCall ? MaxRoundsPerCall : value;
        }

        /// <summary>
        /// Scale factors used by ImpactDelaySeconds / InterShotDelaySeconds / DispersionMeters /
        /// CooldownSeconds: 1 = vanilla, 0.5 = half, &lt;= 0 = none/instant. Clamped to -1..100 so a typo
        /// cannot produce an absurd value; NaN falls back to vanilla.
        /// </summary>
        public static float ClampScale(float value)
        {
            if (float.IsNaN(value))
            {
                return 1f;
            }
            if (value < -1f)
            {
                return -1f;
            }
            return value > 100f ? 100f : value;
        }

        public static float ClampHeading(float value)
        {
            if (float.IsNaN(value) || float.IsInfinity(value))
            {
                return 90f;
            }
            float heading = value % MaxHeadingDegrees;
            if (heading < 0f)
            {
                heading += MaxHeadingDegrees;
            }
            return heading;
        }

        public static float ClampRange(float value, float min, float max)
        {
            if (float.IsNaN(value))
            {
                return min;
            }
            if (value < min)
            {
                return min;
            }
            return value > max ? max : value;
        }

        // ------------------------------------------------------------------
        // Canonical names for logging / round-tripping
        // ------------------------------------------------------------------

        public static string ToConfigName(SlotKind kind)
        {
            switch (kind)
            {
                case SlotKind.ArtillerySmoke:
                    return "ArtillerySmoke";
                case SlotKind.ArtilleryIllumination:
                    return "ArtilleryIllumination";
                case SlotKind.CasFixedWing:
                    return "CASSupport";
                default:
                    return "Artillery";
            }
        }

        public static string ToConfigName(MunitionKind munition)
        {
            return munition.ToString();
        }

        public static string ToConfigName(WeaponKind weapon)
        {
            return weapon == WeaponKind.Any ? "Any" : weapon.ToString();
        }

        public static string ToConfigName(FlyoverKind flyover)
        {
            return flyover.ToString();
        }

        public static string ToConfigName(AttackKind attack)
        {
            switch (attack)
            {
                case AttackKind.AirToGroundMissile:
                    return "AirToGroundMissile";
                default:
                    return attack.ToString();
            }
        }

        private static string Normalize(string raw)
        {
            if (raw == null)
            {
                return string.Empty;
            }

            StringBuilder builder = new StringBuilder(raw.Length);
            for (int i = 0; i < raw.Length; i++)
            {
                char c = raw[i];
                if (char.IsLetterOrDigit(c))
                {
                    builder.Append(char.ToLowerInvariant(c));
                }
            }
            return builder.ToString();
        }
    }
}
