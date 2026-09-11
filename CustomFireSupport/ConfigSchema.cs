using System;
using System.Collections.Generic;
using MelonLoader;

namespace CustomFireSupport
{
    /// <summary>
    /// Config model for the single flat [CustomFireSupport] section of MelonPreferences.cfg.
    ///
    /// IMPORTANT: this mod deliberately does **not** create MelonPreferences entries. MelonLoader's
    /// entry layer was observed to (a) throw "Calling CreateEntry ... when it Already Exists" and
    /// (b) rewrite values on its own load/save cycle (30 -> 0.0, 0.8 -> 0.3,
    /// "Bombs,Rockets,..." -> "Rockets"), which corrupted the user's file. Instead:
    ///
    ///   * the section is created once (CfgFile.EnsureSection) if it is missing,
    ///   * values are always parsed from the file text by CfgSectionParser,
    ///   * MelonPreferences.Load() is called only so MelonLoader's in-memory document also contains
    ///     our section and therefore keeps it (verbatim) when it rewrites the file on shutdown.
    /// </summary>
    internal static class ConfigSchema
    {
        internal const int SlotCount = 6;

        private static Dictionary<string, string> _values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Called once from the MelonMod: creates the section when missing, then reads it.</summary>
        internal static void Initialize()
        {
            CfgFile.EnsureSection(DefaultConfigSection.Build());
            Refresh();
        }

        /// <summary>Re-reads the cfg from disk (once at startup, then at every mission start). Never throws, never writes.</summary>
        internal static void Refresh()
        {
            // Keep MelonLoader's document in sync so its shutdown save neither reverts edits made
            // while the game is running nor drops our section.
            try
            {
                MelonPreferences.Load();
            }
            catch (Exception ex)
            {
                Log.Verbose("MelonPreferences.Load() failed: " + ex.Message);
            }

            _values = CfgFile.ReadCustomSection();
            if (_values.Count == 0)
            {
                Log.Warn("no [CustomFireSupport] keys found in " + CfgFile.Path + " - code defaults are used.");
            }
            else
            {
                Log.Verbose("config loaded from " + CfgFile.Path + " (" + _values.Count + " keys).");
            }
        }

        // ------------------------------------------------------------------
        // Global settings
        // ------------------------------------------------------------------

        internal static GlobalConfig ReadGlobal()
        {
            GlobalConfig config = new GlobalConfig();
            config.Enabled = GetBool("Enabled", true);
            config.HideVanillaFireSupport = GetBool("HideVanillaFireSupport", true);
            config.IlluminationOnlyAtNight = GetBool("IlluminationOnlyAtNight", true);
            // The mirror rule: smoke screens are pointless in the dark, so smoke slots are hidden at
            // night unless this is turned off.
            config.SmokeOnlyDuringDay = GetBool("SmokeOnlyDuringDay", true);
            config.VerboseLogging = GetBool("VerboseLogging", false);
            config.CasDeployDistanceMeters = SlotConfigParsing.ClampRange(GetFloat("CasDeployDistanceMeters", 8000f), 100f, 60000f);
            config.CasDeployBearingDegrees = SlotConfigParsing.ClampHeading(GetFloat("CasDeployBearingDegrees", 180f));
            // Default "auto": pre-loading the CAS family (incl. their hardpoints and the ammo those
            // prefabs reference) is what makes rocket / bomb CAS mountable even when the mission scene
            // itself carries no such armed CAS. Empty = the feature stays off.
            config.CasPrewarmKeys = GetString("CasPrewarmKeys", "auto");
            return config;
        }

        // ------------------------------------------------------------------
        // Slots
        // ------------------------------------------------------------------

        /// <summary>
        /// Reads all five slots. Disabled slots are returned too (Enabled = false) so the caller can
        /// log the whole picture; invalid enum values disable the slot with a warning instead of
        /// silently building something different from what the user typed.
        /// </summary>
        internal static SlotConfig[] ReadSlots()
        {
            SlotConfig[] slots = new SlotConfig[SlotCount];
            for (int i = 0; i < SlotCount; i++)
            {
                slots[i] = ReadSlot(i + 1);
            }
            return slots;
        }

        private static SlotConfig ReadSlot(int index)
        {
            string p = "Slot" + index + "_";
            SlotConfig slot = new SlotConfig();
            slot.Index = index;
            // Every slot is on by default: 1 = artillery, 2 = smoke, 3 = illumination, 4/5/6 = CAS
            // (gun run / rockets / bombs). Turn one off with SlotN_Enabled = false.
            slot.Enabled = GetBool(p + "Enabled", true);
            if (!slot.Enabled)
            {
                return slot;
            }

            SlotKind kind;
            string typeRaw = GetString(p + "Type", DefaultTypeFor(index));
            if (!SlotConfigParsing.TryParseSlotKind(typeRaw, out kind))
            {
                if (SlotConfigParsing.IsRemovedHelicopterType(typeRaw))
                {
                    Log.Warn("slot " + index + ": Type '" + typeRaw + "' was the helicopter CAS slot, which was " +
                             "removed - the mod flies fixed-wing CAS only now; slot disabled. Use " +
                             "Type = \"CASSupport\" for an aircraft sortie.");
                }
                else
                {
                    Log.Warn("slot " + index + ": unknown Type '" + typeRaw + "' (valid: Artillery, ArtillerySmoke, " +
                             "ArtilleryIllumination, CASSupport) - slot disabled.");
                }
                slot.Enabled = false;
                return slot;
            }
            slot.Kind = kind;

            slot.DisplayName = GetString(p + "DisplayName", string.Empty);
            if (string.IsNullOrEmpty(slot.DisplayName))
            {
                slot.DisplayName = DefaultDisplayName(index);
            }

            slot.Missions = SlotConfigParsing.ClampMissions(GetInt(p + "Missions", slot.IsCas ? 2 : 3));
            slot.RoundsPerCall = SlotConfigParsing.ClampRoundsPerCall(GetInt(p + "RoundsPerCall", -1));

            // Shell type (弹种): an explicit SlotN_Munition key wins; otherwise it follows the slot
            // type (ArtillerySmoke -> Smoke, ArtilleryIllumination -> Illumination, else AntiPersonnel).
            // A value that mismatches the button type is still honoured, but flagged as a likely mistake.
            slot.Munition = SlotConfigParsing.DefaultMunitionFor(kind);
            string munitionRaw = GetString(p + "Munition", string.Empty).Trim();
            if (!string.IsNullOrEmpty(munitionRaw))
            {
                MunitionKind munition;
                if (SlotConfigParsing.TryParseMunition(munitionRaw, out munition))
                {
                    slot.Munition = munition;
                }
                else
                {
                    Log.Warn("slot " + index + ": unknown Munition '" + munitionRaw +
                             "' (valid: AntiPersonnel, AntiArmor, Smoke, Illumination) - using " +
                             SlotConfigParsing.ToConfigName(slot.Munition) + ".");
                }
            }
            if (!SlotConfigParsing.MunitionMatchesSlotKind(kind, slot.Munition))
            {
                Log.Warn("slot " + index + ": Munition " + SlotConfigParsing.ToConfigName(slot.Munition) +
                         " does not match slot Type " + SlotConfigParsing.ToConfigName(kind) +
                         "; honoured anyway, but the map button icon/flag may look odd.");
            }

            WeaponKind weapon;
            string weaponRaw = GetString(p + "Weapon", "Any");
            if (!SlotConfigParsing.TryParseWeapon(weaponRaw, out weapon))
            {
                Log.Warn("slot " + index + ": unknown Weapon '" + weaponRaw + "' - using Any.");
            }
            slot.Weapon = weapon;

            slot.ImpactDelaySeconds = SlotConfigParsing.ClampScale(GetFloat(p + "ImpactDelaySeconds", 1f));
            slot.InterShotDelaySeconds = SlotConfigParsing.ClampScale(GetFloat(p + "InterShotDelaySeconds", 1f));
            slot.DispersionMeters = SlotConfigParsing.ClampScale(GetFloat(p + "DispersionMeters", 1f));
            slot.CasAccuracy = SlotConfigParsing.ClampScale(GetFloat(p + "CasAccuracy", 1f));
            slot.CooldownSeconds = SlotConfigParsing.ClampScale(GetFloat(p + "CooldownSeconds", 1f));
            slot.AmmoNameFilter = GetString(p + "AmmoName", string.Empty).Trim();

            FlyoverKind flyover;
            string flyoverRaw = GetString(p + "CasFlyover", string.Empty).Trim();
            if (string.IsNullOrEmpty(flyoverRaw))
            {
                // Empty = auto: CasAirframeCatalog supplies the profile of the chosen airframe.
                slot.Flyover = FlyoverKind.SinglePass;
                slot.FlyoverWasExplicit = false;
            }
            else if (SlotConfigParsing.TryParseFlyover(flyoverRaw, out flyover))
            {
                slot.Flyover = flyover;
                slot.FlyoverWasExplicit = true;
            }
            else
            {
                Log.Warn("slot " + index + ": unknown CasFlyover '" + flyoverRaw + "' - using auto.");
                slot.Flyover = FlyoverKind.SinglePass;
                slot.FlyoverWasExplicit = false;
            }

            string attackProblems;
            slot.AttackTypes = SlotConfigParsing.ParseAttackList(GetString(p + "CasAttackTypes", DefaultAttacks(index)), out attackProblems);
            if (!string.IsNullOrEmpty(attackProblems))
            {
                Log.Warn("slot " + index + ": CasAttackTypes " + attackProblems + ".");
            }

            return slot;
        }

        /// <summary>
        /// The shipped layout lives in <see cref="DefaultConfigSection"/> (which also generates the
        /// documented default section), so a key that is missing from the cfg falls back to exactly the
        /// value that documentation shows. These three helpers only adapt its strings to parsed types.
        /// </summary>
        private static string DefaultTypeFor(int slotNumber)
        {
            return DefaultConfigSection.TypeFor(slotNumber);
        }

        private static string DefaultDisplayName(int slotNumber)
        {
            return DefaultConfigSection.NameFor(slotNumber);
        }

        private static string DefaultAttacks(int slotNumber)
        {
            return DefaultConfigSection.AttacksFor(slotNumber);
        }

        // ------------------------------------------------------------------
        // Typed accessors over the parsed section
        // ------------------------------------------------------------------

        private static bool GetBool(string key, bool fallback)
        {
            return CfgSectionParser.GetBool(_values, key, fallback);
        }

        private static int GetInt(string key, int fallback)
        {
            return CfgSectionParser.GetInt(_values, key, fallback);
        }

        private static float GetFloat(string key, float fallback)
        {
            return CfgSectionParser.GetFloat(_values, key, fallback);
        }

        private static string GetString(string key, string fallback)
        {
            return CfgSectionParser.GetString(_values, key, fallback) ?? fallback;
        }
    }
}
