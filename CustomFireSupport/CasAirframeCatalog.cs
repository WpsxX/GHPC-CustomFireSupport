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
            // The F-15 is a mod addition: GHPC ships it only as scene objects, so it was extracted into a
            // prefab (see CasF15Extract) and bundled. It flies bombs and the air-to-ground missile - not
            // rockets - which is why Rockets is absent from its typical attacks.
            new AirframeInfo
            {
                Pattern = "f15", Side = AirframeSide.Nato, Flyover = FlyoverKind.SinglePass,
                TypicalAttacks = new[] { AttackKind.Bombs, AttackKind.AirToGroundMissile }
            },
            new AirframeInfo
            {
                Pattern = "f-15", Side = AirframeSide.Nato, Flyover = FlyoverKind.SinglePass,
                TypicalAttacks = new[] { AttackKind.Bombs, AttackKind.AirToGroundMissile }
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
        /// The aircraft side a faction flies: NATO for Blue (US / West Germany), Pact for Red (Soviet /
        /// East Germany). Draw pools use this to keep a Soviet task flying Soviet aircraft and a US task
        /// flying American ones, instead of merely preferring one over the other by score.
        ///
        /// Takes the game's Faction as an int on purpose: this file is kept free of the game's Faction
        /// enum (see <see cref="AirframeSide"/>) so it stays testable without the shipped assembly.
        /// Faction.Blue = 3 and Faction.Red = 4 in GHPC; anything else is treated as Red/Pact.
        /// </summary>
        internal static AirframeSide SideOfFactionValue(int faction)
        {
            return faction == 3 ? AirframeSide.Nato : AirframeSide.Pact;
        }

        // ------------------------------------------------------------------
        // The loadout each airframe ships with
        // ------------------------------------------------------------------

        /// <summary>
        /// The loadout each CAS airframe ships with, as "airframe|loadout" (lowercase, exact match).
        ///
        /// Read from the exported Unity project: every airframe prefab's CASHardpointManager carries a
        /// `Loadout` reference, and these are the pairs it points at. SU22 deliberately shares the
        /// MiG-23BN's FAB-250 loadout - that is the game's own binding, not a mod artifact, so it is
        /// listed as SU22's default rather than treated as a cross-pair.
        ///
        /// This table is what makes "the airframe flies its OWN loadout" checkable. Without it the draw
        /// pool could only ask "does this loadout carry the requested weapon", which every cross-pair
        /// also satisfies, so another aircraft's pylon would be admitted to the top-tier pool.
        /// </summary>
        private static readonly string[] DefaultLoadoutPairs =
        {
            "a10|a-10 mk82 focus",
            "f104|f-104g mk82s only",
            "f4_lw|f4 2x triple mk82",
            "f4_usaf|f4 2x triple mk82",
            "f15|f15 2x single mk82",
            "mig17|mig-17 rockets only",
            "mig21|mig-21 rockets only",
            "mig23bn|mig-23bn fab-250s only",
            "su22|mig-23bn fab-250s only",
            // The Su-25's own loadout: every one of its eight under-wing stations carries a FAB-250.
            "su25|su-25 fab-250 x8"
        };

        /// <summary>
        /// True when this loadout is the one the airframe ships with (see <see cref="DefaultLoadoutPairs"/>).
        /// An airframe whose default binding is unknown returns false, so it cannot enter the top-tier
        /// pool; it stays reachable through the synthesized tiers rather than being dropped outright.
        /// </summary>
        internal static bool IsDefaultLoadoutPair(string airframeName, string loadoutName)
        {
            if (string.IsNullOrEmpty(airframeName) || string.IsNullOrEmpty(loadoutName))
            {
                return false;
            }
            string key = airframeName.Trim().ToLowerInvariant() + "|" + loadoutName.Trim().ToLowerInvariant();
            for (int i = 0; i < DefaultLoadoutPairs.Length; i++)
            {
                if (string.Equals(DefaultLoadoutPairs[i], key, System.StringComparison.Ordinal))
                {
                    return true;
                }
            }
            return false;
        }

        // ------------------------------------------------------------------
        // Explicitly approved airframe + loadout pairs
        // ------------------------------------------------------------------

        /// <summary>
        /// Airframe + loadout pairs the mod is allowed to fly even though the pair is not the one the
        /// game binds by default, as "airframe|loadout" (both lowercase, matched exactly).
        ///
        /// WHY THIS LIST EXISTS: 6 of the game's 8 CAS airframes default to a BOMB loadout, and only the
        /// MiG-17 / MiG-21 default to rockets. That leaves the rocket pool with two aircraft, both Soviet,
        /// so a US/NATO task's rocket slot has nothing of its own to draw and has to synthesize a payload
        /// from another aircraft's pylon. Three further rocket loadouts ship in the same bundle and are
        /// mechanically valid fits - they are simply not what their airframe happens to default to:
        ///
        ///   F104    + F-104G Rockets          (5 hardpoint slot(s) vs 5 attach point(s)) - 2x Lau 32 FFAR
        ///   MiG23BN + MiG-23BN rockets only   (4 vs 4) - 2x B8 + 2x UB16
        ///   SU22    + SU-22 rockets Multiple  (4 vs 4) - 2x UB32 + 2x B8
        ///
        /// Each was verified against the exported Unity project: the loadout lists only rocket hardpoints
        /// (CASHardpoint._type == 1), every one is present in the mod's own cas_assets bundle, and each
        /// pair satisfies the game's HasCriticalConfigError() rule (hardpoint length == 1, or >= the
        /// airframe's attach-point count). Adding one here does NOT bypass any validation - the pair still
        /// has to pass FitsAirframe, and a typo simply fails to match and changes nothing.
        ///
        /// The list is deliberately explicit rather than "any loadout whose weapons match": the whole point
        /// of the fix is that an airframe must fly a loadout that belongs to it.
        /// </summary>
        private static readonly string[] ApprovedCrossLoadoutPairs =
        {
            "f104|f-104g rockets",
            "mig23bn|mig-23bn rockets only",
            "su22|su-22 rockets multiple",
            // An airframe gets ONE default loadout, and the Su-25's is the eight-bomb one. Its rocket
            // fitment - B-8 pods on the two innermost stations - is its own as well, so it is approved here
            // to keep the Su-25 in the Red rocket draw instead of being limited to bombs.
            "su25|su-25 rockets inner"
        };

        /// <summary>
        /// How many airframe+loadout pairs were explicitly approved (see
        /// <see cref="ApprovedCrossLoadoutPairs"/>). Exposed so the test suite can assert the table is
        /// populated rather than silently empty - an empty table would quietly drop the three rocket pairs
        /// the approved list exists for.
        /// </summary>
        internal static int ApprovedPairCount
        {
            get { return ApprovedCrossLoadoutPairs.Length; }
        }

        /// <summary>
        /// True when this airframe+loadout pair was explicitly approved above. Both names are matched
        /// case-insensitively and exactly (not as substrings), so an unrelated loadout whose name merely
        /// contains one of these strings cannot slip through.
        /// </summary>
        internal static bool IsApprovedPair(string airframeName, string loadoutName)
        {
            if (string.IsNullOrEmpty(airframeName) || string.IsNullOrEmpty(loadoutName))
            {
                return false;
            }
            string key = airframeName.Trim().ToLowerInvariant() + "|" + loadoutName.Trim().ToLowerInvariant();
            for (int i = 0; i < ApprovedCrossLoadoutPairs.Length; i++)
            {
                if (string.Equals(ApprovedCrossLoadoutPairs[i], key, System.StringComparison.Ordinal))
                {
                    return true;
                }
            }
            return false;
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
        /// What kind of warhead a missile carries. Kept as the catalog's own enum (like
        /// <see cref="AirframeSide"/>) so this file stays free of the game's assemblies and remains
        /// testable without the shipped DLL; CasPayloadFactory maps it onto AmmoType.AmmoCategory.
        /// </summary>
        internal enum WarheadKind
        {
            /// <summary>Blast warhead: damage comes from the charge, penetration from spall and blast.</summary>
            HighExplosive = 0,

            /// <summary>
            /// Shaped charge (the game's "HEAT"): LiveRound sets _isHeat from this, which changes the
            /// damage model to a molten jet and makes RhaPenetration - not the charge - the figure that
            /// decides what the round can defeat. See CasPayloadFactory for the RHA that is kept.
            /// </summary>
            ShapedCharge = 1
        }

        /// <summary>
        /// How an air-to-ground missile finds its target after launch. Kept as the catalog's own enum (like
        /// <see cref="WarheadKind"/>) so this file stays free of the game's assemblies and testable without
        /// the shipped DLL; CasMissileGuidance is what acts on it.
        ///
        /// The two values are not a flavour detail - they are the two weapons' defining difference, and
        /// they also decide what the round does when it loses its guidance:
        ///
        ///   * FireAndForget - the seeker was locked before launch ("机载下视锁定"), so the aircraft may
        ///     leave immediately ("彻底解放"). Losing the target leaves a round with no corrections left:
        ///     it keeps its attitude and its speed and falls away on the last tangent.
        ///   * LaserBeamRider - nothing is locked onto the target itself; the round flies at a laser spot
        ///     the CARRIER paints, and it only sees that spot while the carrier's nose is inside
        ///     LaserMaxOffAxisDegrees of it ("发射后飞行员绝对不能大幅机动"). Losing the spot is a
        ///     control failure, not a silent miss: the round tumbles and dives into the ground.
        /// </summary>
        internal enum GuidanceKind
        {
            /// <summary>Locked before launch, unguided by the carrier afterwards.</summary>
            FireAndForget = 0,

            /// <summary>Follows the carrier's laser spot, and only while the carrier keeps it in view.</summary>
            LaserBeamRider = 1
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
            /// Substring identifying the prefab that is shown hanging on the pylon before launch, so each
            /// side shows ITS OWN round: the AGM-65 for the NATO missile, the Kh-25 for the Soviet one.
            /// Matched case-insensitively against the bundle's prefab names.
            /// </summary>
            internal string PylonBodyHint;

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
            /// Each profile sets its OWN figure now, so the two sides no longer fly at the same speed:
            /// the resolver reads it per round through CasPayloadFactory.CruiseSpeedFor.
            ///
            /// Pinning matters because a bomb-shaped round would otherwise have its speed bled away by drag
            /// (the bomb's drag coefficient is scaled by <see cref="DragScale"/> as well), and inheriting
            /// the aircraft's airspeed made the observed speed depend on the run.
            /// </summary>
            internal float CruiseSpeedMeters = CasAirframeCatalog.CruiseSpeedMetersPerSecond;

            /// <summary>
            /// The warhead this missile carries. Overrides the donor bomb's Category/ShortName: the missile
            /// is meant to be its own weapon, not "a bomb with a rocket motor".
            /// </summary>
            internal WarheadKind Warhead = WarheadKind.HighExplosive;

            /// <summary>
            /// The charge mass in kg TNTe, which is what the game's AmmoType.TntEquivalentKg holds and what
            /// the blast model reads. Overrides the donor bomb's figure.
            /// </summary>
            internal float WarheadChargeKilograms;

            /// <summary>
            /// The shaped charge's penetration in mm RHA, or 0 to keep the donor bomb's.
            ///
            /// This is the field that decides what a shaped charge defeats: LiveRound reads penetration
            /// straight from AmmoType.RhaPenetration for a ShapedCharge round (`if (_isHeat &amp;&amp; !_jetActive)
            /// return Info.RhaPenetration;`), and derives the jet's travel from it as well. Setting it is
            /// therefore how a HEAT warhead gets its real performance; the charge mass alone does not.
            ///
            /// For scale, GHPC's own anti-tank missiles sit at 9M111 400, TOW 430, 9M14M 460, 9M112 440,
            /// PG-7VL 500, 9M112M 520, 3BK18M 585, and 9M113 / 9M114 / 9M17P / MILAN 600, I-TOW 630.
            /// </summary>
            internal float RhaPenetrationMm;

            /// <summary>
            /// The bomb's drag coefficient is multiplied by this for the missile: a bomb is a blunt body, an
            /// air-to-ground missile is not, and without this the doubled speed would wash off in seconds.
            /// </summary>
            internal float DragScale = 0.35f;

            /// <summary>Launch deviation arc in degrees (total arc length, as in the game's field).</summary>
            internal float DeviationDegrees = 0.4f;

            // ------------------------------------------------------------------
            // GUIDANCE AND FLIGHT CHARACTER
            //
            // The two sides' missiles are deliberately NOT the same weapon with different numbers: one is
            // a fire-and-forget imaging-seeker round that arches out, glides and pushes over, and the
            // other is a laser rider that has to be flown by a carrier that keeps the nose on the target,
            // pops up over the line of sight and then comes down hard. Every field below exists because
            // one of those two behaviours needs it, and the two profiles set opposite values for most of
            // them on purpose.
            // ------------------------------------------------------------------

            /// <summary>
            /// How the round is guided after launch, which also decides what happens when the guidance is
            /// lost (see <see cref="LostGuidancePitchDegreesPerSecond"/>).
            /// </summary>
            internal GuidanceKind Guidance = GuidanceKind.FireAndForget;

            /// <summary>
            /// Motor burn time in seconds, measured from launch. It is the clock for two things at once:
            /// the visual plume (CasMissileMotorBurnout switches the flame, the smoke trail, the engine
            /// light and the engine audio off at exactly this age) and the guidance's boost phase, during
            /// which the round may not fly shallower than <see cref="BoostClimbDegrees"/>.
            /// </summary>
            internal float MotorBurnSeconds = 5f;

            /// <summary>
            /// The climb angle the round is held to while the motor burns, in degrees ABOVE the line to
            /// the target. A missile that leaves the rail flat is a big rocket pointed at the target; both
            /// of these are launched with their noses pulled up instead. The AGM-65 lurches up hard (the
            /// player's "迅速抬高机头，约抬升20度"), the Kh-25 lifts less but keeps the height longer.
            /// </summary>
            internal float BoostClimbDegrees = 20f;

            /// <summary>
            /// How far ABOVE the line of sight the round aims while it is still far out, in metres. This is
            /// the whole shape of the trajectory: the round flies at a point that floats above the target
            /// and lets it sink back onto the target as the range closes, so it arrives from above without
            /// the guidance ever having to force an angle steeper than the ground allows.
            ///
            /// The AGM-65's "饱满的抛物拱桥" is a moderate arch; the Kh-25's "山坡 (Gorka)" is a higher
            /// pop-up that keeps the round clear of the dust its own laser has to see through, and it is
            /// what makes its final dive so steep (it is still high when the target is close).
            ///
            /// THE FIGURE IS SCALED TO THE GAME'S ENGAGEMENT RANGE, not to the real weapon's 20 km one. See
            /// TerminalRangeMeters for the measurement that sets it: a loft sized for a 20 km shot puts
            /// 140-270 m of climb under a 2-4 km one, which is where the "strange angles" came from.
            /// </summary>
            internal float LoftHeightMeters = 110f;

            /// <summary>
            /// The flight-path angle the round arrives at, in degrees below the horizontal. The guidance
            /// uses it as a FLOOR under the desired angle (never shallower than the cone, which is the hard
            /// "still able to dive onto it" limit), so the round settles onto the target at this angle
            /// instead of gliding in shallower as the cone relaxes with range.
            ///
            /// The AGM-65's "平稳向下压头，约30度斜俯角" is this number. The Kh-25 sets it high: "以近乎
            /// 笔直的角度强行把机头硬掰朝向激光反射点" is a near-vertical slam.
            /// </summary>
            internal float TerminalDiveDegrees = 30f;

            /// <summary>
            /// The range from the target, in metres, at which the loft is given up and the round aims
            /// straight at the impact point. Zero loft this far out is what turns the arch into a dive;
            /// a longer terminal range starts the push-over earlier and harder.
            ///
            /// SCALED TO THE GAME'S RANGES. The taper is also the flight path angle the loft commands while
            /// it is saturated - atan2(LoftHeight, range) - so it is what decides how steeply the round
            /// climbs and how much of the flight it spends descending. Sized for a real 20 km missile (a
            /// 1.2 km taper under a 140 m arch) a 2.2 km shot gained 143-153 m and then arrived at -40 deg;
            /// at the game's distances the same shot wants roughly a 0.7 ratio of taper to release range, so
            /// the arch is under 100 m, the climb is gentle and the dive is the profile's own angle.
            /// </summary>
            internal float TerminalRangeMeters = 900f;

            /// <summary>
            /// Turn rate in degrees per second while the motor burns, in degrees per second. A missile at
            /// full thrust has the most control authority it will ever have, and this is what makes the
            /// launch pull-up look quick instead of the aircraft's own slow bank. It is a separate number
            /// from the cruise rate on purpose: the round snaps its nose up and then flies smoothly.
            /// </summary>
            internal float BoostTurnRateDegreesPerSecond = 45f;

            /// <summary>
            /// Turn rate in degrees per second while the round is far out, in degrees per second. Without
            /// a limit the heading snaps straight to the computed direction, which reads as an instant
            /// pull-up; the AGM-65's "像一条被拉弯的钢丝，几乎没有突兀扭动" is a low number.
            /// </summary>
            internal float CruiseTurnRateDegreesPerSecond = 12f;

            /// <summary>
            /// Turn rate inside <see cref="TerminalRangeMeters"/>. The Kh-25's "鸭翼剧烈偏转，硬掰机头"
            /// is a high number: the same guidance law, allowed to bite much harder.
            /// </summary>
            internal float TerminalTurnRateDegreesPerSecond = 30f;

            /// <summary>
            /// The closest the aircraft may RELEASE this missile, in metres of horizontal range to the
            /// target. Inside this the shot is not taken: the round would have to come off the rail with the
            /// target nearly underneath it, and (see CasMissileAttackRun) the aircraft goes around for
            /// another pass instead. 0 disables the check.
            ///
            /// These are practical employment minima, not brochure figures - a run starts at the loadout's
            /// ReleaseDistance (1,800 m), so the gate only fires when the target was already closer than this
            /// when the call went in.
            /// </summary>
            internal float MinimumLaunchRangeMeters = 250f;

            /// <summary>
            /// The largest angle between the aircraft's nose and the target that still counts as a launch.
            /// Beyond it the round has to turn through more than its control authority allows before it can
            /// even see the target, which is how a shot ends up sailing past.
            /// </summary>
            internal float MaxLaunchOffAxisDegrees = 60f;

            /// <summary>
            /// The widest angle, in degrees, the CARRIER's nose may be off the laser spot for a
            /// <see cref="GuidanceKind.LaserBeamRider"/> round to keep seeing it. Zero for a round that
            /// does not ride a beam. The player's figure for the Kh-25ML is 30-35 degrees.
            /// </summary>
            internal float LaserMaxOffAxisDegrees;

            /// <summary>
            /// The longest the carrier may be asked to hold its run for one of these rounds, in seconds.
            /// The hold itself is the flight time to the target plus a margin, capped here so a round that
            /// somehow never arrives cannot keep an aircraft flying straight at the target forever.
            /// Zero for a fire-and-forget round, which frees the aircraft at launch.
            /// </summary>
            internal float CarrierHoldMaxSeconds;

            /// <summary>
            /// Guidance lost: how fast the nose drops, in degrees per second. The AGM-65's "失去修正能力，
            /// 沿最后的惯性切线平缓向前下落" is a few degrees per second; the Kh-25's "急剧俯冲下坠" is
            /// an order of magnitude more.
            /// </summary>
            internal float LostGuidancePitchDegreesPerSecond = 5f;

            /// <summary>Guidance lost: how fast the nose wanders, in degrees per second (0 = fly straight).</summary>
            internal float LostGuidanceYawDegreesPerSecond;

            /// <summary>
            /// Guidance lost: how fast the round rolls about its own axis, in degrees per second. This is
            /// the Kh-25's "小幅度剧烈自旋、横滚" and it is deliberately zero for the AGM-65, which is
            /// described as NOT going wild when it loses its target.
            /// </summary>
            internal float LostGuidanceRollDegreesPerSecond;

            /// <summary>
            /// Range from the target at which the attack run releases, in metres. As far out as the guidance
            /// can be trusted: the player asked for the missile to leave the rail as early as possible, and
            /// both profiles' loft law saturates at their terminal range, so a longer release simply means a
            /// longer glide rather than a different flight (validated out to ~3.2 km in _mountcheck).
            /// </summary>
            internal float ReleaseDistanceMeters = 2000f;

            /// <summary>The airframe this payload is pinned to, for the log.</summary>
            internal string Airframe;
        }

        /// <summary>
        /// Blue missile: the AGM-65 carried by the A-10 (GHPC only ships the mesh, mounted as a visible
        /// pylon munition - there is no AGM-65 prefab or ammo asset in the game). Flown at 1,150 km/h with
        /// a 56.25 kg shaped-charge warhead, matching the real AGM-65A/B/D/H: a 125 lb (57 kg) hollow
        /// charge at Mach 0.93.
        ///
        /// PENETRATION: 450 mm RHA, the figure commonly quoted for the Maverick's 57 kg shaped charge. It is
        /// set explicitly because a shaped charge takes its penetration from AmmoType.RhaPenetration, not
        /// from the charge - inheriting the donor Mk-82 would have left the HEAT jet at a general-purpose
        /// bomb's 90 mm. For reference, GHPC's own ATGMs run 400 mm (9M111) to 630 mm (I-TOW).
        ///
        /// FLIGHT: the "饱满的抛物拱桥" - a pull-up to 18 degrees while the motor burns, a moderate arch
        /// (the round aims 90 m above the sight line and sinks back onto it), a gentle glide once the
        /// smoke stops, and an arrival at about 30 degrees. Everything about it is rate-limited and
        /// fire-and-forget: the aircraft is free the moment it leaves the rail, and a lost target leaves it
        /// falling on its last tangent instead of tumbling.
        ///
        /// The loft and the taper are SCALED to the range the game is played at (see the two fields'
        /// remarks): measured over 1.5-4.0 km releases at 250-800 m, the shipped 140 m / 1200 m put
        /// 143-161 m of climb under a 2.2-2.6 km shot and arrived at -40 deg; the figures below peak at
        /// 100 m and arrive at the profile's own 30-40 deg, with the same guaranteed hit (worst miss
        /// 0.8 m across the envelope, measured in _mountcheck/cfs_missile_candidates.py).
        /// </summary>
        internal static readonly MissileProfile NatoMissile = new MissileProfile
        {
            MissileId = "AGM-65 Maverick",
            PrefabHint = "agm-65",
            PylonBodyHint = "pylon agm65",
            Airframe = "A-10",
            // Release as early as the guidance is good for: the A-10's Maverick is a stand-off shot, and the
            // player asked for the missile to leave the rail as soon as the run allows.
            ReleaseDistanceMeters = 2200f,
            // 1,150 km/h exactly: km/h -> m/s is /3.6.
            CruiseSpeedMeters = 1150f / 3.6f,
            Warhead = WarheadKind.ShapedCharge,
            WarheadChargeKilograms = 56.25f,
            RhaPenetrationMm = 450f,
            // Seeker lock before launch: the carrier is released at the rail, so there is no run to hold.
            Guidance = GuidanceKind.FireAndForget,
            MotorBurnSeconds = 5f,
            BoostClimbDegrees = 18f,
            LoftHeightMeters = 90f,
            TerminalDiveDegrees = 30f,
            TerminalRangeMeters = 800f,
            // "迅速抬高机头" at full thrust, then "像一条被拉弯的钢丝" for the rest of the flight.
            BoostTurnRateDegreesPerSecond = 45f,
            CruiseTurnRateDegreesPerSecond = 12f,
            TerminalTurnRateDegreesPerSecond = 22f,
            // A Maverick is a short-range weapon by air-to-ground missile standards, but it still needs a
            // launch envelope: under ~900 m the aircraft is firing at something almost under its nose.
            MinimumLaunchRangeMeters = 250f,
            MaxLaunchOffAxisDegrees = 60f,
            LaserMaxOffAxisDegrees = 0f,
            CarrierHoldMaxSeconds = 0f,
            // Lost seeker: no corrections left, nose falls away slowly, no spin, no wander.
            LostGuidancePitchDegreesPerSecond = 5f,
            LostGuidanceYawDegreesPerSecond = 0f,
            LostGuidanceRollDegreesPerSecond = 0f
        };

        /// <summary>
        /// Red missile, pinned to the MiG-23BN, flying the game's own Soviet missile visual with the TOW
        /// flight effects. Uniformly named "Kh-25" - the name is an identifier, not a claim about which
        /// in-game asset it is: GHPC ships no separate Soviet air-to-ground missile asset (no mesh, no
        /// prefab, no ammo - a full scan of GHPC_Data and of every installed mod bundle finds none), so the
        /// model is the closest in-game Soviet missile while the name stays the one the player asked for.
        /// Flown at 450 m/s with a 90 kg high-explosive warhead.
        ///
        /// FLIGHT: "暴烈的尖锐窜动，先抬后砸" - the motor is a single high-thrust stage (four seconds of
        /// it, most of the flight), the round pops up over the sight line (the "山坡 / Gorka", high enough
        /// that its own laser is not looking through the dust it kicked up), it stays high instead of
        /// gliding down, and the last stretch is a hard, near-straight slam. Its control authority is more
        /// than four times the AGM-65's, which is what "鸭翼剧烈偏转" looks like.
        ///
        /// The laser is the whole weapon: the carrier has to hold its run with the nose within 35 degrees
        /// of the spot until the round lands (CasLaserRunHold keeps the AI aircraft on that run and reports
        /// it in the log), and a beam that breaks takes the round's control with it.
        ///
        /// Its loft and taper are scaled to the game's ranges on the same basis as the AGM's (260 m / 600 m
        /// became 160 m / 700 m): the pop-up still happens and is still visibly higher than the AGM's, but
        /// it no longer puts 218-270 m of climb under a 1.8-2.6 km shot, and the arrival angle comes down
        /// from -73 deg average to about -46 deg - still the near-vertical slam of the brief, without the
        /// round running out of geometry before it gets there.
        /// </summary>
        internal static readonly MissileProfile PactMissile = new MissileProfile
        {
            MissileId = "Kh-25",
            PrefabHint = "kh-25",
            PylonBodyHint = "pylon kh25",
            Airframe = "MiG-23BN",
            CruiseSpeedMeters = 450f,
            Warhead = WarheadKind.HighExplosive,
            WarheadChargeKilograms = 90f,
            // Laser rider: the carrier must keep painting the spot, and the AI aircraft really does hold
            // the run for the flight (that is the "危险的伴飞过程" of the brief, not a cosmetic detail).
            Guidance = GuidanceKind.LaserBeamRider,
            MotorBurnSeconds = 5f,
            // A lower pull-up than the AGM, but held longer and with a much higher loft under it.
            BoostClimbDegrees = 12f,
            LoftHeightMeters = 160f,
            // High enough not to bind: the dive is as steep as the geometry allows, and the loft is what
            // makes that steep.
            TerminalDiveDegrees = 80f,
            TerminalRangeMeters = 700f,
            BoostTurnRateDegreesPerSecond = 60f,
            CruiseTurnRateDegreesPerSecond = 18f,
            // "鸭翼剧烈偏转，以近乎笔直的角度强行把机头硬掰朝向激光反射点".
            TerminalTurnRateDegreesPerSecond = 55f,
            // The laser needs the round to fly long enough for the carrier to hold its run, and the brief's
            // Kh-25 is a stand-off weapon: a release under ~1.5 km leaves the aircraft shooting at something
            // it is already flying past. The aircraft goes around instead (CasMissileAttackRun).
            MinimumLaunchRangeMeters = 250f,
            MaxLaunchOffAxisDegrees = 50f,
            // The player's figure for the Kh-25ML: 30-35 degrees of carrier nose offset.
            LaserMaxOffAxisDegrees = 35f,
            CarrierHoldMaxSeconds = 12f,
            // Lost beam: control failure - the nose slams down and the round spins and wanders as it goes.
            LostGuidancePitchDegreesPerSecond = 140f,
            LostGuidanceYawDegreesPerSecond = 55f,
            LostGuidanceRollDegreesPerSecond = 540f
        };

        /// <summary>
        /// The profile a built missile round belongs to, matched by the MissileId the factory stamps onto
        /// it. Each side's missile flies at its OWN speed now, so the resolver can no longer take the figure
        /// from one profile and apply it to both.
        /// </summary>
        internal static MissileProfile MissileById(string missileId)
        {
            if (string.IsNullOrEmpty(missileId))
            {
                return null;
            }
            if (string.Equals(missileId, NatoMissile.MissileId, System.StringComparison.OrdinalIgnoreCase))
            {
                return NatoMissile;
            }
            if (string.Equals(missileId, PactMissile.MissileId, System.StringComparison.OrdinalIgnoreCase))
            {
                return PactMissile;
            }
            return null;
        }

        /// <summary>The missile profile of a side (Nato = AGM-65, Pact = the Soviet missile visual).</summary>
        internal static MissileProfile MissileFor(AirframeSide side)
        {
            return side == AirframeSide.Pact ? PactMissile : NatoMissile;
        }

        // Blue: the A-10 carries the AGM-65 model on its pylons, and the F-15 (a mod addition, extracted
        // from the game's terrain scenes) flies it too - the two share the NATO missile slot's draw.
        private static readonly string[] NatoMissileAirframes = { "a-10", "a10", "f15", "f-15" };
        // Red: the MiG-23BN, plus the Su-25 (the player's own model, reconstructed by CasSu25Build), which
        // carries the Kh-25 as a symmetric pair.
        private static readonly string[] PactMissileAirframes = { "mig-23bn", "mig23bn", "mig 23bn", "su25", "su-25" };

        /// <summary>One missile, on the first station - the layout for airframes with a single AGM station.</summary>
        private static readonly int[] SingleMissileStation = { 0 };

        /// <summary>
        /// The Su-25's Kh-25 pair: the second-from-outermost pylon on each wing. The station order is the
        /// order CasSu25Build creates them in - L1, L2, L3, L4, R4, R3, R2, R1 - so index 2 is the left
        /// station and index 5 the mirror on the right.
        /// </summary>
        private static readonly int[] Su25MissileStations = { 2, 5 };

        /// <summary>
        /// The F-15's AGM-65 pair. Its three attach points are left inboard (0), belly (1) and right inboard
        /// (2) at x = -2.83 / +0.03 / +2.85, so stations 0 and 2 are the symmetric wing pair - the belly is
        /// left empty because a single centreline missile is not a "pair".
        /// </summary>
        private static readonly int[] F15MissileStations = { 0, 2 };

        /// <summary>
        /// The A-10's AGM-65 pair. It has eleven attach points (H1..H11 at x = +6.03 down to -6.03) and its
        /// own model already carries two baked AGM-65s at x = +3.775 / -3.775, which are closest to H3 and
        /// H9 - indices 2 and 8 - so both rounds launch from under the missiles the player can see.
        /// </summary>
        private static readonly int[] A10MissileStations = { 2, 8 };

        /// <summary>
        /// The MiG-23BN's Kh-25 pair: its four attach points are left wing root (0), left belly (1),
        /// right belly (2) and right wing root (3), so 0 and 3 are the symmetric wing pair.
        /// </summary>
        private static readonly int[] MiG23BNMissileStations = { 0, 3 };

        /// <summary>
        /// Which attach points an air-to-ground missile is mounted on, as indices into the airframe's own
        /// HardpointAttachPoints. The count is what decides how many missiles a sortie carries: the attack
        /// entry gets one trigger pull per station, so a two-station airframe launches two.
        ///
        /// Every airframe that can fly the air-to-ground missile carries a PAIR now - the player asked for
        /// two rounds per sortie, not one. Only the wing pairs are used, never the centreline/belly.
        /// </summary>
        internal static int[] MissileStationsFor(string airframeName)
        {
            if (!string.IsNullOrEmpty(airframeName))
            {
                string lower = airframeName.ToLowerInvariant();
                if (lower.Contains("su25") || lower.Contains("su-25"))
                {
                    return Su25MissileStations;
                }
                if (lower.Contains("f15") || lower.Contains("f-15"))
                {
                    return F15MissileStations;
                }
                if (lower.Contains("a10") || lower.Contains("a-10"))
                {
                    return A10MissileStations;
                }
                if (lower.Contains("mig23bn") || lower.Contains("mig-23bn"))
                {
                    return MiG23BNMissileStations;
                }
            }
            return SingleMissileStation;
        }

        /// <summary>
        /// True for the aircraft a missile slot may fly on the given side: the A-10 and the F-15 (Blue),
        /// the MiG-23BN (Red). The AGM-65 model only exists on the A-10's pylons and the F-15 shares the
        /// NATO pylon family; the MiG-23BN is the Pact's ground attack aircraft. A missile slot DRAWS
        /// among these rather than being pinned to one, so the A-10 and the F-15 alternate.
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




