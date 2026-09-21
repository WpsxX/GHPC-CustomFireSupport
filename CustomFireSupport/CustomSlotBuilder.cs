using System;
using System.Collections.Generic;
using System.Text;
using GHPC;
using GHPC.Vehicle;
using GHPC.Weaponry.Artillery;
using GHPC.Weaponry.CAS;
using GHPC.Weapons.Artillery;
using HarmonyLib;
using UnityEngine;

namespace CustomFireSupport
{
    /// <summary>A built artillery slot: the live battery plus the config that produced it.</summary>
    internal sealed class ArtillerySlot
    {
        internal SlotConfig Config;
        internal ArtilleryBattery Battery;
        internal IndirectFireWeaponType WeaponType;

        /// <summary>Name of the projectile prefab this slot fires (null for codex shells); used by the diagnostics.</summary>
        internal string TemplatePrefabName;

        /// <summary>
        /// True when this slot's whole volley is fired on the frame of the call
        /// (<c>ImpactDelaySeconds &lt;= 0</c>). One definition of "instant", used by both the builder and
        /// the <c>SendFireMission</c> postfix that actually fires the volley.
        /// </summary>
        internal bool InstantVolley;

        /// <summary>Baseline values copied from the vanilla battery the shell came from (for logging).</summary>
        internal int VanillaShots;
        internal float VanillaImpactDelay;
        internal float VanillaInterShot;
        internal float VanillaDispersion;
        internal float VanillaCooldown;
    }

    /// <summary>A built CAS slot: the live airframe unit plus the config that produced it.</summary>
    internal sealed class CasSlot
    {
        internal SlotConfig Config;
        internal CasAirframeUnit Airframe;

        /// <summary>The airframe catalogue this slot was built from, kept so a later call can re-roll it.</summary>
        internal List<CasTemplate> Templates;
    }

    /// <summary>
    /// Marks an aircraft spawned from one of this mod's custom CAS sorties. The precision patch uses it
    /// to tell our planes apart from vanilla / enemy CAS: only our sorties get the zero-spread rockets
    /// and gun runs, so an enemy sortie keeps the game's own accuracy. Added by the
    /// CASController.SetLoadout prefix when the loadout being applied is one this mod created.
    /// </summary>
    internal sealed class CustomCasMarker : MonoBehaviour
    {
    }

    /// <summary>
    /// Turns a SlotConfig into live GHPC objects.
    ///
    /// Spawn height / angle / heading / shell type / dispersion / cooldown / rounds / interval are NOT
    /// configured any more: they are copied from the vanilla battery the shell template came from (or
    /// from GHPC's own code defaults when only an ammo asset was available), and the slot's config only
    /// supplies scale factors (1.0 = vanilla, 0.5 = half, &lt;= 0 = none/instant) plus the call count.
    /// Everything the game keeps in private serialized fields is written through Harmony's FieldRef,
    /// because the game runs the original (non-publicized) assembly where those members are private.
    /// </summary>
    internal static class CustomSlotBuilder
    {
        /// <summary>Displayed call count for a slot with "Missions = -1" (infinite).</summary>
        internal const int InfiniteMissionsDisplay = 99;

        private static readonly AccessTools.FieldRef<ArtilleryBattery, int> BatteryMissionsRef =
            AccessTools.FieldRefAccess<ArtilleryBattery, int>("_missionsAvailable");
        private static readonly AccessTools.FieldRef<ArtilleryBattery, float> BatteryImpactDelayRef =
            AccessTools.FieldRefAccess<ArtilleryBattery, float>("_onCallImpactDelay");
        private static readonly AccessTools.FieldRef<ArtilleryBattery, float> BatteryDispersionRef =
            AccessTools.FieldRefAccess<ArtilleryBattery, float>("_randomDispersionRadiusMeters");

        // Read-only access to the template battery's own (vanilla) values.
        private static readonly AccessTools.FieldRef<ArtilleryBattery, int> SourceShotsRef =
            AccessTools.FieldRefAccess<ArtilleryBattery, int>("_shots");
        private static readonly AccessTools.FieldRef<ArtilleryBattery, float> SourceInterShotRef =
            AccessTools.FieldRefAccess<ArtilleryBattery, float>("_interShotDelaySeconds");
        private static readonly AccessTools.FieldRef<ArtilleryBattery, float> SourceDispersionRef =
            AccessTools.FieldRefAccess<ArtilleryBattery, float>("_randomDispersionRadiusMeters");

        private static readonly AccessTools.FieldRef<CasAirframeUnit, int> CasMissionsRef =
            AccessTools.FieldRefAccess<CasAirframeUnit, int>("_missionsAvailable");

        /// <summary>Loadout assets created for this mission's CAS sorties (see CustomCasMarker).</summary>
        private static readonly HashSet<CASLoadoutScriptable> _ourLoadouts = new HashSet<CASLoadoutScriptable>();

        /// <summary>
        /// Last airframe name flown by each slot, so a bomb / rocket slot's per-call draw never repeats
        /// the same aircraft twice in a row (a slot with only one possible aircraft still reuses it).
        /// </summary>
        private static readonly Dictionary<int, string> _lastPickedAirframe = new Dictionary<int, string>();

        internal static bool IsOurLoadout(CASLoadoutScriptable loadout)
        {
            return loadout != null && _ourLoadouts.Contains(loadout);
        }

        /// <summary>Drops the previous mission's sortie loadouts; called when a mission is prepared.</summary>
        internal static void ResetLoadouts()
        {
            _ourLoadouts.Clear();
        }

        /// <summary>
        /// 1.0 = vanilla, 0.5 = half, &lt;= 0 = zero (instant / no cooldown / no dispersion). The rule itself
        /// lives in <see cref="ArtilleryTiming.Scale"/> so the timing maths in this file and the headless
        /// unit tests can never disagree about it.
        /// </summary>
        internal static float ScaleValue(float vanilla, float scale)
        {
            return ArtilleryTiming.Scale(vanilla, scale);
        }

        internal static int MissionsToStore(SlotConfig config)
        {
            return config.Missions < 0 ? InfiniteMissionsDisplay : config.Missions;
        }

        internal static ArtillerySlot BuildArtillery(SlotConfig config, Dictionary<MunitionKind, List<AmmoTemplate>> ammo, Faction playerFaction, out string failure)
        {
            failure = null;

            List<AmmoTemplate> candidates;
            if (ammo == null || !ammo.TryGetValue(config.Munition, out candidates) || candidates.Count == 0)
            {
                failure = "no " + config.Munition + " shell template is available in this mission";
                return null;
            }

            AmmoTemplate chosen = null;
            if (string.IsNullOrEmpty(config.AmmoNameFilter))
            {
                chosen = candidates[0];
            }
            else
            {
                string filter = config.AmmoNameFilter.ToLowerInvariant();
                for (int i = 0; i < candidates.Count; i++)
                {
                    if (candidates[i].AmmoName.ToLowerInvariant().Contains(filter))
                    {
                        chosen = candidates[i];
                        break;
                    }
                }

                if (chosen == null)
                {
                    failure = "no " + config.Munition + " shell matches AmmoName '" + config.AmmoNameFilter + "'; candidates: " + DescribeCandidates(candidates);
                    return null;
                }
            }

            // ---- cluster munitions: the artillery's ANTI-ARMOUR shell ----
            //
            // The player asked for the anti-armour fire mission to be a cargo round that opens over the
            // target and showers HEDP submunitions (M864 for Blue, 3-O-23 for Red) instead of a single
            // HEAT shell - see ClusterMunitionFactory / ClusterMunitionPatches. The template still
            // decides which battery the slot copies its geometry and timing from, and its shell is the
            // cargo round's visual donor; only the ammo that goes into the battery changes. When the
            // replacement cannot be assembled the vanilla anti-armour shell is kept, so the slot never
            // ends up unable to fire.
            BatteryMunitionsChoice choice = chosen.Choice;
            if (config.Munition == MunitionKind.AntiArmor)
            {
                string cluster;
                BatteryMunitionsChoice replacement = ClusterMunitionFactory.BuildChoice(playerFaction, chosen, out cluster);
                if (replacement != null)
                {
                    choice = replacement;
                    Log.Info("slot " + config.Index + ": anti-armour artillery is a CLUSTER munition - " + cluster +
                             " (was '" + chosen.AmmoName + "').");
                }
                else
                {
                    Log.Warn("slot " + config.Index + ": no cluster munition could be assembled here; firing the " +
                             "ordinary anti-armour shell '" + chosen.AmmoName + "' instead.");
                }
            }

            // ---- baseline: the vanilla battery this shell came from ----
            //
            // A template harvested from a mission battery carries that battery, so the mod fires exactly
            // like the gun that owns the shell. Templates that came from loaded ammo assets or from the
            // bundle have no battery - then the last battery profile seen for the player's side is used
            // (an M109-style mission for Blue, a 2S3-style one for Red) before falling back to the game's
            // own code defaults.
            ArtilleryBattery source = chosen.SourceBattery;
            BatteryProfile profile = source == null ? FactionBatteryProfiles.Recall(playerFaction.ToString()) : null;
            int vanillaShots = source != null ? SourceShotsRef(source) : (profile != null ? profile.Shots : 12);
            float vanillaInterShot = source != null ? SourceInterShotRef(source) : (profile != null ? profile.InterShotSeconds : 0.7f);
            float vanillaDispersion = source != null ? SourceDispersionRef(source) : (profile != null ? profile.DispersionMeters : 100f);
            float vanillaImpact = source != null ? source.OnCallDelaySeconds : (profile != null ? profile.ImpactDelaySeconds : 120f);
            float vanillaCooldown = source != null ? source.CooldownAmount : (profile != null ? profile.CooldownSeconds : 20f);
            float spawnHeight = source != null ? source.SpawnHeight : (profile != null ? profile.SpawnHeightMeters : 300f);
            float spawnAngle = source != null ? source.SpawnAngle : (profile != null ? profile.SpawnAngleDegrees : 60f);
            float heading = source != null ? source.FromHeading : (profile != null ? profile.FromHeadingDegrees : 90f);
            if (profile != null)
            {
                Log.Info("slot " + config.Index + ": shell '" + chosen.AmmoName +
                         "' has no battery of its own - inheriting the last " +
                         playerFaction + " battery profile seen this session: " + profile.Describe() + ".");
            }

            // ---- slot config: scale factors ----
            int shots = config.RoundsPerCall > 0 ? config.RoundsPerCall : vanillaShots;
            float dispersion = ScaleValue(vanillaDispersion, config.DispersionMeters);

            // Timing: ImpactDelaySeconds scales the FIRST-ROUND delay only (>= 1 = vanilla x scale;
            // below 1 = the first round arrives on the call; <= 0 = the whole volley on the call's frame)
            // and InterShotDelaySeconds scales the round SPACING (1 = vanilla, 0.5 = half, 0 = one frame).
            //
            // The two keys used to be multiplied into each other: an impact scale below 1 also shortened
            // the interval by the same factor, with no way to switch that off - so a slot that set the
            // interval to the vanilla value still fired its rounds 0.3x as far apart, which in game looks
            // like the interval key being ignored. ArtilleryTiming has the full story; the verbose line
            // below says out loud what the effective spacing is, so it can be checked against the cfg.
            ArtilleryTiming.Timing timing = ArtilleryTiming.Resolve(
                vanillaImpact, vanillaInterShot, config.ImpactDelaySeconds, config.InterShotDelaySeconds);
            bool instant = timing.InstantVolley;
            float impactDelay = timing.ImpactDelaySeconds;
            float interShot = timing.InterShotSeconds;

            if (!instant && config.ImpactDelaySeconds > 0f && config.ImpactDelaySeconds < 1f)
            {
                Log.Verbose("slot " + config.Index + ": ImpactDelaySeconds=" +
                            config.ImpactDelaySeconds.ToString("0.##") +
                            " only cancels the first-round delay; the " + interShot.ToString("0.##") +
                            "s round spacing comes from InterShotDelaySeconds=" +
                            config.InterShotDelaySeconds.ToString("0.##") + " alone (an impact scale below 1 " +
                            "never shortens the interval).");
            }

            // The panel's cooldown bookkeeping must never reach 0 while the volley runs (CooldownManager
            // drops the entry and the button freezes on "Incoming"), so the stored cooldown is at least
            // the length of the strike. A scale of 0/-1 means "no cooldown": the stored value is only
            // used for that bookkeeping and is cleared again by the DoUpdate postfix.
            float volleyTime = timing.VolleySeconds(shots);
            float cooldown = Mathf.Max(ScaleValue(vanillaCooldown, config.CooldownSeconds), Mathf.Max(volleyTime, 1f));

            // Projectile-prefab munitions (the smoke / illumination templates) skip the ballistic spawn
            // in vanilla DoSingleShot: the prefab is only placed, with no velocity or rotation. Dropping
            // it from straight overhead makes it fall onto the called point.
            if (choice.Ammo == null && choice.DefaultProjectile != null)
            {
                spawnAngle = 90f;
                Log.Verbose("slot " + config.Index + ": '" + chosen.AmmoName + "' is a projectile prefab (no live-round data); " +
                            "dropping it straight down onto the called point.");
            }

            ArtilleryBattery battery = new ArtilleryBattery(
                shots,
                interShot,
                spawnHeight,
                spawnAngle,
                cooldown,
                new BatteryMunitionsChoice[] { choice },
                config.DisplayName);
            battery.FromHeading = heading;

            BatteryMissionsRef(battery) = MissionsToStore(config);
            BatteryImpactDelayRef(battery) = impactDelay;
            BatteryDispersionRef(battery) = dispersion;

            Log.Verbose("slot " + config.Index + ": battery '" + config.DisplayName + "' shell='" +
                        (choice == chosen.Choice ? chosen.AmmoName : "cluster munition (see above)") + "' (" + chosen.Source + ") " +
                        "rounds=" + shots + (config.RoundsPerCall > 0 ? " (cfg)" : " (vanilla)") +
                        " impact=" + impactDelay.ToString("0.#") + "s interShot=" + interShot.ToString("0.##") + "s" +
                        " (battery " + vanillaImpact.ToString("0.#") + "s/" + vanillaInterShot.ToString("0.##") + "s)" +
                        " dispersion=" + dispersion.ToString("0.#") + "m cooldown=" + cooldown.ToString("0.#") + "s" +
                        " spawn=" + spawnHeight.ToString("0") + "m@" + spawnAngle.ToString("0") + "deg heading=" + heading.ToString("0"));

            return new ArtillerySlot
            {
                Config = config,
                Battery = battery,
                InstantVolley = instant,
                TemplatePrefabName = chosen.Choice.DefaultProjectile == null
                    ? null
                    : chosen.Choice.DefaultProjectile.name,
                WeaponType = FireSupportTemplates.ToGameWeapon(config.Weapon),
                VanillaShots = vanillaShots,
                VanillaImpactDelay = vanillaImpact,
                VanillaInterShot = vanillaInterShot,
                VanillaDispersion = vanillaDispersion,
                VanillaCooldown = vanillaCooldown
            };
        }

        /// <summary>
        /// Builds one CAS sortie for the slot's configured attack types (CasAttackTypes). The airframe
        /// and its loadout are picked automatically for the player's faction (mission airframes carry an
        /// authoritative faction, scanned prefabs are identified through CasAirframeCatalog).
        ///
        /// Each slot is ONE sortie / one map button: configure the types you want per slot (for example
        /// Slot4_CasAttackTypes = GunRun and Slot5_CasAttackTypes = Bombs) instead of one slot expanding
        /// into several. An empty CasAttackTypes ("Any") keeps whatever the chosen airframe's own
        /// loadout carries.
        ///
        /// Type-specific behaviour (unchanged):
        ///   * GunRun is pinned to A-10 (Blue) / MiG-23BN (Red) and built by the runtime factory;
        ///   * Bombs / Rockets use the game's own hardpoint prefabs;
        ///   * rockets and gun runs fire with the built-in zero-spread precision.
        /// </summary>
        internal static CasSlot BuildCas(SlotConfig config, List<CasTemplate> templates, Faction playerFaction,
            out string failure)
        {
            failure = null;
            if (templates == null || templates.Count == 0)
            {
                failure = "no CAS airframe template available (mission scene, session cache and loaded assets were all empty)";
                return null;
            }

            CasTemplate template = PickBest(templates, config, playerFaction);
            if (template == null)
            {
                failure = "no airframe can deliver " +
                          (config.AttackTypes != null && config.AttackTypes.Length > 0
                              ? FireSupportTemplates.DescribeAttacks(config.AttackTypes)
                              : "this slot's loadout") +
                          "; available: " + DescribeTemplates(templates);
                return null;
            }

            if (template.Faction != playerFaction && template.Faction != Faction.Neutral)
            {
                Log.Warn("slot " + config.Index + ": no " + playerFaction + " airframe is available here; using '" +
                         template.Name + "' (" + template.Faction + ") instead.");
            }

            // Resolve the payload: the template's own hardpoints unless a requested attack type cannot
            // be mounted on them. The game can only fire attack types whose hardpoints are physically
            // mounted (CASHardpointManager.CanDoAttackType), so an aircraft that ships with bombs only
            // can never do a GunRun no matter what CASAttackMeta entries exist - TrySynthesizePayload
            // re-mounts the pylons from the loaded hardpoint library in that case.
            GameObject[] payloadPrefabs;
            AttackKind[] requestedKinds;
            bool synthesized = TrySynthesizePayload(config, template, out payloadPrefabs, out requestedKinds);

            CASLoadout loadout = new CASLoadout();
            GameObject[] hardpoints = synthesized
                ? payloadPrefabs
                : (template.Loadout != null && template.Loadout.Loadout != null
                    ? template.Loadout.Loadout.HardpointPrefabs
                    : null);
            CASAttackMeta[] attacks = synthesized
                ? BuildAttacksForKinds(requestedKinds, template)
                : FilterAttacks(config, template);

            // Filtering CASAttackMeta alone is not enough. GHPC chooses the final attack from the
            // mounted hardpoints, so a Rockets-only slot that still carries the template's Bombs
            // pylons can select Bombs for a soft target even though its Bombs metadata was removed;
            // GetAttackMetaByType then returns null and the aircraft flies the pass without firing.
            // Restrict the physical payload to the explicitly requested types as well. A single
            // matching prefab is reused on every pylon, which is always a valid GHPC loadout.
            if (config.AttackTypes != null && config.AttackTypes.Length > 0)
            {
                hardpoints = RestrictHardpoints(hardpoints, config.AttackTypes, template.AttachPointCount);
            }

            // Final guarantee that the airframe can actually be configured. CASHardpointManager
            // .HasCriticalConfigError() rejects a HardpointPrefabs list that is neither a single entry
            // nor one entry per attach point; DoConfig() then returns before SetUpHardpoints(), so
            // _configDone stays false and every Fire() is refused - the plane flies its pass and never
            // drops anything ("cannot mount"). TrySynthesizePayload normally produces a valid list, but
            // its fallbacks keep the template's own loadout, and a donor whose attach-point count could
            // not be measured (AttachPointCount <= 0) cannot be validated at all. Collapsing to one
            // prefab is always valid (the game reuses it on every attach point), so do that rather than
            // hand the game a loadout it will refuse.
            if (!HardpointListFits(hardpoints, template.AttachPointCount))
            {
                GameObject first = FirstHardpoint(hardpoints);
                if (first != null)
                {
                    Log.Warn("slot " + config.Index + ": the " + (hardpoints == null ? 0 : hardpoints.Length) +
                             "-entry hardpoint list cannot be mounted on '" + template.Name + "' (" +
                             (template.AttachPointCount <= 0 ? "unknown" : template.AttachPointCount.ToString()) +
                             " attach point(s)); collapsing to a single prefab so the sortie can still fire.");
                    hardpoints = new[] { first };
                    AttackKind singleKind;
                    if (TryKindOfHardpoint(first, out singleKind))
                    {
                        attacks = BuildAttacksForKinds(new[] { singleKind }, template);
                    }
                    else
                    {
                        // The one prefab left carries a weapon the mod no longer offers (air-to-air missile
                        // / training round). Declaring no attack is honest: the alternative is a bomb entry
                        // that the pylon cannot actually deliver.
                        Log.Warn("slot " + config.Index + ": the only mountable hardpoint on '" + template.Name +
                                 "' carries an attack type the mod no longer offers; the sortie has no usable weapon.");
                        attacks = new CASAttackMeta[0];
                    }
                }
            }

            loadout.HardpointPrefabs = hardpoints;
            loadout.Attacks = attacks;

            CASLoadoutScriptable loadoutAsset = ScriptableObject.CreateInstance<CASLoadoutScriptable>();
            loadoutAsset.Loadout = loadout;
            _ourLoadouts.Add(loadoutAsset);

            CasAirframeUnit airframe = new CasAirframeUnit();
            airframe.airframePrefab = template.Prefab;
            airframe.Loadout = loadoutAsset;
            // Flyover: an explicit cfg value wins; an empty CasFlyover means "auto" and uses the
            // typical profile of the chosen airframe.
            FlyoverKind flyover = config.Flyover;
            if (!config.FlyoverWasExplicit)
            {
                AirframeInfo info = CasAirframeCatalog.Match(template.Name);
                // Unknown airframe: every CAS aircraft in the game is a jet or a prop plane, so a single
                // pass is the safe default.
                flyover = info != null ? info.Flyover : FlyoverKind.SinglePass;
                Log.Verbose("slot " + config.Index + ": CasFlyover is empty - using " + flyover + " for '" + template.Name + "'.");
            }

            airframe.flyoverType = flyover == FlyoverKind.Linger
                ? CasAirframeUnit.FlyoverType.Linger
                : CasAirframeUnit.FlyoverType.SinglePass;
            CasMissionsRef(airframe) = MissionsToStore(config);

            Log.Verbose("slot " + config.Index + ": CAS airframe built from template '" + template.Name + "' + loadout '" +
                        template.LoadoutName + "' (" + template.Source + ", " + template.Faction + "), flyover=" + flyover +
                        ", attacks=" + FireSupportTemplates.DescribeAttacks(config.AttackTypes) +
                        ", sorties=" + (config.Missions < 0 ? "infinite" : config.Missions.ToString()));

            return new CasSlot
            {
                Config = config,
                Airframe = airframe,
                Templates = templates
            };
        }


        /// <summary>
        /// Attempts to give the slot exactly the attack types the config asks for.
        ///
        /// Returns true (with a re-synthesised hardpoint list) when the template's own loadout cannot
        /// mount at least one requested type but the loaded hardpoint library can supply it. Returns
        /// false when the template's loadout is fine as-is (it carries every requested type) or when
        /// nothing better is possible - the caller then keeps the template's hardpoints and
        /// FilterAttacks reports which requested types had to be dropped.
        /// </summary>
        private static bool TrySynthesizePayload(SlotConfig config, CasTemplate template,
            out GameObject[] hardpoints, out AttackKind[] kinds)
        {
            hardpoints = null;
            kinds = null;

            if (config.AttackTypes == null || config.AttackTypes.Length == 0)
            {
                return false;
            }

            // Distinct requested types, config order preserved.
            List<CASAttackType> requested = new List<CASAttackType>();
            for (int i = 0; i < config.AttackTypes.Length; i++)
            {
                CASAttackType type = FireSupportTemplates.ToGameAttack(config.AttackTypes[i]);
                if (!requested.Contains(type))
                {
                    requested.Add(type);
                }
            }

            // Which of them can the template's own hardpoint prefabs already deliver?
            bool[] carried = new bool[requested.Count];
            bool allCarried = true;
            for (int i = 0; i < requested.Count; i++)
            {
                carried[i] = FirstTemplatePrefabOfType(template, requested[i]) != null;
                if (!carried[i])
                {
                    allCarried = false;
                }
            }
            // GunRun and the air-to-ground missile are the only types the runtime factory builds (GHPC
            // has no gun hardpoint and no AGM payload at all). Bombs / rockets must come from the game's
            // own hardpoint prefabs, so a template whose own loadout already carries the requested type
            // keeps that loadout untouched - real vanilla pylons, ammo counts and visuals included.
            bool needsFactory = false;
            for (int i = 0; i < requested.Count; i++)
            {
                if (requested[i] == CASAttackType.GunRun ||
                    requested[i] == CASAttackType.AirToGroundMissile)
                {
                    needsFactory = true;
                    break;
                }
            }
            if (allCarried && !needsFactory)
            {
                return false; // the template's own (vanilla) loadout already satisfies the request
            }

            // GunRun / AGM: the runtime factory.
            // Everything else: the template's own vanilla hardpoint, then the loaded hardpoint library.
            // A type nobody can supply is reported and the template's loadout is kept, so FilterAttacks
            // degrades with a warning instead of producing an aircraft that never fires.
            GameObject[] pool = new GameObject[requested.Count];
            List<AttackKind> missing = new List<AttackKind>();
            for (int i = 0; i < requested.Count; i++)
            {
                CASAttackType type = requested[i];

                if (type == CASAttackType.GunRun || type == CASAttackType.AirToGroundMissile)
                {
                    pool[i] = CasPayloadFactory.EnsureWeapon(type, template.Name, config.CasAccuracy);
                }
                else
                {
                    if (carried[i])
                    {
                        pool[i] = FirstTemplatePrefabOfType(template, type);
                    }
                    if (pool[i] == null)
                    {
                        pool[i] = CasAttackLibrary.FirstFor(type);
                    }
                }

                if (pool[i] == null)
                {
                    AttackKind described;
                    FireSupportTemplates.TryFromGameAttack(type, out described);
                    missing.Add(described);
                }
            }
            if (missing.Count > 0)
            {
                Log.Warn("slot " + config.Index + ": cannot mount " +
                         FireSupportTemplates.DescribeAttacks(missing.ToArray()) + " on '" + template.Name +
                         "' - no loaded hardpoint delivers it, so that attack type stays unavailable.");
                return false;
            }

            int attachPoints = template.AttachPointCount;
            if (requested.Count > 1 && attachPoints <= 0)
            {
                Log.Warn("slot " + config.Index + ": cannot mix attack types on '" + template.Name +
                         "' because its hardpoint attach point count is unknown; keeping the template's own loadout.");
                return false;
            }
            if (requested.Count > 1 && requested.Count > attachPoints)
            {
                Log.Warn("slot " + config.Index + ": requested " + requested.Count +
                         " attack types but '" + template.Name + "' has only " + attachPoints +
                         " hardpoint attach point(s); keeping the template's own loadout instead.");
                return false;
            }

            if (requested.Count == 1)
            {
                // A single prefab is reused on every attach point (HardpointPrefabs.Length == 1) and it
                // makes every other attack type unavailable, so CASController.GetIdealAttackType has no
                // choice but the requested type - the only way to force e.g. GunRun deterministically.
                hardpoints = new[] { pool[0] };
            }
            else
            {
                hardpoints = new GameObject[attachPoints];
                for (int p = 0; p < attachPoints; p++)
                {
                    hardpoints[p] = pool[p % requested.Count];
                }
            }

            kinds = new AttackKind[requested.Count];
            for (int i = 0; i < requested.Count; i++)
            {
                // requested[] comes from the config's CasAttackTypes, which can only name a supported
                // type, so the mapping always succeeds here.
                AttackKind kind;
                FireSupportTemplates.TryFromGameAttack(requested[i], out kind);
                kinds[i] = kind;
            }

            Log.Info("slot " + config.Index + ": CAS payload mounted for '" + template.Name + "' - " +
                     FireSupportTemplates.DescribeAttacks(kinds) + " over " + hardpoints.Length + " hardpoint(s) (" +
                     "gun run and the air-to-ground missile = runtime hardpoints, everything else = the " +
                     "game's own hardpoint prefabs).");
            return true;
        }

        /// <summary>
        /// Mirrors CASHardpointManager.HasCriticalConfigError()'s hardpoint rule: the list must be
        /// non-empty and either a single entry (reused on every attach point) or at least one entry per
        /// attach point. An unknown attach-point count (0) can only be satisfied safely by one entry.
        /// </summary>
        private static bool HardpointListFits(GameObject[] hardpoints, int attachPoints)
        {
            if (hardpoints == null || hardpoints.Length == 0)
            {
                return false;
            }
            if (hardpoints.Length == 1)
            {
                return true;
            }
            return attachPoints > 0 && hardpoints.Length >= attachPoints;
        }

        /// <summary>
        /// Keeps the mounted hardpoints in lockstep with an explicit CasAttackTypes filter. The game
        /// does not use CASAttackMeta as the source of CanDoAttackType(); it inspects the instantiated
        /// hardpoints, so leaving an unrequested Bombs/Rockets prefab here reintroduces the attack type.
        /// </summary>
        private static GameObject[] RestrictHardpoints(GameObject[] source, AttackKind[] requested,
            int attachPoints)
        {
            if (source == null || source.Length == 0 || requested == null || requested.Length == 0)
            {
                return source;
            }

            List<GameObject> matching = new List<GameObject>();
            for (int i = 0; i < source.Length; i++)
            {
                GameObject prefab = source[i];
                CASHardpoint hardpoint = prefab == null
                    ? null
                    : prefab.GetComponentInChildren<CASHardpoint>(true);
                if (!FireSupportTemplates.IsUsableHardpoint(hardpoint))
                {
                    continue;
                }

                AttackKind kind;
                if (!FireSupportTemplates.TryFromGameAttack(hardpoint.Type, out kind))
                {
                    continue;
                }
                for (int r = 0; r < requested.Length; r++)
                {
                    if (requested[r] == kind)
                    {
                        matching.Add(prefab);
                        break;
                    }
                }
            }

            if (matching.Count == 0)
            {
                return source;
            }

            if (requested.Length == 1 || matching.Count == 1)
            {
                return new[] { matching[0] };
            }

            int count = attachPoints > 0 ? attachPoints : matching.Count;
            GameObject[] result = new GameObject[count];
            for (int i = 0; i < result.Length; i++)
            {
                result[i] = matching[i % matching.Count];
            }
            return result;
        }

        private static GameObject FirstHardpoint(GameObject[] hardpoints)
        {
            if (hardpoints == null)
            {
                return null;
            }
            for (int i = 0; i < hardpoints.Length; i++)
            {
                if (hardpoints[i] != null)
                {
                    return hardpoints[i];
                }
            }
            return null;
        }

        /// <summary>
        /// The attack kind a hardpoint prefab delivers. False when the prefab carries no CASHardpoint at
        /// all, or carries one of a type the mod no longer offers (air-to-air missile, training round) -
        /// the caller then has to drop it rather than guess a kind for it.
        /// </summary>
        private static bool TryKindOfHardpoint(GameObject prefab, out AttackKind kind)
        {
            CASHardpoint hardpoint = prefab != null ? prefab.GetComponentInChildren<CASHardpoint>(true) : null;
            if (!FireSupportTemplates.IsUsableHardpoint(hardpoint))
            {
                kind = AttackKind.Bombs;
                return false;
            }
            return FireSupportTemplates.TryFromGameAttack(hardpoint.Type, out kind);
        }

        /// <summary>The first hardpoint prefab of the template's own loadout whose mounted weapon type matches, or null.</summary>
        private static GameObject FirstTemplatePrefabOfType(CasTemplate template, CASAttackType type)
        {
            if (template == null || template.Loadout == null || template.Loadout.Loadout == null ||
                template.Loadout.Loadout.HardpointPrefabs == null)
            {
                return null;
            }
            GameObject[] prefabs = template.Loadout.Loadout.HardpointPrefabs;
            for (int i = 0; i < prefabs.Length; i++)
            {
                GameObject prefab = prefabs[i];
                if (prefab == null)
                {
                    continue;
                }
                CASHardpoint hardpoint = prefab.GetComponentInChildren<CASHardpoint>(true);
                if (FireSupportTemplates.IsUsableHardpoint(hardpoint) && hardpoint.Type == type)
                {
                    return prefab;
                }
            }
            return null;
        }

        /// <summary>
        /// Attack entries for a re-synthesised payload. Each requested type keeps the template's own
        /// authored timing when it declared one (approach/release distance, trigger pulls, ...) and
        /// falls back to CASAttackMeta's defaults otherwise - a plain entry is enough, the actual
        /// munition comes from the mounted hardpoint.
        /// </summary>
        private static CASAttackMeta[] BuildAttacksForKinds(AttackKind[] kinds, CasTemplate template)
        {
            CASAttackMeta[] source = template != null && template.Loadout != null && template.Loadout.Loadout != null
                ? template.Loadout.Loadout.Attacks
                : null;

            List<CASAttackMeta> list = new List<CASAttackMeta>(kinds.Length);
            for (int i = 0; i < kinds.Length; i++)
            {
                CASAttackType type = FireSupportTemplates.ToGameAttack(kinds[i]);
                bool duplicate = false;
                for (int j = 0; j < list.Count; j++)
                {
                    if (list[j].UniqueType == type)
                    {
                        duplicate = true;
                        break;
                    }
                }
                if (duplicate)
                {
                    continue;
                }

                CASAttackMeta authored = null;
                if (source != null)
                {
                    for (int j = 0; j < source.Length; j++)
                    {
                        if (source[j] != null && source[j].UniqueType == type)
                        {
                            authored = source[j];
                            break;
                        }
                    }
                }
                CASAttackMeta meta = authored != null ? authored.DeepCopy() : new CASAttackMeta { UniqueType = type };

                // The unified gun run streams its magazine at a fixed rate; the per-airframe authored
                // timing (if any) is overridden so every GunRun behaves identically.
                if (type == CASAttackType.GunRun)
                {
                    CasPayloadFactory.ApplyGunRateOfFire(meta);
                }
                else if (type == CASAttackType.AirToGroundMissile)
                {
                    // The missile payload is ours, so its attack timing is too: one missile per trigger
                    // pull, a second apart, released further out than a bomb (a missile is meant to be
                    // launched from stand-off range, and the mod flies it onto the impact point anyway).
                    CasPayloadFactory.ApplyMissileAttackProfile(meta, template.Faction);
                }

                list.Add(meta);
            }
            return list.ToArray();
        }

        /// <summary>
        /// Builds the attack list of the cloned loadout. An empty config list keeps every attack the
        /// template has; an explicit list keeps the intersection with what the template can actually
        /// carry, so a typo cannot produce an aircraft that never fires.
        /// </summary>
        private static CASAttackMeta[] FilterAttacks(SlotConfig config, CasTemplate template)
        {
            CASAttackMeta[] source = template.Loadout.Loadout.Attacks;
            if (source == null || source.Length == 0)
            {
                // The loadout declares no attack entries of its own - what it can do is implied by its
                // hardpoint prefabs. CASHardpointManager copies its attack list from Loadout.Attacks, so
                // leaving this empty means the aircraft flies its pass and never fires. Build the
                // entries from the configured types (or from what the hardpoints actually carry).
                Log.Warn("slot " + config.Index + ": template '" + template.Name + "' declares no attack entries; " +
                         "building them from its hardpoints (" +
                         FireSupportTemplates.DescribeAttacks(template.AvailableAttacks) + ").");
                return BuildDefaultAttacks(config.AttackTypes != null && config.AttackTypes.Length > 0
                    ? config.AttackTypes
                    : template.AvailableAttacks);
            }

            if (config.AttackTypes == null || config.AttackTypes.Length == 0)
            {
                return DeepCopySupported(source, template);
            }

            List<CASAttackMeta> kept = new List<CASAttackMeta>();
            List<AttackKind> unavailable = new List<AttackKind>();
            for (int i = 0; i < config.AttackTypes.Length; i++)
            {
                AttackKind wanted = config.AttackTypes[i];
                if (!template.Supports(wanted))
                {
                    unavailable.Add(wanted);
                    continue;
                }

                CASAttackMeta found = null;
                for (int j = 0; j < source.Length; j++)
                {
                    AttackKind sourceKind;
                    if (source[j] != null &&
                        FireSupportTemplates.TryFromGameAttack(source[j].UniqueType, out sourceKind) &&
                        sourceKind == wanted)
                    {
                        found = source[j];
                        break;
                    }
                }

                // Some loadouts carry an attack type only through their hardpoint prefabs and never
                // list it in Attacks. Those entries still have to exist, otherwise
                // CASHardpointManager.GetAttackMetaByType() returns null and the pass is flown dry.
                kept.Add(found != null
                    ? found.DeepCopy()
                    : new CASAttackMeta { UniqueType = FireSupportTemplates.ToGameAttack(wanted) });
            }

            if (unavailable.Count > 0)
            {
                Log.Warn("slot " + config.Index + ": template '" + template.Name + "' does not carry " + DescribeAttacks(unavailable) +
                         "; ignored. It carries: " + FireSupportTemplates.DescribeAttacks(template.AvailableAttacks));
            }

            if (kept.Count == 0)
            {
                Log.Warn("slot " + config.Index + ": none of the requested CasAttackTypes are carried by template '" + template.Name +
                         "'; using every attack the template has instead.");
                CASAttackMeta[] all = DeepCopySupported(source, template);
                if (all.Length > 0)
                {
                    return all;
                }

                // The template declares no attack entries at all - build plain ones from the config so
                // the sortie is still deliverable instead of silently doing nothing.
                Log.Warn("slot " + config.Index + ": template '" + template.Name + "' declares no attack entries; " +
                         "building default ones for " + FireSupportTemplates.DescribeAttacks(config.AttackTypes) + ".");
                return BuildDefaultAttacks(config.AttackTypes);
            }

            return kept.ToArray();
        }

        /// <summary>Plain attack entries using CASAttackMeta's own defaults (one trigger pull, 1000 m release).</summary>
        private static CASAttackMeta[] BuildDefaultAttacks(AttackKind[] kinds)
        {
            if (kinds == null || kinds.Length == 0)
            {
                return new CASAttackMeta[0];
            }

            List<CASAttackMeta> list = new List<CASAttackMeta>(kinds.Length);
            for (int i = 0; i < kinds.Length; i++)
            {
                CASAttackType type = FireSupportTemplates.ToGameAttack(kinds[i]);
                bool duplicate = false;
                for (int j = 0; j < list.Count; j++)
                {
                    if (list[j].UniqueType == type)
                    {
                        duplicate = true;
                        break;
                    }
                }
                if (!duplicate)
                {
                    list.Add(new CASAttackMeta { UniqueType = type });
                }
            }
            return list.ToArray();
        }

        /// <summary>
        /// Copies the template's own attack entries for the "keep everything the aircraft has" path -
        /// but never an attack type the mod no longer offers (air-to-air missile, training round), so the
        /// default CasAttackTypes = "Any" cannot smuggle one back onto the sortie.
        /// </summary>
        private static CASAttackMeta[] DeepCopySupported(CASAttackMeta[] source, CasTemplate template)
        {
            List<CASAttackMeta> copies = new List<CASAttackMeta>(source.Length);
            for (int i = 0; i < source.Length; i++)
            {
                AttackKind kind;
                if (source[i] != null && FireSupportTemplates.TryFromGameAttack(source[i].UniqueType, out kind) &&
                    (template == null || template.Supports(kind)))
                {
                    copies.Add(source[i].DeepCopy());
                }
            }
            return copies.ToArray();
        }

        /// <summary>
        /// Scores every candidate for one attack type.
        ///
        ///   * deliverable at all: a candidate that neither carries the type nor can have it synthesized
        ///     is rejected outright;
        ///   * faction: own side +1000, unknown (a scanned prefab the catalog could not identify) +100,
        ///     the enemy -500 - an enemy airframe is only ever used when nothing else exists;
        ///   * a real hardpoint for the type +50 each (+100 when the loadout carries everything asked
        ///     for), so an airframe whose own loadout carries it always beats one that has to be
        ///     synthesized;
        ///   * GunRun only: mission-scene airframe +400 (the strafing plane is pinned to the mission's
        ///     own aircraft) and fixed wing +20;
        ///   * an exact hardpoint/attach-point match +5, fixed wing +2, prefab over live instance +1.
        ///
        /// The score only decides WHO is eligible, not who flies: every deliverable candidate of the
        /// player's faction is in the draw, and one is picked at random (see DrawRandomAirframe), so a
        /// bomb or rocket slot sends a different aircraft with a different loadout on every call.
        /// </summary>
        private static CasTemplate PickBest(List<CasTemplate> candidates, SlotConfig config, Faction playerFaction)
        {
            AttackKind[] wanted = config.AttackTypes != null && config.AttackTypes.Length > 0
                ? config.AttackTypes
                : null;
            bool wantsGunRun = wanted != null && Array.IndexOf(wanted, AttackKind.GunRun) >= 0;
            bool wantsMissile = wanted != null && Array.IndexOf(wanted, AttackKind.AirToGroundMissile) >= 0;
            bool wantsRockets = wanted != null && Array.IndexOf(wanted, AttackKind.Rockets) >= 0;
            bool wantsBombs = wanted != null && Array.IndexOf(wanted, AttackKind.Bombs) >= 0;
            // Both of these are pinned to one designated aircraft per side (A-10 / MiG-23BN) and must not
            // be re-drawn per call.
            bool pinnedAirframe = wantsGunRun || wantsMissile;

            List<CasTemplate> ordered = new List<CasTemplate>(candidates);
            ordered.Sort(CompareTemplates);

            // Candidates that can actually deliver the request: `scored` = deliverable at all (the
            // payload may have to be synthesized), `native` = the template's OWN loadout already
            // carries everything the slot asked for.
            List<CasTemplate> scored = new List<CasTemplate>();
            List<CasTemplate> native = new List<CasTemplate>();

            CasTemplate best = null;
            int bestScore = int.MinValue;
            for (int i = 0; i < ordered.Count; i++)
            {
                CasTemplate candidate = ordered[i];

                // Every requested type must be obtainable from this airframe (carried, or available
                // from the loaded hardpoint library / the GunRun factory).
                int supported = 0;
                bool deliverable = true;
                if (wanted != null)
                {
                    for (int w = 0; w < wanted.Length; w++)
                    {
                        if (candidate.Supports(wanted[w]))
                        {
                            supported++;
                        }
                        else if (!CanDeliver(wanted[w], candidate))
                        {
                            deliverable = false;
                            break;
                        }
                    }
                }
                if (!deliverable)
                {
                    continue;
                }

                int score = 0;

                if (candidate.Faction == playerFaction)
                {
                    score += 1000;
                }
                else if (candidate.Faction == Faction.Neutral)
                {
                    score += 100;
                }
                else
                {
                    score -= 500;
                }

                if (wanted != null)
                {
                    score += supported * 50;
                    if (supported == wanted.Length)
                    {
                        score += 100;
                    }
                }

                if (wantsGunRun)
                {
                    score += DesignatedGunBonus(candidate, playerFaction);
                    if (candidate.Source == MissionSceneSource)
                    {
                        score += 400;
                    }
                }

                if (wantsMissile)
                {
                    score += DesignatedMissileBonus(candidate, playerFaction);
                    if (candidate.Source == MissionSceneSource)
                    {
                        score += 400;
                    }
                }

                if (candidate.AttachPointCount > 0 && candidate.HardpointCount == candidate.AttachPointCount)
                {
                    score += 5;
                }

                if (candidate.IsAsset)
                {
                    score += 1;
                }

                scored.Add(candidate);
                if ((wanted == null || supported == wanted.Length) &&
                    CasAirframeCatalog.LoadoutFitsSide(candidate.Name, candidate.LoadoutName))
                {
                    native.Add(candidate);
                }
                if (score > bestScore)
                {
                    bestScore = score;
                    best = candidate;
                }
            }

            // Bomb and rocket slots fly a DIFFERENT airframe with a different loadout on every call.
            // Gun runs and missile slots are deliberately excluded: their airframe is the designated one
            // (Blue = A-10, Red = MiG-23BN) and must stay fixed.
            if (best != null && !pinnedAirframe)
            {
                // Rockets skip the two gun-run aircraft (they are what the player sees on every strafe
                // already); a slot that also asks for bombs is a pure draw, so nothing is excluded.
                bool skipGunRunAirframes = wantsRockets && !wantsBombs;
                List<CasTemplate> pool = native.Count > 0 ? native : scored;
                best = DrawRandomAirframe(pool, config, playerFaction, skipGunRunAirframes, best) ?? best;

                if (native.Count == 0)
                {
                    Log.Warn("slot " + config.Index + ": no airframe here carries " +
                             FireSupportTemplates.DescribeAttacks(config.AttackTypes) +
                             " in its own loadout, so the payload has to be synthesized from the loaded " +
                             "hardpoint library (that is another aircraft's pylon; a rocket pod mounted " +
                             "this way may look wrong in game).");
                }
            }

            if (best != null)
            {
                _lastPickedAirframe[config.Index] = best.Name;
                Log.Verbose("slot " + config.Index + ": picked airframe '" + best.Name + "' (" + best.Faction + ", " +
                            best.Source + ", score " + bestScore + ") for " +
                            (wanted != null ? FireSupportTemplates.DescribeAttacks(wanted) : "the airframe's own loadout") +
                            " / faction " + playerFaction +
                            (wantsGunRun ? ", gun run: designated airframe (fixed)" : ", random draw per call") + ".");
            }
            return best;
        }

        /// <summary>
        /// Draws the airframe for a bomb / rocket slot: one uniform pick out of every deliverable
        /// candidate of the player's faction, avoiding the one this slot flew last so two consecutive
        /// calls never send the same aircraft. The pool widens to neutral (then to the enemy, which
        /// BuildCas already warns about) only when the player's own side has nothing to offer at all.
        ///
        /// This replaces the old "within 25 points of the winner" pool, which was usually a single
        /// candidate - the faction and "carries the type" bonuses dominate the score - so the "random"
        /// airframe never actually changed.
        /// </summary>
        private static CasTemplate DrawRandomAirframe(List<CasTemplate> scored, SlotConfig config,
            Faction playerFaction, bool skipGunRunAirframes, CasTemplate fallback)
        {
            List<CasTemplate> pool = BuildDrawPool(scored, playerFaction, skipGunRunAirframes);
            if (pool.Count == 0)
            {
                // Only the designated gun-run aircraft can do what this slot asked for: rather than
                // dropping the call, allow them (the slot still needs a plane).
                pool = BuildDrawPool(scored, playerFaction, false);
                if (pool.Count > 0)
                {
                    Log.Warn("slot " + config.Index + ": only the gun-run aircraft can deliver " +
                             FireSupportTemplates.DescribeAttacks(config.AttackTypes) +
                             " here; using it despite the rocket-slot exclusion.");
                }
            }
            if (pool.Count == 0)
            {
                return fallback;
            }

            string previous;
            _lastPickedAirframe.TryGetValue(config.Index, out previous);

            // Draw a candidate that is NOT the aircraft this slot flew last (a single-pass pool that
            // only holds that one aircraft repeats it - sending nothing would be worse).
            CasTemplate drawn = DrawDifferent(pool, previous) ?? pool[UnityEngine.Random.Range(0, pool.Count)];

            if (CustomFireSupportMod.VerboseLogging)
            {
                Log.Verbose("slot " + config.Index + ": airframe draw from " + pool.Count + " candidate(s) -> '" +
                            drawn.Name + "'" +
                            (previous != null ? " (previous call: '" + previous + "')" : string.Empty) +
                            ", pool: " + DescribeDrawPool(pool));
            }
            return drawn;
        }

        /// <summary>
        /// One uniform draw out of the pool, skipping every entry with the previous aircraft's name.
        /// Returns null when the pool holds only that aircraft.
        /// </summary>
        private static CasTemplate DrawDifferent(List<CasTemplate> pool, string previous)
        {
            if (string.IsNullOrEmpty(previous))
            {
                return pool[UnityEngine.Random.Range(0, pool.Count)];
            }

            int alternatives = 0;
            for (int i = 0; i < pool.Count; i++)
            {
                if (!string.Equals(pool[i].Name, previous, StringComparison.Ordinal))
                {
                    alternatives++;
                }
            }
            if (alternatives == 0)
            {
                return null;
            }

            // Pick the n-th alternative, n drawn uniformly.
            int wanted = UnityEngine.Random.Range(0, alternatives);
            for (int i = 0; i < pool.Count; i++)
            {
                if (string.Equals(pool[i].Name, previous, StringComparison.Ordinal))
                {
                    continue;
                }
                if (wanted == 0)
                {
                    return pool[i];
                }
                wanted--;
            }
            return null;
        }

        /// <summary>Deliverable candidates of one faction (the player's own first, then neutral, then any).</summary>
        private static List<CasTemplate> BuildDrawPool(List<CasTemplate> scored, Faction playerFaction, bool skipGunRunAirframes)
        {
            List<CasTemplate> pool = CollectDrawPool(scored, playerFaction, skipGunRunAirframes);
            if (pool.Count == 0)
            {
                pool = CollectDrawPool(scored, Faction.Neutral, skipGunRunAirframes);
            }
            if (pool.Count == 0)
            {
                pool = CollectDrawPool(scored, Faction.Neutral, skipGunRunAirframes, everything: true);
            }
            return pool;
        }

        private static List<CasTemplate> CollectDrawPool(List<CasTemplate> scored, Faction faction,
            bool skipGunRunAirframes, bool everything = false)
        {
            List<CasTemplate> pool = new List<CasTemplate>();
            for (int i = 0; i < scored.Count; i++)
            {
                CasTemplate candidate = scored[i];
                if (!everything && candidate.Faction != faction)
                {
                    continue;
                }
                if (skipGunRunAirframes && CasAirframeCatalog.IsGunRunAirframe(candidate.Name))
                {
                    continue;
                }
                if (!CasAirframeCatalog.LoadoutFitsSide(candidate.Name, candidate.LoadoutName))
                {
                    continue; // e.g. an F-4 carrying a Soviet rocket pod: the pod is another side's
                }
                pool.Add(candidate);
            }
            return pool;
        }

        private static string DescribeDrawPool(List<CasTemplate> pool)
        {
            StringBuilder builder = new StringBuilder();
            for (int i = 0; i < pool.Count; i++)
            {
                if (i > 0)
                {
                    builder.Append(", ");
                }
                builder.Append('\'').Append(pool[i].Name).Append("' + '").Append(pool[i].LoadoutName).Append('\'');
            }
            return builder.ToString();
        }

        /// <summary>Source tag CasDonorProvider gives the current mission's own airframes.</summary>
        private const string MissionSceneSource = "mission scene";

        /// <summary>Stable order so airframe selection is reproducible for a given mission.</summary>
        private static int CompareTemplates(CasTemplate a, CasTemplate b)
        {
            int byName = string.CompareOrdinal(a != null ? a.Name : string.Empty, b != null ? b.Name : string.Empty);
            if (byName != 0)
            {
                return byName;
            }
            int byLoadout = string.CompareOrdinal(a != null ? a.LoadoutName : string.Empty, b != null ? b.LoadoutName : string.Empty);
            if (byLoadout != 0)
            {
                return byLoadout;
            }
            return string.CompareOrdinal(a != null ? a.Source : string.Empty, b != null ? b.Source : string.Empty);
        }

        /// <summary>
        /// True when an attack type can be mounted even on an airframe whose own loadout does not carry
        /// it: GunRun and the air-to-ground missile through the runtime factory (GHPC ships no gun
        /// hardpoint and no AGM payload), everything else through a vanilla hardpoint prefab in the
        /// loaded hardpoint library.
        /// </summary>
        private static bool CanDeliver(AttackKind kind, CasTemplate candidate)
        {
            if (kind == AttackKind.GunRun)
            {
                return CasPayloadFactory.CanBuild(CASAttackType.GunRun);
            }
            if (kind == AttackKind.AirToGroundMissile)
            {
                // The missile model is per side, and both composed prefabs ship in the bundle, so any
                // candidate qualifies as long as the payload can be assembled at all.
                return CasPayloadFactory.CanBuild(CASAttackType.AirToGroundMissile);
            }
            return CasAttackLibrary.CanSupply(FireSupportTemplates.ToGameAttack(kind));
        }

        /// <summary>
        /// The designated strafing aircraft per side: A-10 for Blue (US), MiG-23BN for Red (Soviet /
        /// East German) - CasAirframeCatalog.IsGunRunAirframe holds the name patterns. The bonus
        /// outweighs the mission-scene preference, so GunRun uses that airframe whenever it is loaded
        /// and otherwise falls back to the mission's own / same-faction aircraft.
        /// </summary>
        private static int DesignatedGunBonus(CasTemplate candidate, Faction playerFaction)
        {
            string name = candidate != null && candidate.Name != null ? candidate.Name.ToLowerInvariant() : string.Empty;
            if (playerFaction == Faction.Blue && (name.Contains("a10") || name.Contains("a-10")))
            {
                return 700;
            }
            if (playerFaction == Faction.Red && (name.Contains("mig23bn") || name.Contains("mig-23bn")))
            {
                return 700;
            }
            return 0;
        }

        /// <summary>
        /// The aircraft the air-to-ground missile is pinned to, per side: the A-10 (Blue) carries the
        /// AGM-65 model, the MiG-23BN (Red) flies the game's Soviet missile visual - the same two the gun run uses, so
        /// the designated pair stays the player's familiar ground attack aircraft.
        /// </summary>
        private static int DesignatedMissileBonus(CasTemplate candidate, Faction playerFaction)
        {
            string name = candidate != null && candidate.Name != null ? candidate.Name.ToLowerInvariant() : string.Empty;
            AirframeSide side = playerFaction == Faction.Red ? AirframeSide.Pact : AirframeSide.Nato;
            return CasAirframeCatalog.IsMissileAirframe(name, side) ? 700 : 0;
        }

        private static string DescribeCandidates(List<AmmoTemplate> candidates)
        {
            StringBuilder builder = new StringBuilder();
            for (int i = 0; i < candidates.Count; i++)
            {
                if (i > 0)
                {
                    builder.Append(", ");
                }
                builder.Append('\'').Append(candidates[i].AmmoName).Append('\'');
            }
            return builder.ToString();
        }

        private static string DescribeTemplates(List<CasTemplate> templates)
        {
            StringBuilder builder = new StringBuilder();
            for (int i = 0; i < templates.Count; i++)
            {
                if (i > 0)
                {
                    builder.Append(", ");
                }
                builder.Append('\'').Append(templates[i].Name).Append("' + '").Append(templates[i].LoadoutName).Append('\'');
            }
            return builder.ToString();
        }

        private static string DescribeAttacks(List<AttackKind> attacks)
        {
            StringBuilder builder = new StringBuilder();
            for (int i = 0; i < attacks.Count; i++)
            {
                if (i > 0)
                {
                    builder.Append(", ");
                }
                builder.Append(SlotConfigParsing.ToConfigName(attacks[i]));
            }
            return builder.ToString();
        }
    }
}
