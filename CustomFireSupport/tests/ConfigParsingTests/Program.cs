using System;
using System.Collections.Generic;
using CustomFireSupport;

namespace ConfigParsingTests
{
    /// <summary>
    /// Headless tests for SlotConfigParsing: every config value a user can type is fed through the
    /// parser, including typos, empty strings and out-of-range numbers. Exit code 0 = all passed.
    /// </summary>
    internal static class Program
    {
        private static int _passed;
        private static readonly List<string> _failures = new List<string>();

        private static int Main()
        {
            SlotKinds();
            Munitions();
            Weapons();
            Flyovers();
            AttackLists();
            DerivedValues();
            CasAccuracyRadius();
            Clamps();
            RoundTrips();
            CfgSection();
            Catalog();
            GunRunAirframes();
            LoadoutSides();
            FactionShells();
            BatteryProfiles();
            SlotTiming();
            ShippedLayout();
            MissilePayloads();

            Console.WriteLine();
            Console.WriteLine("passed: " + _passed + ", failed: " + _failures.Count);
            for (int i = 0; i < _failures.Count; i++)
            {
                Console.WriteLine("FAIL: " + _failures[i]);
            }
            return _failures.Count == 0 ? 0 : 1;
        }

        /// <summary>
        /// Faction fit of the smoke / illumination projectile prefabs GHPC's batteries use: Blue flies the
        /// US 155 mm white-phosphorus shells (M110A1 / M116A1 "Smoke Artillery"), Red the Soviet 152 mm ones
        /// (2S3 Smoke / 2S1 Artillery smoke, 2S3 Illumination / 2S1 Artillery Illumination), and the two
        /// look different - so the wrong pick would hand a Soviet player an American smoke screen.
        /// </summary>
        private static void FactionShells()
        {
            // Blue: the US smoke shells.
            Check("blue smoke: M110A1 wins", FactionShellCatalog.Score("M110A1 Smoke Artillery", false, AirframeSide.Nato) > 100);
            Check("blue smoke: M116A1 wins", FactionShellCatalog.Score("M116A1 Smoke Artillery", false, AirframeSide.Nato) > 100);
            Check("blue smoke: US beats Soviet", FactionShellCatalog.Score("M110A1 Smoke Artillery", false, AirframeSide.Nato) >
                                                 FactionShellCatalog.Score("2S3 Smoke", false, AirframeSide.Nato));

            // Red: the Soviet smoke shells.
            Check("red smoke: 2S3 wins", FactionShellCatalog.Score("2S3 Smoke", false, AirframeSide.Pact) > 100);
            Check("red smoke: 2S1 variants win",
                FactionShellCatalog.Score("2S1 Artillery smoke WP", false, AirframeSide.Pact) > 100 &&
                FactionShellCatalog.Score("2S1 Artillery smoke", false, AirframeSide.Pact) > 100);
            Check("red smoke: Soviet beats US", FactionShellCatalog.Score("2S3 Smoke", false, AirframeSide.Pact) >
                                                 FactionShellCatalog.Score("M110A1 Smoke Artillery", false, AirframeSide.Pact));

            // Illumination is faction specific as well.
            Check("blue illum: IlluminationFlare wins", FactionShellCatalog.Score("IlluminationFlare", true, AirframeSide.Nato) > 100);
            Check("red illum: 2S3 wins", FactionShellCatalog.Score("2S3 Illumination", true, AirframeSide.Pact) > 100);
            Check("red illum: Soviet beats US", FactionShellCatalog.Score("2S3 Illumination", true, AirframeSide.Pact) >
                                                 FactionShellCatalog.Score("IlluminationFlare", true, AirframeSide.Pact));

            // The names the game actually uses (found in GHPC_Data): M109 Smoke / M109 Smoke WP /
            // M109 Illumination for the US, 2S1 Smoke / 2S1 Smoke WP / 2S3 Smoke and 2S3 Illumination /
            // 2S1 Artillery Illumination for the Soviets.
            Check("blue smoke: M109 Smoke WP wins",
                FactionShellCatalog.Score("M109 Smoke WP", false, AirframeSide.Nato) >
                FactionShellCatalog.Score("2S1 Smoke WP", false, AirframeSide.Nato));
            Check("red smoke: 2S1 Smoke WP wins",
                FactionShellCatalog.Score("2S1 Smoke WP", false, AirframeSide.Pact) >
                FactionShellCatalog.Score("M109 Smoke WP", false, AirframeSide.Pact));
            Check("blue illum: M109 Illumination wins",
                FactionShellCatalog.Score("M109 Illumination", true, AirframeSide.Nato) >
                FactionShellCatalog.Score("2S3 Illumination", true, AirframeSide.Nato));
            Check("red illum: 2S3 Illumination wins",
                FactionShellCatalog.Score("2S3 Illumination", true, AirframeSide.Pact) >
                FactionShellCatalog.Score("M109 Illumination", true, AirframeSide.Pact));
            // Things that are not fire-mission effect prefabs must never be picked.
            Check("smoke: vehicle damage smoke rejected",
                FactionShellCatalog.Score("Side Smoke Large 1", false, AirframeSide.Nato) == 0 &&
                FactionShellCatalog.Score("Hatch Smoke Non-Scaling", false, AirframeSide.Nato) == 0 &&
                FactionShellCatalog.Score("Smouldering Smoke Medium", false, AirframeSide.Nato) == 0);
            Check("illum: unrelated flares rejected",
                FactionShellCatalog.Score("road flare half r", true, AirframeSide.Nato) == 0 &&
                FactionShellCatalog.Score("Flare Trigger", true, AirframeSide.Nato) == 0);
            Check("smoke name is not illumination and vice versa",
                FactionShellCatalog.Score("M110A1 Smoke Artillery", true, AirframeSide.Nato) == 0 &&
                FactionShellCatalog.Score("IlluminationFlare", false, AirframeSide.Nato) == 0);
        }

        /// <summary>
        /// The per-side battery profile store: a mission without batteries falls back to the last profile
        /// seen for the player's side (so Red keeps 2S3-style fire missions, Blue M109-style ones).
        /// </summary>
        private static void BatteryProfiles()
        {
            BatteryProfile red = new BatteryProfile { SourceName = "2S3", Shots = 8, DispersionMeters = 50f };
            BatteryProfile blue = new BatteryProfile { SourceName = "M109", Shots = 12, DispersionMeters = 100f };

            Check("profile: nothing remembered yet", FactionBatteryProfiles.Recall("Red") == null);
            FactionBatteryProfiles.Remember("Red", red);
            FactionBatteryProfiles.Remember("Blue", blue);
            Check("profile: red recalled", ReferenceEquals(FactionBatteryProfiles.Recall("Red"), red) &&
                                           FactionBatteryProfiles.Recall("Red").Shots == 8);
            Check("profile: blue recalled", ReferenceEquals(FactionBatteryProfiles.Recall("Blue"), blue) &&
                                            FactionBatteryProfiles.Recall("Blue").DispersionMeters == 100f);
            Check("profile: unknown side is null", FactionBatteryProfiles.Recall("Green") == null);
            Check("profile: null side is null", FactionBatteryProfiles.Recall(null) == null);

            BatteryProfile newer = new BatteryProfile { SourceName = "2S3 (2)", Shots = 6 };
            FactionBatteryProfiles.Remember("Red", newer);
            Check("profile: newest wins", FactionBatteryProfiles.Recall("Red").Shots == 6);
            FactionBatteryProfiles.Remember("Red", null);
            Check("profile: null profile ignored", FactionBatteryProfiles.Recall("Red").Shots == 6);
            Check("profile: describe mentions the battery", newer.Describe().Contains("2S3 (2)") &&
                                                          newer.Describe().Contains("6 rounds"));
        }

        /// <summary>
        /// The two timing keys of a slot, resolved by ArtilleryTiming.
        ///
        /// The regression this group exists for: ImpactDelaySeconds below 1 used to multiply the round
        /// spacing as well, so a slot that asked for "arrive fast, but space the rounds like vanilla"
        /// (ImpactDelaySeconds = 0.3, InterShotDelaySeconds = 1.0) fired its rounds
        /// 0.7 x 0.3 = 0.21 s apart. The interval key was being written into the cfg and silently
        /// overridden, which is what "the interval does not work in game" looked like.
        /// </summary>
        private static void SlotTiming()
        {
            const float vanillaImpact = 120f;
            const float vanillaInterShot = 0.7f;

            // The reported configuration: ImpactDelaySeconds = 0.3 with InterShotDelaySeconds = 1.0.
            ArtilleryTiming.Timing reported = ArtilleryTiming.Resolve(vanillaImpact, vanillaInterShot, 0.3f, 1f);
            Check("timing: impact below 1 cancels the first-round delay", reported.ImpactDelaySeconds == 0f);
            Check("timing: the interval key is honoured",
                Math.Abs(reported.InterShotSeconds - 0.7f) < 0.0001f);
            Check("timing: the silent 0.3x compression of the interval is gone",
                Math.Abs(reported.InterShotSeconds - 0.21f) > 0.001f);
            Check("timing: not an instant volley", !reported.InstantVolley);
            Check("timing: three rounds span 1.4 s", Math.Abs(reported.VolleySeconds(3) - 1.4f) < 0.0001f);

            // The interval key on its own.
            Check("timing: half interval",
                Math.Abs(ArtilleryTiming.Resolve(vanillaImpact, vanillaInterShot, 1f, 0.5f).InterShotSeconds - 0.35f) < 0.0001f);
            Check("timing: interval 0 = one frame",
                ArtilleryTiming.Resolve(vanillaImpact, vanillaInterShot, 1f, 0f).InterShotSeconds == 0f);
            Check("timing: interval 0 with a below-1 impact scale too",
                ArtilleryTiming.Resolve(vanillaImpact, vanillaInterShot, 0.5f, 0f).InterShotSeconds == 0f);

            // The impact key on its own.
            Check("timing: scale 1 = the battery's own delay",
                Math.Abs(ArtilleryTiming.Resolve(vanillaImpact, vanillaInterShot, 1f, 1f).ImpactDelaySeconds - 120f) < 0.0001f);
            Check("timing: scale 2 = twice the delay",
                Math.Abs(ArtilleryTiming.Resolve(vanillaImpact, vanillaInterShot, 2f, 1f).ImpactDelaySeconds - 240f) < 0.0001f);
            Check("timing: scale 0.5 = the first round arrives on the call",
                ArtilleryTiming.Resolve(vanillaImpact, vanillaInterShot, 0.5f, 1f).ImpactDelaySeconds == 0f);
            Check("timing: a one-round volley has no spacing",
                Math.Abs(ArtilleryTiming.Resolve(vanillaImpact, vanillaInterShot, 1f, 1f).VolleySeconds(1) - 120f) < 0.0001f);

            // Instant volleys (< = 0) have to drop the spacing: every round goes out on one frame.
            ArtilleryTiming.Timing instant = ArtilleryTiming.Resolve(vanillaImpact, vanillaInterShot, 0f, 1f);
            Check("timing: scale 0 = instant volley", instant.InstantVolley && instant.InterShotSeconds == 0f);
            Check("timing: scale 0 has no delay and no length",
                instant.ImpactDelaySeconds == 0f && instant.VolleySeconds(3) == 0f);
            Check("timing: negative scale = instant volley",
                ArtilleryTiming.Resolve(vanillaImpact, vanillaInterShot, -1f, 1f).InstantVolley);

            // The shared scale helper.
            Check("scale: vanilla -> vanilla", Math.Abs(ArtilleryTiming.Scale(0.7f, 1f) - 0.7f) < 0.0001f);
            Check("scale: negative -> 0", ArtilleryTiming.Scale(10f, -2f) == 0f);
            Check("scale: NaN -> 0", ArtilleryTiming.Scale(10f, float.NaN) == 0f);
            Check("scale: zero vanilla stays zero", ArtilleryTiming.Scale(0f, 4f) == 0f);
        }
        private static void SlotKinds()
        {            SlotKind kind;
            Check("slot Artillery", SlotConfigParsing.TryParseSlotKind("Artillery", out kind) && kind == SlotKind.Artillery);
            Check("slot HE alias", SlotConfigParsing.TryParseSlotKind("HE", out kind) && kind == SlotKind.Artillery);
            Check("slot arty alias", SlotConfigParsing.TryParseSlotKind("arty", out kind) && kind == SlotKind.Artillery);
            Check("slot Smoke", SlotConfigParsing.TryParseSlotKind("Smoke", out kind) && kind == SlotKind.ArtillerySmoke);
            Check("slot ArtillerySmoke", SlotConfigParsing.TryParseSlotKind("ArtillerySmoke", out kind) && kind == SlotKind.ArtillerySmoke);
            Check("slot Illum", SlotConfigParsing.TryParseSlotKind("Illum", out kind) && kind == SlotKind.ArtilleryIllumination);
            Check("slot Flare", SlotConfigParsing.TryParseSlotKind("flare", out kind) && kind == SlotKind.ArtilleryIllumination);
            Check("slot CAS", SlotConfigParsing.TryParseSlotKind("CAS", out kind) && kind == SlotKind.CasFixedWing);
            Check("slot Fixed Wing", SlotConfigParsing.TryParseSlotKind("Fixed Wing", out kind) && kind == SlotKind.CasFixedWing);
            Check("slot CASSupport", SlotConfigParsing.TryParseSlotKind("CASSupport", out kind) && kind == SlotKind.CasFixedWing);
            Check("slot garbage rejected", !SlotConfigParsing.TryParseSlotKind("banana", out kind));
            Check("slot null rejected", !SlotConfigParsing.TryParseSlotKind(null, out kind));
            Check("slot empty rejected", !SlotConfigParsing.TryParseSlotKind("", out kind));
            Check("removed heli type detected", SlotConfigParsing.IsRemovedHelicopterType("CASHeliSupport")
                && SlotConfigParsing.IsRemovedHelicopterType("heli") && !SlotConfigParsing.IsRemovedHelicopterType("CAS"));
        }

        private static void Munitions()
        {
            MunitionKind munition;
            Check("munition AP", SlotConfigParsing.TryParseMunition("AP", out munition) && munition == MunitionKind.AntiPersonnel);
            Check("munition HE", SlotConfigParsing.TryParseMunition("he", out munition) && munition == MunitionKind.AntiPersonnel);
            Check("munition HEAT", SlotConfigParsing.TryParseMunition("HEAT", out munition) && munition == MunitionKind.AntiArmor);
            Check("munition Anti-Tank", SlotConfigParsing.TryParseMunition("Anti-Tank", out munition) && munition == MunitionKind.AntiArmor);
            Check("munition Smoke", SlotConfigParsing.TryParseMunition("smoke", out munition) && munition == MunitionKind.Smoke);
            Check("munition WP", SlotConfigParsing.TryParseMunition("WP", out munition) && munition == MunitionKind.Smoke);
            Check("munition Illum", SlotConfigParsing.TryParseMunition("illum", out munition) && munition == MunitionKind.Illumination);
            Check("munition garbage rejected", !SlotConfigParsing.TryParseMunition("zzz", out munition));
        }

        private static void Weapons()
        {
            WeaponKind weapon;
            Check("weapon Guns", SlotConfigParsing.TryParseWeapon("guns", out weapon) && weapon == WeaponKind.Guns);
            Check("weapon Howitzer", SlotConfigParsing.TryParseWeapon("howitzer", out weapon) && weapon == WeaponKind.Guns);
            Check("weapon Mortars", SlotConfigParsing.TryParseWeapon("Mortars", out weapon) && weapon == WeaponKind.Mortars);
            Check("weapon Rockets", SlotConfigParsing.TryParseWeapon("rocket", out weapon) && weapon == WeaponKind.Rockets);
            Check("weapon empty = Any", SlotConfigParsing.TryParseWeapon("", out weapon) && weapon == WeaponKind.Any);
            Check("weapon garbage rejected", !SlotConfigParsing.TryParseWeapon("zzz", out weapon) && weapon == WeaponKind.Any);
        }

        private static void Flyovers()
        {
            FlyoverKind flyover;
            Check("flyover Linger", SlotConfigParsing.TryParseFlyover("Linger", out flyover) && flyover == FlyoverKind.Linger);
            Check("flyover loiter", SlotConfigParsing.TryParseFlyover("loiter", out flyover) && flyover == FlyoverKind.Linger);
            Check("flyover SinglePass", SlotConfigParsing.TryParseFlyover("single", out flyover) && flyover == FlyoverKind.SinglePass);
            Check("flyover garbage rejected", !SlotConfigParsing.TryParseFlyover("zzz", out flyover) && flyover == FlyoverKind.SinglePass);
        }

        private static void AttackLists()
        {
            string problems;
            AttackKind attack;

            Check("attacks empty = all", SlotConfigParsing.ParseAttackList("", out problems).Length == 0 && problems == null);
            Check("attacks Any = all", SlotConfigParsing.ParseAttackList("Any", out problems).Length == 0 && problems == null);
            Check("attacks * = all", SlotConfigParsing.ParseAttackList("*", out problems).Length == 0 && problems == null);

            AttackKind[] parsed = SlotConfigParsing.ParseAttackList("Bombs, Rockets", out problems);
            Check("attacks two entries", parsed.Length == 2 && parsed[0] == AttackKind.Bombs && parsed[1] == AttackKind.Rockets && problems == null);

            parsed = SlotConfigParsing.ParseAttackList("bombs;bombs|bomb", out problems);
            Check("attacks deduplicated", parsed.Length == 1 && parsed[0] == AttackKind.Bombs);

            parsed = SlotConfigParsing.ParseAttackList("GunRun,ATGM", out problems);
            Check("attacks gun run + atgm", parsed.Length == 2 && parsed[0] == AttackKind.GunRun && parsed[1] == AttackKind.AirToGroundMissile);

            parsed = SlotConfigParsing.ParseAttackList("bombs,zzz", out problems);
            Check("attacks partial typo keeps valid", parsed.Length == 1 && parsed[0] == AttackKind.Bombs && problems != null && problems.Contains("zzz"));

            parsed = SlotConfigParsing.ParseAttackList("zzz", out problems);
            Check("attacks all-typo falls back to all", parsed.Length == 0 && problems != null && problems.Contains("zzz"));

            parsed = SlotConfigParsing.ParseAttackList("bombs,Any", out problems);
            Check("attacks Any anywhere = all", parsed.Length == 0);

            // The air-to-air missile and the training round were dropped, so the config must reject them
            // with an explicit "no longer supported" note rather than accepting or silently listing them.
            Check("air-to-air missile is not a supported attack type",
                !SlotConfigParsing.TryParseAttack("AirToAirMissile", out attack) &&
                !SlotConfigParsing.TryParseAttack("aam", out attack) &&
                !SlotConfigParsing.TryParseAttack("air-air", out attack));
            Check("training round is not a supported attack type",
                !SlotConfigParsing.TryParseAttack("Inert", out attack) &&
                !SlotConfigParsing.TryParseAttack("training", out attack) &&
                !SlotConfigParsing.TryParseAttack("dummy", out attack));
            Check("removed attack types are recognised as removed",
                SlotConfigParsing.IsRemovedAttackType("AirToAirMissile") &&
                SlotConfigParsing.IsRemovedAttackType("aam") &&
                SlotConfigParsing.IsRemovedAttackType("Inert") &&
                SlotConfigParsing.IsRemovedAttackType("training"));
            Check("supported attack types are not reported as removed",
                !SlotConfigParsing.IsRemovedAttackType("Bombs") &&
                !SlotConfigParsing.IsRemovedAttackType("Rockets") &&
                !SlotConfigParsing.IsRemovedAttackType("AirToGroundMissile") &&
                !SlotConfigParsing.IsRemovedAttackType("GunRun") &&
                !SlotConfigParsing.IsRemovedAttackType("Any") &&
                !SlotConfigParsing.IsRemovedAttackType(null));

            parsed = SlotConfigParsing.ParseAttackList("Bombs,AirToAirMissile", out problems);
            Check("removed attack entry is dropped, the rest is kept",
                parsed.Length == 1 && parsed[0] == AttackKind.Bombs && problems != null &&
                problems.Contains("AirToAirMissile") && problems.Contains("no longer supported"));

            parsed = SlotConfigParsing.ParseAttackList("Inert,Bombs", out problems);
            Check("training round is dropped, the rest is kept",
                parsed.Length == 1 && parsed[0] == AttackKind.Bombs && problems != null &&
                problems.Contains("Inert") && problems.Contains("no longer supported"));

            parsed = SlotConfigParsing.ParseAttackList("Inert,AirToAirMissile", out problems);
            Check("all-removed list falls back to Any", parsed.Length == 0 && problems != null &&
                                                      problems.Contains("Inert") && problems.Contains("AirToAirMissile"));

            parsed = SlotConfigParsing.ParseAttackList("Bombs,zzz,AAM", out problems);
            Check("removed and unknown entries are reported separately",
                parsed.Length == 1 && parsed[0] == AttackKind.Bombs && problems != null &&
                problems.Contains("no longer supported") && problems.Contains("unknown") &&
                problems.Contains("zzz") && problems.Contains("AAM"));

            Check("the config names of the removed types are gone",
                SlotConfigParsing.ToConfigName(AttackKind.AirToGroundMissile) == "AirToGroundMissile" &&
                SlotConfigParsing.ToConfigName(AttackKind.GunRun) == "GunRun");

            Check("every remaining attack kind round-trips",
                SlotConfigParsing.TryParseAttack(SlotConfigParsing.ToConfigName(AttackKind.Bombs), out attack) &&
                attack == AttackKind.Bombs &&
                SlotConfigParsing.TryParseAttack(SlotConfigParsing.ToConfigName(AttackKind.AirToGroundMissile), out attack) &&
                attack == AttackKind.AirToGroundMissile);
        }

        private static void DerivedValues()
        {
            Check("default munition smoke slot", SlotConfigParsing.DefaultMunitionFor(SlotKind.ArtillerySmoke) == MunitionKind.Smoke);
            Check("default munition illum slot", SlotConfigParsing.DefaultMunitionFor(SlotKind.ArtilleryIllumination) == MunitionKind.Illumination);
            Check("default munition artillery slot", SlotConfigParsing.DefaultMunitionFor(SlotKind.Artillery) == MunitionKind.AntiPersonnel);
            Check("default munition CAS slot", SlotConfigParsing.DefaultMunitionFor(SlotKind.CasFixedWing) == MunitionKind.AntiPersonnel);

            Check("match smoke/smoke", SlotConfigParsing.MunitionMatchesSlotKind(SlotKind.ArtillerySmoke, MunitionKind.Smoke));
            Check("mismatch smoke/HE", !SlotConfigParsing.MunitionMatchesSlotKind(SlotKind.ArtillerySmoke, MunitionKind.AntiPersonnel));
            Check("mismatch artillery/smoke", !SlotConfigParsing.MunitionMatchesSlotKind(SlotKind.Artillery, MunitionKind.Smoke));
            Check("match artillery/AT", SlotConfigParsing.MunitionMatchesSlotKind(SlotKind.Artillery, MunitionKind.AntiArmor));
            Check("match illum/illum", SlotConfigParsing.MunitionMatchesSlotKind(SlotKind.ArtilleryIllumination, MunitionKind.Illumination));
            Check("CAS slots always match", SlotConfigParsing.MunitionMatchesSlotKind(SlotKind.CasFixedWing, MunitionKind.Smoke));

            Check("IsCasSlot fixed wing", SlotConfigParsing.IsCasSlot(SlotKind.CasFixedWing));
            Check("IsCasSlot artillery", !SlotConfigParsing.IsCasSlot(SlotKind.Artillery));
        }

        /// <summary>
        /// The CasAccuracy impact circle: 0 (or the mod's "off" spelling -1) = radius 0 = every round is
        /// flown into the target's centre, 1 = 5 m, 0.5 = 2.5 m, > 1 = wider.
        /// </summary>
        private static void CasAccuracyRadius()
        {
            Check("cas radius 0 = precise hit", SlotConfigParsing.CasAccuracyRadius(0f) == 0f);
            Check("cas radius -1 = precise hit", SlotConfigParsing.CasAccuracyRadius(-1f) == 0f);
            Check("cas radius 1 = 15 m", Math.Abs(SlotConfigParsing.CasAccuracyRadius(1f) - 15f) < 0.0001f);
            Check("cas radius 0.5 = 7.5 m (50 %)", Math.Abs(SlotConfigParsing.CasAccuracyRadius(0.5f) - 7.5f) < 0.0001f);
            Check("cas radius 0.2 = 3 m (20 %)", Math.Abs(SlotConfigParsing.CasAccuracyRadius(0.2f) - 3f) < 0.0001f);
            Check("cas radius 0.3 = 4.5 m (30 %)", Math.Abs(SlotConfigParsing.CasAccuracyRadius(0.3f) - 4.5f) < 0.0001f);
            Check("cas radius 0.4 = 6 m (40 %)", Math.Abs(SlotConfigParsing.CasAccuracyRadius(0.4f) - 6f) < 0.0001f);
            Check("cas radius 2 = 30 m (wider)", Math.Abs(SlotConfigParsing.CasAccuracyRadius(2f) - 30f) < 0.0001f);
            Check("cas radius NaN -> 15 m", Math.Abs(SlotConfigParsing.CasAccuracyRadius(float.NaN) - 15f) < 0.0001f);
            Check("cas radius is a percentage of 15 m",
                Math.Abs(SlotConfigParsing.CasAccuracyRadius(0.1f) - 1.5f) < 0.0001f &&
                Math.Abs(SlotConfigParsing.CasAccuracyRadius(0.75f) - 11.25f) < 0.0001f &&
                Math.Abs(SlotConfigParsing.CasAccuracyRadius(1f) - SlotConfigParsing.CasAccuracyRadiusAtOne) < 0.0001f);
            Check("cas radius monotonic", SlotConfigParsing.CasAccuracyRadius(0.25f) < SlotConfigParsing.CasAccuracyRadius(0.75f));
        }

        private static void Clamps()
        {
            Check("missions negative -> -1 (infinite)", SlotConfigParsing.ClampMissions(-5) == -1 && SlotConfigParsing.ClampMissions(-1) == -1);
            Check("missions zero stays 0", SlotConfigParsing.ClampMissions(0) == 0);
            Check("missions capped", SlotConfigParsing.ClampMissions(5000) == 999);
            Check("missions huge -> max", SlotConfigParsing.ClampMissions(5000) == SlotConfigParsing.MaxMissions);
            Check("missions normal kept", SlotConfigParsing.ClampMissions(7) == 7);

            Check("rounds negative/zero -> -1 (vanilla)", SlotConfigParsing.ClampRoundsPerCall(-1) == -1 && SlotConfigParsing.ClampRoundsPerCall(0) == -1);
            Check("rounds positive kept", SlotConfigParsing.ClampRoundsPerCall(12) == 12 && SlotConfigParsing.ClampRoundsPerCall(5000) == 999);
            Check("scale 1 is vanilla", SlotConfigParsing.ClampScale(1f) == 1f);
            Check("scale 0.5 kept", Math.Abs(SlotConfigParsing.ClampScale(0.5f) - 0.5f) < 0.0001f);
            Check("scale -1 means off", SlotConfigParsing.ClampScale(-1f) == -1f && SlotConfigParsing.ClampScale(-9f) == -1f);
            Check("scale NaN -> vanilla", SlotConfigParsing.ClampScale(float.NaN) == 1f);
            Check("scale capped", SlotConfigParsing.ClampScale(1e6f) == 100f);
            Check("rounds huge -> max", SlotConfigParsing.ClampRoundsPerCall(100000) == SlotConfigParsing.MaxRoundsPerCall);

            Check("heading wraps 370 -> 10", Math.Abs(SlotConfigParsing.ClampHeading(370f) - 10f) < 0.001f);
            Check("heading wraps -90 -> 270", Math.Abs(SlotConfigParsing.ClampHeading(-90f) - 270f) < 0.001f);
            Check("heading NaN -> 90", Math.Abs(SlotConfigParsing.ClampHeading(float.NaN) - 90f) < 0.001f);
        }

        private static void RoundTrips()
        {
            foreach (SlotKind kind in Enum.GetValues(typeof(SlotKind)))
            {
                string name = SlotConfigParsing.ToConfigName(kind);
                SlotKind parsed;
                Check("round trip slot " + name, SlotConfigParsing.TryParseSlotKind(name, out parsed) && parsed == kind);
            }

            foreach (MunitionKind munition in Enum.GetValues(typeof(MunitionKind)))
            {
                string name = SlotConfigParsing.ToConfigName(munition);
                MunitionKind parsed;
                Check("round trip munition " + name, SlotConfigParsing.TryParseMunition(name, out parsed) && parsed == munition);
            }

            foreach (WeaponKind weapon in Enum.GetValues(typeof(WeaponKind)))
            {
                string name = SlotConfigParsing.ToConfigName(weapon);
                WeaponKind parsed;
                Check("round trip weapon " + name, SlotConfigParsing.TryParseWeapon(name, out parsed) && parsed == weapon);
            }

            foreach (FlyoverKind flyover in Enum.GetValues(typeof(FlyoverKind)))
            {
                string name = SlotConfigParsing.ToConfigName(flyover);
                FlyoverKind parsed;
                Check("round trip flyover " + name, SlotConfigParsing.TryParseFlyover(name, out parsed) && parsed == flyover);
            }

            foreach (AttackKind attack in Enum.GetValues(typeof(AttackKind)))
            {
                string name = SlotConfigParsing.ToConfigName(attack);
                AttackKind parsed;
                Check("round trip attack " + name, SlotConfigParsing.TryParseAttack(name, out parsed) && parsed == attack);
            }
        }

        private static void CfgSection()
        {
            // Realistic cfg text: BOM, CRLF, other mods' sections, whole-line and trailing comments,
            // quoted strings containing commas / Chinese / '#', MelonLoader's long float form.
            string text =
                "\uFEFF[CheatMode]\r\n" +
                "SelfInvincible = true\r\n" +
                "\r\n" +
                "[CustomFireSupport]\r\n" +
                "# whole line comment\r\n" +
                "Enabled = true\r\n" +
                "HideVanillaFireSupport = true # trailing comment\r\n" +
                "CasPrewarmKeys = \"auto\"\r\n" +
                "CasDeployDistanceMeters = 8000.0\r\n" +
                "Slot1_Type = \"ArtillerySmoke\"\r\n" +
                "Slot1_DisplayName = \"烟幕遮蔽 #1, 主要道路\"\r\n" +
                "Slot1_Missions = 4\r\n" +
                "Slot1_ImpactDelaySeconds = 30\r\n" +
                "Slot1_InterShotDelaySeconds = 0.699999988079071\r\n" +
                "Slot1_DispersionMeters = 40.0\r\n" +
                "Slot1_Enabled = true\r\n" +
                "Slot1_CasAttackTypes = \"Bombs,Rockets,GunRun,AirToGroundMissile\"\r\n" +
                "Slot2_Enabled = false\r\n" +
                "Slot2_Missions = 7\r\n" +
                "Slot2_Missions = 9\r\n" +          // duplicate: last wins
                "Slot3_DisplayName = \"\"\r\n" +
                "Slot4_Enabled = 1\r\n" +
                "Slot5_Enabled = off\r\n" +
                "\r\n" +
                "[WeatherControl]\r\n" +
                "Enabled = false\r\n";

            var values = CfgSectionParser.ParseSection(text, "CustomFireSupport");
            Check("cfg: section isolated", values.Count == 17 && !values.ContainsKey("SelfInvincible"));
            Check("cfg: value from our section", CfgSectionParser.GetString(values, "CasPrewarmKeys", "x") == "auto");
            Check("cfg: bool true", CfgSectionParser.GetBool(values, "Enabled", false));
            Check("cfg: bool with trailing comment", CfgSectionParser.GetBool(values, "HideVanillaFireSupport", false));
            Check("cfg: bool numeric 1", CfgSectionParser.GetBool(values, "Slot4_Enabled", false));
            Check("cfg: bool 'off'", !CfgSectionParser.GetBool(values, "Slot5_Enabled", true));
            Check("cfg: int", CfgSectionParser.GetInt(values, "Slot1_Missions", 0) == 4);
            Check("cfg: duplicate key last wins", CfgSectionParser.GetInt(values, "Slot2_Missions", 0) == 9);
            Check("cfg: int from float text", CfgSectionParser.GetInt(values, "Slot1_DispersionMeters", 0) == 40);
            Check("cfg: float integer text", Math.Abs(CfgSectionParser.GetFloat(values, "Slot1_ImpactDelaySeconds", -1f) - 30f) < 0.001f);
            Check("cfg: float long form", Math.Abs(CfgSectionParser.GetFloat(values, "Slot1_InterShotDelaySeconds", -1f) - 0.7f) < 0.001f);
            Check("cfg: float 8000", Math.Abs(CfgSectionParser.GetFloat(values, "CasDeployDistanceMeters", -1f) - 8000f) < 0.001f);
            Check("cfg: quoted string with comma", CfgSectionParser.GetString(values, "Slot1_CasAttackTypes", "x") == "Bombs,Rockets,GunRun,AirToGroundMissile");
            Check("cfg: '#' inside quotes preserved", CfgSectionParser.GetString(values, "Slot1_DisplayName", "x") == "烟幕遮蔽 #1, 主要道路");
            Check("cfg: empty quoted string", CfgSectionParser.GetString(values, "Slot3_DisplayName", "x") == "");
            Check("cfg: missing key falls back", CfgSectionParser.GetInt(values, "Slot1_NotAKey", 42) == 42);
            Check("cfg: missing bool falls back", !CfgSectionParser.GetBool(values, "Slot1_NotAKey", false));
            Check("cfg: missing float falls back", Math.Abs(CfgSectionParser.GetFloat(values, "Slot1_NotAKey", 5f) - 5f) < 0.001f);
            Check("cfg: garbage float falls back", Math.Abs(CfgSectionParser.GetFloat(values, "Slot1_Type", 5f) - 5f) < 0.001f);
            Check("cfg: unknown section returns empty", CfgSectionParser.ParseSection(text, "NoSuchSection").Count == 0);
            Check("cfg: null text safe", CfgSectionParser.ParseSection(null, "CustomFireSupport").Count == 0);
            Check("cfg: null section safe", CfgSectionParser.ParseSection(text, null).Count == 0);

            // The exact text this mod writes must round-trip through its own parser.
            var written = CfgSectionParser.ParseSection(
                "[CustomFireSupport]\nSlot1_ImpactDelaySeconds = 30\nSlot1_CasAttackTypes = \"Bombs,Rockets,GunRun,AirToGroundMissile\"\n",
                "CustomFireSupport");
            Check("cfg: hand-written values survive", CfgSectionParser.GetFloat(written, "Slot1_ImpactDelaySeconds", -1f) == 30f
                && CfgSectionParser.GetString(written, "Slot1_CasAttackTypes", "") == "Bombs,Rockets,GunRun,AirToGroundMissile");
        }

        private static void Catalog()
        {
            AirframeInfo info = CasAirframeCatalog.Match("F4_USAF");
            Check("catalog F4_USAF -> Nato jet single pass",
                info != null && info.Side == AirframeSide.Nato && info.Flyover == FlyoverKind.SinglePass);

            info = CasAirframeCatalog.Match("A10");
            Check("catalog A10 -> Nato linger", info != null && info.Side == AirframeSide.Nato && info.Flyover == FlyoverKind.Linger);

            info = CasAirframeCatalog.Match("MiG23BN");
            Check("catalog MiG23BN -> Pact jet single pass",
                info != null && info.Side == AirframeSide.Pact && info.Flyover == FlyoverKind.SinglePass);

            info = CasAirframeCatalog.Match("MiG21");
            Check("catalog MiG21 -> Pact jet single pass",
                info != null && info.Side == AirframeSide.Pact && info.Flyover == FlyoverKind.SinglePass);

            info = CasAirframeCatalog.Match("SU22");
            Check("catalog SU22 -> Pact jet single pass",
                info != null && info.Side == AirframeSide.Pact && info.Flyover == FlyoverKind.SinglePass);

            Check("catalog F104 -> Nato", CasAirframeCatalog.GuessSide("F104") == AirframeSide.Nato);
            Check("catalog SU22 -> Pact", CasAirframeCatalog.GuessSide("SU22") == AirframeSide.Pact);
            Check("catalog MiG23BN -> Pact", CasAirframeCatalog.GuessSide("MiG23BN") == AirframeSide.Pact);
            Check("catalog unknown -> Unknown", CasAirframeCatalog.GuessSide("M109 howitzer") == AirframeSide.Unknown);
            Check("catalog empty/null safe", CasAirframeCatalog.Match("") == null && CasAirframeCatalog.Match(null) == null);
            Check("catalog MiG23BN attacks hint", CasAirframeCatalog.Match("MiG23BN").TypicalAttacks.Length > 0);
        }

        /// <summary>
        /// The two designated strafing aircraft: gun-run slots are pinned to them and rocket slots skip
        /// them, so the name test has to catch every spelling the donors appear under.
        /// </summary>
        private static void GunRunAirframes()
        {
            Check("gun airframe A10", CasAirframeCatalog.IsGunRunAirframe("A10"));
            Check("gun airframe A-10A", CasAirframeCatalog.IsGunRunAirframe("A-10A"));
            Check("gun airframe a10 (case)", CasAirframeCatalog.IsGunRunAirframe("a10"));
            Check("gun airframe MiG23BN", CasAirframeCatalog.IsGunRunAirframe("MiG23BN"));
            Check("gun airframe MiG-23BN", CasAirframeCatalog.IsGunRunAirframe("MiG-23BN"));
            Check("gun airframe MiG-23BN + loadout suffix", CasAirframeCatalog.IsGunRunAirframe("MiG-23BN Rockets"));

            Check("rocket airframe F104 is not a gun airframe", !CasAirframeCatalog.IsGunRunAirframe("F104"));
            Check("rocket airframe F4_USAF is not a gun airframe", !CasAirframeCatalog.IsGunRunAirframe("F4_USAF"));
            Check("rocket airframe MiG21 is not a gun airframe", !CasAirframeCatalog.IsGunRunAirframe("MiG21"));
            Check("rocket airframe SU22 is not a gun airframe", !CasAirframeCatalog.IsGunRunAirframe("SU22"));
            Check("gun airframe empty/null safe",
                !CasAirframeCatalog.IsGunRunAirframe("") && !CasAirframeCatalog.IsGunRunAirframe(null));
        }

        /// <summary>
        /// The air-to-ground missile payload: the AttackTypes entry "AirToGroundMissile" is
        /// pinned to the A-10 (Blue, AGM-65 model) and the MiG-23BN (Red), and each side's missile
        /// profile names the composed prefab the bundle ships.
        /// </summary>
        private static void MissilePayloads()
        {
            // The designated missile aircraft per side.
            Check("missile airframe A10 (Blue)", CasAirframeCatalog.IsMissileAirframe("A10", AirframeSide.Nato));
            Check("missile airframe A-10A (Blue)", CasAirframeCatalog.IsMissileAirframe("A-10A", AirframeSide.Nato));
            Check("missile airframe MiG23BN (Red)", CasAirframeCatalog.IsMissileAirframe("MiG23BN", AirframeSide.Pact));
            Check("missile airframe MiG-23BN (Red)", CasAirframeCatalog.IsMissileAirframe("MiG-23BN", AirframeSide.Pact));

            // A Blue slot must not be pinned to a Soviet jet and vice versa.
            Check("missile airframe MiG23BN is not Blue", !CasAirframeCatalog.IsMissileAirframe("MiG23BN", AirframeSide.Nato));
            Check("missile airframe A10 is not Red", !CasAirframeCatalog.IsMissileAirframe("A10", AirframeSide.Pact));
            Check("missile airframe F4 is not designated", !CasAirframeCatalog.IsMissileAirframe("F4_USAF", AirframeSide.Nato));
            Check("missile airframe MiG21 is not designated", !CasAirframeCatalog.IsMissileAirframe("MiG21", AirframeSide.Pact));
            Check("missile airframe empty/null safe",
                !CasAirframeCatalog.IsMissileAirframe("", AirframeSide.Nato) &&
                !CasAirframeCatalog.IsMissileAirframe(null, AirframeSide.Pact));

            // The two composed prefabs, one per side, and their airframe pairing in the log.
            CasAirframeCatalog.MissileProfile blue = CasAirframeCatalog.MissileFor(AirframeSide.Nato);
            CasAirframeCatalog.MissileProfile red = CasAirframeCatalog.MissileFor(AirframeSide.Pact);
            Check("blue missile is the AGM-65 on the A-10",
                blue != null && blue.PrefabHint == "agm-65" && blue.Airframe == "A-10");
            Check("red missile is the Kh-25 on the MiG-23BN",
                red != null && red.PrefabHint == "kh-25" && red.Airframe == "MiG-23BN");
            Check("missile profiles carry a sane payload",
                blue.Munitions > 0 && red.Munitions > 0 && blue.DeviationDegrees > 0f && red.DeviationDegrees > 0f);
            // The payload is a single-shot guaranteed hit: one missile, and CasAccuracy must not be able to
            // put the round anywhere but the target's own centre.
            Check("missile is a single-shot payload", blue.Munitions == 1 && red.Munitions == 1);
            Check("missile always hits the centre", blue.GuaranteedHit && red.GuaranteedHit);
            // Speed: the gravity bomb the data comes from launches at 0 m/s and has a blunt body's drag, so
            // the missile needs its own motor boost and a slimmer coefficient to be fast enough.
            Check("missile has its own motor boost", blue.BoostVelocityMeters >= 150f && red.BoostVelocityMeters >= 150f);
            // Speed: the player asked for Mach 1.2 for BOTH missiles, and the mod's flight resolver pins the
            // round to it for the whole flight. GHPC's AmmoType only knows m/s, so the Mach number lives in
            // the catalog and the conversion is asserted here (1.2 * 340.29 = 408.348 m/s).
            Check("missile cruise speed is Mach 1.2",
                Math.Abs(blue.CruiseSpeedMeters - 408.3f) < 0.5f && Math.Abs(red.CruiseSpeedMeters - 408.3f) < 0.5f);
            Check("both missiles fly at the same speed",
                Math.Abs(blue.CruiseSpeedMeters - red.CruiseSpeedMeters) < 0.001f &&
                Math.Abs(CasAirframeCatalog.CruiseSpeedMetersPerSecond - 408.3f) < 0.5f);
            Check("Mach conversion round-trips",
                Math.Abs(CasAirframeCatalog.MachOf(CasAirframeCatalog.CruiseSpeedMetersPerSecond) - 1.2f) < 0.001f &&
                Math.Abs(CasAirframeCatalog.MachOf(CasAirframeCatalog.SpeedOfSoundSeaLevelMetersPerSecond) - 1f) < 0.001f);
            Check("missile holds a cruise speed", blue.CruiseSpeedMeters >= 300f && red.CruiseSpeedMeters >= 300f);
            Check("missile drag is scaled down",
                blue.DragScale > 0f && blue.DragScale < 0.6f && red.DragScale > 0f && red.DragScale < 0.6f);
            Check("unknown side falls back to the Blue missile", CasAirframeCatalog.MissileFor(AirframeSide.Unknown) == blue);

            // The config value that selects it, including the aliases a player is likely to type.
            AttackKind attack;
            Check("attack AirToGroundMissile parses", SlotConfigParsing.TryParseAttack("AirToGroundMissile", out attack) &&
                                                       attack == AttackKind.AirToGroundMissile);
            Check("attack AGM parses", SlotConfigParsing.TryParseAttack("AGM", out attack) &&
                                       attack == AttackKind.AirToGroundMissile);
            Check("attack ATGM parses", SlotConfigParsing.TryParseAttack("atgm", out attack) &&
                                        attack == AttackKind.AirToGroundMissile);
            Check("attack missile parses", SlotConfigParsing.TryParseAttack("Missile", out attack) &&
                                           attack == AttackKind.AirToGroundMissile);
            Check("attack AGM round-trips to the config name",
                SlotConfigParsing.ToConfigName(AttackKind.AirToGroundMissile) == "AirToGroundMissile");

            // The missile borrows a BOMB's data, and each faction must get its own bomb: the airframe
            // patterns cannot answer this (a bomb is called "MK82" or "FAB250"), so the bomb hints do.
            Check("bomb side MK82 is Blue", CasAirframeCatalog.GuessBombSide("MK82") == AirframeSide.Nato);
            Check("bomb side Mk-82 hardpoint is Blue",
                CasAirframeCatalog.GuessBombSide("Mk-82 single on hardpoint") == AirframeSide.Nato);
            Check("bomb side BRU-42 (3x mk82) is Blue",
                CasAirframeCatalog.GuessBombSide("BRU-42 (3x mk82 high drag)") == AirframeSide.Nato);
            Check("bomb side FAB250 is Red", CasAirframeCatalog.GuessBombSide("FAB250") == AirframeSide.Pact);
            Check("bomb side FAB-250 single is Red",
                CasAirframeCatalog.GuessBombSide("FAB-250 single") == AirframeSide.Pact);
            Check("bomb side MBD-3 (FAB250) is Red",
                CasAirframeCatalog.GuessBombSide("MBD-3 (FAB250)") == AirframeSide.Pact);
            Check("bomb side unknown name is Unknown",
                CasAirframeCatalog.GuessBombSide("something else") == AirframeSide.Unknown);
            Check("bomb side empty/null safe",
                CasAirframeCatalog.GuessBombSide("") == AirframeSide.Unknown &&
                CasAirframeCatalog.GuessBombSide(null) == AirframeSide.Unknown);
        }

        /// <summary>
        /// The layout the mod ships with: 1 artillery, 2 smoke, 3 illumination, 4/5/6 CAS (gun run /
        /// rockets / bombs), all six switched on. DefaultConfigSection is the single definition of it -
        /// the generated default section and the reader's fallback for a missing key both come from
        /// here - so asserting it once covers both paths.
        /// </summary>
        private static void ShippedLayout()
        {
            Check("layout slot 1 is artillery", DefaultConfigSection.TypeFor(1) == "Artillery");
            Check("layout slot 2 is smoke", DefaultConfigSection.TypeFor(2) == "ArtillerySmoke");
            Check("layout slot 3 is illumination", DefaultConfigSection.TypeFor(3) == "ArtilleryIllumination");
            Check("layout slot 4/5/6 are CAS", DefaultConfigSection.TypeFor(4) == "CASSupport" &&
                                              DefaultConfigSection.TypeFor(5) == "CASSupport" &&
                                              DefaultConfigSection.TypeFor(6) == "CASSupport");

            Check("layout slot 1 keeps the plain name", DefaultConfigSection.NameFor(1) == "Custom Artillery");
            Check("layout slot 3 name is illumination", DefaultConfigSection.NameFor(3) == "Custom Illumination");
            Check("layout CAS names are distinct",
                DefaultConfigSection.NameFor(4) == "CAS Gun Run" &&
                DefaultConfigSection.NameFor(5) == "CAS Rockets" &&
                DefaultConfigSection.NameFor(6) == "CAS Bombs");

            Check("layout CAS attacks are gun run / rockets / bombs",
                DefaultConfigSection.AttacksFor(4) == "GunRun" &&
                DefaultConfigSection.AttacksFor(5) == "Rockets" &&
                DefaultConfigSection.AttacksFor(6) == "Bombs");
            Check("layout artillery slots do not filter attacks",
                DefaultConfigSection.AttacksFor(1) == "Any" &&
                DefaultConfigSection.AttacksFor(2) == "Any" &&
                DefaultConfigSection.AttacksFor(3) == "Any");

            // The generated section must carry that layout, with every slot switched on - and the attack
            // defaults must still be values the parser accepts (a typo here would ship a broken preset).
            string section = DefaultConfigSection.Build();
            Check("generated section has all six slots on",
                section.Contains("Slot1_Enabled = true") && section.Contains("Slot2_Enabled = true") &&
                section.Contains("Slot3_Enabled = true") && section.Contains("Slot4_Enabled = true") &&
                section.Contains("Slot5_Enabled = true") && section.Contains("Slot6_Enabled = true"));
            Check("generated section carries the CAS attack defaults",
                section.Contains("Slot4_CasAttackTypes = \"GunRun\"") &&
                section.Contains("Slot5_CasAttackTypes = \"Rockets\"") &&
                section.Contains("Slot6_CasAttackTypes = \"Bombs\""));
            for (int n = 1; n <= 6; n++)
            {
                AttackKind parsed;
                Check("generated slot " + n + " attack default parses",
                    SlotConfigParsing.TryParseAttack(DefaultConfigSection.AttacksFor(n), out parsed) ||
                    DefaultConfigSection.AttacksFor(n) == "Any");
            }
        }

        /// <summary>
        /// Loadout / airframe side pairing: the donor scan pairs every airframe with every loadout whose
        /// hardpoint count fits, so a US jet can end up holding a Soviet rocket pod - which is what made
        /// the rockets render as white blocks. The draw pool keeps only same-side (or unknown) pairs.
        /// </summary>
        private static void LoadoutSides()
        {
            Check("loadout F-104G Rockets on F4_LW", CasAirframeCatalog.LoadoutFitsSide("F4_LW", "F-104G Rockets"));
            Check("loadout A-10 Mk82 focus on A10", CasAirframeCatalog.LoadoutFitsSide("A10", "A-10 Mk82 focus"));
            Check("loadout F4 2x triple Mk82 on F104", CasAirframeCatalog.LoadoutFitsSide("F104", "F4 2x triple Mk82"));
            Check("loadout MiG-21 rockets only on MiG21", CasAirframeCatalog.LoadoutFitsSide("MiG21", "MiG-21 rockets only"));
            Check("loadout SU-22 rockets on SU22", CasAirframeCatalog.LoadoutFitsSide("SU22", "SU-22 rockets Multiple"));
            Check("loadout MiG-23BN rockets on MiG23BN", CasAirframeCatalog.LoadoutFitsSide("MiG23BN", "MiG-23BN rockets only"));

            Check("soviet pod on a US jet rejected", !CasAirframeCatalog.LoadoutFitsSide("F4_LW", "MiG-21 rockets only"));
            Check("soviet pod on a US jet rejected (SU-22)", !CasAirframeCatalog.LoadoutFitsSide("F4_USAF", "SU-22 rockets Multiple"));
            Check("US pod on a Soviet jet rejected", !CasAirframeCatalog.LoadoutFitsSide("MiG21", "A-10 Mk82 focus"));
            Check("US pod on a Soviet jet rejected (F-104)", !CasAirframeCatalog.LoadoutFitsSide("SU22", "F-104G MK82s only"));

            Check("unknown loadout name accepted", CasAirframeCatalog.LoadoutFitsSide("F4_USAF", "(unnamed loadout)"));
            Check("unknown airframe name accepted", CasAirframeCatalog.LoadoutFitsSide("mystery plane", "F-104G Rockets"));
            Check("empty names accepted", CasAirframeCatalog.LoadoutFitsSide("", "") &&
                                          CasAirframeCatalog.LoadoutFitsSide(null, null));

            // Faction -> airframe side, used by the draw pool to keep a Pact task flying Pact aircraft
            // and a US task flying American ones. Faction.Blue = 3, Faction.Red = 1 (GHPC), and the int
            // overload keeps this file free of the game's Faction enum.
            Check("faction Blue -> Nato", CasAirframeCatalog.SideOfFactionValue(3) == AirframeSide.Nato);
            Check("faction Red -> Pact", CasAirframeCatalog.SideOfFactionValue(1) == AirframeSide.Pact);
            Check("faction Neutral -> Pact (never silently Nato)",
                CasAirframeCatalog.SideOfFactionValue(0) == AirframeSide.Pact);
            Check("faction Green -> Pact", CasAirframeCatalog.SideOfFactionValue(2) == AirframeSide.Pact);

            // The real game bindings (read from the exported Unity project) must land on the side the
            // slot's faction expects, so a Soviet task never draws an A-10 and vice versa. This is the
            // regression guard for "NATO aircraft flying for the Pact" reports.
            Check("US jet is Nato-side for a Blue task",
                CasAirframeCatalog.GuessSide("F4_USAF") == CasAirframeCatalog.SideOfFactionValue(3));
            Check("A-10 is Nato-side for a Blue task",
                CasAirframeCatalog.GuessSide("A10") == CasAirframeCatalog.SideOfFactionValue(3));
            Check("F-104 is Nato-side for a Blue task",
                CasAirframeCatalog.GuessSide("F104") == CasAirframeCatalog.SideOfFactionValue(3));
            Check("MiG-21 is Pact-side for a Red task",
                CasAirframeCatalog.GuessSide("MiG21") == CasAirframeCatalog.SideOfFactionValue(1));
            Check("MiG-23BN is Pact-side for a Red task",
                CasAirframeCatalog.GuessSide("MiG23BN") == CasAirframeCatalog.SideOfFactionValue(1));
            Check("SU-22 is Pact-side for a Red task",
                CasAirframeCatalog.GuessSide("SU22") == CasAirframeCatalog.SideOfFactionValue(1));
            Check("MiG-17 is Pact-side for a Red task",
                CasAirframeCatalog.GuessSide("MiG17") == CasAirframeCatalog.SideOfFactionValue(1));

            // And the reverse must NOT hold: an enemy aircraft is never "on my side".
            Check("A-10 is not Pact-side",
                CasAirframeCatalog.GuessSide("A10") != CasAirframeCatalog.SideOfFactionValue(1));
            Check("MiG-21 is not Nato-side",
                CasAirframeCatalog.GuessSide("MiG21") != CasAirframeCatalog.SideOfFactionValue(3));

            // Approved airframe+loadout pairs. 6 of the 8 CAS airframes default to a bomb loadout, so
            // without these the rocket pool holds only the two Soviet defaults (MiG-17/MiG-21) and a
            // US task's rocket slot has nothing of its own. Each pair below ships in the mod's own
            // cas_assets bundle and is a valid fit for its airframe.
            Check("approved: F104 + F-104G Rockets", CasAirframeCatalog.IsApprovedPair("F104", "F-104G Rockets"));
            Check("approved: MiG23BN + MiG-23BN rockets only",
                CasAirframeCatalog.IsApprovedPair("MiG23BN", "MiG-23BN rockets only"));
            Check("approved: SU22 + SU-22 rockets Multiple",
                CasAirframeCatalog.IsApprovedPair("SU22", "SU-22 rockets Multiple"));
            Check("approved pairs are case-insensitive",
                CasAirframeCatalog.IsApprovedPair("f104", "f-104g rockets"));
            Check("approved pairs tolerate surrounding whitespace",
                CasAirframeCatalog.IsApprovedPair(" F104 ", " F-104G Rockets "));

            // The DEFAULT bomb pairs must NOT be in the approved list - they are reached through `native`.
            Check("default pair is not an approved override",
                !CasAirframeCatalog.IsApprovedPair("F104", "F-104G MK82s only"));
            // A pair that does not exist must never match, so a typo in the table cannot admit anything.
            Check("nonexistent pair is not approved",
                !CasAirframeCatalog.IsApprovedPair("F104", "F-104G Sidewinders"));
            Check("empty / null pair is not approved",
                !CasAirframeCatalog.IsApprovedPair("", "") &&
                !CasAirframeCatalog.IsApprovedPair(null, null) &&
                !CasAirframeCatalog.IsApprovedPair("F104", null));

            // Substring safety: an unrelated loadout whose name merely CONTAINS an approved name must not
            // be admitted (the match is exact, not a substring test).
            Check("substring is not enough (suffix)",
                !CasAirframeCatalog.IsApprovedPair("F104", "F-104G Rockets extra"));
            Check("substring is not enough (prefix)",
                !CasAirframeCatalog.IsApprovedPair("F104", "Old F-104G Rockets"));
            Check("wrong airframe for an approved loadout is rejected",
                !CasAirframeCatalog.IsApprovedPair("F4_USAF", "F-104G Rockets"));
            Check("approved table is populated", CasAirframeCatalog.ApprovedPairCount == 3);

            // Each approved pair must be side-consistent (airframe and loadout on the same side), which
            // is the other gate the draw pool applies before admitting it.
            Check("F104 rockets pair is side-consistent",
                CasAirframeCatalog.LoadoutFitsSide("F104", "F-104G Rockets"));
            Check("MiG23BN rockets pair is side-consistent",
                CasAirframeCatalog.LoadoutFitsSide("MiG23BN", "MiG-23BN rockets only"));
            Check("SU22 rockets pair is side-consistent",
                CasAirframeCatalog.LoadoutFitsSide("SU22", "SU-22 rockets Multiple"));

            // Default (shipped) airframe+loadout bindings, read from each prefab's
            // CASHardpointManager.Loadout reference. These define "the airframe flies its OWN loadout",
            // which is what keeps another aircraft's pylon out of the top-tier draw.
            Check("default: A10 + A-10 Mk82 focus",
                CasAirframeCatalog.IsDefaultLoadoutPair("A10", "A-10 Mk82 focus"));
            Check("default: F104 + F-104G MK82s only",
                CasAirframeCatalog.IsDefaultLoadoutPair("F104", "F-104G MK82s only"));
            Check("default: F4_USAF + F4 2x triple Mk82",
                CasAirframeCatalog.IsDefaultLoadoutPair("F4_USAF", "F4 2x triple Mk82"));
            Check("default: F4_LW + F4 2x triple Mk82",
                CasAirframeCatalog.IsDefaultLoadoutPair("F4_LW", "F4 2x triple Mk82"));
            Check("default: MiG17 + MiG-17 rockets only",
                CasAirframeCatalog.IsDefaultLoadoutPair("MiG17", "MiG-17 rockets only"));
            Check("default: MiG21 + MiG-21 rockets only",
                CasAirframeCatalog.IsDefaultLoadoutPair("MiG21", "MiG-21 rockets only"));
            Check("default: MiG23BN + MiG-23BN FAB-250s only",
                CasAirframeCatalog.IsDefaultLoadoutPair("MiG23BN", "MiG-23BN FAB-250s only"));
            // The game genuinely binds SU22 to a MiG-23BN loadout; that is the shipped pair, not a bug.
            Check("default: SU22 + MiG-23BN FAB-250s only (the game's own binding)",
                CasAirframeCatalog.IsDefaultLoadoutPair("SU22", "MiG-23BN FAB-250s only"));
            Check("default pairs are case-insensitive",
                CasAirframeCatalog.IsDefaultLoadoutPair("f104", "f-104g mk82s only"));

            // THE REGRESSION GUARD: cross-pairs (another aircraft's loadout on this airframe) must NOT be
            // treated as the airframe's own. Before this guard the donor scan put pairs like these into
            // the top-tier draw, because every cross-pair "carries the right weapon".
            Check("cross-pair rejected: MiG17 + MiG-21 rockets only",
                !CasAirframeCatalog.IsDefaultLoadoutPair("MiG17", "MiG-21 rockets only"));
            Check("cross-pair rejected: MiG17 + SU-22 rockets Multiple",
                !CasAirframeCatalog.IsDefaultLoadoutPair("MiG17", "SU-22 rockets Multiple"));
            Check("cross-pair rejected: MiG21 + MiG-17 rockets only",
                !CasAirframeCatalog.IsDefaultLoadoutPair("MiG21", "MiG-17 rockets only"));
            Check("cross-pair rejected: MiG23BN + SU-22 rockets Multiple",
                !CasAirframeCatalog.IsDefaultLoadoutPair("MiG23BN", "SU-22 rockets Multiple"));
            Check("cross-pair rejected: F4_LW + F-104G Rockets",
                !CasAirframeCatalog.IsDefaultLoadoutPair("F4_LW", "F-104G Rockets"));
            Check("cross-pair rejected: F104 + A-10 Mk82 focus",
                !CasAirframeCatalog.IsDefaultLoadoutPair("F104", "A-10 Mk82 focus"));
            Check("cross-pair rejected: SU22 + SU-22 FAB-250s only (not SU22's binding)", 
                !CasAirframeCatalog.IsDefaultLoadoutPair("SU22", "SU-22 FAB-250s only"));

            // The three approved rocket loadouts are NOT defaults, but they ARE approved - so they still
            // reach the top tier through the approval list rather than through the default table.
            Check("approved rockets pair is not a default pair (uses the approval list)",
                !CasAirframeCatalog.IsDefaultLoadoutPair("F104", "F-104G Rockets") &&
                CasAirframeCatalog.IsApprovedPair("F104", "F-104G Rockets"));
            Check("approved MiG23BN rockets pair is not a default pair",
                !CasAirframeCatalog.IsDefaultLoadoutPair("MiG23BN", "MiG-23BN rockets only") &&
                CasAirframeCatalog.IsApprovedPair("MiG23BN", "MiG-23BN rockets only"));
            Check("approved SU22 rockets pair is not a default pair",
                !CasAirframeCatalog.IsDefaultLoadoutPair("SU22", "SU-22 rockets Multiple") &&
                CasAirframeCatalog.IsApprovedPair("SU22", "SU-22 rockets Multiple"));

            // Unknown / empty input never matches either table.
            Check("unknown default pair rejected",
                !CasAirframeCatalog.IsDefaultLoadoutPair("M109 howitzer", "some loadout"));
            Check("empty default pair rejected",
                !CasAirframeCatalog.IsDefaultLoadoutPair("", "") &&
                !CasAirframeCatalog.IsDefaultLoadoutPair(null, null));
        }

        private static void Check(string name, bool condition)
        {
            if (condition)
            {
                _passed++;
                return;
            }
            _failures.Add(name);
        }
    }
}
