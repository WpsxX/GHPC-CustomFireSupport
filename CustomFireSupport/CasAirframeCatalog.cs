namespace CustomFireSupport
{
    /// <summary>
    /// Static knowledge about the game's CAS aircraft, extracted from the shipped prefabs / loadouts
    /// (Assets\PrefabHierarchyObject: A10, F104, F4_LW, F4_USAF, MiG17, MiG21, MiG23BN, SU22 - and the
    /// loadout asset names seen in mission logs, e.g. "F-104G Rockets", "F4 2x triple Mk82",
    /// "MiG-21 FAB-250 only").
    ///
    /// It is used for four things:
    ///   1. guessing the faction of an airframe prefab found by scanning the loaded assets (a mission
    ///      airframe's faction is authoritative and wins over this table),
    ///   2. choosing a suitable airframe for the player's faction (see CustomSlotBuilder.PickBest),
    ///   3. the default flyover profile when SlotN_CasFlyover is left empty,
    ///   4. recognising the designated strafing aircraft and rejecting cross-side loadout pairings
    ///      (IsGunRunAirframe / LoadoutFitsSide).
    ///
    /// The attack list is only a hint for logging: the attacks a slot may use always come from the
    /// loadout asset the airframe actually carries (CASLoadout.Attacks).
    /// </summary>
    /// <summary>Which side the airframe belongs to. Kept free of the game's Faction enum so the
    /// catalog can be unit-tested without the game assemblies.</summary>
    public enum AirframeSide
    {
        Unknown,
        Nato,
        Pact
    }

    internal sealed class AirframeInfo
    {
        internal string Pattern;
        internal AirframeSide Side;
        internal FlyoverKind Flyover;
        internal AttackKind[] TypicalAttacks;
    }

    internal static class CasAirframeCatalog
    {
        // Longest matching pattern wins, so "mi24v nva" beats "mi24" and "su22" beats "su".
        private static readonly AirframeInfo[] Entries =
        {
            // ---- Blue / NATO ------------------------------------------------
            new AirframeInfo
            {
                Pattern = "a10", Side = AirframeSide.Nato, Flyover = FlyoverKind.Linger,
                TypicalAttacks = new[] { AttackKind.GunRun, AttackKind.Bombs, AttackKind.Rockets }
            },
            new AirframeInfo
            {
                Pattern = "a-10", Side = AirframeSide.Nato, Flyover = FlyoverKind.Linger,
                TypicalAttacks = new[] { AttackKind.GunRun, AttackKind.Bombs, AttackKind.Rockets }
            },
            new AirframeInfo
            {
                Pattern = "f4", Side = AirframeSide.Nato, Flyover = FlyoverKind.SinglePass,
                TypicalAttacks = new[] { AttackKind.Bombs, AttackKind.Rockets }
            },
            new AirframeInfo
            {
                Pattern = "f-4", Side = AirframeSide.Nato, Flyover = FlyoverKind.SinglePass,
                TypicalAttacks = new[] { AttackKind.Bombs, AttackKind.Rockets }
            },
            new AirframeInfo
            {
                Pattern = "f104", Side = AirframeSide.Nato, Flyover = FlyoverKind.SinglePass,
                TypicalAttacks = new[] { AttackKind.Rockets, AttackKind.Bombs }
            },
            new AirframeInfo
            {
                Pattern = "f-104", Side = AirframeSide.Nato, Flyover = FlyoverKind.SinglePass,
                TypicalAttacks = new[] { AttackKind.Rockets, AttackKind.Bombs }
            },

            // ---- Red / Warsaw Pact ------------------------------------------
            new AirframeInfo
            {
                Pattern = "mig", Side = AirframeSide.Pact, Flyover = FlyoverKind.SinglePass,
                TypicalAttacks = new[] { AttackKind.Bombs, AttackKind.Rockets }
            },
            new AirframeInfo
            {
                Pattern = "su22", Side = AirframeSide.Pact, Flyover = FlyoverKind.SinglePass,
                TypicalAttacks = new[] { AttackKind.Bombs, AttackKind.Rockets }
            },
            new AirframeInfo
            {
                Pattern = "su-22", Side = AirframeSide.Pact, Flyover = FlyoverKind.SinglePass,
                TypicalAttacks = new[] { AttackKind.Bombs, AttackKind.Rockets }
            },
            new AirframeInfo
            {
                Pattern = "su25", Side = AirframeSide.Pact, Flyover = FlyoverKind.Linger,
                TypicalAttacks = new[] { AttackKind.Rockets, AttackKind.Bombs, AttackKind.GunRun }
            },
            new AirframeInfo
            {
                Pattern = "su-25", Side = AirframeSide.Pact, Flyover = FlyoverKind.Linger,
                TypicalAttacks = new[] { AttackKind.Rockets, AttackKind.Bombs, AttackKind.GunRun }
            },
        };

        /// <summary>Longest-pattern match; returns null for an unknown airframe.</summary>
        internal static AirframeInfo Match(string airframeName)
        {
            if (string.IsNullOrEmpty(airframeName))
            {
                return null;
            }

            string lower = airframeName.ToLowerInvariant();
            AirframeInfo best = null;
            for (int i = 0; i < Entries.Length; i++)
            {
                if (!lower.Contains(Entries[i].Pattern))
                {
                    continue;
                }
                if (best == null || Entries[i].Pattern.Length > best.Pattern.Length)
                {
                    best = Entries[i];
                }
            }
            return best;
        }

        internal static AirframeSide GuessSide(string airframeName)
        {
            AirframeInfo info = Match(airframeName);
            return info != null ? info.Side : AirframeSide.Unknown;
        }

        /// <summary>
        /// Patterns of the two aircraft the gun-run slots are pinned to: the A-10 (Blue) and the
        /// MiG-23BN (Red). Longest/first match wins, and matching is a case-insensitive substring test
        /// because donors appear as "A10", "A-10A", "MiG23BN", "MiG-23BN" depending on the source.
        /// </summary>
        private static readonly string[] GunRunPatterns = { "a-10", "a10", "mig-23bn", "mig23bn", "mig 23bn" };

        /// <summary>
        /// True for the designated strafing aircraft. Gun-run slots are pinned to them, and rocket
        /// slots deliberately skip them: the player already sees these two on every gun run, so the
        /// rocket slots draw from the rest of the side's aircraft instead.
        /// </summary>
        internal static bool IsGunRunAirframe(string airframeName)
        {
            if (string.IsNullOrEmpty(airframeName))
            {
                return false;
            }
            string lower = airframeName.ToLowerInvariant();
            for (int i = 0; i < GunRunPatterns.Length; i++)
            {
                if (lower.Contains(GunRunPatterns[i]))
                {
                    return true;
                }
            }
            return false;
        }

        // ------------------------------------------------------------------
        // Air to ground missile (AGM) profile
        // ------------------------------------------------------------------

        /// <summary>Speed of sound at ISA sea level, in m/s. The mod quotes its Mach numbers against it.</summary>
        internal const float SpeedOfSoundSeaLevelMetersPerSecond = 340.29f;

        /// <summary>
        /// The flight speed of both air-to-ground missiles, as a Mach number - the figure the player asked
        /// for. GHPC has no notion of Mach anywhere (AmmoType carries plain m/s), so the mod keeps the Mach
        /// number and converts it exactly once, here.
        /// </summary>
        internal const float CruiseMach = 1.2f;

        /// <summary>
        /// Mach 1.2 in m/s (408.3): the speed an air-to-ground missile of ours flies at for its WHOLE
        /// flight. The mod's own impact resolver pins the round to it (see CasPayloadFactory.CruiseSpeedFor),
        /// so the speed no longer depends on how fast the launching aircraft happened to be flying - the
        /// launch inherits the aircraft's airspeed, which used to make the same missile fly at a different
        /// speed from a different run.
        /// </summary>
        internal const float CruiseSpeedMetersPerSecond = CruiseMach * SpeedOfSoundSeaLevelMetersPerSecond;

        /// <summary>Mach number of a speed, for the log ("Mach 1.2"). Unit-testable, hence kept here.</summary>
        internal static float MachOf(float metersPerSecond)
        {
            return metersPerSecond / SpeedOfSoundSeaLevelMetersPerSecond;
        }

        /// <summary>
        /// The spec of the synthesized air-to-ground missile payload. The mod fires the game's own
        /// missile MODEL with the TOW missile's flight effects, but with a BOMB's data - so the model
        /// and the warhead come from different donors and the profile names both.
        /// </summary>
        internal sealed class MissileProfile
        {
            /// <summary>Human readable name for the log, e.g. "AGM-65 Maverick".</summary>
            internal string MissileId;

            /// <summary>
            /// Name fragment of the composed missile prefab in the bundle (CasMissileComposer builds it
            /// from the model plus the TOW flight effects).
            /// </summary>
            internal string PrefabHint;

            /// <summary>
            /// Missiles carried per sortie. Fixed at ONE: the air-to-ground missile is a single-shot
            /// weapon here (one launch per call), so the sortie fires exactly one missile and the slot's
            /// RoundsPerCall is deliberately ignored for it.
            /// </summary>
            internal int Munitions = 1;

            /// <summary>
            /// True = this payload always hits the locked target's centre, whatever SlotN_CasAccuracy says.
            /// The missile is flown by the mod (see CasPayloadFactory.ImpactOffsetFor), so the impact point
            /// can simply be the target itself: no draw from the impact circle, no dispersion, 100 % hits.
            /// </summary>
            internal bool GuaranteedHit = true;

            /// <summary>
            /// The missile's own motor boost, in m/s, added to the aircraft's speed at launch. The bomb the
            /// data comes from has MuzzleVelocity 0 (a gravity bomb is simply released), which left the
            /// missile flying at the aircraft's speed only - far too slow for a missile. 250 m/s on top of
            /// a CAS aircraft's ~150-250 m/s puts the LAUNCH in the right region (about Mach 1.2-1.5).
            ///
            /// It only decides the first frame, though: the mod's impact resolver pins the whole flight to
            /// <see cref="CruiseSpeedMetersPerSecond"/> straight away, so this value is the motor's kick
            /// rather than the speed the missile is seen flying at.
            /// </summary>
            internal float BoostVelocityMeters = 250f;

            /// <summary>
            /// The speed the mod's impact resolver pins this missile to for the whole flight, in m/s.
            /// Mach 1.2 (<see cref="CruiseSpeedMetersPerSecond"/>): a bomb-shaped round would otherwise have
            /// its speed bled away by drag (the bomb's drag coefficient is scaled by <see cref="DragScale"/>
            /// as well), and inheriting the aircraft's airspeed made the observed speed depend on the run.
            /// </summary>
            internal float CruiseSpeedMeters = CasAirframeCatalog.CruiseSpeedMetersPerSecond;

            /// <summary>
            /// The bomb's drag coefficient is multiplied by this for the missile: a bomb is a blunt body, an
            /// air-to-ground missile is not, and without this the doubled speed would wash off in seconds.
            /// </summary>
            internal float DragScale = 0.35f;

            /// <summary>Launch deviation arc in degrees (total arc length, as in the game's field).</summary>
            internal float DeviationDegrees = 0.4f;

            /// <summary>
            /// Range from the target at which the attack run releases, in metres. Further out than a bomb
            /// (the game's default is 1000 m): a missile is a stand-off weapon, and the mod flies it onto
            /// the impact point regardless of where it was released.
            /// </summary>
            internal float ReleaseDistanceMeters = 2600f;

            /// <summary>The airframe this payload is pinned to, for the log.</summary>
            internal string Airframe;
        }

        /// <summary>
        /// Blue missile: the AGM-65 carried by the A-10 (GHPC only ships the mesh, mounted as a visible
        /// pylon munition - there is no AGM-65 prefab or ammo asset in the game), pinned to the A-10.
        /// </summary>
        internal static readonly MissileProfile NatoMissile = new MissileProfile
        {
            MissileId = "AGM-65 Maverick",
            PrefabHint = "agm-65",
            Airframe = "A-10"
        };

        /// <summary>
        /// Red missile, pinned to the MiG-23BN, flying the game's own Soviet missile visual with the TOW
        /// flight effects. Uniformly named "Kh-25" - the name is an identifier, not a claim about which
        /// in-game asset it is: GHPC ships no separate Soviet air-to-ground missile asset (no mesh, no
        /// prefab, no ammo - a full scan of GHPC_Data and of every installed mod bundle finds none), so the
        /// model is the closest in-game Soviet missile while the name stays the one the player asked for.
        /// </summary>
        internal static readonly MissileProfile PactMissile = new MissileProfile
        {
            MissileId = "Kh-25",
            PrefabHint = "kh-25",
            Airframe = "MiG-23BN"
        };

        /// <summary>The missile profile of a side (Nato = AGM-65, Pact = the Soviet missile visual).</summary>
        internal static MissileProfile MissileFor(AirframeSide side)
        {
            return side == AirframeSide.Pact ? PactMissile : NatoMissile;
        }

        private static readonly string[] NatoMissileAirframes = { "a-10", "a10" };
        private static readonly string[] PactMissileAirframes = { "mig-23bn", "mig23bn", "mig 23bn" };

        /// <summary>
        /// True for the aircraft a missile slot is pinned to on the given side: A-10 (Blue) and
        /// MiG-23BN (Red) - the same two the gun run uses, because the AGM-65 model only exists on the
        /// A-10's pylons and the MiG-23BN is the Pact's ground attack aircraft.
        /// </summary>
        internal static bool IsMissileAirframe(string airframeName, AirframeSide side)
        {
            if (string.IsNullOrEmpty(airframeName))
            {
                return false;
            }
            string lower = airframeName.ToLowerInvariant();
            string[] patterns = side == AirframeSide.Pact ? PactMissileAirframes : NatoMissileAirframes;
            for (int i = 0; i < patterns.Length; i++)
            {
                if (lower.Contains(patterns[i]))
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// Side of an AIR-DROPPED BOMB, by name. The airframe table above cannot answer this: a bomb is
        /// called "MK82" or "FAB250", names that contain none of the aircraft patterns. It is used to give
        /// each faction's air-to-ground missile the data of ITS OWN bomb - a Red sortie clones the Soviet
        /// 250 kg FAB, a Blue one the US Mk-82 - instead of whatever bomb happens to be first in memory.
        /// </summary>
        internal static AirframeSide GuessBombSide(string bombName)
        {
            if (string.IsNullOrEmpty(bombName))
            {
                return AirframeSide.Unknown;
            }

            string lower = bombName.ToLowerInvariant();
            for (int i = 0; i < NatoBombHints.Length; i++)
            {
                if (lower.Contains(NatoBombHints[i]))
                {
                    return AirframeSide.Nato;
                }
            }
            for (int i = 0; i < PactBombHints.Length; i++)
            {
                if (lower.Contains(PactBombHints[i]))
                {
                    return AirframeSide.Pact;
                }
            }
            return AirframeSide.Unknown;
        }

        private static readonly string[] NatoBombHints =
        {
            "mk82", "mk-82", "mk 82", "m117", "mk83", "mk-83", "gbu", "paveway", "500lbs", "500 lbs"
        };

        private static readonly string[] PactBombHints =
        {
            "fab", "ofab", "250kg", "500kg", "100kg", "soviet"
        };

        /// <summary>
        /// True when a loadout belongs to the same side as the airframe it would be mounted on, i.e. an
        /// F-4 gets an American rocket pod and a MiG-21 gets a Soviet one.
        ///
        /// The donor scan pairs every loaded airframe prefab with every loadout asset whose hardpoint
        /// count fits, which produces nonsense pairs like "F4_LW + SU-22 rockets Multiple". Those are
        /// mechanically mountable (the mod hands the loadout to the game, which only counts hardpoints),
        /// but the pylons and their projectiles come from the other side - which is what turned a
        /// strafing F-4's rockets into white blocks. Unknown on either side is accepted.
        /// </summary>
        internal static bool LoadoutFitsSide(string airframeName, string loadoutName)
        {
            AirframeSide airframe = GuessSide(airframeName);
            AirframeSide loadout = GuessSide(loadoutName);
            return airframe == AirframeSide.Unknown || loadout == AirframeSide.Unknown || airframe == loadout;
        }

        // ------------------------------------------------------------------
        // Gun-run profile
        // ------------------------------------------------------------------

        /// <summary>
        /// The spec of one synthesized gun hardpoint. GHPC's shipped content has no addressable (and no
        /// guaranteed in-scene) gun hardpoint, so a GunRun always has to be constructed at runtime by
        /// CasPayloadFactory, which uses the single unified profile below.
        /// </summary>
        internal sealed class GunProfile
        {
            /// <summary>Human-readable gun id for logs, e.g. "GAU-8/Avenger 30mm".</summary>
            internal string GunId;

            /// <summary>Keywords matched (case-insensitive) against loaded AmmoCodexScriptable names.</summary>
            internal string[] AmmoHints;

            /// <summary>Stored munition count of the generated hardpoint.</summary>
            internal int Munitions = 400;

            /// <summary>Launch deviation arc in degrees (total arc length, as in the game's field).</summary>
            internal float DeviationDegrees = 0.8f;

            /// <summary>true = the round inherits the launch platform's velocity.</summary>
            internal bool InheritVelocity = true;

            /// <summary>Stream fire rate in rounds per minute; 0 = a single trigger pull (vanilla behaviour).</summary>
            internal float RateOfFireRPM = 0f;
        }
    }
}
