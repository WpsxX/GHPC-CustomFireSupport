using System;
using UnityEngine;
using GHPC.Vehicle;

namespace CustomFireSupport
{
    /// <summary>
    /// The flight of one air-to-ground missile, which is the whole difference between the two rounds the
    /// mod fires (see CasAirframeCatalog.MissileProfile): one is a fire-and-forget imaging-seeker round that
    /// arches out, glides and pushes over onto its target, the other is a laser rider that pops up over the
    /// sight line and then slams down, and that loses its control altogether when its beam breaks.
    ///
    /// The round carries this component instead of the game's own guidance, because the mod fires BOMB data
    /// (see CasPayloadFactory.BuildMissileHardpoint) - the game's LiveRound has no idea it is flying a
    /// missile. CasImpactAimPatch calls <see cref="TryStep"/> once per frame and then writes the flight
    /// state; everything about the shape of the flight is decided here.
    ///
    /// ---------------------------------------------------------------------------------------------
    /// THE FLIGHT LAW
    ///
    /// Per frame, with d = the horizontal range to the impact point:
    ///
    ///     los      = atan2(spot.y - pos.y, d)                        the direct collision course
    ///     loft(d)  = LoftHeightMeters * smoothstep(clamp(d / TerminalRange, 0, 1))
    ///     arch     = los + atan2(loft, d)                            aim floated above the line
    ///     climbCap = asin(clamp((ceiling - pos.y) / (speed * FlightPathLiftSeconds), 0, 1))
    ///     boost    = max(arch, min(BoostClimbDegrees, climbCap))
    ///     desired  = lerp(arch, boost, smooth burn and range fade)
    ///     desired  = clamp(desired, -85, +40)
    ///     turn     = smoothly blended boost/cruise/terminal rate * dt
    ///
    /// The loft offset vanishes at the target, so the approach converges on the impact point. The profile's
    /// dive angle sizes the distance needed to begin the push-over; it is not commanded as an absolute
    /// attitude, which would send a high release into the ground short of the target.
    ///
    /// ---------------------------------------------------------------------------------------------
    /// THE LAUNCH PULL-UP (the "the missile swerves and porpoises" the player saw)
    ///
    /// The pull-up used to be switched by a HARD altitude gate - `(position.y - _releaseY) < LoftHeight`
    /// - and the command it switched to was an ATTITUDE angle (BoostClimbDegrees). The round therefore
    /// flew through its own ceiling: every time it poked back above the gate the command collapsed to the
    /// arch's -9 deg, the rate limiter swung the nose down, the round sank under the gate, the command
    /// snapped back to +18 deg, and it climbed again. From 2.6 km at 500 m that limit cycle ran at about
    /// 2 Hz, swinging the flight path between +9 and -11 deg for four seconds - which is exactly the
    /// "sudden irregular turn, then climb / level / climb again" the player reported.
    ///
    /// The gate is now a continuous vertical-speed command: the round asks to close the remaining height
    /// in FlightPathLiftSeconds. The climb fades over the distance needed to turn into the dive, and over
    /// the end of motor burn, so neither transition changes the requested attitude in one frame.
    ///
    /// ---------------------------------------------------------------------------------------------
    /// SCALING THE 20 km FLIGHT TO THE GAME'S 2-4 km
    ///
    /// The profile's loft and terminal range were sized like a real stand-off missile's - 110-260 m of
    /// arch and a kilometric push-over - and at the ranges GHPC is actually played at (a call is usually
    /// 2-4 km, with the release at ReleaseDistanceMeters) that put 140-270 m of zoom climb under a 2.5 km
    /// shot, i.e. angles nothing like the real thing. Both profiles' LoftHeightMeters and
    /// TerminalRangeMeters are therefore scaled down to the game's distances, which flattens the approach
    /// angles and the launch pull-up by roughly the same proportion. See the two profile blocks in
    /// CasAirframeCatalog for the figures and the measurements behind them.
    ///
    /// ---------------------------------------------------------------------------------------------
    /// LOSING GUIDANCE
    ///
    /// What "lost" means depends on the guidance kind, and the two behaviours are the two weapons' fail
    /// states from the design brief:
    ///
    ///   * FireAndForget (AGM-65): the seeker had the target before launch and there is nobody left to
    ///     correct the round, so it keeps its attitude and its speed and falls away on the last tangent -
    ///     a nose that drops a few degrees a second, no spin, landing in the dirt around the target.
    ///   * LaserBeamRider (Kh-25): the aircraft IS the guidance, so losing the beam is a control failure.
    ///     The nose slams down, the round yaws and rolls violently and it goes in wherever that takes it.
    /// </summary>
    internal sealed class CasMissileGuidance : MonoBehaviour
    {
        /// <summary>Safety clamp on how steeply the round may ever be pointed.</summary>
        private const float MaxDiveDegrees = 85f;

        /// <summary>Safety clamp on how far up the round may ever be pointed.</summary>
        private const float MaxClimbDegrees = 40f;

        /// <summary>
        /// The launch pull-up's time constant, in seconds: the round may ask for the vertical speed that
        /// closes the distance to its loft ceiling in this much time, and no more.
        /// </summary>
        private const float FlightPathLiftSeconds = 0.35f;

        /// <summary>Time used to ease the climb and boost authority away at motor burnout.</summary>
        private const float BurnFadeSeconds = 0.75f;

        /// <summary>Inside this distance the mod hands the last few metres back to the game's own impact.</summary>
        private const float HandoverDistance = 4f;

        /// <summary>
        /// Inside this distance a round that has stopped closing the range counts as "past the target" and is
        /// handed over as well. See <see cref="StepGuided"/>.
        /// </summary>
        private const float PassGuardDistance = 120f;

        /// <summary>
        /// A frame this many times longer than the one before it is treated as a hitch rather than as
        /// elapsed flight: per-frame estimates are skipped and the close-range guard is re-based.
        /// </summary>
        private const float FrameSpikeRatio = 4f;

        /// <summary>Floor for the ratio test, for the very short frames of a high frame rate.</summary>
        private const float MinFrameSeconds = 0.005f;

        /// <summary>
        /// Below this step a per-frame displacement says nothing useful about the target's velocity: the
        /// round is at 300-450 m/s and even a stationary vehicle's transform moves a little, and a division
        /// by a near-zero dt turns that into the 120 m/s clamp - a bogus target heading the round would
        /// then steer onto.
        /// </summary>
        private const float MinTargetSampleSeconds = 0.002f;

        /// <summary>
        /// The largest distance the target may appear to move in one FLIGHT step and still be believed, as a
        /// multiple of how far the round itself moved. A jump beyond it is a respawn / teleport / pause gap,
        /// not motion, and is ignored rather than flown at.
        /// </summary>
        private const float MaxTargetJumpRatio = 4f;

        /// <summary>Set once the round is within <see cref="PassGuardDistance"/> and closing.</summary>
        private float _lastRange = float.MaxValue;

        /// <summary>The previous frame's step, used to recognise a pause / hitch (see <see cref="FrameSpikeRatio"/>).</summary>
        private float _referenceDt;

        /// <summary>
        /// The direction the guidance wanted for the round on the last frame it ran. CasImpactAimPatch reads
        /// it when the round's own update has handed the transform a degenerate forward vector (a zero step)
        /// so the nose can be put back on the heading the mod is actually flying.
        /// </summary>
        internal Vector3 IntendedDirection { get { return _intended; } }

        /// <summary>
        /// The full attitude (roll included) the guidance left the round in on the last frame it flew.
        /// CasMissilePauseHold restores it while the game is paused, when nothing else is running.
        /// </summary>
        internal Quaternion IntendedRotation { get { return _intendedRotation; } }

        /// <summary>The last direction <see cref="TryStep"/> wrote, for the zero-step nose repair.</summary>
        private Vector3 _intended;
        private Quaternion _intendedRotation;

        private CasAirframeCatalog.MissileProfile _profile;
        private Transform _carrier;
        private Transform _target;
        private Vector3 _lastTargetPosition;
        private Vector3 _targetVelocity;
        private bool _targetSampleValid;

        /// <summary>The laser spot / seeker point: re-read while the target lives, then remembered.</summary>
        private Vector3 _spot;
        private bool _spotValid;

        private float _age;
        private float _releaseY;
        private bool _loggedProfile;
        private bool _lost;
        private float _lostAge;
        private bool _loggedLoss;

        internal bool Lost { get { return _lost; } }
        internal CASController CarrierController
        {
            get { return _carrier == null ? null : _carrier.GetComponentInParent<CASController>(); }
        }

        internal bool HasLiveTarget
        {
            get { return _target != null; }
        }

        /// <summary>
        /// Binds the round to its profile and its carrier. Called once, when the round spawns (LiveRound.Init),
        /// because that is the first moment both are known.
        /// </summary>
        internal void Init(CasAirframeCatalog.MissileProfile profile, Transform carrier)
        {
            _profile = profile;
            _carrier = carrier;
            _target = null;
            _targetSampleValid = false;
            _targetVelocity = Vector3.zero;
            _releaseY = transform.position.y;
            _age = 0f;

            // Every per-shot value is reset: LiveRoundMarshaller hands a round to any weapon of the same
            // visual type, and a component left over from an earlier shot must never fly the new one.
            _spotValid = false;
            _lost = false;
            _lostAge = 0f;
            _loggedLoss = false;
            _loggedProfile = false;
            _lastRange = float.MaxValue;
            _referenceDt = 0f;
            _intended = Vector3.zero;
            _intendedRotation = Quaternion.identity;

            if (profile != null && !_loggedProfile)
            {
                _loggedProfile = true;
                Log.Info("CAS missile flight: '" + profile.MissileId + "' is " +
                         (profile.Guidance == CasAirframeCatalog.GuidanceKind.LaserBeamRider
                             ? "LASER-GUIDED - the carrier has to keep its nose within " +
                               profile.LaserMaxOffAxisDegrees.ToString("0") +
                               " deg of the spot until it lands"
                             : "FIRE-AND-FORGET - the carrier is free at the rail") +
                         "; " + profile.MotorBurnSeconds.ToString("0.#") + " s of motor at " +
                         profile.CruiseSpeedMeters.ToString("0") + " m/s (Mach " +
                         CasAirframeCatalog.MachOf(profile.CruiseSpeedMeters).ToString("0.0#") + "), a " +
                         profile.BoostClimbDegrees.ToString("0") + " deg pull-up into a " +
                         profile.LoftHeightMeters.ToString("0") + " m arch, terminal from " +
                         profile.TerminalRangeMeters.ToString("0") + " m arriving at up to " +
                         profile.TerminalDiveDegrees.ToString("0") + " deg (" +
                         profile.TerminalTurnRateDegreesPerSecond.ToString("0") +
                         " deg/s of control authority there).");
            }

            // This component holds the missile's attitude while the game is paused, when the round's own
            // update - and therefore this guidance - is not running at all.
            CasMissilePauseHold hold = gameObject.GetComponent<CasMissilePauseHold>();
            if (hold == null)
            {
                hold = gameObject.AddComponent<CasMissilePauseHold>();
            }
            hold.Guidance = this;
            hold.enabled = true;
        }

        internal void SetTarget(Transform target)
        {
            _target = target;
            _targetSampleValid = target != null;
            if (_targetSampleValid)
            {
                _lastTargetPosition = target.position;
            }
        }

        /// <summary>
        /// One frame of flight. Returns false when the mod's control of this round is over and the game
        /// should have it back (the round reached its impact point); `direction` is the direction the
        /// caller must give the round's velocity.
        /// </summary>
        internal bool TryStep(bool hasPoint, Vector3 point, float dt, float rawDt, out Vector3 direction)
        {
            direction = transform.forward;
            if (_profile == null)
            {
                return false;
            }

            // PAUSED: do not integrate at all. Time.deltaTime is 0 while the game is paused, but the frame
            // the pause is released (and any long hitch) hands out a much bigger step, which used to let the
            // round swing its nose through tens of degrees in one frame - the "pause the game and the
            // missile veers" the player saw. A frame longer than 30 fps is treated as 30 fps for control
            // purposes: the aircraft's own physics still moves the round, but the attitude stays smooth.
            if (Time.timeScale <= 0f)
            {
                return true;
            }
            dt = Mathf.Min(dt, 1f / 30f);

            // A frame that stands out against the previous one is a hitch rather than elapsed flight: its
            // step should not update the target-velocity estimate or be compared against a stale range.
            bool frameSpike = rawDt > Mathf.Max(_referenceDt * FrameSpikeRatio, MinFrameSeconds);
            _referenceDt = rawDt;
            _age += dt;

            if (_target != null)
            {
                Vector3 current = _target.position;
                Vector3 jump = current - _lastTargetPosition;
                // The round covers roughly the aircraft's launch speed per step, so a target that "moved"
                // many times that is not moving at all: it is being sampled across a pause, or the unit was
                // replaced. Believing it would aim the round hundreds of metres off the target it can see.
                float believable = Mathf.Max(1f, _profile.CruiseSpeedMeters) * Mathf.Max(dt, 0.001f) *
                                   MaxTargetJumpRatio;
                if (_targetSampleValid && dt >= MinTargetSampleSeconds && !frameSpike &&
                    jump.sqrMagnitude <= believable * believable)
                {
                    _targetVelocity = Vector3.ClampMagnitude(jump / dt, 120f);
                }
                // ...otherwise the last good estimate is kept, so a held-over frame predicts the target
                // where it was last seen travelling instead of along a bogus heading.
                _lastTargetPosition = current;
                _targetSampleValid = true;
                float speed = Mathf.Max(1f, _profile.CruiseSpeedMeters);
                float timeToTarget = Mathf.Clamp(Vector3.Distance(transform.position, current) / speed,
                                                  0.05f, 8f);
                // One correction pass accounts for the target's displacement during the first estimate;
                // this is stable for ground vehicles and avoids the large lag of chasing the old centre.
                Vector3 predicted = current + _targetVelocity * timeToTarget;
                timeToTarget = Mathf.Clamp(Vector3.Distance(transform.position, predicted) / speed,
                                           0.05f, 8f);
                predicted = current + _targetVelocity * timeToTarget;
                _spot = predicted;
                _spotValid = true;
            }

            // ---- keep the aim point ----------------------------------------------------------------
            if (!_lost)
            {
                if (hasPoint)
                {
                    _spot = point;
                    _spotValid = true;
                }
                else if (_profile.Guidance == CasAirframeCatalog.GuidanceKind.FireAndForget || !_spotValid)
                {
                    // No seeker left to correct the round with, or a laser round that never had the spot.
                    EnterLost(_profile.Guidance == CasAirframeCatalog.GuidanceKind.LaserBeamRider
                        ? "the carrier never put the spot where it could be seen"
                        : "the seeker's target is gone");
                }
                // A laser rider keeps flying at the spot it last saw even after the VEHICLE is gone: the
                // spot is on the ground, and the brief's Kh-25 fails on losing the beam, not on the target
                // being destroyed. Nothing to do here - the remembered _spot is used as it stands.
            }

            // ---- the beam --------------------------------------------------------------------------
            if (!_lost && _profile.Guidance == CasAirframeCatalog.GuidanceKind.LaserBeamRider)
            {
                CheckLaserBeam();
            }

            if (_lost)
            {
                direction = StepLost(dt);
            }
            else if (!StepGuided(dt, frameSpike, out direction))
            {
                return false;
            }

            // ---- orientation -----------------------------------------------------------------------
            direction = direction.normalized;
            _intended = direction;
            transform.rotation = LookAlong(direction, transform.up);
            if (_lost && _profile.LostGuidanceRollDegreesPerSecond > 0f)
            {
                // A lost laser round tumbles: the roll is applied after the look rotation, so it survives
                // into the next frame's aim instead of being zeroed by it.
                transform.Rotate(0f, 0f, _profile.LostGuidanceRollDegreesPerSecond * dt, Space.Self);
            }
            // The attitude this frame ended with, which CasMissilePauseHold restores while the game is
            // paused and this component is not running.
            _intendedRotation = transform.rotation;

            return true;
        }

        /// <summary>
        /// The guided flight law. Returns false once the round has reached the impact point (or passed it),
        /// which is where the game's own impact handling takes over.
        /// </summary>
        private bool StepGuided(float dt, bool frameSpike, out Vector3 direction)
        {
            Vector3 position = transform.position;
            Vector3 toSpot = _spot - position;
            Vector3 flat = new Vector3(toSpot.x, 0f, toSpot.z);
            float range = flat.magnitude;

            if (range <= HandoverDistance)
            {
                direction = transform.forward;
                return false;   // close enough for the game's collision/impact handling
            }

            Vector3 flatDir = flat / range;
            float losDegrees = Mathf.Atan2(_spot.y - position.y, range) * Mathf.Rad2Deg;

            // ---- handing over to the game's impact ------------------------------------------------
            // A fixed 4 m window is smaller than the distance the round covers in one frame: the profile
            // flies at ~Mach 1, so a 50 fps frame is 6-7 m and a slow frame is 17 m. The round would step
            // straight over the window, keep steering, and then be pulled back around the target - the
            // "wild" close-range flight and the misses that follow it. The window therefore has to grow with
            // the speed, and a round that has stopped closing the range inside PassGuardDistance is treated
            // as past the target as well (which is what keeps a fast round from looping).
            float speed = Mathf.Max(1f, _profile.CruiseSpeedMeters);
            float window = Mathf.Max(HandoverDistance, speed * Mathf.Min(dt, 0.05f) * 1.5f);
            if (frameSpike)
            {
                // Across a pause the previous range is from before the gap: comparing against it would
                // "prove" the round had passed a target it is still flying at, and handing the round back
                // to the game there is the missile that suddenly changes its mind about the flight. The
                // guard simply starts again from the range the round now has.
                _lastRange = range;
            }
            else
            {
                // "Past the target" needs the range to OPEN, and to open by more than the jitter of one
                // step: a frame that simply covered several metres can nudge the range up by a fraction of
                // a metre while the round is still closing, and latching the hand-over on that is a round
                // that quits steering for the rest of its flight. A real fly-by opens the range by metres.
                float rangeOpened = range - _lastRange;
                bool passed = range <= PassGuardDistance &&
                              rangeOpened > speed * dt * 0.25f;
                if (range <= window || passed)
                {
                    direction = transform.forward;
                    return false;   // close enough for the game's collision/impact handling
                }
                _lastRange = range;
            }

            // The arch offset shrinks to zero at impact. A smooth boost fade starts early enough to let
            // the round pitch over before terminal range, including when the motor outlasts that range.
            float loftRatio = Mathf.Clamp01(range / Mathf.Max(1f, _profile.TerminalRangeMeters));
            float loft = _profile.LoftHeightMeters * loftRatio * loftRatio * (3f - 2f * loftRatio);
            float archDegrees = losDegrees + Mathf.Atan2(loft, Mathf.Max(range, 1f)) * Mathf.Rad2Deg;
            float turnDistance = speed * _profile.TerminalDiveDegrees /
                                 Mathf.Max(1f, _profile.TerminalTurnRateDegreesPerSecond);
            float terminalBlend = Mathf.SmoothStep(0f, 1f,
                Mathf.Clamp01((_profile.TerminalRangeMeters + turnDistance - range) /
                              Mathf.Max(1f, turnDistance)));
            float burnBlend = Mathf.SmoothStep(0f, 1f,
                Mathf.Clamp01((_profile.MotorBurnSeconds - _age) / BurnFadeSeconds));

            float heightLeft = Mathf.Max(0f, _releaseY + _profile.LoftHeightMeters - position.y);
            float climbCap = Mathf.Asin(Mathf.Clamp01(heightLeft /
                (speed * FlightPathLiftSeconds))) * Mathf.Rad2Deg;
            float boost = Mathf.Max(archDegrees,
                Mathf.Min(_profile.BoostClimbDegrees, climbCap));
            float desired = Mathf.Lerp(archDegrees, boost, burnBlend * (1f - terminalBlend));
            desired = Mathf.Clamp(desired, -MaxDiveDegrees, MaxClimbDegrees);

            float radians = desired * Mathf.Deg2Rad;
            Vector3 wanted = (flatDir * Mathf.Cos(radians) + Vector3.up * Mathf.Sin(radians)).normalized;

            // Blend control authority on the same transitions as the requested attitude.
            float rate = Mathf.Lerp(_profile.CruiseTurnRateDegreesPerSecond,
                                    _profile.BoostTurnRateDegreesPerSecond, burnBlend);
            rate = Mathf.Lerp(rate, _profile.TerminalTurnRateDegreesPerSecond, terminalBlend);
            float maxTurn = rate * Mathf.Min(dt, 0.05f) * Mathf.Deg2Rad;

            direction = Vector3.RotateTowards(transform.forward, wanted, maxTurn, 0f);
            return true;
        }

        /// <summary>
        /// The lost round: nose down at the profile's rate, with the laser round's yaw wander on top. The
        /// slow version is the AGM-65's "沿最后的惯性切线平缓向前下落"; the fast one is the Kh-25's
        /// "急剧俯冲下坠".
        /// </summary>
        private Vector3 StepLost(float dt)
        {
            _lostAge += dt;
            Vector3 direction = transform.forward;

            // Pitch down: rotate the current heading toward the vertical in the round's own vertical plane.
            Vector3 flat = new Vector3(direction.x, 0f, direction.z);
            Vector3 down = flat.sqrMagnitude > 0.0001f ? (flat.normalized - Vector3.up).normalized : Vector3.down;
            float pitch = _profile.LostGuidancePitchDegreesPerSecond * dt;
            direction = Vector3.RotateTowards(direction, down.normalized, pitch * Mathf.Deg2Rad, 0f);

            // Yaw wander, so a lost laser round does not just fly a clean arc into the ground.
            float yawRate = _profile.LostGuidanceYawDegreesPerSecond;
            if (yawRate > 0f)
            {
                float wander = Mathf.Sin(_lostAge * 6.5f) * yawRate * dt;
                direction = Quaternion.AngleAxis(wander, Vector3.up) * direction;
            }

            return direction.normalized;
        }

        /// <summary>
        /// The laser itself: the round only sees the spot while the carrier's nose is inside the profile's
        /// limit of it. That is the brief's "偏角不能超过30度~35度", and the reason the AI aircraft has to
        /// hold its run (CasLaserRunHold) for the weapon to work at all.
        /// </summary>
        private void CheckLaserBeam()
        {
            if (_carrier == null)
            {
                EnterLost("the carrier is gone");
                return;
            }

            float offAxis = Vector3.Angle(_carrier.forward, _spot - _carrier.position);
            if (offAxis > _profile.LaserMaxOffAxisDegrees)
            {
                EnterLost("the carrier's nose is " + offAxis.ToString("0") + " deg off the spot (limit " +
                          _profile.LaserMaxOffAxisDegrees.ToString("0") + " deg)");
            }
        }

        private void EnterLost(string reason)
        {
            if (_lost)
            {
                return;
            }
            _lost = true;
            _lostAge = 0f;

            // The carrier is free the moment the round stops needing it.
            CasLaserRunHold.End(_carrier);

            if (!_loggedLoss)
            {
                _loggedLoss = true;
                Log.Warn("CAS missile guidance lost: '" + _profile.MissileId + "' - " + reason + ". " +
                         (_profile.Guidance == CasAirframeCatalog.GuidanceKind.LaserBeamRider
                             ? "A laser round with no beam has no control left: it tumbles and dives in."
                             : "A round with no corrections left keeps its attitude and speed and falls " +
                               "away on the last tangent."));
            }
        }

        private void OnDestroy()
        {
            // Whatever happens to the round (impact, despawn, scene change), the carrier must be released.
            CasLaserRunHold.End(_carrier);
            FireSupportPatches.CasTargetSpreadPatch.ReleaseMissile(this);
        }

        /// <summary>
        /// Points the round along `direction` while keeping the roll it already had.
        ///
        /// Quaternion.LookRotation(direction, Vector3.up) is what made the round snap and roll: the moment
        /// the nose passes near the vertical (which the Kh-25's 80 degree terminal dive does on purpose) the
        /// up reference becomes degenerate and the resulting roll is arbitrary, so the model visibly flipped
        /// from frame to frame - the "missile veers by itself" the player reported, and much worse when a
        /// pause/hitch made two frames far apart. Projecting the CURRENT up onto the plane perpendicular to
        /// the new direction keeps the attitude continuous, and only falls back when the round really is
        /// pointing straight up or down.
        /// </summary>
        private static Quaternion LookAlong(Vector3 direction, Vector3 currentUp)
        {
            Vector3 up = Vector3.ProjectOnPlane(currentUp, direction);
            if (up.sqrMagnitude < 1e-4f)
            {
                up = Vector3.ProjectOnPlane(Vector3.forward, direction);
            }
            if (up.sqrMagnitude < 1e-4f)
            {
                up = Vector3.ProjectOnPlane(Vector3.right, direction);
            }
            return Quaternion.LookRotation(direction, up.normalized);
        }
    }
}

