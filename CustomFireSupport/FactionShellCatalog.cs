namespace CustomFireSupport
{
    /// <summary>
    /// Which projectile prefab GHPC's artillery batteries use as their smoke / illumination shell, per side.
    ///
    /// The two sides do not share effects: Blue (US) fires the 155 mm white-phosphorus rounds
    /// "M110A1 Smoke Artillery" / "M116A1 Smoke Artillery" and the "IlluminationFlare" flare, while Red
    /// (Soviet) fires the 152 mm "2S3 Smoke" / "2S1 Artillery smoke" rounds and the "2S3 Illumination" /
    /// "2S1 Artillery Illumination" flares. They look different (different smoke sheets, tints and effect
    /// hierarchies), so the mod picks by side instead of taking whatever is loaded first.
    ///
    /// The scoring lives here - in a file the unit tests also compile - so the choice can be verified
    /// without the game assemblies.
    /// </summary>
    internal static class FactionShellCatalog
    {
        private static readonly string[] NatoHints = { "m109 smoke", "m109 illum", "m110a1", "m116a1", "m110", "m116", "155mm", "155 mm", "nato", "illuminationflare" };

        private static readonly string[] PactHints = { "2s1 smoke", "2s3 smoke", "2s1 artillery", "2s3 illum", "2s1 illum", "2s1", "2s3", "152mm", "152 mm", "d-4", "soviet", "pact" };

        /// <summary>
        /// How well a prefab name fits "the smoke / illumination shell of this side": 0 means unusable and
        /// the highest score wins. Markers of the other side count against a candidate, so a Soviet player
        /// gets the Soviet shell whenever the mission has one loaded.
        /// </summary>
        internal static int Score(string name, bool illumination, AirframeSide side)
        {
            if (string.IsNullOrEmpty(name))
            {
                return 0;
            }

            string lower = name.ToLowerInvariant();

            if (illumination)
            {
                if (!lower.Contains("illum") && !lower.Contains("flare"))
                {
                    return 0;
                }
                if (lower.Contains("flare trigger") || lower.Contains("road flare") || lower.Contains("bloom"))
                {
                    return 0;
                }
            }
            else
            {
                if (!lower.Contains("smoke"))
                {
                    return 0;
                }
                // Vehicle damage / VFX smoke is not a fire-mission shell.
                if (lower.Contains("hatch") || lower.Contains("turret") || lower.Contains("side") ||
                    lower.Contains("plume") || lower.Contains("smoulder") || lower.Contains("skid") ||
                    lower.Contains("car") || lower.Contains("hole") || lower.Contains("grenade"))
                {
                    return 0;
                }
            }

            bool shellWord = lower.Contains("artillery") || lower.Contains("arty") || lower.Contains("shell");
            bool knownShell = illumination
                ? lower.Contains("illuminationflare") || lower.Contains("illumination art") ||
                  lower.Contains("artillery illumination") || lower.Contains("arty illumination")
                : lower.Contains("smoke art") || lower.Contains("artillery smoke") || lower.Contains("arty smoke") ||
                  lower.Contains("wp smoke") || lower.Contains("smoke wp");

            bool nato = ContainsAny(lower, NatoHints);
            bool pact = ContainsAny(lower, PactHints);
            if (!shellWord && !knownShell && !nato && !pact)
            {
                return 0; // not a fire-mission smoke / illumination shell at all
            }

            int score = 60;
            if (shellWord)
            {
                score += 40;
            }
            if (knownShell)
            {
                score += 60;
            }
            if (!illumination && (lower.Contains("wp") || lower.Contains("white") || lower.Contains("phosphor")))
            {
                score += 20;
            }

            if (nato && !pact)
            {
                score += side == AirframeSide.Nato ? 50 : -80;
            }
            else if (pact && !nato)
            {
                score += side == AirframeSide.Pact ? 50 : -80;
            }
            return score;
        }

        private static bool ContainsAny(string haystack, string[] needles)
        {
            for (int i = 0; i < needles.Length; i++)
            {
                if (haystack.Contains(needles[i]))
                {
                    return true;
                }
            }
            return false;
        }
    }
}
