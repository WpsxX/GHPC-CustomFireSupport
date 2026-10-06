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
    /// THE FLIGHT LAW (validated numerically before it was written - see _mountcheck/missile_sim3.py)
    ///
    /// Per frame, with d = the horizontal range to the impact point:
    ///
    ///     los      = atan2(spot.y - pos.y, d)                        the direct collision course
    ///     loft(d)  = LoftHeightMeters * clamp(d / TerminalRange, 0, 1)^2
    ///     arch     = los + atan2(loft, d)                            aim floated above the line
    ///     cone     = atan2(spot.y + d*tan(TerminalDive) - pos.y, d)  "still able to dive onto it"
    ///     desired  = min(arch, cone)
    ///     while burning, still outside TerminalRange and below the loft ceiling:
    ///              desired = max(desired, min(BoostClimbDegrees, cone))    the launch pull-up
    ///     desired  = clamp(desired, -85, +40)
    ///     turn     = (terminal | boost | cruise turn rate) * dt
    ///
    /// Two things make this safe to fly a guaranteed-hit weapon with:
    ///
    ///   * the LOFT is an aim point above the target that sinks back onto it as the range closes, so the
    ///     arch is shaped without ever steering the round away from the target for good, and
    ///   * the CONE is a hard geometric limit: it is the steepest line to the target the round could still
    ///     fly, so the round can never be lofted so high that it is no longer able to come down on the
    ///     target. Every flight in the envelope (2.2-3.0 km release, 300-800 m altitude, target on flat
    ///     ground or on a 120 m rise) passes within 2 m of the impact point.
    ///
    /// A previous version simply forced the flight-path angle to the profile's dive angle, which is not a
    /// collision course at all: from 2.6 km released at 500 m that meant hitting the ground a kilometre
    /// short, or - with the loft it was later given - sailing over the target. The cone is what fixes it.
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

        /// <summary>Inside this distance the mod hands the last few metres back to the game's own impact.</summary>
        private const float HandoverDistance = 4f;

        /// <summary>
        /// Inside this distance a round that has stopped closing the range counts as "past the target" and is
        /// handed over as well. See <see cref="StepGuided"/>.
        /// </summary>
        private const float PassGuardDistance = 120f;

        /// <summary>Set once the round is within <see cref="PassGuardDistance"/> and closing.</summary>
        private float _lastRange = float.MaxValue;

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
        internal bool TryStep(bool hasPoint, Vector3 point, float dt, out Vector3 direction)
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

            _age += dt;

            if (_target != null)
            {
                Vector3 current = _target.position;
                if (_targetSampleValid && dt > 0.0001f)
                {
                    _targetVelocity = Vector3.ClampMagnitude((current - _lastTargetPosition) / dt, 120f);
                }
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
            else if (!StepGuided(dt, out direction))
            {
                return false;
            }

            // ---- orientation -----------------------------------------------------------------------
            direction = direction.normalized;
            transform.rotation = LookAlong(direction, transform.up);
            if (_lost && _profile.LostGuidanceRollDegreesPerSecond > 0f)
            {
                // A lost laser round tumbles: the roll is applied after the look rotation, so it survives
                // into the next frame's aim instead of being zeroed by it.
                transform.Rotate(0f, 0f, _profile.LostGuidanceRollDegreesPerSecond * dt, Space.Self);
            }

            return true;
        }

        /// <summary>
        /// The guided flight law. Returns false once the round has reached the impact point (or passed it),
        /// which is where the game's own impact handling takes over.
        /// </summary>
        private bool StepGuided(float dt, out Vector3 direction)
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
            bool passed = range <= PassGuardDistance && range > _lastRange;
            if (range <= window || passed)
            {
                direction = transform.forward;
                return false;   // close enough for the game's collision/impact handling
            }
            _lastRange = range;

            // The arch: aim above the straight line, and let that offset sink back onto the target as the
            // range closes, so the round arrives from above without ever being steered off the target.
            bool burning = _age < _profile.MotorBurnSeconds;
            float loftRatio = Mathf.Clamp01(range / Mathf.Max(1f, _profile.TerminalRangeMeters));
            float loft = _profile.LoftHeightMeters * loftRatio * loftRatio;
            float archDegrees = losDegrees + Mathf.Atan2(loft, Mathf.Max(range, 1f)) * Mathf.Rad2Deg;

            // The cone: the steepest line to the target this round could still fly. Never go above it.
            float coneDegrees = Mathf.Atan2(
                _spot.y + range * Mathf.Tan(_profile.TerminalDiveDegrees * Mathf.Deg2Rad) - position.y,
                range) * Mathf.Rad2Deg;

            float desired = Mathf.Min(archDegrees, coneDegrees);

            // The launch pull-up: full thrust, outside the terminal run, and only while the arch's height
            // has not been reached - which is what bounds the zoom climb to the profile's own loft.
            if (burning && range > _profile.TerminalRangeMeters &&
                (position.y - _releaseY) < _profile.LoftHeightMeters)
            {
                desired = Mathf.Max(desired, Mathf.Min(_profile.BoostClimbDegrees, coneDegrees));
            }
            desired = Mathf.Clamp(desired, -MaxDiveDegrees, MaxClimbDegrees);

            float radians = desired * Mathf.Deg2Rad;
            Vector3 wanted = (flatDir * Mathf.Cos(radians) + Vector3.up * Mathf.Sin(radians)).normalized;

            // Control authority: the most at full thrust, the least on the long glide, and the profile's
            // own terminal figure once the target is close (the Kh-25 "硬掰机头" happens here).
            float rate = range <= _profile.TerminalRangeMeters
                ? _profile.TerminalTurnRateDegreesPerSecond
                : (burning ? _profile.BoostTurnRateDegreesPerSecond : _profile.CruiseTurnRateDegreesPerSecond);
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

