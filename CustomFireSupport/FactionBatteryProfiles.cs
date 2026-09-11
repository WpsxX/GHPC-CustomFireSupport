using System.Collections.Generic;

namespace CustomFireSupport
{
    /// <summary>
    /// The firing parameters of one GHPC artillery battery, mirrored from its serialized fields.
    ///
    /// The two sides' batteries differ here (an M109 battery and a 2S3 battery have their own spawn
    /// height, approach angle, heading, round count, dispersion, interval and cooldown), which is why the
    /// same smoke / illumination projectile prefab still looks and behaves differently per faction.
    /// </summary>
    internal sealed class BatteryProfile
    {
        /// <summary>Battery name for the log (e.g. "2S3", "M109 Smoke WP").</summary>
        internal string SourceName = "(unknown)";

        internal int Shots = 12;
        internal float InterShotSeconds = 0.7f;
        internal float DispersionMeters = 100f;
        internal float ImpactDelaySeconds = 120f;
        internal float CooldownSeconds = 20f;
        internal float SpawnHeightMeters = 300f;
        internal float SpawnAngleDegrees = 60f;
        internal float FromHeadingDegrees = 90f;

        internal string Describe()
        {
            return Shots + " rounds, " + DispersionMeters.ToString("0") + " m dispersion, " +
                   InterShotSeconds.ToString("0.##") + " s interval, " + SpawnHeightMeters.ToString("0") +
                   " m @ " + SpawnAngleDegrees.ToString("0") + " deg, heading " +
                   FromHeadingDegrees.ToString("0") + " (battery '" + SourceName + "')";
        }
    }

    /// <summary>
    /// Remembers the last battery profile seen for each side, so a mission whose artillery the mod has to
    /// create itself (no batteries at all, e.g. a custom terrain map) still fires the way that side's
    /// batteries do - a Red player gets 2S3-style missions and a Blue player M109-style ones - instead of
    /// the game's bare code defaults. Deliberately session-wide, not per mission.
    /// </summary>
    internal static class FactionBatteryProfiles
    {
        private static readonly Dictionary<string, BatteryProfile> _bySide = new Dictionary<string, BatteryProfile>();

        internal static void Remember(string side, BatteryProfile profile)
        {
            if (string.IsNullOrEmpty(side) || profile == null)
            {
                return;
            }
            _bySide[side] = profile;
        }

        internal static BatteryProfile Recall(string side)
        {
            BatteryProfile profile;
            if (!string.IsNullOrEmpty(side) && _bySide.TryGetValue(side, out profile))
            {
                return profile;
            }
            return null;
        }

        internal static int Count
        {
            get { return _bySide.Count; }
        }
    }
}
