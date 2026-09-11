using System;
using GHPC;
using GHPC.PhysicsHelpers;
using GHPC.Weaponry.Artillery;
using GHPC.Weapons;
using GHPC.Weapons.Artillery;
using HarmonyLib;
using UnityEngine;

namespace CustomFireSupport
{
    /// <summary>
    /// The patches that turn the artillery's anti-armour shell into a cargo (cluster) round.
    ///
    ///  1. ArtilleryBattery.DoSingleShot - Prefix: the round about to be spawned is a cargo round, so
    ///                                    remember the point the battery was called onto - plus that
    ///                                    round's own draw inside the battery's dispersion disc, so a
    ///                                    multi-round volley bursts over the same spread of points its
    ///                                    shells would have landed on. DoSingleShot never returns the
    ///                                    round, hence the small hand-off to patch 2.
    ///  2. LiveRound.Init                - Postfix: arm the ClusterMunition component on the round
    ///                                    DoSingleShot just created with its burst point (and its Prefix
    ///                                    disarms a pooled round that is being recycled by anything else).
    ///                                    A postfix because LiveRound.ID only exists once Init's body
    ///                                    has run, and the shot id is what keeps a recycled round from
    ///                                    opening on the previous shot's burst point.
    ///  3. LiveRound.DoUpdate            - Prefix: fly the cargo round at the burst point (100 m above
    ///                                    the called point) and open it there. The burst point is a
    ///                                    point, not a moment, so the mod does not depend on GHPC's
    ///                                    battery spawn height / angle / heading at all - a shell
    ///                                    spawned by any battery, at any geometry, still opens exactly
    ///                                    above the target.
    ///  4. LiveRound.Detonate            - Postfix: throw the submunition's visible fragments. See
    ///                                    <see cref="ClusterFragments"/> for why the mod spawns them
    ///                                    rather than letting the game's own spall-on-detonation field do
    ///                                    it (that path exists, but it hands out invisible rounds).
    ///
    /// Only the cargo round and its submunitions are affected. Every submunition is an ordinary game
    /// round that the vanilla pipeline flies, fuses, damages and draws, so nothing here can affect the
    /// rest of the battle.
    /// </summary>
    internal static class ClusterMunitionPatches
    {
        /// <summary>
        /// Set by patch 1, consumed by patch 2. A single slot of state is enough: the game spawns the
        /// round synchronously inside DoSingleShot, so the hand-off never spans a frame.
        /// </summary>
        private static ClusterLaunch _pending;
        private static AmmoType _pendingAmmo;
        private static Vector3 _pendingAimPoint;

        private static void ClearPending()
        {
            _pending = null;
            _pendingAmmo = null;
            _pendingAimPoint = Vector3.zero;
        }

        /// <summary>
        /// 1. The battery is about to spawn one round: if that round is a cargo round of ours, take the
        ///    point the mission was called onto while it is still on the battery.
        ///
        /// Both fields are private serialized ones on ArtilleryBattery, so they are read through
        /// AccessTools - the same way the shell templates read the battery's firing parameters.
        /// </summary>
        [HarmonyPatch(typeof(ArtilleryBattery), "DoSingleShot")]
        internal static class ClusterLaunchPatch
        {
            private static readonly AccessTools.FieldRef<ArtilleryBattery, BatteryMunitionsChoice> CurrentMunitionsRef =
                AccessTools.FieldRefAccess<ArtilleryBattery, BatteryMunitionsChoice>("_currentMunitions");

            private static readonly AccessTools.FieldRef<ArtilleryBattery, Vector3> TargetPointRef =
                AccessTools.FieldRefAccess<ArtilleryBattery, Vector3>("_currentTargetPoint");

            /// <summary>
            /// The battery's live dispersion radius (vanilla's <c>_randomDispersionRadiusMeters</c>, already
            /// scaled by the slot's DispersionMeters).
            ///
            /// Vanilla draws the round's SPAWN position from this disc, which is also where the round lands.
            /// A cargo round is burst over a point instead of landing on one, so the mod draws the same disc
            /// itself and bursts each round over its own point inside it - that is what turns a three-round
            /// volley into three separate cluster bursts spread around the called point instead of three
            /// bursts stacked on one spot.
            /// </summary>
            private static readonly AccessTools.FieldRef<ArtilleryBattery, float> CurrentRadiusRef =
                AccessTools.FieldRefAccess<ArtilleryBattery, float>("_currentRadius");

            private static bool _warnedNoTargetPoint;

            private static void Prefix(ArtilleryBattery __instance)
            {
                try
                {
                    ClearPending();
                    if (__instance == null)
                    {
                        return;
                    }

                    BatteryMunitionsChoice choice = CurrentMunitionsRef(__instance);
                    if (choice == null || choice.Ammo == null || choice.Ammo.AmmoType == null)
                    {
                        return;
                    }

                    ClusterLaunch launch = ClusterMunitionFactory.LaunchFor(choice.Ammo.AmmoType);
                    if (launch == null)
                    {
                        return; // an ordinary shell: nothing to do
                    }

                    Vector3 called = TargetPointRef(__instance);
                    if (called == Vector3.zero && !_warnedNoTargetPoint)
                    {
                        _warnedNoTargetPoint = true;
                        Log.Warn("cluster munition: the battery reports its target point as the world origin " +
                                 "(0, 0, 0), so the burst would be placed over the map origin instead of the " +
                                 "called point. Report this line - ArtilleryBattery._currentTargetPoint is " +
                                 "either not written or read wrong here.");
                    }

                    _pending = launch;
                    _pendingAmmo = choice.Ammo.AmmoType;
                    _pendingAimPoint = called + DispersionOffset(__instance);
                }
                catch (Exception ex)
                {
                    ClearPending();
                    Log.Error("cluster munition: could not read the cargo round's target point: " + ex);
                }
            }

            /// <summary>
            /// One round's own offset inside the battery's dispersion disc - the exact draw vanilla makes for
            /// a round's spawn position (a random radius in [-r, r] rotated to a random heading).
            /// </summary>
            private static Vector3 DispersionOffset(ArtilleryBattery battery)
            {
                float radius = CurrentRadiusRef(battery);
                if (radius <= 0f)
                {
                    return Vector3.zero;
                }
                float distance = UnityEngine.Random.Range(-radius, radius);
                return Quaternion.AngleAxis(UnityEngine.Random.Range(0f, 360f), Vector3.up) *
                       (distance * Vector3.forward);
            }
        }

        /// <summary>
        /// 2. The round exists now. LiveRound.Init is the hook the game itself uses to give a round its
        ///    identity (and it is where a pooled round starts its new life), so this is where the burst
        ///    parameters go on - and where a recycled round that is NOT a cargo round anymore loses them.
        ///
        /// The attach is a POSTFIX, not a prefix: <c>LiveRound.ID</c> is assigned inside Init's body, and
        /// the shot id is what keeps a recycled round from opening on the previous shot's burst point.
        /// </summary>
        [HarmonyPatch(typeof(LiveRound), "Init")]
        internal static class ClusterAttachPatch
        {
            /// <summary>Disarm before Init: a pooled round must never carry the previous shot's burst.</summary>
            private static void Prefix(LiveRound __instance)
            {
                try
                {
                    if (__instance == null)
                    {
                        return;
                    }
                    ClusterMunition stale = __instance.GetComponent<ClusterMunition>();
                    if (stale != null)
                    {
                        stale.Disarm();
                    }
                }
                catch (Exception ex)
                {
                    Log.Error("cluster munition: could not disarm a recycled round: " + ex);
                }
            }

            private static void Postfix(LiveRound __instance)
            {
                try
                {
                    if (__instance == null)
                    {
                        return;
                    }

                    // The aim point is handed back BY TakePending: consuming the pending state clears the
                    // stored aim point, and reading the field after that is the bug that made every burst
                    // happen at (0, 100, 0) - the world origin - whatever point the player called.
                    Vector3 aimPoint;
                    ClusterLaunch launch = TakePending(__instance.Info, out aimPoint);
                    if (launch == null)
                    {
                        return; // an ordinary round (or a submunition): nothing to arm
                    }

                    ClusterMunition carrier = __instance.GetComponent<ClusterMunition>();
                    if (carrier == null)
                    {
                        carrier = __instance.gameObject.AddComponent<ClusterMunition>();
                    }
                    carrier.Arm(launch, aimPoint, __instance.ID);

                    Log.Verbose("cluster munition: '" + carrier.ShellName + "' round #" + __instance.ID +
                                " armed towards " + carrier.AimPoint.ToString("0.#") + " - it will open at " +
                                carrier.BurstPoint.ToString("0.#") + " (" + carrier.BurstHeight.ToString("0") +
                                " m above the call), " + carrier.Submunitions + " x " + carrier.SubmunitionName + ".");
                }
                catch (Exception ex)
                {
                    Log.Error("cluster munition: could not arm the cargo round: " + ex);
                }
            }

            /// <summary>
            /// Consumes the pending launch, but only for the round it belongs to, and gives back the aim
            /// point that came with it. The aim point is read BEFORE the pending state is cleared: clearing
            /// it resets the stored aim point, and reading the field afterwards was exactly the bug that
            /// sent every burst to the world origin.
            /// </summary>
            private static ClusterLaunch TakePending(AmmoType ammo, out Vector3 aimPoint)
            {
                aimPoint = Vector3.zero;
                if (_pending == null || !ReferenceEquals(ammo, _pendingAmmo))
                {
                    return null;
                }
                ClusterLaunch launch = _pending;
                aimPoint = _pendingAimPoint;
                ClearPending();
                return launch;
            }
        }

        /// <summary>
        /// 3. Steer the cargo round at its burst point and open it there.
        ///
        /// This is a Prefix, i.e. it runs immediately before the round's own movement for the frame, and
        /// it rewrites the round's flight state rather than moving the transform: the game integrates
        /// that state and raycasts along the displacement it produces, so the round cannot be pulled off
        /// course by its spawn geometry, its drag or anything else. (A component Update could not do
        /// this - rounds are updated by LiveRoundBatchHandler, not by Unity.)
        /// </summary>
        [HarmonyPatch(typeof(LiveRound), "DoUpdate")]
        internal static class ClusterBurstPatch
        {
            private static readonly AccessTools.FieldRef<LiveRound, MotionState> MotionRef =
                AccessTools.FieldRefAccess<LiveRound, MotionState>("_currentMotionState");

            private static void Prefix(LiveRound __instance, float dt)
            {
                try
                {
                    // Cheap reject first: this runs for every live round of the battle every frame.
                    if (__instance == null || __instance.IsSpall || __instance.Pooled ||
                        !ClusterMunitionFactory.IsOurAmmo(__instance.Info))
                    {
                        return;
                    }

                    ClusterMunition carrier = __instance.GetComponent<ClusterMunition>();
                    if (carrier == null || !carrier.Armed || carrier.ShotId != __instance.ID)
                    {
                        return; // already opened, or a pooled round carrying an older shot's parameters
                    }

                    if (dt <= 0f)
                    {
                        dt = Time.deltaTime;
                    }
                    Steer(__instance, carrier, dt);
                }
                catch (Exception ex)
                {
                    Log.Error("cluster munition: steering failed: " + ex);
                }
            }

            private static void Steer(LiveRound round, ClusterMunition carrier, float dt)
            {
                Vector3 burstPoint = carrier.BurstPoint;
                Vector3 position = round.transform.position;
                Vector3 toBurst = burstPoint - position;
                float distance = toBurst.magnitude;

                float speed = carrier.CarrierSpeed > 0f ? carrier.CarrierSpeed : ClusterMunition.CarrierSpeedMetersPerSecond;
                float step = Mathf.Max(0.01f, speed * dt);

                if (distance <= step)
                {
                    Open(round, carrier, burstPoint);
                    return;
                }

                Vector3 direction = toBurst / distance;
                round.transform.forward = direction;

                MotionState state = MotionRef(round);
                state.position = position;
                state.velocity = direction * speed;
                MotionRef(round) = state;
            }

            /// <summary>
            /// The cargo round has arrived: open it, then take it out of the battle.
            ///
            /// The round is retired through the game's own "fell below minimum altitude" path (by moving
            /// it under the world) rather than by ForceDestroy, because that path is the one that also
            /// reports the shot and lets LiveRoundBatchHandler repool the object - a bare ForceDestroy on
            /// a round that never "impacted" would leave it waiting for a report that never comes. The
            /// player never sees the round move: it is teleported and destroyed in the same frame.
            /// </summary>
            private static void Open(LiveRound round, ClusterMunition carrier, Vector3 burstPoint)
            {
                carrier.Armed = false;   // exactly one burst per shot
                carrier.Bursts++;

                ClusterBurst.Open(burstPoint, carrier);

                round.transform.position = new Vector3(burstPoint.x, -1000f, burstPoint.z);
            }
        }

        /// <summary>
        /// 4. A submunition that goes off throws its own visible fragments.
        ///
        /// On Detonate, because that is the game's own "this round goes off" moment and it is reached by
        /// every NON-penetrating impact - i.e. exactly the ground hits a cluster round is used for. A
        /// submunition whose jet penetrated does not come here, and does not need to: the vanilla jet path
        /// already throws visible armour spall behind what it defeated.
        ///
        /// Cheap on a normal battle: this runs for every detonating round, so it rejects on a single bool
        /// read (<c>IsSpall</c>, which filters out the mod's own 1000+ fragments instantly), then on the
        /// marker component only our submunitions carry, then on that marker's one-shot flag.
        /// </summary>
        [HarmonyPatch(typeof(LiveRound), "Detonate")]
        internal static class ClusterDetonatePatch
        {
            private static void Postfix(LiveRound __instance)
            {
                try
                {
                    if (__instance == null || __instance.IsSpall)
                    {
                        return;
                    }
                    ClusterFragments.Throw(__instance);
                }
                catch (Exception ex)
                {
                    Log.Error("cluster munition: could not throw the submunition's fragments: " + ex);
                }
            }
        }
    }
}
