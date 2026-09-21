using System;
using System.Collections.Generic;
using System.Text;
using GHPC.Weapons;
using HarmonyLib;
using UnityEngine;

namespace CustomFireSupport
{
    /// <summary>
    /// "The jet is swallowed by the armour" - the diagnostic that answers WHY, from the player's own
    /// mission instead of from a guess.
    ///
    /// THE QUESTION. A HEDP submunition is a 50 mm dual-purpose bomblet with a 70 mm RHAe shaped charge
    /// (<see cref="ClusterMunitionFactory.HedpRhaPenetration"/>). When one lands on a tank the player
    /// sometimes sees the 20 mm explosion and then nothing at all: no penetration, no behind-armour
    /// effect. That is either
    ///
    ///   * CORRECT - the plate it hit is thicker than the jet (a T-72M's hull side is 80 mm RHAe CE, a
    ///     T-55A's is 80, the fuel tanks and cupolas are 100), or
    ///   * WRONG - the game's own HEAT rules ate the jet on the way in (see <see cref="LiveRound"/>
    ///     CurrentPenRating: the jet is degraded by 75 mm RHAe per metre of travel once it is active and
    ///     the game floors that at 50 mm; a ricochet sets _heatRicochet, after which the jet can never
    ///     form; slat armour destroys or detonates a HEAT round that does not set IgnoreSlat/Tandem).
    ///
    /// WHICH ONE IT IS CANNOT BE SEEN FROM THE OUTSIDE, so this file stops trying to guess: the game
    /// already records every round's own story (<see cref="LiveRound.ShotStory"/>, filled by
    /// LiveRound.AddEvent -> "Hit hull side", "Stopped by hull side", "Penetrated hull front deck",
    /// "Destroyed by slat armor", "Stopped by terrain"). This patch prints that story for our
    /// submunitions, plus the ammo's penetration and the round's CURRENT penetration rating - the number
    /// the game actually judged the plate with, which is where the 75 mm/m degradation shows up.
    ///
    /// It is diagnosis only: it writes log lines and counts, it changes no round behaviour, and it is
    /// gated behind the existing <c>VerboseLogging</c> config key (no new key, no schema change). Turn
    /// VerboseLogging off and it is a single boolean test per detonation.
    /// </summary>
    internal static class ClusterDiagnostics
    {
        /// <summary>
        /// The last story logged for each shot. Keyed by <c>LiveRound.ID</c> - the marshaller's own
        /// per-SHOT counter, NOT the instance id: the rounds are pooled and one instance flies as a
        /// submunition many times in a mission, so an instance-keyed guard would report the first shot and
        /// silently drop every later one. Keying on the shot id lets a round that detonates twice (the
        /// ricochet branches call Detonate and keep flying) report its story once more when that story has
        /// actually grown.
        /// </summary>
        private static readonly Dictionary<int, string> _lastStory = new Dictionary<int, string>();

        private static int _submunitions;
        private static int _hitVehicle;
        private static int _penetrated;
        private static int _stopped;
        private static int _ricochetedOrSlat;
        private static int _terrain;
        private static readonly List<string> _penetratedZones = new List<string>();
        private static readonly List<string> _stoppedZones = new List<string>();
        private static bool _warnedNoStory;

        /// <summary>How often the running summary is printed, in submunitions.</summary>
        private const int SummaryEvery = 24;

        /// <summary>
        /// Records one submunition's story. Called from the <see cref="LiveRound.Detonate"/> postfix: a
        /// Detonate is where a submunition's story ENDS in every case that matters here - a jet that failed
        /// to penetrate detonates right there, and a jet that DID penetrate keeps flying until it hits the
        /// terrain (or times out) and then detonates, with the penetration still in the same story.
        /// </summary>
        internal static void Note(LiveRound round)
        {
            if (!CustomFireSupportMod.VerboseLogging || round == null || round.IsSpall)
            {
                return;
            }
            if (!ClusterMunitionFactory.IsOurSubmunition(round.Info))
            {
                return;
            }

            try
            {
                LiveRound.ShotStory story = round.Story;
                string events = story != null ? story.FinalString : null;
                if (string.IsNullOrEmpty(events))
                {
                    events = "(the game recorded no story events for this round)";
                    if (!_warnedNoStory)
                    {
                        _warnedNoStory = true;
                        Log.Warn("cluster diagnostics: the game's per-round ShotStory is empty - this build " +
                                 "of GHPC does not fill it, so the zone/outcome of a hit cannot be read from " +
                                 "the log. Nothing else is affected.");
                    }
                }

                // One line per shot, and one more only if that shot's story grew after a further Detonate.
                string previous;
                if (_lastStory.TryGetValue(round.ID, out previous) && previous == events)
                {
                    return;
                }
                if (_lastStory.Count > 8192)
                {
                    _lastStory.Clear();
                }
                _lastStory[round.ID] = events;

                _submunitions++;
                string outcome = Classify(events);
                int penShots = story != null ? story.PenShots.Count : 0;
                int effective = story != null ? story.EffectiveRhaShots.Count : 0;
                AmmoType info = round.Info;

                Log.Info("cluster diagnostics: " + outcome + " | " + info.Name +
                         ": line " + info.RhaPenetration.ToString("0") + " mm RHAe" +
                         (round.RhaPenetrationOverride > 0f
                             ? " (this round: " + round.RhaPenetrationOverride.ToString("0") + ")"
                             : string.Empty) +
                         ", judged at " + round.CurrentPenRating.ToString("0") + " mm, " +
                         round.CurrentSpeed.ToString("0") + "/" + round.MaxSpeed.ToString("0") + " m/s" +
                         ", t=" + round.TimeTraveled.ToString("0.00") + " s, slat=" + info.IgnoreSlat +
                         ", tandem=" + info.Tandem + ", ricochet angle=" +
                         info.CertainRicochetAngle.ToString("0") + " deg" +
                         (penShots > 0 ? ", failed-penetration shots=" + penShots : string.Empty) +
                         (effective > 0 ? ", effective hits=" + effective : string.Empty) +
                         " || events: " + Flatten(events));

                if (_submunitions % SummaryEvery == 0)
                {
                    LogSummary();
                }
            }
            catch (Exception ex)
            {
                Log.Error("cluster diagnostics: could not read a submunition's story: " + ex);
            }
        }

        /// <summary>
        /// The one-line verdict, derived from the game's own event strings. The zone names come from GHPC's
        /// own hit-zone components, so they can be looked up in the vehicle prefabs (T-72M "hull side" =
        /// 80 mm RHAe CE, "hull front deck" = 30, ...).
        /// </summary>
        private static string Classify(string events)
        {
            if (events.IndexOf("Destroyed by slat", StringComparison.Ordinal) >= 0)
            {
                _ricochetedOrSlat++;
                return "SLAT KILLED THE ROUND (no jet, no explosion)";
            }
            if (events.IndexOf("Detonated by slat", StringComparison.Ordinal) >= 0)
            {
                _ricochetedOrSlat++;
                return "SLAT DETONATED THE ROUND (the hull behind it is judged next)";
            }

            string penetrated = ZoneAfter(events, "Penetrated ");
            string stopped = ZoneAfter(events, "Stopped by ");
            bool groundHit = stopped == "terrain" ||
                             events.IndexOf("Stopped by terrain", StringComparison.Ordinal) >= 0 ||
                             events.IndexOf("Hit terrain", StringComparison.Ordinal) >= 0;

            if (penetrated != "?")
            {
                _hitVehicle++;
                _penetrated++;
                _penetratedZones.Add(penetrated);
                if (!groundHit && stopped != "?")
                {
                    _stoppedZones.Add(stopped);
                    return "JET PENETRATED " + penetrated + ", THEN STOPPED BY " + stopped;
                }
                return "JET PENETRATED " + penetrated;
            }
            if (stopped != "?" && !groundHit)
            {
                _hitVehicle++;
                _stopped++;
                _stoppedZones.Add(stopped);
                return "JET STOPPED BY " + stopped +
                       " (thicker than the jet, or the jet had been degraded on the way in)";
            }
            if (groundHit)
            {
                _terrain++;
                return "GROUND HIT - fragmentation only";
            }
            return "DETONATED with no plate struck";
        }

        /// <summary>The first zone name that follows a marker, e.g. "Penetrated hull front deck".</summary>
        private static string ZoneAfter(string events, string marker)
        {
            int at = events.IndexOf(marker, StringComparison.Ordinal);
            if (at < 0)
            {
                return "?";
            }
            int start = at + marker.Length;
            int end = events.IndexOfAny(new[] { '\n', '\r' }, start);
            string zone = end < 0 ? events.Substring(start) : events.Substring(start, end - start);
            return zone.Trim();
        }

        /// <summary>One story line, so the whole event list fits on a single log line.</summary>
        private static string Flatten(string events)
        {
            if (string.IsNullOrEmpty(events))
            {
                return "(none)";
            }
            StringBuilder builder = new StringBuilder(events.Length);
            bool space = false;
            for (int i = 0; i < events.Length; i++)
            {
                char c = events[i];
                if (c == '\r' || c == '\n')
                {
                    if (!space)
                    {
                        builder.Append(" | ");
                        space = true;
                    }
                    continue;
                }
                space = false;
                builder.Append(c);
            }
            return builder.ToString().TrimEnd(' ', '|');
        }

        /// <summary>
        /// The tally the player can read after one volley: how many submunitions came down, how many met a
        /// vehicle plate, how many defeated it, and WHICH zones did what. That is the answer to "is the jet
        /// being eaten, or am I hitting thicker armour than a 70 mm HEDP can defeat?".
        /// </summary>
        internal static void LogSummary()
        {
            Log.Info("cluster diagnostics summary: " + _submunitions + " submunition(s) down - " +
                     _hitVehicle + " met a vehicle plate (" + _penetrated + " penetrated, " + _stopped +
                     " stopped), " + _ricochetedOrSlat + " lost to slat, " + _terrain + " hit the ground. " +
                     "Penetrated: " + Describe(_penetratedZones) + ". Stopped: " + Describe(_stoppedZones) + ".");
        }

        private static string Describe(List<string> zones)
        {
            if (zones.Count == 0)
            {
                return "(none)";
            }
            Dictionary<string, int> counts = new Dictionary<string, int>();
            List<string> order = new List<string>();
            for (int i = 0; i < zones.Count; i++)
            {
                string zone = zones[i];
                int seen;
                if (!counts.TryGetValue(zone, out seen))
                {
                    counts[zone] = 0;
                    order.Add(zone);
                }
                counts[zone] = seen + 1;
            }
            StringBuilder builder = new StringBuilder();
            for (int i = 0; i < order.Count; i++)
            {
                if (i > 0)
                {
                    builder.Append(", ");
                }
                builder.Append(order[i]).Append(" x").Append(counts[order[i]]);
            }
            return builder.ToString();
        }
    }

    /// <summary>
    /// The hook: every detonation in the game passes through here, and <see cref="ClusterDiagnostics"/>
    /// throws away everything that is not one of our submunitions on the first cheap test.
    /// </summary>
    [HarmonyPatch(typeof(LiveRound), "Detonate")]
    internal static class ClusterDetonateDiagnosticsPatch
    {
        private static void Postfix(LiveRound __instance)
        {
            ClusterDiagnostics.Note(__instance);
        }
    }
}
