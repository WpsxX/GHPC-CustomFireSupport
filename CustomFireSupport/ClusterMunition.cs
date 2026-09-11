using System;
using GHPC.Effects;
using GHPC.Weaponry;
using GHPC.Weapons;
using UnityEngine;

namespace CustomFireSupport
{
    /// <summary>
    /// One faction's cargo (cluster) round: what it is called, how many submunitions it carries and
    /// what calibre it is fired from. The two rounds the player asked for are
    ///
    ///   Blue  / US    M864    155 mm, 72 HEDP submunitions
    ///   Red   / Soviet 3-O-23 152 mm, 42 HEDP submunitions
    ///
    /// Everything else about them is the same, so it lives in <see cref="ClusterMunition.Launch"/>.
    /// </summary>
    internal sealed class ClusterShellSpec
    {
        internal string ShellName = string.Empty;
        internal string SubmunitionName = string.Empty;
        internal int Submunitions = 72;
        internal float CaliberMillimeters = 155f;
    }

    /// <summary>
    /// Everything the runtime needs to turn one fired cargo round into an airburst plus a shower of
    /// submunitions. Built once per faction by <see cref="ClusterMunitionFactory"/> and handed to the
    /// round through the <see cref="ClusterMunition"/> component when the game spawns it.
    /// </summary>
    internal sealed class ClusterLaunch
    {
        /// <summary>The cargo round's own ammo (reference compared, so a pooled round cannot mistake it).</summary>
        internal AmmoType CarrierAmmo;

        /// <summary>The cargo round's ammo codex - what the battery's munitions choice hands the game.</summary>
        internal AmmoCodexScriptable Codex;

        /// <summary>Display data for the log ("M864 Cluster", "M864 HEDP", 72).</summary>
        internal ClusterShellSpec Spec;

        /// <summary>The cartridges released at the burst point.</summary>
        internal AmmoCodexScriptable Submunition;

        /// <summary>
        /// The ammo whose impact-effect descriptor / impact audio ARE the game's 20 mm HE round - the
        /// burst puff and every submunition impact use it (see <see cref="ClusterMunitionFactory"/>).
        /// </summary>
        internal AmmoType BurstEffect;

        /// <summary>How fast the cargo round is flown to the burst point, in m/s.</summary>
        internal float CarrierSpeed = ClusterMunition.CarrierSpeedMetersPerSecond;

        /// <summary>Height of the airburst above the called impact point, in metres.</summary>
        internal float BurstHeightMeters = ClusterMunition.BurstHeightMeters;

        internal string Describe()
        {
            // SubmunitionName already carries the HEDP designation ("M864 HEDP"), so it is not repeated.
            return Spec.ShellName + " (" + Spec.CaliberMillimeters.ToString("0") + " mm, " +
                   Spec.Submunitions + " x " + Spec.SubmunitionName + ", airburst " +
                   BurstHeightMeters.ToString("0") + " m above the target)";
        }
    }

    /// <summary>
    /// Marks the live round of a cargo (cluster) shell and carries that round's burst parameters.
    ///
    /// Attached by <see cref="ClusterMunitionPatches.ClusterAttachPatch"/> in LiveRound.Init, i.e. on
    /// the same object the game pooled for this shot, and re-armed / disarmed on every reuse so a
    /// recycled round can never inherit the previous shot's burst point.
    /// </summary>
    internal sealed class ClusterMunition : MonoBehaviour
    {
        /// <summary>Height of the airburst above the called impact point, in metres.</summary>
        internal const float BurstHeightMeters = 100f;

        /// <summary>
        /// How fast the mod flies the cargo round to the burst point. GHPC's own battery geometry
        /// (spawn height / angle / heading) would put the shell over a point a few dozen metres short
        /// of the target by the time it is 100 m up, so the round is steered instead - then the burst
        /// really is "above the target", whatever the mission's battery happens to be configured like.
        /// </summary>
        internal const float CarrierSpeedMetersPerSecond = 620f;

        /// <summary>
        /// Release speed of a HEDP submunition, in m/s. Real M864 submunitions are thrown out of the
        /// carrier and fall at roughly this sort of speed; with a 100 m airburst it makes the pattern
        /// about 0.9 s long and up to ~55 m wide, the right scale for a cargo round.
        /// </summary>
        internal const float SubmunitionSpeedMetersPerSecond = 110f;

        /// <summary>Half-angle of the release cone around straight down, in degrees.</summary>
        internal const float ReleaseConeHalfAngleDegrees = 35f;

        /// <summary>
        /// Release speed spread, in m/s. Every submunition gets its own offset drawn from +-3..7 m/s, so the
        /// submunitions of one burst do not all leave at the same speed - which, on top of the release cone,
        /// keeps them from reaching the ground in one instant (the cone alone spreads the fall over ~0.16 s;
        /// this widens the spread and staggers the impacts).
        /// </summary>
        internal const float SubmunitionSpeedSpreadMinMetersPerSecond = 3f;
        internal const float SubmunitionSpeedSpreadMaxMetersPerSecond = 7f;

        /// <summary>Number of the live round this burst belongs to (pooling / shot-id guard).</summary>
        internal int ShotId;

        /// <summary>False for a pooled round that is not currently carrying a cluster payload.</summary>
        internal bool Armed;

        internal Vector3 AimPoint;
        internal float BurstHeight = BurstHeightMeters;
        internal int Submunitions;
        internal AmmoCodexScriptable Submunition;
        internal AmmoType BurstEffect;
        internal float CarrierSpeed = CarrierSpeedMetersPerSecond;
        internal string ShellName = string.Empty;
        internal string SubmunitionName = string.Empty;

        /// <summary>Diagnostics: how many times this object has opened (should be exactly one per shot).</summary>
        internal int Bursts;

        internal Vector3 BurstPoint
        {
            get { return AimPoint + Vector3.up * BurstHeight; }
        }

        internal void Arm(ClusterLaunch launch, Vector3 aimPoint, int shotId)
        {
            ShotId = shotId;
            Armed = true;
            AimPoint = aimPoint;
            BurstHeight = launch.BurstHeightMeters;
            Submunitions = launch.Spec != null ? launch.Spec.Submunitions : 0;
            Submunition = launch.Submunition;
            BurstEffect = launch.BurstEffect;
            CarrierSpeed = launch.CarrierSpeed > 0f ? launch.CarrierSpeed : CarrierSpeedMetersPerSecond;
            ShellName = launch.Spec != null ? launch.Spec.ShellName : string.Empty;
            SubmunitionName = launch.Spec != null ? launch.Spec.SubmunitionName : string.Empty;
        }

        internal void Disarm()
        {
            Armed = false;
            ShotId = 0;
            Submunition = null;
            BurstEffect = null;
        }
    }

    /// <summary>
    /// The airburst itself: the opening puff, the submunition shower, and the flight of one
    /// submunition. Written against the game's public API only (LiveRoundUtility / LiveRoundMarshaller
    /// / ParticleEffectsManager / ImpactSFXManager), so the Harmony patches stay thin.
    /// </summary>
    internal static class ClusterBurst
    {
        /// <summary>
        /// How the opening charge is pointed: STRAIGHT DOWN, i.e. the effect is turned upside down.
        ///
        /// The effect resolved for this ammo is the game's own 20 mm HE GROUND explosion (the descriptor is
        /// the 20 mm HE round's, see ClusterMunitionFactory), and the game draws a ground explosion
        /// unrotated - <c>LiveRound.doImpactVFX</c> sets <c>Quaternion.identity</c> for a non-AP terrain hit -
        /// because such a prefab is authored to sit on the ground already, with its plume rising. Flipping
        /// that prefab 180 degrees about X points the plume down instead, which is what a dispenser's opening
        /// charge does.
        ///
        /// <c>Quaternion.LookRotation(Vector3.down)</c> looks like the obvious call but does NOT work: the
        /// requested forward is parallel to the default up vector, and Unity's degenerate handling leaves the
        /// effect essentially unrotated - which is exactly why the burst was not pointing down.
        /// </summary>
        private static readonly Quaternion AirburstRotation = Quaternion.Euler(180f, 0f, 0f);

        private static bool _warnedNoBurstEffect;

        /// <summary>
        /// Opens the cargo round: one "small explosion" (the game's 20 mm HE effect, pointed straight
        /// down, plus the game's autocannon explosion sound) and one HEDP submunition per charge.
        /// </summary>
        internal static void Open(Vector3 burstPoint, ClusterMunition carrier)
        {
            SpawnAirburstEffect(carrier.BurstEffect, burstPoint);

            int count = Mathf.Max(0, carrier.Submunitions);
            for (int i = 0; i < count; i++)
            {
                SpawnSubmunition(carrier, burstPoint, i);
            }

            Log.Info("cluster munition: '" + carrier.ShellName + "' opened " +
                     carrier.BurstHeight.ToString("0") + " m above the called point and released " +
                     count + " x " + carrier.SubmunitionName +
                     " (" + burstPoint.ToString("0.#") + ").");

            if (CustomFireSupportMod.VerboseLogging)
            {
                Log.Verbose("cluster munition: burst point " + burstPoint.ToString("0.##") +
                            ", aim point " + carrier.AimPoint.ToString("0.##") +
                            ", submunition speed " + ClusterMunition.SubmunitionSpeedMetersPerSecond.ToString("0") +
                            " m/s +/- " + ClusterMunition.SubmunitionSpeedSpreadMinMetersPerSecond.ToString("0") +
                            "-" + ClusterMunition.SubmunitionSpeedSpreadMaxMetersPerSecond.ToString("0") +
                            " m/s per submunition, in a " +
                            ClusterMunition.ReleaseConeHalfAngleDegrees.ToString("0") +
                            " deg cone around straight down.");
            }
        }

        /// <summary>
        /// The opening puff. The user asked for the game's 20 mm HE explosion effect, pointed DOWN, and
        /// the 20 mm explosion sound on it - both come from the ammo of GHPC's own 20 mm HE round (see
        /// ClusterMunitionFactory), so the burst looks and sounds exactly like a 20 mm HE hit.
        /// </summary>
        private static void SpawnAirburstEffect(AmmoType effect, Vector3 position)
        {
            bool spawned = false;

            ParticleEffectsManager manager = ParticleEffectsManager.Instance;
            if (manager != null && effect != null)
            {
                try
                {
                    GameObject fx = manager.CreateImpactEffectOfType(effect,
                        ParticleEffectsManager.FusedStatus.Fuzed,
                        ParticleEffectsManager.SurfaceMaterial.Dirt,
                        false, position);
                    if (fx != null)
                    {
                        // "爆炸特效向下": the pooled effect is placed but not oriented, so flip it (see
                        // AirburstRotation for why this is a 180 degree X flip and not LookRotation(down)).
                        fx.transform.rotation = AirburstRotation;
                        spawned = true;
                    }
                }
                catch (Exception ex)
                {
                    Log.Error("cluster munition: could not spawn the airburst effect: " + ex);
                }
            }

            // The game could not resolve an effect for this descriptor (a stripped or unexpected effect
            // database). Fall back to the prefab that came with the 20 mm round, still pointed down, and
            // say so - a silent, invisible airburst is exactly what the player would file as a bug.
            if (!spawned && effect != null && effect.DetonateEffect != null)
            {
                GameObject fx = UnityEngine.Object.Instantiate(effect.DetonateEffect, position, AirburstRotation);
                fx.name = "CFS cluster airburst " + effect.Name;
                UnityEngine.Object.Destroy(fx, 15f);
                spawned = true;
            }

            if (!spawned && !_warnedNoBurstEffect)
            {
                _warnedNoBurstEffect = true;
                Log.Warn("cluster munition: the game resolved no 20 mm HE explosion effect for the airburst " +
                         "(the mission's effect database is missing that entry); the burst itself still fires " +
                         "and the submunitions still work.");
            }

            PlayAirburstSound(position);
        }

        /// <summary>
        /// The 20 mm explosion sound of the burst. Played through ImpactSFXManager, i.e. the very call
        /// the game makes for a real 20 mm HE impact, so it keeps the distance / speed-of-sound delay.
        /// </summary>
        private static void PlayAirburstSound(Vector3 position)
        {
            GHPC.Audio.ImpactSFXManager manager = GHPC.Audio.ImpactSFXManager.Instance;
            if (manager == null)
            {
                return;
            }
            manager.PlaySimpleImpactAudio(GHPC.Audio.ImpactAudioType.AutocannonExplosive, position);
        }

        /// <summary>
        /// Releases one HEDP submunition: a real game round (so penetration, spall, blast, explosion
        /// effect, impact decal and the 20 mm explosion sound are all the game's own), pointed into the
        /// release cone below the burst point.
        /// </summary>
        private static void SpawnSubmunition(ClusterMunition carrier, Vector3 origin, int index)
        {
            AmmoCodexScriptable codex = carrier.Submunition;
            if (codex == null || codex.AmmoType == null)
            {
                return;
            }

            AmmoType ammo = codex.AmmoType;
            Vector3 direction = ReleaseDirection();
            float speed = SubmunitionSpeed();
            // A small random jitter, so no two submunitions of a burst share a transform - the game
            // raycasts each round from its own position, and identical ones would look like a single trace.
            Vector3 position = origin + UnityEngine.Random.insideUnitSphere * 0.5f;

            LiveRound round;
            try
            {
                round = LiveRoundUtility.GetNewLiveRound(ammo);
            }
            catch (Exception ex)
            {
                Log.Error("cluster munition: could not take a live round for a submunition: " + ex);
                return;
            }
            if (round == null)
            {
                return;
            }

            GameObject go = round.gameObject;
            go.name = "CFS " + ammo.Name + " " + index;
            go.transform.position = position;
            go.transform.rotation = Quaternion.LookRotation(direction);

            round.Info = ammo;
            round.Guided = false;
            round.UseGravity = true;
            round.NpcRound = false;
            round.Shooter = null;
            // The Invisible pool also serves the game's own explosion spall, so a round taken from it may
            // still be flagged as spall; IsSpall would suppress this round's impact effect and blast.
            round.IsSpall = false;
            round.CurrentSpeed = speed;
            round.MaxSpeed = speed;
            // Identity + per-shot fragment state for ClusterFragments. Reset on every spawn for the same
            // reason ClusterMunition is disarmed on every spawn: this pooled object may have flown as
            // something else a moment ago.
            ClusterSubmunition marker = go.GetComponent<ClusterSubmunition>();
            if (marker == null)
            {
                marker = go.AddComponent<ClusterSubmunition>();
            }
            marker.FragmentsThrown = false;
            round.Init();
        }

        /// <summary>
        /// The release speed of one submunition: the nominal speed plus or minus a 3-7 m/s draw, so the
        /// submunitions of a burst do not all fall at the same rate and therefore do not all land at the
        /// same instant (see ClusterMunition.SubmunitionSpeedSpreadMinMetersPerSecond).
        /// </summary>
        private static float SubmunitionSpeed()
        {
            float spread = UnityEngine.Random.Range(
                ClusterMunition.SubmunitionSpeedSpreadMinMetersPerSecond,
                ClusterMunition.SubmunitionSpeedSpreadMaxMetersPerSecond);
            if (UnityEngine.Random.value < 0.5f)
            {
                spread = -spread;
            }
            return ClusterMunition.SubmunitionSpeedMetersPerSecond + spread;
        }

        /// <summary>
        /// A uniformly random direction inside the release cone around straight down. Uniform in the
        /// cosine of the polar angle, so the pattern is an even disk on the ground rather than a ring.
        /// </summary>
        private static Vector3 ReleaseDirection()
        {
            float cosLimit = Mathf.Cos(ClusterMunition.ReleaseConeHalfAngleDegrees * Mathf.Deg2Rad);
            float cosTheta = Mathf.Lerp(cosLimit, 1f, UnityEngine.Random.value);
            float sinTheta = Mathf.Sqrt(Mathf.Max(0f, 1f - cosTheta * cosTheta));
            float phi = UnityEngine.Random.value * Mathf.PI * 2f;
            return new Vector3(sinTheta * Mathf.Cos(phi), -cosTheta, sinTheta * Mathf.Sin(phi)).normalized;
        }
    }

    /// <summary>
    /// Marks one released HEDP submunition - never the cargo round, and never the game's own explosion
    /// spall. It exists for two reasons:
    ///
    ///   1. IDENTITY. "Is this one of our submunitions?" cannot be answered from the ammo alone: the cargo
    ///      round is one of ours too (and carries no warhead at all), and the submunitions come out of the
    ///      same <c>Invisible</c> pool the game uses for its own explosion spall - an object can be a
    ///      submunition one moment and the game's spall the next. Only
    ///      <see cref="ClusterBurst"/>'s submunition spawn attaches this component.
    ///   2. ONE-SHOT STATE. <c>LiveRound.Detonate</c> can run more than once for the same round (the
    ///      ricochet paths call it and then let the march loop continue), so the fragment burst needs a
    ///      flag - and that flag has to be reset on every spawn, for the same reason the ClusterMunition
    ///      component is disarmed on every spawn: a pooled round must never inherit the previous shot's
    ///      state.
    /// </summary>
    internal sealed class ClusterSubmunition : MonoBehaviour
    {
        /// <summary>True once this round's detonation has thrown its fragments.</summary>
        internal bool FragmentsThrown;
    }

    /// <summary>
    /// The visible fragments one HEDP submunition throws when it goes off.
    ///
    /// WHY THE MOD SPAWNS THEM ITSELF
    ///
    /// The game has a spall-on-detonation path - <c>AmmoType.DetonateSpallCount</c> drives
    /// <c>LiveRound.createExplosion</c>, and LiveRound honours it for HEAT rounds only. The submunition
    /// used to just set that field. But createExplosion takes its rounds from the INVISIBLE pool, so those
    /// fragments were simulated and lethal while being completely invisible: a 72-submunition burst looked
    /// like 72 small puffs with nothing coming out of them.
    ///
    /// So the count moved here, and the fragments are taken from the game's own VISIBLE spall pool
    /// (<c>LiveRoundVisualType.Spall</c> - the pool the game uses for armour spall). Everything else is the
    /// game's own machinery: the spall round's ballistics (200 m/s, 0.1 kg, its own drag), the
    /// marshaller's pooling, the game's damage handling. Only two details differ from createExplosion:
    ///
    ///   * the visual type (visible, as above), and
    ///   * <c>RhaPenetrationOverride</c> is written AFTER Init, not before. Init ends with
    ///     <c>resetLocalValues()</c>, which zeroes that property, so a value written before it never
    ///     reaches the shot - which is exactly what the game's own createSpall does, and why vanilla spall
    ///     always penetrates the static spall round's 4 mm whatever it drew.
    ///
    /// A penetrating submunition does not come here: its jet path does not call Detonate, and it does not
    /// need to - vanilla already throws VISIBLE armour spall behind whatever the jet defeats.
    /// </summary>
    internal static class ClusterFragments
    {
        /// <summary>Fragment speed in m/s - the game's own spall round speed.</summary>
        private const float SpeedMetersPerSecond = 200f;

        /// <summary>The mod's own fragment round, built once per session.</summary>
        private static AmmoType _fragmentAmmo;

        private static bool _warnedNoSpallAmmo;

        /// <summary>
        /// Throws one submunition's fragments at its current position, once. Safe to call for any round: a
        /// round that is not one of the mod's submunitions is rejected on the first field read.
        /// </summary>
        internal static void Throw(LiveRound round)
        {
            ClusterSubmunition marker;
            try
            {
                if (round == null || round.IsSpall || round.Pooled)
                {
                    return;
                }
                marker = round.GetComponent<ClusterSubmunition>();
                if (marker == null || marker.FragmentsThrown)
                {
                    return;
                }
                marker.FragmentsThrown = true;
            }
            catch (Exception ex)
            {
                Log.Error("cluster fragments: could not check the submunition: " + ex);
                return;
            }

            LiveRoundMarshaller marshaller = LiveRoundMarshaller.Instance;
            AmmoType ammo = FragmentAmmo();
            if (marshaller == null || ammo == null)
            {
                return;
            }

            Vector3 origin = round.transform.position;
            int thrown = 0;
            for (int i = 0; i < ClusterMunitionFactory.HedpFragmentCount; i++)
            {
                LiveRound fragment;
                try
                {
                    fragment = marshaller.GetRoundOfVisualType(LiveRoundMarshaller.LiveRoundVisualType.Spall);
                }
                catch (Exception ex)
                {
                    Log.Error("cluster fragments: could not take a live round for a fragment: " + ex);
                    break;
                }
                if (fragment == null)
                {
                    break;
                }

                GameObject go = fragment.gameObject;
                go.name = "CFS HEDP fragment " + i;
                // A little jitter, so no two fragments share a transform - the game raycasts each round from
                // its own position, and identical ones would look like a single trace.
                go.transform.position = origin + UnityEngine.Random.insideUnitSphere * 0.15f;
                // An upward cone instead of the game's full sphere: the burst opens over the target, and the
                // rounds fired straight down only plough into the ground below it. LiveRound travels along
                // transform.forward, so this rotation IS the departure direction.
                go.transform.rotation = ConeRotation();

                fragment.Info = ammo;
                fragment.IsSpall = true;
                fragment.NpcRound = round.NpcRound;
                fragment.Shooter = round.Shooter;
                fragment.CurrentSpeed = SpeedMetersPerSecond;
                fragment.MaxSpeed = SpeedMetersPerSecond;
                fragment.Init(round);
                // AFTER Init - see the class comment.
                fragment.RhaPenetrationOverride = UnityEngine.Random.Range(
                    ClusterMunitionFactory.HedpMinSpallRha, ClusterMunitionFactory.HedpMaxSpallRha);
                thrown++;
            }

            if (thrown > 0 && CustomFireSupportMod.VerboseLogging)
            {
                Log.Verbose("cluster fragments: threw " + thrown + " fragment(s) at " + origin.ToString("0.#") +
                            " (" + ClusterMunitionFactory.HedpMinSpallRha.ToString("0") + "-" +
                            ClusterMunitionFactory.HedpMaxSpallRha.ToString("0") + " mm RHAe each, " +
                            SpeedMetersPerSecond.ToString("0") + " m/s, upward " +
                            ClusterMunitionFactory.HedpFragmentConeDegrees.ToString("0") + " degree cone).");
            }
        }

        /// <summary>
        /// One fragment's departure direction: somewhere inside a HedpFragmentConeDegrees cone around
        /// straight up - world up, not the submunition's own up, because the round dives into its burst
        /// point and so its local up faces the ground. The tilt stops just short of the vertical because
        /// LookRotation wants a direction that is not parallel to the up reference it is handed.
        /// </summary>
        private static Quaternion ConeRotation()
        {
            float tilt = UnityEngine.Random.Range(1f, ClusterMunitionFactory.HedpFragmentConeDegrees * 0.5f);
            Vector3 direction = Quaternion.AngleAxis(UnityEngine.Random.Range(0f, 360f), Vector3.up) *
                                (Quaternion.AngleAxis(tilt, Vector3.right) * Vector3.up);
            return Quaternion.LookRotation(direction, Vector3.up);
        }

        /// <summary>
        /// The mod's own fragment round: a copy of the game's spall round with the VISIBLE spall visual.
        ///
        /// A copy rather than the game's <c>LiveRound.SpallAmmoType</c> itself, because that static is
        /// written by the game's own code (createExplosion and createSpall both assign its VisualType) - and
        /// the pooling key IS that field, so the mod must own the value its fragments are stored back
        /// under. Built once and reused.
        /// </summary>
        private static AmmoType FragmentAmmo()
        {
            if (_fragmentAmmo != null)
            {
                return _fragmentAmmo;
            }

            AmmoType spall = LiveRound.SpallAmmoType;
            if (spall == null)
            {
                if (!_warnedNoSpallAmmo)
                {
                    _warnedNoSpallAmmo = true;
                    Log.Warn("cluster fragments: the game has no spall round loaded, so the submunitions' " +
                             "fragments are skipped (their blast and frag cloud still work).");
                }
                return null;
            }

            AmmoType fragment = ClusterMunitionFactory.CloneAmmoType(spall);
            fragment.Name = "HEDP fragment";
            fragment.VisualType = LiveRoundMarshaller.LiveRoundVisualType.Spall;
            fragment.RhaPenetration = ClusterMunitionFactory.HedpMinSpallRha;
            fragment.MinSpallRha = ClusterMunitionFactory.HedpMinSpallRha;
            fragment.MaxSpallRha = ClusterMunitionFactory.HedpMaxSpallRha;
            // The spall round's descriptor has HasImpactEffect = false, so no impact effect is looked up;
            // -1 keeps the clone from inheriting a stale cache index from the donor.
            fragment.CachedIndex = -1;
            _fragmentAmmo = fragment;
            return fragment;
        }
    }
}
