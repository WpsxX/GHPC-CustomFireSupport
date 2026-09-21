using HarmonyLib;
using System;
using System.Collections.Generic;
using System.Text;
using GHPC;
using GHPC.UI.Map;
using GHPC.Vehicle;
using GHPC.Weaponry;
using GHPC.Weaponry.Artillery;
using GHPC.Weaponry.CAS;
using GHPC.Weapons.Artillery;
using UnityEngine;

namespace CustomFireSupport
{
    /// <summary>A ready-to-use indirect-fire shell template.</summary>
    internal sealed class AmmoTemplate
    {
        internal MunitionKind Munition;
        internal BatteryMunitionsChoice Choice;
        internal string Source;

        /// <summary>
        /// The vanilla battery this shell came from (null for ammo-asset-only templates). The custom
        /// battery copies its spawn height/angle/heading, round count, interval, dispersion, arrival
        /// delay and cooldown from it, so GHPC's own values are used unless the slot scales them.
        /// </summary>
        internal ArtilleryBattery SourceBattery;

        internal string AmmoName
        {
            get
            {
                if (Choice == null)
                {
                    return "(none)";
                }
                if (Choice.Ammo != null && Choice.Ammo.AmmoType != null)
                {
                    return Choice.Ammo.AmmoType.Name;
                }
                if (Choice.DefaultProjectile != null)
                {
                    return Choice.DefaultProjectile.name + " (projectile prefab)";
                }
                return "(unnamed)";
            }
        }

        internal string Describe()
        {
            string detail = AmmoDetail();
            string name = AmmoName;
            if (detail.Length > 0)
            {
                name += " (" + detail + ")";
            }
            return name + " [" + Source + "]";
        }

        /// <summary>
        /// Calibre / category / explosive content of the shell behind this template, so a log reader can
        /// see what a slot is really going to fire (an ATGM and a 30 mm round both passed for "155 mm HE"
        /// in the log of a Fulda 1989 mission, and neither name nor source said so).
        /// </summary>
        internal string AmmoDetail()
        {
            if (Choice == null || Choice.Ammo == null || Choice.Ammo.AmmoType == null)
            {
                return string.Empty;
            }

            AmmoType ammo = Choice.Ammo.AmmoType;
            StringBuilder detail = new StringBuilder();
            detail.Append(ammo.GetFinalCaliber().ToString("0.#")).Append("mm, ").Append(ammo.Category);
            if (ammo.Guidance != AmmoType.GuidanceType.Unguided)
            {
                detail.Append(", ").Append(ammo.Guidance);
            }
            if (ammo.TntEquivalentKg > 0f)
            {
                detail.Append(", ").Append(ammo.TntEquivalentKg.ToString("0.#")).Append("kg TNTe");
            }
            return detail.ToString();
        }
    }

    /// <summary>A candidate aircraft + loadout that can be cloned into a custom CAS slot.</summary>
    internal sealed class CasTemplate
    {
        internal GameObject Prefab;
        internal CASLoadoutScriptable Loadout;
        internal string Name;
        internal string LoadoutName;
        internal Faction Faction;

        /// <summary>Attack types the loadout can actually deliver.</summary>
        internal AttackKind[] AvailableAttacks = new AttackKind[0];

        /// <summary>
        /// Attack types backed by a real CASHardpoint prefab in this loadout. CASAttackMeta may contain
        /// an entry without a mounted weapon; the game ultimately checks the instantiated hardpoints.
        /// </summary>
        internal AttackKind[] MountedAttacks = new AttackKind[0];

        /// <summary>Where this candidate came from (mission scene / session cache / loaded assets).</summary>
        internal string Source = string.Empty;

        /// <summary>True when the donor is a prefab asset rather than a live scene instance.</summary>
        internal bool IsAsset;

        /// <summary>Hardpoint prefab count of the loadout and attach point count of the airframe.
        /// A mismatch means the game refuses to configure the aircraft (it can never fire).</summary>
        internal int HardpointCount;
        internal int AttachPointCount;

        internal bool Supports(AttackKind attack)
        {
            if (MountedAttacks == null || MountedAttacks.Length == 0)
            {
                return false;
            }
            for (int i = 0; i < MountedAttacks.Length; i++)
            {
                if (MountedAttacks[i] == attack)
                {
                    return true;
                }
            }
            return false;
        }
    }

    /// <summary>
    /// Collects the building blocks the custom slots need from the current mission scene.
    ///
    /// GHPC has no global asset database for these objects: shell assets (AmmoCodexScriptable) and
    /// aircraft prefabs only exist in memory when the loaded scene references them. So the mod harvests
    /// templates from the live FireMissionManager / CasSupportManager (plus every AmmoCodexScriptable
    /// that happens to be loaded) and reports exactly what it found, instead of guessing.
    /// </summary>
    internal static class FireSupportTemplates
    {
        /// <summary>
        /// Exported CAS prefabs can retain an attack type after their ammo reference was stripped.
        /// Such a hardpoint makes the aircraft fly a pass with no usable projectile.
        /// </summary>
        internal static bool IsUsableHardpoint(CASHardpoint hardpoint)
        {
            try
            {
                return hardpoint != null && hardpoint.Ammo != null &&
                       hardpoint.Ammo.ShotVisual != null && hardpoint.TotalMunitionsCapacity > 0;
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>All shell candidates per shell type, best source first (scene batteries, then loaded codex assets).</summary>
        internal static Dictionary<MunitionKind, List<AmmoTemplate>> HarvestAmmo(FireMissionManager manager, Faction playerFaction)
        {
            Dictionary<MunitionKind, List<AmmoTemplate>> templates = new Dictionary<MunitionKind, List<AmmoTemplate>>();

            // Scene batteries first: the player's own faction before the enemy's.
            if (manager != null)
            {
                CollectFromBatteries(templates, manager.BlueArtilleryBatteries, Faction.Blue, playerFaction);
                CollectFromBatteries(templates, manager.RedArtilleryBatteries, Faction.Red, playerFaction);
            }

            // Fallback: any ammo codex asset currently loaded (covers missions with no artillery at all,
            // as long as the game has the shell assets in memory).
            //
            // This scan sees EVERY loaded ammo asset - the mod's own 30 mm CAS rounds, ATGMs, rifle
            // cartridges, hand grenades - and the slot fires the first candidate of its shell type, so
            // the fallback is filtered down to plausible shells and then ranked best-first. Without
            // that, a mission whose batteries offer no shells (or no batteries at all) ended up
            // "firing" a 9M14 Malyutka ATGM or a PGU-13/B 30 mm round as its 155 mm HE.
            AmmoCodexScriptable[] codexes = Resources.FindObjectsOfTypeAll<AmmoCodexScriptable>();
            List<AmmoTemplate> fallback = new List<AmmoTemplate>();
            int skipped = 0;
            for (int i = 0; i < codexes.Length; i++)
            {
                AmmoCodexScriptable codex = codexes[i];
                if (codex == null || codex.AmmoType == null)
                {
                    continue;
                }

                string ammoName = codex.AmmoType.Name;
                if (!LooksLikeArtilleryShell(codex.AmmoType, ammoName))
                {
                    skipped++;
                    continue;
                }

                MunitionKind kind = ClassifyAmmoName(ammoName);
                if (ContainsAmmo(fallback, codex))
                {
                    continue;
                }

                fallback.Add(new AmmoTemplate
                {
                    Munition = kind,
                    Choice = new BatteryMunitionsChoice
                    {
                        Type = ToGameMunition(kind),
                        Ammo = codex,
                        DefaultProjectile = null
                    },
                    Source = "loaded ammo asset"
                });
            }

            // Best shell first, then a stable name order: the slot takes the first candidate of its
            // type, so this decides what a call actually fires instead of the asset scan order.
            fallback.Sort(delegate(AmmoTemplate a, AmmoTemplate b)
            {
                int byScore = ArtilleryShellScore(ShellOf(b), b.AmmoName, playerFaction)
                    .CompareTo(ArtilleryShellScore(ShellOf(a), a.AmmoName, playerFaction));
                return byScore != 0 ? byScore : string.CompareOrdinal(a.AmmoName, b.AmmoName);
            });
            for (int i = 0; i < fallback.Count; i++)
            {
                GetOrCreate(templates, fallback[i].Munition).Add(fallback[i]);
            }
            Log.Info("loaded-ammo fallback: " + fallback.Count + " artillery shell candidate(s) kept, " +
                     skipped + " other ammo asset(s) skipped (missiles, rockets, small-calibre and bomb-sized " +
                     "rounds); a slot fires the best-ranked candidate of its shell type (see below).");

            // Smoke fallback: GHPC's smoke artillery is a per-mission battery choice (the M110A1 /
            // M116A1 "Smoke Artillery" projectile prefab), not an ammo codex, so a mission whose
            // batteries offer no smoke (e.g. PA_deliberate_destruction) would disable the smoke slot
            // entirely. Use the bundle's smoke projectile prefab instead - cas_assets ships both, so it
            // is available in every mission. The bundle is the ONLY source on purpose: see
            // FindArtilleryEffectPrefab for why scanning all loaded objects crashes the game.
            List<AmmoTemplate> smoke;
            if (!templates.TryGetValue(MunitionKind.Smoke, out smoke) || smoke.Count == 0)
            {
                GameObject smokePrefab = FindSmokeProjectilePrefab(playerFaction);
                if (smokePrefab != null)
                {
                    GetOrCreate(templates, MunitionKind.Smoke).Add(new AmmoTemplate
                    {
                        Munition = MunitionKind.Smoke,
                        Choice = new BatteryMunitionsChoice
                        {
                            Type = IndirectFireMunitionType.Smoke,
                            Ammo = null,
                            DefaultProjectile = smokePrefab
                        },
                        Source = DescribePrefabSource(smokePrefab) + " smoke shell for " + playerFaction
                    });
                    Log.Info("this mission's batteries carry no smoke shell; falling back to the projectile prefab '" +
                             smokePrefab.name + "' (" + DescribePrefabSource(smokePrefab) + ", " +
                             DescribeFactionFit(smokePrefab.name, playerFaction) + ") so smoke slots still work." +
                             DescribeBrokenVisuals(smokePrefab));
                }
            }

            // Illumination fallback: like smoke, illumination is a projectile prefab on a battery
            // choice, not an ammo codex. Use the loaded flare prefab when the mission has none.
            List<AmmoTemplate> illumination;
            if (!templates.TryGetValue(MunitionKind.Illumination, out illumination) || illumination.Count == 0)
            {
                GameObject flare = FindIlluminationPrefab(playerFaction);
                if (flare != null)
                {
                    GetOrCreate(templates, MunitionKind.Illumination).Add(new AmmoTemplate
                    {
                        Munition = MunitionKind.Illumination,
                        Choice = new BatteryMunitionsChoice
                        {
                            Type = IndirectFireMunitionType.Illumination,
                            Ammo = null,
                            DefaultProjectile = flare
                        },
                        Source = DescribePrefabSource(flare) + " illumination shell for " + playerFaction
                    });
                    Log.Info("this mission's batteries carry no illumination shell; falling back to the projectile " +
                             "prefab '" + flare.name + "' (" + DescribePrefabSource(flare) + ", " +
                             DescribeFactionFit(flare.name, playerFaction) + ") so illumination slots " +
                             "still work." + DescribeBrokenVisuals(flare));
                }
            }

            if (templates.Count == 0)
            {
                Log.Warn("no artillery shell templates found in this mission (no batteries and no loaded ammo codex) - artillery slots cannot be created here.");
            }
            else
            {
                StringBuilder builder = new StringBuilder();
                foreach (KeyValuePair<MunitionKind, List<AmmoTemplate>> pair in templates)
                {
                    if (builder.Length > 0)
                    {
                        builder.Append("; ");
                    }
                    builder.Append(pair.Key).Append(" = ");
                    for (int i = 0; i < pair.Value.Count; i++)
                    {
                        if (i > 0)
                        {
                            builder.Append(" | ");
                        }
                        builder.Append('\'').Append(pair.Value[i].Describe()).Append('\'');
                    }
                }
                Log.Info("shell templates: " + builder);
            }
            return templates;
        }

        /// <summary>Where a fallback prefab came from, for the log ("game asset" / "cas_assets bundle").</summary>
        private static string DescribePrefabSource(GameObject prefab)
        {
            return CasPrewarmer.IsFromOurBundle(prefab)
                ? "from the mod's cas_assets bundle"
                : "from the game's own loaded assets";
        }

        /// <summary>
        /// Reports materials whose shader is missing. The cas_assets bundle is built from an exported copy
        /// of the game's assets, and an exporter cannot recover compiled shaders, so a shader-less material
        /// there is exactly what turns a smoke / flare effect into opaque white blocks - better to say so in
        /// the log than to leave the player guessing. Empty material slots are normal (several particle
        /// systems in these prefabs have none on purpose) and are not counted.
        /// </summary>
        private static string DescribeBrokenVisuals(GameObject prefab)
        {
            Renderer[] renderers = prefab.GetComponentsInChildren<Renderer>(true);
            int broken = 0;
            for (int i = 0; i < renderers.Length; i++)
            {
                Renderer renderer = renderers[i];
                if (renderer == null)
                {
                    continue;
                }
                Material[] materials = renderer.sharedMaterials;
                for (int m = 0; m < materials.Length; m++)
                {
                    if (materials[m] != null && materials[m].shader == null)
                    {
                        broken++;
                    }
                }
            }
            return broken == 0
                ? string.Empty
                : " [WARN] " + broken + " material(s) on '" + prefab.name +
                  "' have no shader - those parts render as untextured blocks; reinstall the cas_assets bundle.";
        }

        /// <summary>
        /// Finds a loaded smoke-artillery projectile prefab for one faction. GHPC's batteries use faction
        /// specific shells as their DefaultProjectile: the US 155 mm white-phosphorus rounds (M110A1 /
        /// M116A1 "Smoke Artillery") for Blue, the Soviet 152 mm ones (2S3 Smoke / 2S1 Artillery smoke)
        /// for Red - and the two look different (different smoke sheet, tint and effect hierarchy).
        ///
        /// Only WHOLE prefabs count (no transform parent): the smoke prefabs are full effect hierarchies
        /// whose children are named things like "WP Smoke Billowing Cloud Big (2)", and picking such a
        /// child spawns a single bare particle system instead of a smoke shell. Between equally suitable
        /// prefabs the copy the game itself owns wins (see CasPrewarmer.IsFromOurBundle).
        /// </summary>
        private static GameObject FindSmokeProjectilePrefab(Faction playerFaction)
        {
            return FindArtilleryEffectPrefab(playerFaction, false);
        }

        /// <summary>The illumination shell (flare) equivalent of <see cref="FindSmokeProjectilePrefab"/>.</summary>
        private static GameObject FindIlluminationPrefab(Faction playerFaction)
        {
            return FindArtilleryEffectPrefab(playerFaction, true);
        }

        /// <summary>
        /// Shared search for the projectile prefabs GHPC's artillery batteries use for smoke and
        /// illumination. Score first (which shell is this at all), then faction fit.
        ///
        /// The candidates come from <see cref="CasPrewarmer.BundlePrefabs"/> and nowhere else. That list is
        /// pinned by the pre-warmer for the whole session, so every object in it is alive by construction.
        /// The obvious alternative - <c>Resources.FindObjectsOfTypeAll&lt;GameObject&gt;()</c> - is a native
        /// access violation waiting to happen: from the SECOND mission of a session onwards that array also
        /// contains wrappers whose native object is already gone (the previous mission's scene, and the
        /// assets the addressables system released with it). <c>candidate == null</c> does NOT filter those
        /// out, and reading a property on one of them kills the process outright - no try/catch can stop a
        /// native access violation. That is exactly how the game died here: the crash stack was
        /// <c>Transform.get_parent</c> &lt;- FindArtilleryEffectPrefab &lt;- HarvestAmmo &lt;- PrepareMission,
        /// on the first map open after a mission restart. The bundle ships both shells, so nothing is lost.
        /// </summary>
        private static GameObject FindArtilleryEffectPrefab(Faction playerFaction, bool illumination)
        {
            List<GameObject> prefabs = CasPrewarmer.BundlePrefabs;
            GameObject best = null;
            int bestScore = 0;
            for (int i = 0; i < prefabs.Count; i++)
            {
                GameObject candidate = prefabs[i];
                if (candidate == null)
                {
                    continue;
                }

                int score = ScoreEffectPrefab(candidate.name, illumination, playerFaction);
                if (score > bestScore)
                {
                    bestScore = score;
                    best = candidate;
                }
            }

            if (best != null && CustomFireSupportMod.VerboseLogging)
            {
                // Both sides are listed: this is how the faction specific shell names a mission happens to
                // have loaded are identified, whichever side the player is on.
                Log.Verbose(DescribeEffectCandidates(illumination, Faction.Blue, best));
                Log.Verbose(DescribeEffectCandidates(illumination, Faction.Red, best));
            }
            return best;
        }

        /// <summary>
        /// Verbose candidate list for the smoke / illumination pick: every bundled prefab that scores
        /// above zero, best first, with its score and whether it was the one chosen. This is what identifies
        /// the faction specific shells on offer (GHPC names its ammunition after the gun that fires it,
        /// e.g. "M110A1 Smoke Artillery" / "2S3 Smoke").
        /// </summary>
        private static string DescribeEffectCandidates(bool illumination, Faction faction, GameObject chosen)
        {
            List<GameObject> prefabs = CasPrewarmer.BundlePrefabs;
            List<string> lines = new List<string>();
            for (int i = 0; i < prefabs.Count; i++)
            {
                GameObject candidate = prefabs[i];
                if (candidate == null)
                {
                    continue;
                }
                int score = ScoreEffectPrefab(candidate.name, illumination, faction);
                if (score <= 0)
                {
                    continue;
                }
                lines.Add("'" + candidate.name + "' (score " + score +
                          (ReferenceEquals(candidate, chosen) ? ", CHOSEN" : string.Empty) + ")");
            }
            lines.Sort();
            return (illumination ? "illumination" : "smoke") + " candidates for " + faction + ": " +
                   (lines.Count == 0 ? "(none in the bundle)" : string.Join("; ", lines.ToArray()));
        }

        /// <summary>
        /// Faction fit of a candidate, delegated to <see cref="FactionShellCatalog"/> (kept in its own file so
        /// the unit tests can verify it without the game assemblies).
        /// </summary>
        internal static int ScoreEffectPrefab(string name, bool illumination, Faction playerFaction)
        {
            return FactionShellCatalog.Score(name, illumination,
                playerFaction == Faction.Red ? AirframeSide.Pact : AirframeSide.Nato);
        }
        /// <summary>Human readable faction fit of a chosen prefab, for the log.</summary>
        private static string DescribeFactionFit(string prefabName, Faction playerFaction)
        {
            int score = ScoreEffectPrefab(prefabName, prefabName.ToLowerInvariant().Contains("illum") ||
                                                     prefabName.ToLowerInvariant().Contains("flare"), playerFaction);
            return score >= 100
                ? "a " + playerFaction + " shell"
                : "not a " + playerFaction + "-specific shell (the bundle ships one for " +
                  (playerFaction == Faction.Red ? "Blue" : "Red") + " only)";
        }

        /// <summary>
        /// Reads a battery's firing parameters. All of them are private serialized fields on GHPC's
        /// ArtilleryBattery, so they are reached through AccessTools (the public API only exposes the
        /// munitions list and the display name).
        /// </summary>
        private static BatteryProfile ReadProfile(ArtilleryBattery battery, Faction side)
        {
            BatteryProfile profile = new BatteryProfile();
            profile.SourceName = string.IsNullOrEmpty(battery.FriendlyName) ? side.ToString() : battery.FriendlyName;
            profile.ImpactDelaySeconds = battery.OnCallDelaySeconds;
            profile.CooldownSeconds = battery.CooldownAmount;
            profile.FromHeadingDegrees = battery.FromHeading;
            profile.SpawnHeightMeters = battery.SpawnHeight;
            profile.SpawnAngleDegrees = battery.SpawnAngle;
            try
            {
                profile.Shots = BatteryShotsRef(battery);
                profile.InterShotSeconds = BatteryInterShotRef(battery);
                profile.DispersionMeters = BatteryDispersionRef(battery);
            }
            catch (System.Exception ex)
            {
                Log.Verbose("could not read battery '" + profile.SourceName + "' internals: " + ex.Message);
            }
            return profile;
        }

        private static readonly AccessTools.FieldRef<ArtilleryBattery, int> BatteryShotsRef =
            AccessTools.FieldRefAccess<ArtilleryBattery, int>("_shots");
        private static readonly AccessTools.FieldRef<ArtilleryBattery, float> BatteryInterShotRef =
            AccessTools.FieldRefAccess<ArtilleryBattery, float>("_interShotDelaySeconds");
        private static readonly AccessTools.FieldRef<ArtilleryBattery, float> BatteryDispersionRef =
            AccessTools.FieldRefAccess<ArtilleryBattery, float>("_randomDispersionRadiusMeters");

        /// <summary>
        /// Reads a battery's firing parameters. All of them are private serialized fields on GHPC's
        /// ArtilleryBattery, so they are reached through AccessTools (the public API only exposes the
        /// munitions list and the display name).
        /// </summary>
        private static void CollectFromBatteries(
            Dictionary<MunitionKind, List<AmmoTemplate>> templates,
            ArtilleryBattery[] batteries,
            Faction batteryFaction,
            Faction playerFaction)
        {
            if (batteries == null)
            {
                return;
            }

            for (int i = 0; i < batteries.Length; i++)
            {
                ArtilleryBattery battery = batteries[i];
                if (battery == null || battery.MunitionsChoices == null)
                {
                    continue;
                }

                // Remember how this side's batteries fire (rounds, dispersion, interval, spawn geometry):
                // a mission without any batteries then still gets that side's behaviour instead of the
                // game's bare defaults. The player's own side wins over the enemy's.
                FactionBatteryProfiles.Remember(batteryFaction.ToString(), ReadProfile(battery, batteryFaction));

                for (int j = 0; j < battery.MunitionsChoices.Length; j++)
                {
                    BatteryMunitionsChoice choice = battery.MunitionsChoices[j];
                    if (choice == null)
                    {
                        continue;
                    }

                    MunitionKind kind = FromGameMunition(choice.Type);
                    List<AmmoTemplate> candidates = GetOrCreate(templates, kind);
                    if (ContainsAmmo(candidates, choice.Ammo) && ContainsProjectile(candidates, choice.DefaultProjectile))
                    {
                        continue;
                    }

                    candidates.Add(new AmmoTemplate
                    {
                        Munition = kind,
                        Choice = new BatteryMunitionsChoice
                        {
                            Type = choice.Type,
                            Ammo = choice.Ammo,
                            DefaultProjectile = choice.DefaultProjectile
                        },
                        SourceBattery = battery,
                        Source = batteryFaction + " battery '" + battery.FriendlyName + "'" +
                                 (batteryFaction == playerFaction ? " (player)" : string.Empty)
                    });
                }
            }
        }

        private static List<AmmoTemplate> GetOrCreate(Dictionary<MunitionKind, List<AmmoTemplate>> templates, MunitionKind kind)
        {
            List<AmmoTemplate> candidates;
            if (!templates.TryGetValue(kind, out candidates))
            {
                candidates = new List<AmmoTemplate>();
                templates.Add(kind, candidates);
            }
            return candidates;
        }

        /// <summary>The AmmoType behind a template (null when it carries only a projectile prefab).</summary>
        private static AmmoType ShellOf(AmmoTemplate template)
        {
            if (template == null || template.Choice == null || template.Choice.Ammo == null)
            {
                return null;
            }
            return template.Choice.Ammo.AmmoType;
        }

        /// <summary>Smaller than an 82 mm mortar is a gun/rocket round, never an artillery shell.</summary>
        private const float MinArtilleryCaliberMillimeters = 82f;

        /// <summary>Heavier than this and it is an aircraft bomb, not artillery ammunition.</summary>
        private const float MaxArtilleryTntKilograms = 60f;

        /// <summary>
        /// True when a loaded ammo codex could plausibly be an ARTILLERY shell. The loaded-asset
        /// fallback sees every ammo the game has in memory - the mod's own 30 mm CAS rounds, ATGMs,
        /// rifle cartridges, hand grenades - and a slot fires the first candidate of its shell type,
        /// so a nonsense round used to become "the HE shell": the log of a Fulda 1989 mission showed
        /// slot 2 firing a 9M14 Malyutka ATGM and then a PGU-13/B 30 mm round, with the mission's real
        /// '155mm HE shell' sitting at the tail of the candidate list.
        ///
        /// Nothing on AmmoType says "this is artillery", so the test is: unguided, big, and not
        /// obviously something else (a missile, a rocket, a grenade, a bomb).
        /// </summary>
        private static bool LooksLikeArtilleryShell(AmmoType ammo, string name)
        {
            if (ammo == null || CasPayloadFactory.IsOurRound(ammo) || ClusterMunitionFactory.IsOurAmmo(ammo))
            {
                return false; // one of the mod's own CAS gun rounds, or a cluster round / HEDP submunition
            }
            if (ammo.Guidance != AmmoType.GuidanceType.Unguided)
            {
                return false; // ATGM / guided missile
            }
            if (ammo.GetFinalCaliber() < MinArtilleryCaliberMillimeters)
            {
                return false; // rifles, autocannons, grenade launchers, 57-80 mm barrage rockets
            }
            if (ammo.TntEquivalentKg > MaxArtilleryTntKilograms)
            {
                return false; // MK82 / FAB-250 class bombs
            }

            string lower = string.IsNullOrEmpty(name) ? string.Empty : name.ToLowerInvariant();
            for (int i = 0; i < NotAShellHints.Length; i++)
            {
                if (lower.Contains(NotAShellHints[i]))
                {
                    return false;
                }
            }
            return true;
        }

        /// <summary>Name fragments of things that are definitely not artillery shells.</summary>
        private static readonly string[] NotAShellHints =
        {
            "grenade", "rocket", "missile", "atgm", "bomb", "mk82", "fab", " mine", "mine ", "rpg", "law",
            "cartridge", "belt", "magazine", "reduced", "malyutka", "refleks", "invar", "bastion", "kobra",
            "konkurs", "fagot", "falanga", "kokon", "tow", "milan"
        };

        /// <summary>Name fragments that mark a good artillery HE shell, best first.</summary>
        private static readonly string[] ArtilleryShellHints =
        {
            "artillery", "he shell", "he-shell", "he-frag", "he frag", "he-fr", "hef-fs", "he-fs", "he-t",
            "of-", "3of", "m107", "m795"
        };

        /// <summary>
        /// Name fragments of armour-piercing rounds: still usable as artillery in a pinch (they are
        /// big and they explode), but never what an HE fire mission should land on the target.
        /// </summary>
        private static readonly string[] ArmorPiercingHints =
        {
            "apfsds", "apds", "apcr", "aphe", "apbc", "hvap", "heat", "hesh", "mpat", "sabot", "hedp",
            "hep-t", "api", "ap-t"
        };

        /// <summary>
        /// Ranks one candidate shell for the artillery fallback: higher is a better fit. The score is
        /// purely name/caliber based (the AmmoType carries no "is artillery" flag), and it is what makes
        /// the choice deterministic - a slot fires the best-ranked candidate of its shell type no matter
        /// what order the asset scan happened to return.
        /// </summary>
        internal static int ArtilleryShellScore(AmmoType ammo, string name, Faction playerFaction)
        {
            if (ammo == null)
            {
                return int.MinValue;
            }

            string lower = string.IsNullOrEmpty(name) ? string.Empty : name.ToLowerInvariant();
            int score = 0;

            for (int i = 0; i < ArtilleryShellHints.Length; i++)
            {
                if (lower.Contains(ArtilleryShellHints[i]))
                {
                    score += 60 - i * 4; // earlier hints are stronger signals
                    break;
                }
            }

            // Big shells are artillery; the player's own side first (155 mm for NATO, 152 mm for the Pact).
            float caliber = ammo.GetFinalCaliber();
            if (caliber >= 100f)
            {
                score += 40;
            }
            if (playerFaction == Faction.Blue)
            {
                if (lower.Contains("155") || lower.Contains("m107") || lower.Contains("m795"))
                {
                    score += 25;
                }
            }
            else if (playerFaction == Faction.Red)
            {
                if (lower.Contains("152") || lower.Contains("of-") || lower.Contains("3of"))
                {
                    score += 25;
                }
            }

            if (ammo.Category == AmmoType.AmmoCategory.Explosive)
            {
                score += 10;
            }
            if (ammo.TntEquivalentKg > 0f)
            {
                score += 5;
            }

            for (int i = 0; i < ArmorPiercingHints.Length; i++)
            {
                if (lower.Contains(ArmorPiercingHints[i]))
                {
                    score -= 50; // a tank's antitank round, not a fire mission
                    break;
                }
            }
            return score;
        }

        private static bool ContainsAmmo(List<AmmoTemplate> candidates, AmmoCodexScriptable ammo)
        {
            if (ammo == null)
            {
                return false;
            }
            for (int i = 0; i < candidates.Count; i++)
            {
                if (candidates[i].Choice != null && ReferenceEquals(candidates[i].Choice.Ammo, ammo))
                {
                    return true;
                }
            }
            return false;
        }

        private static bool ContainsProjectile(List<AmmoTemplate> candidates, GameObject projectile)
        {
            if (projectile == null)
            {
                return false;
            }
            for (int i = 0; i < candidates.Count; i++)
            {
                if (candidates[i].Choice != null && ReferenceEquals(candidates[i].Choice.DefaultProjectile, projectile))
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>CAS templates are collected by CasDonorProvider (mission scene, session cache, loaded assets).</summary>
        internal static List<CasTemplate> HarvestCas(CasSupportManager manager, Faction playerFaction)
        {
            return CasDonorProvider.Collect(manager, playerFaction);
        }

        /// <summary>
        /// The attack types a loadout can deliver, from its own attack entries and from the hardpoints it
        /// mounts. Attack types the mod no longer offers (air-to-air missile, training round) are left
        /// out, so a slot can neither request nor report them - see
        /// <see cref="SlotConfigParsing.IsRemovedAttackType"/>.
        /// </summary>
        internal static AttackKind[] CollectAttackTypes(CASLoadout loadout)
        {
            List<AttackKind> kinds = new List<AttackKind>();

            if (loadout.Attacks != null)
            {
                for (int i = 0; i < loadout.Attacks.Length; i++)
                {
                    CASAttackMeta meta = loadout.Attacks[i];
                    if (meta == null)
                    {
                        continue;
                    }
                    AttackKind kind;
                    if (!TryFromGameAttack(meta.UniqueType, out kind))
                    {
                        continue;
                    }
                    if (!kinds.Contains(kind))
                    {
                        kinds.Add(kind);
                    }
                }
            }

            if (loadout.HardpointPrefabs != null)
            {
                for (int i = 0; i < loadout.HardpointPrefabs.Length; i++)
                {
                    GameObject prefab = loadout.HardpointPrefabs[i];
                    if (prefab == null)
                    {
                        continue;
                    }
                    CASHardpoint hardpoint = prefab.GetComponentInChildren<CASHardpoint>(true);
                    if (hardpoint == null)
                    {
                        continue;
                    }
                    AttackKind kind;
                    if (!TryFromGameAttack(hardpoint.Type, out kind))
                    {
                        continue;
                    }
                    if (!kinds.Contains(kind))
                    {
                        kinds.Add(kind);
                    }
                }
            }

            return kinds.ToArray();
        }

        /// <summary>
        /// Name-based shell classification. GHPC's AmmoType has no smoke / illumination category, so the
        /// asset name is the only signal available on the loaded-codex fallback path.
        /// </summary>
        internal static MunitionKind ClassifyAmmoName(string ammoName)
        {
            if (string.IsNullOrEmpty(ammoName))
            {
                return MunitionKind.AntiPersonnel;
            }

            string lower = ammoName.ToLowerInvariant();
            if (lower.Contains("illum") || lower.Contains("flare") || lower.Contains("star shell") || lower.Contains("star-shell"))
            {
                return MunitionKind.Illumination;
            }
            if (lower.Contains("smoke") || lower.Contains("smk") || lower.Contains("white phos") || lower.Contains("phosphor"))
            {
                return MunitionKind.Smoke;
            }
            if (lower.Contains("heat") || lower.Contains("apfsds") || lower.Contains("sabot") || lower.Contains(" atgm") ||
                lower.Contains("missile") || lower.Contains("anti-tank") || lower.Contains("antitank") ||
                lower.Contains("hedp") || lower.Contains("heat-mp"))
            {
                return MunitionKind.AntiArmor;
            }
            return MunitionKind.AntiPersonnel;
        }

        internal static string DescribeAttacks(AttackKind[] attacks)
        {
            if (attacks == null || attacks.Length == 0)
            {
                return "none";
            }

            StringBuilder builder = new StringBuilder();
            for (int i = 0; i < attacks.Length; i++)
            {
                if (i > 0)
                {
                    builder.Append('+');
                }
                builder.Append(SlotConfigParsing.ToConfigName(attacks[i]));
            }
            return builder.ToString();
        }

        // ------------------------------------------------------------------
        // Mod enums <-> game enums
        // ------------------------------------------------------------------

        internal static IndirectFireMunitionType ToGameMunition(MunitionKind kind)
        {
            switch (kind)
            {
                case MunitionKind.AntiArmor:
                    return IndirectFireMunitionType.AntiArmor;
                case MunitionKind.Smoke:
                    return IndirectFireMunitionType.Smoke;
                case MunitionKind.Illumination:
                    return IndirectFireMunitionType.Illumination;
                default:
                    return IndirectFireMunitionType.AntiPersonnel;
            }
        }

        internal static MunitionKind FromGameMunition(IndirectFireMunitionType type)
        {
            switch (type)
            {
                case IndirectFireMunitionType.AntiArmor:
                    return MunitionKind.AntiArmor;
                case IndirectFireMunitionType.Smoke:
                    return MunitionKind.Smoke;
                case IndirectFireMunitionType.Illumination:
                    return MunitionKind.Illumination;
                default:
                    return MunitionKind.AntiPersonnel;
            }
        }

        internal static IndirectFireWeaponType ToGameWeapon(WeaponKind kind)
        {
            switch (kind)
            {
                case WeaponKind.Guns:
                    return IndirectFireWeaponType.Guns;
                case WeaponKind.Rockets:
                    return IndirectFireWeaponType.Rockets;
                case WeaponKind.Mortars:
                    return IndirectFireWeaponType.Mortars;
                default:
                    return IndirectFireWeaponType.Any;
            }
        }

        /// <summary>
        /// Maps a game attack type onto the config enum. Returns false for a type the mod no longer
        /// offers - the air-to-air missile and the training / "Inert" entry - so callers MUST skip those
        /// instead of substituting something else: a mission loadout the game ships may still declare
        /// them, and mounting or describing one is exactly what the player asked the mod to stop doing.
        /// </summary>
        internal static bool TryFromGameAttack(CASAttackType type, out AttackKind kind)
        {
            switch (type)
            {
                case CASAttackType.Bombs:
                    kind = AttackKind.Bombs;
                    return true;
                case CASAttackType.Rockets:
                    kind = AttackKind.Rockets;
                    return true;
                case CASAttackType.AirToGroundMissile:
                    kind = AttackKind.AirToGroundMissile;
                    return true;
                case CASAttackType.GunRun:
                    kind = AttackKind.GunRun;
                    return true;
                default:
                    // AirToAirMissile, Inert, and anything a future game build adds.
                    kind = AttackKind.Bombs;
                    return false;
            }
        }

        internal static CASAttackType ToGameAttack(AttackKind kind)
        {
            switch (kind)
            {
                case AttackKind.Rockets:
                    return CASAttackType.Rockets;
                case AttackKind.AirToGroundMissile:
                    return CASAttackType.AirToGroundMissile;
                case AttackKind.GunRun:
                    return CASAttackType.GunRun;
                default:
                    return CASAttackType.Bombs;
            }
        }

        internal static Faction ToFaction(AirframeSide side)
        {
            switch (side)
            {
                case AirframeSide.Nato:
                    return Faction.Blue;
                case AirframeSide.Pact:
                    return Faction.Red;
                default:
                    return Faction.Neutral;
            }
        }

        internal static MapControlFlag ToMapControlFlag(SlotKind kind)
        {
            switch (kind)
            {
                case SlotKind.ArtillerySmoke:
                    return MapControlFlag.ArtillerySmoke;
                case SlotKind.ArtilleryIllumination:
                    return MapControlFlag.ArtilleryIllumination;
                case SlotKind.CasFixedWing:
                    return MapControlFlag.CASSupport;
                default:
                    return MapControlFlag.Artillery;
            }
        }
    }
}
