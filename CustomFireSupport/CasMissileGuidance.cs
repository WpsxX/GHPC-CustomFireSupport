using System;
using UnityEngine;
using GHPC;
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
    ///
    /// ---------------------------------------------------------------------------------------------
    /// AIR TARGETS (the player's "can the AGM-65 / Kh-25 attack a helicopter?")
    ///
    /// A round fired at an AIRCRAFT is the same round with a different flight, decided once, at
    /// SetTarget, from the launch target the payload factory already resolved (CasAirTargets.IsAirUnit:
    /// TargetShortNameUs Chopper / FastMover, or an IAircraft that reports itself a helicopter). With
    /// that flag set:
    ///
    ///   * the flight is StepGuidedAir: a pure 3-D lead pursuit with no loft, no arch and no terminal
    ///     dive (those exist to arc a round over the ground onto a target below), and every range that
    ///     decides the hand-over is the SLANT range - the horizontal-range test is what would hand an
    ///     airborne target's round over in mid-air while it was still hundreds of metres short;
    ///   * the intercept lead is iterated (profile.AirLeadIterations) instead of corrected once, because
    ///     a 60-80 m/s crossing target moves hundreds of metres during a 3-6 s flight;
    ///   * the turn-rate triple, the hand-over floor and the pass guard come from the profile's air
    ///     fields, and the target-velocity clamp is raised (a fast mover's per-frame step is above the
    ///     ground clamp);
    ///   * an air target that goes away - destroyed, or the seeker's spot lost - HANDS THE ROUND BACK to
    ///     the game's own impact handling on the line it is flying (HandBackToGame) instead of entering
    ///     the LOST fail state, so a miss cannot tumble into a fake ground hit. A broken LASER BEAM is
    ///     still EnterLost: that is the Kh-25's own fail state, not a lost target.
    ///
    /// With the flag unset, every line of the ground flight above is the line it always was: the air
    /// code is a separate method behind one bool, not a set of conditionals threaded through the law.
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

        /// <summary>
        /// Floor for the ratio test, for the very short frames of a high frame rate.
        /// </summary>
        private const float MinFrameSeconds = 0.005f;

        /// <summary>
        /// How far the round may travel in one frame and still be handed over to the game's impact
        /// handling against an AIR target, as a multiple of that travel. The ground figure (1.5, see
        /// StepGuided) exists to stop the round stepping over its hand-over point; an airborne target is a
        /// few metres across and moving, so the window is kept as tight as that constraint allows - one
        /// frame of flight - and the profile's AirHandoverDistanceMeters is the floor under it.
        /// </summary>
        private const float AirHandoverFrameScale = 1f;

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
        private bool _lost;
        private float _lostAge;

        /// <summary>
        /// The unit the round is flying at, and whether it is an AIRCRAFT (see CasAirTargets). Everything
        /// in this class that reads these two is an air-only branch: with <see cref="_airTarget"/> false
        /// the flight is the ground flight, byte for byte.
        /// </summary>
        private Unit _targetUnit;
        private bool _airTarget;

        /// <summary>
        /// Set when an AIR target's round is given back early rather than being declared lost: the mod
        /// stops steering and the game's own impact handling takes the round from here (see TryStep).
        /// </summary>
        private bool _handedToGame;

        internal bool Lost { get { return _lost; } }

        /// <summary>True while this round is flown as an anti-aircraft round (an air target, our sortie).</summary>
        internal bool AirTarget { get { return _airTarget; } }
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
            _lastRange = float.MaxValue;
            _referenceDt = 0f;
            _intended = Vector3.zero;
            _intendedRotation = Quaternion.identity;
            _targetUnit = null;
            _airTarget = false;
            _handedToGame = false;

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
            SetTarget(target, null);
        }

        /// <summary>
        /// Binds the round to the target it was launched at. <paramref name="targetUnit"/> is the launch
        /// target the payload factory already resolved (CasPayloadFactory.SetPendingImpact is handed
        /// `controller.FinalTarget`), and is preferred over the transform lookup because it is the unit
        /// itself rather than a guess from the aim point's parents.
        ///
        /// This is also where the round learns whether it is an ANTI-AIRCRAFT round: an air target takes
        /// the 3-D lead-pursuit flight below, a ground target takes the arch-and-dive flight this class
        /// has always flown. The kind is stated in one log line per round, so a log can say which flight
        /// was flown without guessing from the numbers.
        /// </summary>
        internal void SetTarget(Transform target, Unit targetUnit)
        {
            _target = target;
            _targetSampleValid = target != null;
            if (_targetSampleValid)
            {
                _lastTargetPosition = target.position;
            }

            _targetUnit = targetUnit != null ? targetUnit : CasAirTargets.UnitOf(target);
            _airTarget = CasAirTargets.IsAirUnit(_targetUnit);
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
                    // An air target may legitimately be a fast mover, whose per-frame displacement is
                    // above the ground clamp (a helicopter's is not); the clamp only exists to stop a
                    // near-zero dt turning a stationary vehicle's transform jitter into a bogus heading.
                    float clamp = _airTarget && _profile != null
                        ? Mathf.Max(120f, _profile.AirTargetVelocityClampMeters) : 120f;
                    _targetVelocity = Vector3.ClampMagnitude(jump / dt, clamp);
                }
                // ...otherwise the last good estimate is kept, so a held-over frame predicts the target
                // where it was last seen travelling instead of along a bogus heading.
                _lastTargetPosition = current;
                _targetSampleValid = true;
                float speed = Mathf.Max(1f, _profile.CruiseSpeedMeters);
                Vector3 predicted;
                if (_airTarget)
                {
                    // A helicopter at 60-80 m/s moves several hundred metres during a 3-6 second flight,
                    // and one correction pass leaves most of that as lag on a crossing target - the round
                    // then flies at where the target WAS. Iterating the intercept to a fixed point is the
                    // lead a missile seeker actually computes; two or three passes converge for these
                    // speeds and ranges. (Ground keeps its own single correction pass, unchanged.)
                    float airTime = Mathf.Clamp(Vector3.Distance(transform.position, current) / speed,
                                                0.05f, 8f);
                    int passes = Mathf.Clamp(_profile.AirLeadIterations, 1, 8);
                    for (int pass = 0; pass < passes; pass++)
                    {
                        predicted = current + _targetVelocity * airTime;
                        airTime = Mathf.Clamp(Vector3.Distance(transform.position, predicted) / speed,
                                              0.05f, 8f);
                    }
                    predicted = current + _targetVelocity * airTime;
                }
                else
                {
                    float timeToTarget = Mathf.Clamp(Vector3.Distance(transform.position, current) / speed,
                                                      0.05f, 8f);
                    // One correction pass accounts for the target's displacement during the first estimate;
                    // this is stable for ground vehicles and avoids the large lag of chasing the old centre.
                    predicted = current + _targetVelocity * timeToTarget;
                    timeToTarget = Mathf.Clamp(Vector3.Distance(transform.position, predicted) / speed,
                                               0.05f, 8f);
                    predicted = current + _targetVelocity * timeToTarget;
                }
                _spot = predicted;
                _spotValid = true;
            }

            // ---- the target going away -------------------------------------------------------------
            // An AIR target that is gone - destroyed, despawned, or shot down by somebody else - hands the
            // round back to the game's own impact handling on the line it is already flying, instead of
            // entering the LOST fail state. The fail states are the two weapons' characters (a nose that
            // falls away for the AGM-65, a 540 deg/s tumble for the Kh-25) and both end in the ground: for
            // an air target that turns a miss into a fake ground hit hundreds of metres from anything. The
            // ground behaviour is untouched - a ground round whose target dies still goes lost exactly as
            // it always did.
            if (!_lost && _airTarget && TargetGone())
            {
                HandBackToGame();
                direction = transform.forward;
                _intended = direction;
                _intendedRotation = transform.rotation;
                return false;
            }

            // ---- keep the aim point ----------------------------------------------------------------
            if (!_lost)
            {
                if (hasPoint)
                {
                    // THE LEAD IS ONLY USED AGAINST AN AIR TARGET, AND THAT IS DELIBERATE.
                    //
                    // `point` is the round's own aim (CasPayloadFactory.TryGetImpactPoint), and for the
                    // mod's missiles that is the target's LIVE CENTRE plus a ZERO offset (the profiles are
                    // guaranteed-hit, ImpactOffsetFor returns Vector3.zero). So the ground flight has always
                    // flown at the centre itself and thrown the prediction computed above away - which is
                    // exactly right for a 10 m/s vehicle that cannot move far in the last second, and it is
                    // why the ground law's remark says the single correction pass "avoids the large lag of
                    // chasing the old centre".
                    //
                    // A crossing helicopter at 60-80 m/s is the case that reasoning does not cover: pure
                    // pursuit of the current centre is a tail chase that arrives behind the target. An air
                    // target therefore KEEPS the iterated intercept point computed above, and the ground
                    // path below keeps the centre, untouched.
                    if (!_airTarget)
                    {
                        _spot = point;
                        _spotValid = true;
                    }
                }
                else if (_profile.Guidance == CasAirframeCatalog.GuidanceKind.FireAndForget || !_spotValid)
                {
                    // No seeker left to correct the round with, or a laser round that never had the spot.
                    if (_airTarget)
                    {
                        // Same reasoning as above, and for the laser round as well: an air target with no
                        // spot left is a shot that cannot be guided any more, not a control failure the
                        // round should be thrown into the ground over.
                        HandBackToGame();
                        direction = transform.forward;
                        _intended = direction;
                        _intendedRotation = transform.rotation;
                        return false;
                    }
                    EnterLost();
                }
                // A laser rider keeps flying at the spot it last saw even after the VEHICLE is gone: the
                // spot is on the ground, and the brief's Kh-25 fails on losing the beam, not on the target
                // being destroyed. Nothing to do here - the remembered _spot is used as it stands.
            }

            // ---- the beam --------------------------------------------------------------------------
            // NOTE: a broken BEAM is still EnterLost for an air target as well. That is not the target
            // being lost, it is the Kh-25's designed fail state ("losing the beam takes the round's
            // control with it") and it is the weapon's character, not a flight-geometry problem.
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

            if (_airTarget)
            {
                return StepGuidedAir(position, toSpot, dt, frameSpike, out direction);
            }

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
        /// THE ANTI-AIRCRAFT FLIGHT: a straight 3-D lead pursuit of the intercept point, and nothing else.
        ///
        /// WHY IT IS A SEPARATE LAW. The ground law is written in terms of the HORIZONTAL range: the
        /// hand-over window, the pass guard and the loft are all sized by it, and the requested attitude is
        /// an angle in the vertical plane that contains the horizontal line of sight. Against an airborne
        /// target every one of those is wrong in a different way:
        ///
        ///   * a round passing under a hovering helicopter has a horizontal range near zero while it is
        ///     still hundreds of metres away, so the "range &lt;= window" hand-over fires and the round is
        ///     released from guidance in mid-air, nowhere near the target;
        ///   * the pass guard tests the same horizontal range, so a legitimate crossing engagement - the
        ///     range to the LEAD POINT opens for a frame or two while the round is still turning onto it -
        ///     latches the round as "past the target";
        ///   * the loft exists to arc a round over the ground and drop it onto a target below; pointed at a
        ///     target that is already hundreds of metres up it is a wasted climb into a 30-80 degree dive,
        ///     which a crossing aircraft simply steps out from under.
        ///
        /// So an air target is flown as pure lead pursuit in three dimensions: the round turns at the
        /// profile's air rate onto the moving intercept point (computed in TryStep), and every range that
        /// decides anything here is the SLANT range - which is the range a round passing under an airborne
        /// target does NOT have going to zero. Nothing in this method is reachable with an air flag unset.
        /// </summary>
        private bool StepGuidedAir(Vector3 position, Vector3 toSpot, float dt, bool frameSpike,
            out Vector3 direction)
        {
            direction = transform.forward;
            float slant = toSpot.magnitude;
            float speed = Mathf.Max(1f, _profile.CruiseSpeedMeters);

            // The hand-over window: the tightest one that still cannot be stepped over in a single frame,
            // because the round's frozen straight line has to intersect a target a few metres across.
            float window = Mathf.Max(_profile.AirHandoverDistanceMeters,
                speed * Mathf.Min(dt, 0.05f) * AirHandoverFrameScale);
            if (frameSpike)
            {
                // Same reason as the ground guard: after a pause/hitch the previous range is from before
                // the gap and must not be used to "prove" a fly-by.
                _lastRange = slant;
            }
            else
            {
                float rangeOpened = slant - _lastRange;
                bool passed = slant <= _profile.AirPassGuardDistanceMeters &&
                              rangeOpened > speed * dt * 0.25f;
                if (slant <= window || passed)
                {
                    return false;   // close enough for the game's collision/impact handling
                }
                _lastRange = slant;
            }

            // Control authority: the profile's air triple, blended exactly as the ground law blends its own
            // (most authority while the motor burns, the terminal rate in the last stretch).
            float burnBlend = Mathf.SmoothStep(0f, 1f,
                Mathf.Clamp01((_profile.MotorBurnSeconds - _age) / BurnFadeSeconds));
            float turnDistance = speed * 1f / Mathf.Max(1f, _profile.AirTerminalTurnRateDegreesPerSecond);
            float terminalBlend = Mathf.SmoothStep(0f, 1f,
                Mathf.Clamp01((_profile.TerminalRangeMeters + turnDistance - slant) /
                              Mathf.Max(1f, turnDistance)));
            float rate = Mathf.Lerp(_profile.AirCruiseTurnRateDegreesPerSecond,
                                    _profile.AirBoostTurnRateDegreesPerSecond, burnBlend);
            rate = Mathf.Lerp(rate, _profile.AirTerminalTurnRateDegreesPerSecond, terminalBlend);

            Vector3 wanted = slant > 0.01f ? toSpot / slant : direction;
            float maxTurn = rate * Mathf.Min(dt, 0.05f) * Mathf.Deg2Rad;
            direction = Vector3.RotateTowards(transform.forward, wanted, maxTurn, 0f);
            return true;
        }

        /// <summary>
        /// True when this round's AIR target is no longer there to fly at: the transform is gone (Unity's
        /// destroyed-object equality), or the unit has been neutralized. Only ever consulted under the air
        /// flag; a ground round keeps the fail states it has always had.
        /// </summary>
        private bool TargetGone()
        {
            if (_target == null)
            {
                return true;
            }
            return _targetUnit != null && _targetUnit.Neutralized;
        }

        /// <summary>
        /// Stops steering an AIR target's round and gives it back to the game on the heading it already
        /// has. The laser carrier is released here for the same reason EnterLost releases it: the round no
        /// longer needs the run held.
        /// </summary>
        private void HandBackToGame()
        {
            if (_handedToGame)
            {
                return;
            }
            _handedToGame = true;
            CasLaserRunHold.End(_carrier);
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
                EnterLost();
                return;
            }

            float offAxis = Vector3.Angle(_carrier.forward, _spot - _carrier.position);
            if (offAxis > _profile.LaserMaxOffAxisDegrees)
            {
                EnterLost();
            }
        }

        private void EnterLost()
        {
            if (_lost)
            {
                return;
            }
            _lost = true;
            _lostAge = 0f;

            // The carrier is free the moment the round stops needing it.
            CasLaserRunHold.End(_carrier);

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

