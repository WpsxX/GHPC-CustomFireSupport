using System.Text;

namespace CustomFireSupport
{
    /// <summary>
    /// Global (non per-slot) settings, mapped 1:1 onto the [CustomFireSupport] section of
    /// UserData/MelonPreferences.cfg.
    /// </summary>
    internal sealed class GlobalConfig
    {
        /// <summary>Master switch. false = the mod never touches anything.</summary>
        public bool Enabled = true;

        /// <summary>
        /// true = vanilla artillery / CAS buttons are suppressed so only the custom slots are shown.
        /// The vanilla battery / airframe arrays are never modified, so scripted (planned, reaction,
        /// suppression) fire missions keep working exactly as before.
        /// </summary>
        public bool HideVanillaFireSupport = true;

        /// <summary>
        /// true = illumination slots only appear at night (vanilla behaviour). false (default) = the
        /// slot is always available, whatever the time of day.
        /// </summary>
        public bool IlluminationOnlyAtNight = true;

        /// <summary>
        /// true (default) = smoke slots are hidden at night (they do nothing useful in the dark);
        /// false = smoke is available around the clock. The mirror of IlluminationOnlyAtNight.
        /// </summary>
        public bool SmokeOnlyDuringDay = true;

        /// <summary>Print template discovery / per-slot resolution details to the log.</summary>
        public bool VerboseLogging = false;

        /// <summary>
        /// Only used when a mission scene contains no CasSupportManager at all: the mod creates one
        /// and places the aircraft deploy point this many metres from the player's start position.
        /// </summary>
        public float CasDeployDistanceMeters = 8000f;

        /// <summary>Bearing (degrees, 0 = +Z / north) of the created deploy point, relative to the player's start position.</summary>
        public float CasDeployBearingDegrees = 180f;

        /// <summary>
        /// Comma-separated Addressables keys to pre-load once per session, before/while a mission is
        /// being set up, so CAS assets that only exist in the game's addressable catalog are in memory
        /// before the mission's own scene decides what CAS it offers. Empty = disabled.
        /// Loaded assets are pinned for the session and feed both the CAS donor scan and the hardpoint
        /// weapon library. Fixed-wing CAS airframes and gun hardpoints are scene-internal assets and
        /// have no addressable key, so they cannot be pre-warmed this way.
        /// </summary>
        public string CasPrewarmKeys = string.Empty;
    }

    /// <summary>
    /// One custom fire-support slot, mapped 1:1 onto a [CustomFireSupport.SlotN] section.
    /// </summary>
    internal sealed class SlotConfig
    {
        /// <summary>1-based slot number, used in logs only.</summary>
        public int Index;

        public bool Enabled;

        public SlotKind Kind = SlotKind.Artillery;

        public string DisplayName = string.Empty;

        /// <summary>Number of calls (sorties for CAS) this slot provides. -1 = unlimited.</summary>
        public int Missions = 1;

        /// <summary>Rounds fired per call (artillery only). &lt;= 0 = the vanilla battery's own count.</summary>
        public int RoundsPerCall = -1;

        public MunitionKind Munition = MunitionKind.AntiPersonnel;

        public WeaponKind Weapon = WeaponKind.Any;

        /// <summary>
        /// Time-to-target scale (CheatMode semantics): 1 = the vanilla first-round delay, &lt; 1 cancels
        /// the first-round delay and scales the spread, &lt;= 0 = the whole volley lands at once.
        /// </summary>
        public float ImpactDelaySeconds = 1f;

        /// <summary>Interval scale between rounds: 1 = vanilla, 0.5 = half, &lt;= 0 = all on one frame.</summary>
        public float InterShotDelaySeconds = 1f;

        /// <summary>Dispersion scale: 1 = vanilla radius, 0.5 = half, &lt;= 0 = every round on the same spot.</summary>
        public float DispersionMeters = 1f;

        /// <summary>
        /// CAS accuracy: the radius of the impact circle around the locked target, divided by five
        /// (SlotConfigParsing.CasAccuracyRadius: radius = 10 x value), so 0 = radius 0 = every round is flown into the
        /// target's own centre (a guaranteed hit), 0.5 = 5 m, 1 = 10 m, &gt; 1 = wider, and the mod's
        /// other "off" spelling -1 behaves as 0.
        ///
        /// Gun runs and rocket pods are flown onto that point by the mod's own impact resolver, so the
        /// radius is what actually happens. Bombs and missiles are still flown by the game, where the
        /// value keeps its historical meaning at fire time: a multiplier of the hardpoint's own launch
        /// deviation (1 = natural, 0.5 = half, 0 = none).
        /// </summary>
        public float CasAccuracy = 1f;

        /// <summary>
        /// Artillery: cooldown scale (1 = vanilla, 0.5 = half, &lt;= 0 = none).
        /// CAS: the actual sortie recharge in seconds (the game's native 120 s is removed), &lt;= 0 = none.
        /// </summary>
        public float CooldownSeconds = 1f;

        /// <summary>
        /// Optional case-insensitive substring that the shell's AmmoType.Name must contain. Empty =
        /// use the first template of the requested shell type.
        /// </summary>
        public string AmmoNameFilter = string.Empty;

        public FlyoverKind Flyover = FlyoverKind.SinglePass;

        /// <summary>False when CasFlyover was left empty, i.e. "let the mod pick from the airframe type".</summary>
        public bool FlyoverWasExplicit;

        /// <summary>Attack types to keep; an empty array means "every type the template supports".</summary>
        public AttackKind[] AttackTypes = new AttackKind[0];


        public bool IsCas
        {
            get { return SlotConfigParsing.IsCasSlot(Kind); }
        }

        /// <summary>One-line summary for the startup log.</summary>
        public string Describe()
        {
            StringBuilder builder = new StringBuilder();
            builder.Append("slot ").Append(Index).Append(": ");
            builder.Append(SlotConfigParsing.ToConfigName(Kind));
            builder.Append(" \"").Append(string.IsNullOrEmpty(DisplayName) ? "(no name)" : DisplayName).Append("\"");
            builder.Append(" missions=").Append(Missions < 0 ? "infinite" : Missions.ToString());

            if (IsCas)
            {
                builder.Append(" flyover=").Append(SlotConfigParsing.ToConfigName(Flyover));
                builder.Append(" attacks=").Append(DescribeAttacks());
                builder.Append(" cooldown=").Append(DescribeScale(CooldownSeconds, "x"));
                builder.Append(" airframe=auto(").Append(FlyoverWasExplicit ? "cfg flyover" : "auto flyover").Append(')');
            }
            else
            {
                builder.Append(" rounds=").Append(RoundsPerCall < 1 ? "vanilla" : RoundsPerCall.ToString());
                builder.Append(" munition=").Append(SlotConfigParsing.ToConfigName(Munition));
                builder.Append(" weapon=").Append(SlotConfigParsing.ToConfigName(Weapon));
                builder.Append(" impact=").Append(DescribeScale(ImpactDelaySeconds, "x"));
                builder.Append(" interval=").Append(DescribeScale(InterShotDelaySeconds, "x"));
                builder.Append(" dispersion=").Append(DescribeScale(DispersionMeters, "x"));
                builder.Append(" cooldown=").Append(DescribeScale(CooldownSeconds, "x"));
                if (!string.IsNullOrEmpty(AmmoNameFilter))
                {
                    builder.Append(" ammo~").Append(AmmoNameFilter);
                }
            }
            return builder.ToString();
        }

        private static string DescribeScale(float scale, string unit)
        {
            if (scale <= 0f)
            {
                return unit == "x" ? "0 (off)" : "none";
            }
            return scale.ToString("0.##") + unit;
        }

        private string DescribeAttacks()
        {
            if (AttackTypes == null || AttackTypes.Length == 0)
            {
                return "Any";
            }

            StringBuilder builder = new StringBuilder();
            for (int i = 0; i < AttackTypes.Length; i++)
            {
                if (i > 0)
                {
                    builder.Append('+');
                }
                builder.Append(SlotConfigParsing.ToConfigName(AttackTypes[i]));
            }
            return builder.ToString();
        }
    }
}
