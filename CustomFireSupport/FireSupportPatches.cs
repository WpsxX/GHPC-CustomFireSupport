using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using FMODUnity;
using GHPC;
using GHPC.Effects;
using GHPC.PhysicsHelpers;
using GHPC.UI;
using GHPC.UI.Map;
using GHPC.Vehicle;
using GHPC.Weaponry;
using GHPC.Weaponry.CAS;
using GHPC.Weaponry.Interfaces;
using GHPC.Weapons;
using GHPC.Weapons.Artillery;
using GHPC.World;
using HarmonyLib;
using UnityEngine;

namespace CustomFireSupport
{
    /// <summary>
    /// All Harmony patches. Each one is a narrow gate: it never reimplements vanilla logic, it only
    /// lets the custom slots exist alongside it, plus the mod's own hit resolution for CAS (19-21).
    ///
    ///  1. MapController.InitControlState            - Prefix: build + inject the slots before vanilla
    ///                                                generates buttons / subscribes its map handlers.
    ///  2. FireMissionManager.FactionHasBatteries    - Postfix: the mission must look like it has
    ///                                                artillery, otherwise vanilla never subscribes
    ///                                                TopoMapCamera.MapClick += TryCallFireMission.
    ///  3. FireMissionManager.FactionHasReadyBatteries - Postfix: vanilla blocks a call when no ready
    ///                                                battery of that shell type is in its (stale) cache.
    ///  4. MapController.TryCallCAS                  - Prefix/Postfix: mark "the player is calling CAS
    ///                                                from the map" so a player call can be told apart
    ///                                                from a mission-scripted one.
    ///  5. CasSupportManager.SendCasSupport          - Prefix: route the call to the airframe the player
    ///                                                selected, re-rolling a bomb / rocket slot onto a
    ///                                                different aircraft + loadout first.
    ///  6. MapFireSupportPanel.AddButton             - Prefix: skip our injected objects (we build our
    ///                                                own buttons) and hide the vanilla buttons.
    ///  7. ArtilleryBattery.SendFireMission          - Prefix/Postfix: unlimited calls (Missions = -1) and
    ///                                                the instant volley of ImpactDelaySeconds &lt;= 0.
    ///  8. ArtilleryBattery.DoUpdate                 - Postfix: "no cooldown" (CooldownSeconds &lt;= 0)
    ///                                                by clearing the residual cooldown once the volley
    ///                                                has ended - the vanilla bookkeeping (which the panel
    ///                                                needs) is kept intact.
    ///  9. CasAirframeUnit.ReduceMissionsAvailable   - Prefix: unlimited sorties (Missions = -1).
    /// 10. CasAirframeUnit.ResetCooldown             - Prefix: per-slot sortie recharge (CooldownSeconds).
    /// 11. ArtilleryBattery.get_WeaponType           - Postfix: per-slot weapon type.
    /// 12. CASHardpoint.Fire                         - Prefix/Postfix: zeroes the launch deviation of the
    ///                                                hardpoints the mod flies itself (gun runs / rockets
    ///                                                / bombs) for the duration of the call and cycles the
    ///                                                gun-run belt; missiles keep the CasAccuracy scale.
    /// 13. CASController.SearchForTarget             - Postfix: multi-plane CAS target spreading (ported
    ///                                                from CheatMode) - planes that would all pick the same
    ///                                                target are re-routed to distinct ones. Prefix: the
    ///                                                AIR-TARGET GATE - an enemy aircraft near the point the
    ///                                                call was made against becomes the sortie's target
    ///                                                through CASController.CheatTargetUnit, but only after
    ///                                                vanilla's own visibility test from where the aircraft
    ///                                                is at that moment (see CasAirTargets; the field is
    ///                                                re-decided, and cleared, on every search).
    /// 14. CASController.SetLoadout                  - Prefix: activate the hardpoint manager chain a
    ///                                                donor prefab hides on an inactive child, and mark the
    ///                                                aircraft as ours (CustomCasMarker).
    /// 15. CASController.get_Velocity                - Postfix: analytic airspeed, so the launch inherits
    ///                                                what the aim computer assumes.
    /// 16. CASController.GetAimPosition              - Postfix: drop / lead from the LIVE range for our
    ///                                                sorties (vanilla solves it once for a fixed range).
    /// 17. CASAttackMeta.Init                        - Postfix: a gun run fires from ONE hardpoint (the
    ///                                                runtime gun closest to the centreline) instead of
    ///                                                alternating between every pylon-mounted copy.
    /// 18. CASHardpointManager.MultiFire             - Prefix: frame-rate independent gun burst (releases
    ///                                                every owed round per frame, so 3900 rpm and the
    ///                                                pilot's travel compensation actually match).
    /// 19. CASHardpoint.LaunchSingleMunition         - Prefix: the mod's own hit logic, step 1 - decide the
    ///                                                impact point of the round about to be spawned from
    ///                                                the slot's CasAccuracy (0 = the target's centre,
    ///                                                1 = a 5 m circle) and whether it falls (a bomb).
    /// 20. LiveRound.DoUpdate                        - Prefix: the mod's own hit logic, step 2 - take that
    ///                                                round to that point every frame: straight-line flight
    ///                                                for bullets / rockets, a gravity-aware terminal
    ///                                                correction for bombs. The game's ballistics / aim /
    ///                                                guidance cannot move the round off the point.
    /// 21. LiveRound.Init                            - Postfix: attach the impact aim to the round
    ///                                                SpawnMunition just created (pooling-safe).
    /// 22. LiveRound.doImpactVFX                     - Postfix: guaranteed impact effect when the vanilla
    ///                                                lookup finds nothing (an AP round's own effect is a
    ///                                                spark, invisible from a kilometre away).
    /// 23. CASHardpoint.LaunchSingleMunition         - Prefix/Postfix: play the gun one-shot on every
    ///                                                second round only (audio cost at 3900 rpm).
    ///
    /// 24-26 (ClusterMunitionPatches.cs) - the artillery's ANTI-ARMOUR shell is a cargo (cluster)
    ///                                                round: ArtilleryBattery.DoSingleShot remembers the
    ///                                                called point, LiveRound.Init arms the round with it,
    ///                                                LiveRound.DoUpdate flies it to 100 m above that point
    ///                                                and opens it into HEDP submunitions. See that file.
    /// 27. LiveRound.DoImpactDecal                  - Prefix: the terrain CRATER for the mod's own
    ///                                                air-to-ground missiles AND for the rounds of its
    ///                                                own gun runs (both factions - any round
    ///                                                CasPayloadFactory.IsOurRound identifies). It takes
    ///                                                the game's own decal call over (so the game cannot
    ///                                                stamp a second one), because LiveRound.doImpactVFX
    ///                                                can skip the decal altogether when the impact
    ///                                                EFFECT lookup was empty and neither call result is
    ///                                                checked anywhere; and because the game refuses a
    ///                                                Dirt decal outright for Bullet / Autocannon ammo,
    ///                                                which is every one of these 30 mm clones. A missile
    ///                                                is passed through as itself; a gun round is passed
    ///                                                through as CasPayloadFactory.CachedDecalAmmo, a
    ///                                                cached clone whose only difference is the effect
    ///                                                SIZE, so the round's own descriptor still resolves
    ///                                                its own explosion. Vanilla, campaign and enemy
    ///                                                rounds - and every non-Dirt surface - run the
    ///                                                original body untouched. For one of the mod's own
    ///                                                missiles that struck something which is NOT the
    ///                                                terrain, the ground UNDER the detonation is
    ///                                                additionally scarred (StampGroundScar): a 250 kg
    ///                                                warhead that goes off on a hull still craters the
    ///                                                dirt beneath it, while the object's own (vanilla)
    ///                                                decal for the hit itself runs unchanged.
    /// </summary>
    internal static class FireSupportPatches
    {
        [HarmonyPatch(typeof(MapController), "InitControlState")]
        internal static class InitControlStatePatch
        {
            private static void Prefix(MapController __instance)
            {
                try
                {
                    CustomSupportRegistry.PrepareMission(__instance);
                }
                catch (Exception)
                {
                }
            }
        }

        [HarmonyPatch(typeof(FireMissionManager), "FactionHasBatteries")]
        internal static class FactionHasBatteriesPatch
        {
            private static void Postfix(Faction faction, ref bool __result)
            {
                if (!__result && CustomSupportRegistry.HasCustomArtilleryFor(faction))
                {
                    __result = true;
                }
            }
        }

        [HarmonyPatch(typeof(FireMissionManager), "FactionHasReadyBatteries")]
        internal static class FactionHasReadyBatteriesPatch
        {
            private static void Postfix(Faction faction, IndirectFireMunitionType munitionType, ref bool __result)
            {
                if (!__result && CustomSupportRegistry.HasReadyCustomArtillery(faction, munitionType))
                {
                    __result = true;
                }
            }
        }

        [HarmonyPatch(typeof(MapController), "TryCallCAS")]
        internal static class TryCallCasPatch
        {
            private static void Prefix()
            {
                CustomSupportRegistry.BeginPlayerCasCall();
            }

            private static void Postfix()
            {
                CustomSupportRegistry.EndPlayerCasCall();
            }
        }

        [HarmonyPatch(typeof(CasSupportManager), "SendCasSupport")]
        internal static class SendCasSupportPatch
        {
            /// <summary>
            /// The full leading parameter list is declared on purpose: Harmony matches patch arguments
            /// by name first and falls back to position, and the fallback would otherwise line
            /// "unitFaction" up with "supportPosition". Declaring supportPosition keeps both matching
            /// strategies correct for <c>SendCasSupport(Vector3, Faction, int, bool, Unit)</c>.
            ///
            /// Returns false (with an empty result) only when the selected custom airframe cannot be
            /// flown at all, so the call fails cleanly instead of throwing inside vanilla.
            /// </summary>
            private static bool Prefix(
                CasSupportManager __instance,
                Vector3 supportPosition,
                Faction unitFaction,
                ref int casIndex,
                ref MapMissionResult __result)
            {
                try
                {
                    // A manager the mod created for a CAS-less mission may have been built before the
                    // player unit spawned; place its deploy point now that the position is known.
                    CustomSupportRegistry.EnsureCreatedCasDeployPoints();

                    // Scripted CAS events call this directly; only redirect a real player call.
                    if (!CustomSupportRegistry.PlayerCasCallInProgress)
                    {
                        return true;
                    }

                    CasAirframeUnit airframe;
                    if (!CustomSupportRegistry.TryGetActiveCasAirframe(unitFaction, out airframe))
                    {
                        return true;
                    }

                    CasAirframeUnit[] array = unitFaction == Faction.Blue ? __instance.BlueCasAirframes : __instance.RedCasAirframes;
                    int index = array == null ? -1 : Array.IndexOf(array, airframe);
                    if (index < 0)
                    {
                        return true;
                    }

                    casIndex = index;

                    // Bomb / rocket slots fly a different airframe with a different loadout each call
                    // (gun runs keep their designated airframe). The re-roll mutates this very airframe
                    // object, so the panel button, the manager's array and this call all stay in sync.
                    CustomSupportRegistry.TryRerollAirframeForCall(unitFaction, airframe);

                    // Vanilla instantiates the prefab and immediately does GetComponent<CASController>()
                    // on the clone. A donor whose controller is not on its own root would throw there
                    // (NullReferenceException) and abort the whole call, so refuse it cleanly.
                    // CasDonorProvider now resolves donors to the aircraft root, which makes this a
                    // pure safety net rather than the normal path.
                    if (airframe.airframePrefab == null)
                    {
                        __result = MapMissionResult.Empty;
                        return false;
                    }
                    if (airframe.airframePrefab.GetComponent<CASController>() == null)
                    {
                        CASController nested = airframe.airframePrefab.GetComponentInChildren<CASController>(true);
                        __result = MapMissionResult.Empty;
                        return false;
                    }

                    // The game's own gate is "array[casIndex].IsReady", and when it refuses, the call
                    // returns MapMissionResult.Empty and NO AIRCRAFT IS SENT AT ALL. IsReady is
                    // "_missionsAvailable > 0 && RemainingCooldown <= 0", and the game lowers both on its
                    // own (ReduceMissionsAvailable on every send, ResetCooldown to the 120 s RechargeTime
                    // when a sortie returns). Make the airframe ready through those same fields when the
                    // slot's configuration says it should be, instead of bypassing the gate - the bypass
                    // also skips the game's mission bookkeeping, which is what broke the earlier attempt.
                    CasSlot calledSlot;
                    if (CustomSupportRegistry.TryGetCasSlot(airframe, out calledSlot) && calledSlot.Config != null)
                    {
                        CasCallReadinessRepair.EnsureReadyForCall(airframe, calledSlot.Config.Missions,
                            calledSlot.Config.CooldownSeconds);
                    }
                    CustomSupportRegistry.MarkNextSpawnedSortie();
                }
                catch (Exception)
                {
                }
                return true;
            }

            /// <summary>
            /// Always clears the "the next spawned aircraft is ours" flag: vanilla may return before it
            /// instantiates anything (no ready airframe), and a stale flag would mark a later scripted
            /// aircraft as ours.
            /// </summary>
            private static void Postfix()
            {
                CustomSupportRegistry.ClearMarkedSortie();
            }
        }

        [HarmonyPatch(typeof(MapFireSupportPanel), "AddButton")]
        internal static class AddButtonPatch
        {
            /// <summary>false = skip vanilla's button creation for this entry.</summary>
            private static bool Prefix(IMapSupportInfo supportInfo)
            {
                if (supportInfo == null)
                {
                    return true;
                }

                if (CustomSupportRegistry.IsOurSupportInfo(supportInfo))
                {
                    // Our slots get their own buttons (vanilla would merge every CAS entry into one
                    // button and only ever show one smoke / illumination battery).
                    return false;
                }

                return !CustomSupportRegistry.HideVanillaThisMission;
            }
        }

        /// <summary>
        /// Ported from CheatMode: "Missions = -1" means unlimited calls (the deducted count is restored
        /// after every successful call, so the button keeps showing the same number), and an instant
        /// arrival (ImpactDelaySeconds &lt;= 0) fires the whole volley on the frame of the call so the
        /// panel never sits on "Incoming".
        /// </summary>
        [HarmonyPatch(typeof(ArtilleryBattery), "SendFireMission")]
        internal static class ArtillerySendFireMissionPatch
        {
            private static readonly AccessTools.FieldRef<ArtilleryBattery, int> MissionsRef =
                AccessTools.FieldRefAccess<ArtilleryBattery, int>("_missionsAvailable");
            private static readonly AccessTools.FieldRef<ArtilleryBattery, int> ShotQuotaRef =
                AccessTools.FieldRefAccess<ArtilleryBattery, int>("_currentShotQuota");
            private static readonly AccessTools.FieldRef<ArtilleryBattery, int> ShotCounterRef =
                AccessTools.FieldRefAccess<ArtilleryBattery, int>("_shotCounter");
            private static readonly AccessTools.FieldRef<ArtilleryBattery, float> RemainingDelayRef =
                AccessTools.FieldRefAccess<ArtilleryBattery, float>("<RemainingDelay>k__BackingField");

            private static readonly MethodInfo DoSingleShotMethod =
                AccessTools.Method(typeof(ArtilleryBattery), "DoSingleShot");

            private sealed class CallState
            {
                internal ArtillerySlot Slot;
                internal int MissionsOriginal = -1;
            }

            private static void Prefix(ArtilleryBattery __instance, out CallState __state)
            {
                __state = null;
                ArtillerySlot slot;
                if (!CustomSupportRegistry.TryGetArtillerySlot(__instance, out slot))
                {
                    return; // Vanilla battery or scripted strike: untouched.
                }

                __state = new CallState { Slot = slot, MissionsOriginal = MissionsRef(__instance) };
            }

            private static void Postfix(ArtilleryBattery __instance, bool __result, CallState __state)
            {
                if (__state == null || !__result)
                {
                    return;
                }

                ArtillerySlot slot = __state.Slot;

                if (slot.Config.Missions < 0 && __state.MissionsOriginal >= 0)
                {
                    MissionsRef(__instance) = __state.MissionsOriginal;
                }

                if (!slot.InstantVolley)
                {
                    return; // Normal arrival: the battery's own values already match the config.
                }

                // Instant: fire every remaining round of this volley on the same frame and end the
                // volley cleanly (RemainingDelay 0), so the panel returns to Ready immediately.
                try
                {
                    int quota = ShotQuotaRef(__instance);
                    if (DoSingleShotMethod != null && !AarController.InAar)
                    {
                        for (int i = ShotCounterRef(__instance); i < quota; i++)
                        {
                            DoSingleShotMethod.Invoke(__instance, null);
                        }
                    }
                    ShotCounterRef(__instance) = quota;
                    RemainingDelayRef(__instance) = 0f;
                }
                catch (Exception)
                {
                }
            }
        }

        /// <summary>
        /// Ported from CheatMode: "CooldownSeconds = -1" (or any value &lt;= 0) means no cooldown. The
        /// vanilla bookkeeping is kept while the volley runs (the panel needs a cooldown &gt; 0 for the
        /// whole strike, otherwise CooldownManager drops the entry and the button freezes on
        /// "Incoming"); the residual cooldown is only cleared once the battery has stopped firing.
        /// </summary>
        [HarmonyPatch(typeof(ArtilleryBattery), "DoUpdate")]
        internal static class ArtilleryNoCooldownPatch
        {
            private static readonly AccessTools.FieldRef<ArtilleryBattery, float> RemainingCooldownRef =
                AccessTools.FieldRefAccess<ArtilleryBattery, float>("<RemainingCooldown>k__BackingField");

            private static void Postfix(ArtilleryBattery __instance)
            {
                if (__instance == null || __instance.IsFiring)
                {
                    return;
                }

                ArtillerySlot slot;
                if (!CustomSupportRegistry.TryGetArtillerySlot(__instance, out slot) || slot.Config.CooldownSeconds > 0f)
                {
                    return;
                }

                RemainingCooldownRef(__instance) = 0f;
            }
        }

        /// <summary>Ported from CheatMode: "Missions = -1" never depletes a custom sortie.</summary>
        [HarmonyPatch(typeof(CasAirframeUnit), "ReduceMissionsAvailable")]
        internal static class CasUnlimitedMissionsPatch
        {
            private static bool Prefix(CasAirframeUnit __instance)
            {
                CasSlot slot;
                if (!CustomSupportRegistry.TryGetCasSlot(__instance, out slot))
                {
                    return true;
                }
                return slot.Config.Missions >= 0;
            }
        }

        /// <summary>
        /// The mod's own hit logic, step 1: decide WHERE the round about to be fired will land.
        ///
        /// This runs immediately before CASHardpoint.SpawnMunition creates the round (SpawnMunition
        /// never hands the round out, but it does call LiveRound.Init on it, which is where
        /// CasImpactAttachPatch picks this stash up and attaches CasImpactAim to that exact round).
        ///
        /// The impact point is the locked target's centre plus one draw from the slot's impact circle
        /// (CasPayloadFactory.SampleImpactOffset): SlotN_CasAccuracy = 0 gives the centre itself (a
        /// guaranteed hit), 1 gives anywhere inside 5 m, 0.5 inside 2.5 m. The point is decided here,
        /// once per round; the target's live position is re-read every frame while the round flies, so
        /// a moving target is tracked and the round cannot drift off it.
        ///
        /// Nothing here touches the hardpoint: the launch deviation is irrelevant from now on because
        /// CasImpactAimPatch overrides the flight direction on the round's first frame.
        /// </summary>
        [HarmonyPatch(typeof(CASHardpoint), "LaunchSingleMunition")]
        internal static class CasImpactPointPatch
        {
            private static bool Prefix(CASHardpoint __instance, ref Transform target)
            {
                bool missile = false;
                try
                {
                    if (__instance != null)
                    {
                        // Register the live hardpoint wrapper before classification. Unity can
                        // deserialize the runtime template into a distinct AmmoType object.
                        bool runtimeMissile = __instance.Type == CASAttackType.AirToGroundMissile &&
                            CasPayloadFactory.IsRuntimeHardpoint(__instance) &&
                            CasFireChainRepair.IsOurSortie(__instance.GetComponentInParent<CASController>());
                        if (runtimeMissile)
                        {
                            CasPayloadFactory.RegisterRuntimeMissile(__instance.Ammo);
                        }
                        // A same-name native AGM-65/Kh-25 is not ours. Ownership comes from the runtime
                        // hardpoint marker; the AmmoType identity is registered only inside that boundary.
                        missile = runtimeMissile && CasPayloadFactory.IsOurMissile(__instance.Ammo);
                    }
                    // Never carry a stale point into this round.
                    CasPayloadFactory.ClearPendingImpact();

                    if (__instance == null || (!missile && !CasPayloadFactory.UsesImpactResolver(__instance)))
                    {
                        return true;
                    }
                    if (__instance.TotalMunitionsRemaining <= 0) return true;

                    CASController controller = __instance.GetComponentInParent<CASController>();
                    // The native manager already passes its locked Transform to this call. FinalTarget
                    // is not a substitute for that argument: it can be cleared while a lock still exists.
                    Unit launchTarget = missile && target != null ? CasAirTargets.UnitOf(target) : null;
                    if (launchTarget == null && controller != null) launchTarget = controller.FinalTarget;
                    if (launchTarget == null && controller != null && missile)
                    {
                        launchTarget = CasMissileAttackRun.RecoverLaunchTarget(controller, __instance.Ammo);
                    }
                    Transform launchPoint = missile && target != null ? target :
                        (launchTarget != null ? launchTarget.Center : null);
                    if (missile && controller != null && launchTarget != null &&
                        CasMissileAttackRun.IsAgm65(__instance.Ammo))
                    {
                        // AGM-65 is the only A/B salvo path. Kh-25 deliberately keeps the
                        // controller's single target so both laser rounds remain on that target.
                        launchTarget = CasMissileAttackRun.SelectAgmTarget(controller, __instance, launchTarget);
                        if (launchTarget == null || launchTarget.Center == null)
                        {
                            // Do not create an AGM-65 with a null guidance target when both the
                            // planned B target and the valid A fallback have disappeared.
                            return false;
                        }
                        // The native argument remains A for both trigger pulls. The committed AGM plan
                        // owns B, so only this path replaces the native point with the selected unit.
                        launchPoint = launchTarget.Center;
                        target = launchPoint;
                    }
                    if (controller == null || launchPoint == null)
                    {
                        if (missile)
                        {
                            return false;
                        }
                        return true;
                    }

                    // The controller we just resolved is handed on, so the slot lookup does not have to
                    // walk the hierarchy a second time for every round of a 140-round burst.
                    float accuracy = CasPayloadFactory.SlotAccuracy(controller, __instance);
                    Vector3 offset = CasPayloadFactory.ImpactOffsetFor(__instance, accuracy);
                    if (missile) target = launchPoint;
                    CasPayloadFactory.SetPendingImpact(launchPoint, offset, __instance.Ammo,
                        CasPayloadFactory.IsGravityAware(__instance), controller.transform, launchTarget);

                    // A LASER-GUIDED missile needs its carrier to stay on the run: the round only sees the
                    // spot while that aircraft's nose is within the profile's limit of it, and the game's own
                    // run ends about two seconds after the shot while the round needs seven. The hold is
                    // registered here, at the launch that creates the beam (CasLaserRunHold does the rest).
                    // Hold registration occurs after LiveRound.Init confirms that this round exists.
                }
                catch (Exception)
                {
                    CasPayloadFactory.ClearPendingImpact();
                    if (missile)
                    {
                        return false;
                    }
                    return true;
                }
                return true;
            }

            private static void Postfix() { CasPayloadFactory.ClearPendingImpact(); }

            private static Exception Finalizer(Exception __exception)
            {
                CasPayloadFactory.ClearPendingImpact();
                return __exception;
            }
        }

        /// <summary>
        /// Exact synchronous launch boundary. No scene-wide nearest-aircraft lookup is allowed:
        /// the hardpoint and refAmmo arguments identify the real launcher and ammo. A nested spawn
        /// has its own scope and cannot erase/consume its caller's context.
        /// </summary>
        [HarmonyPatch(typeof(CASHardpoint), "SpawnMunition")]
        internal static class CasMissileSpawnContextPatch
        {
            internal sealed class LaunchContext
            {
                internal LaunchContext Previous;
                internal AmmoType Ammo;
                internal Transform Target;
                internal Transform Carrier;
                internal Unit Unit;
                internal Vector3 SpawnPoint;
                internal LiveRound Round;
            }

            [ThreadStatic] private static LaunchContext _active;

            private static void Prefix(CASHardpoint __instance, AmmoCodexScriptable refAmmo,
                Transform target, Vector3 spawnPoint, out LaunchContext __state)
            {
                // Even an unrelated nested SpawnMunition masks the outer scope until it returns.
                __state = new LaunchContext { Previous = _active };
                _active = __state;
                if (__instance == null || refAmmo == null || refAmmo.AmmoType == null ||
                    __instance.Type != CASAttackType.AirToGroundMissile ||
                    !CasPayloadFactory.IsRuntimeHardpoint(__instance)) return;
                CASController controller = __instance.GetComponentInParent<CASController>();
                if (controller == null) return;
                CasPayloadFactory.RegisterRuntimeMissile(refAmmo.AmmoType);
                __state.Ammo = refAmmo.AmmoType;
                // LaunchSingleMunition's ref target already carries AGM A/B selection. It must
                // not be selected again at Init, which would overwrite B with shared FinalTarget.
                __state.Target = target;
                __state.Unit = CasAirTargets.UnitOf(target);
                __state.Carrier = controller.transform;
                __state.SpawnPoint = spawnPoint;
            }

            internal static LaunchContext Capture(LiveRound round)
            {
                LaunchContext context = _active;
                if (context == null || context.Ammo == null || context.Target == null ||
                    context.Carrier == null || context.Round != null || round == null ||
                    round.Info == null || round.IsSpall || !round.NpcRound || round.Shooter != null ||
                    (round.transform.position - context.SpawnPoint).sqrMagnitude > 4f ||
                    !string.Equals(round.name, "live cas muntion " + context.Ammo.Name,
                        StringComparison.Ordinal)) return null;
                // The generated name fallback is scoped to THIS SpawnMunition only, and the native
                // spawned-object name is checked too. A same-name arbitrary Init cannot take it.
                if (!ReferenceEquals(round.Info, context.Ammo) &&
                    !string.Equals(round.Info.Name, context.Ammo.Name,
                        StringComparison.OrdinalIgnoreCase)) return null;
                context.Round = round;
                CasPayloadFactory.RegisterRuntimeMissile(round.Info);
                return context;
            }

            internal static bool OwnsRound(LiveRound round)
            {
                return _active != null && ReferenceEquals(_active.Round, round);
            }

            private static Exception Finalizer(LaunchContext __state, Exception __exception)
            {
                if (__state != null && ReferenceEquals(_active, __state)) _active = __state.Previous;
                return __exception;
            }
        }

        /// <summary>
        /// CAS sortie recharge is the slot's CooldownSeconds directly, in seconds (the game's native
        /// 120 s is removed); &lt;= 0 means no recharge at all.
        /// </summary>
        [HarmonyPatch(typeof(CasAirframeUnit), "ResetCooldown")]
        internal static class CasResetCooldownPatch
        {
            private static readonly AccessTools.FieldRef<CasAirframeUnit, float> RemainingCooldownRef =
                AccessTools.FieldRefAccess<CasAirframeUnit, float>("<RemainingCooldown>k__BackingField");

            private static bool Prefix(CasAirframeUnit __instance)
            {
                CasSlot slot;
                if (!CustomSupportRegistry.TryGetCasSlot(__instance, out slot))
                {
                    return true;
                }

                float recharge = slot.Config.CooldownSeconds <= 0f
                    ? 0f
                    : slot.Config.CooldownSeconds;
                RemainingCooldownRef(__instance) = recharge;
                return false;
            }
        }

        /// <summary>
        /// Airframes donated by the loaded-asset scan (SU-22, A-10, ...) carry their CASHardpointManager
        /// on a child node (the exported prefab has it on a "HardpointHolder" child). If that child, or
        /// any node above it, is inactive at instantiation time, the vanilla SetLoadout lookup
        /// (GetComponentInChildren, which skips inactive objects) fails and the summoned aircraft can
        /// never fire. This prefix finds the manager including inactive and activates its whole chain
        /// before the vanilla lookup runs; if it is genuinely absent, the hierarchy is dumped once so
        /// the donor can be fixed instead of guessed at.
        /// </summary>
        [HarmonyPatch(typeof(CASController), "SetLoadout")]
        internal static class CasSetLoadoutManagerPatch
        {
            private static void Prefix(CASController __instance, CASLoadoutScriptable loadout)
            {
                try
                {
                    if (__instance == null)
                    {
                        return;
                    }

                    // Mark the aircraft as one of our sorties so the precision patch can tell it apart
                    // from vanilla / enemy CAS (whose pylons keep the game's own spread). Two ways in:
                    // the loadout is one this mod built, or the CAS call that is spawning this aircraft
                    // right now was routed to one of our airframes (a slot flying the game's own
                    // loadout keeps that loadout, so the marker has to come from the call instead).
                    bool ours = CustomSlotBuilder.IsOurLoadout(loadout) ||
                                CustomSupportRegistry.ConsumeMarkedSortie();
                    if (ours)
                    {
                        CustomCasMarker marker = __instance.gameObject.GetComponent<CustomCasMarker>();
                        if (marker == null) marker = __instance.gameObject.AddComponent<CustomCasMarker>();
                        marker.Bind(__instance);
                    }

                    GameObject root = __instance.gameObject;

                    CASHardpointManager manager = root.GetComponentInChildren<CASHardpointManager>(true);
                    if (manager != null)
                    {
                        // Bring the manager and every inactive ancestor to life so the active-only
                        // GetComponentInChildren inside the vanilla SetLoadout can see it. Walk the
                        // WHOLE chain up to the root: the manager node itself may be active while an
                        // intermediate ancestor is inactive (activeInHierarchy is then false).
                        Transform t = manager.transform;
                        while (t != null)
                        {
                            if (!t.gameObject.activeSelf)
                            {
                                t.gameObject.SetActive(true);
                            }
                            t = t.parent;
                        }
                        // DoConfig() runs inside the vanilla method, right after this, and it refuses the
                        // whole loadout - leaving the aircraft unable to fire for its entire sortie - when
                        // the hardpoint prefab list is neither a single entry nor one entry per attach
                        // point. Repair that here, while it is still possible (see CasFireChainRepair).
                        CasFireChainRepair.EnsureLoadoutConfigurable(manager, loadout);
                        return;
                    }

                }
                catch (Exception)
                {
                }
            }

        }

        [HarmonyPatch(typeof(ArtilleryBattery), "get_WeaponType")]
        internal static class ArtilleryWeaponTypePatch
        {
            private static void Postfix(ArtilleryBattery __instance, ref IndirectFireWeaponType __result)
            {
                IndirectFireWeaponType configured;
                if (CustomSupportRegistry.TryGetWeaponType(__instance, out configured))
                {
                    __result = configured;
                }
            }
        }

        /// <summary>
        /// Makes every round launched by a CAS aircraft inherit the plane's REAL velocity.
        ///
        /// The aircraft is a kinematic Rigidbody moved with MovePosition (CASController.FixedUpdate),
        /// and a kinematic body does not report its own motion through Rigidbody.velocity: the engine
        /// does not integrate it, so Velocity can read back stale or zero values. The fire-control
        /// chain reads exactly that property for the launch inherit vector
        /// (CASHardpointManager.Airspeed -> Controller.Velocity -> _rb.velocity), while the ballistic
        /// aim assumes the round leaves at muzzle velocity PLUS the aircraft's TrueAirSpeed
        /// (CASController.OnFinal, GetCustomComputer). When the two disagree, every inherited round -
        /// the unified gun run and all rocket pods, whose missiles travel far slower than the plane -
        /// lands short of the aim point, which is why gun runs and rocket salvos walk into the ground
        /// while bombs (inherit = false) stay accurate.
        ///
        /// The aircraft's actual motion is analytic and exact: it moves along its own forward at
        /// _currentSpeed (that is literally what MovePosition applies), so Velocity is replaced by
        /// forward * _currentSpeed. The launch inherit then equals what the aim computer assumed
        /// (both vectors are parallel, magnitudes add) and impacts land on the aim point.
        /// </summary>
        [HarmonyPatch(typeof(CASController), "get_Velocity")]
        internal static class CasInheritVelocityPatch
        {
            private static readonly AccessTools.FieldRef<CASController, Rigidbody> RigidbodyRef =
                AccessTools.FieldRefAccess<CASController, Rigidbody>("_rb");
            private static readonly AccessTools.FieldRef<CASController, Transform> TransformRef =
                AccessTools.FieldRefAccess<CASController, Transform>("_transform");
            private static readonly AccessTools.FieldRef<CASController, float> SpeedRef =
                AccessTools.FieldRefAccess<CASController, float>("_currentSpeed");

            private static void Postfix(CASController __instance, ref Vector3 __result)
            {
                try
                {
                    if (__instance == null)
                    {
                        return;
                    }
                    Transform transform = TransformRef(__instance);
                    if (transform == null)
                    {
                        Rigidbody rb = RigidbodyRef(__instance);
                        transform = rb != null ? rb.transform : null;
                        if (transform == null)
                        {
                            return;
                        }
                    }
                    __result = transform.forward * SpeedRef(__instance);
                }
                catch (Exception)
                {
                }
            }
        }

        /// <summary>
        /// Live-range aim for our continuous-fire attacks (the unified gun run and our own rocket
        /// salvos).
        ///
        /// Vanilla GetAimPosition solves the fall-of-shot once for a FIXED range:
        /// num2 = ReleaseDistance - 0.7 * burstDuration * airspeed. That formula was designed for a
        /// bomb dropped in one go / a short multi-release: it picks the range the aircraft is meant to
        /// be at when the salvo centre passes the target. A STREAMED burst is different - the aircraft
        /// keeps closing while it fires, so every round of the gun run is aimed with the drop of a
        /// range the plane is no longer at. At the start of the burst the real range is LONGER than
        /// num2, so the compensation is too small and the rounds land SHORT of the target - the burst
        /// walks into the ground in front of it. That is exactly the "落点靠前" the players reported.
        ///
        /// This postfix re-solves the drop and the moving-target lead from the LIVE range on every
        /// aim tick (every ~0.2 s), so a streamed burst tracks the target all the way in. Only our
        /// own sorties are touched: GunRun (no vanilla gun exists, every runtime gun is ours) and
        /// Rockets on an aircraft carrying the CustomCasMarker. Bombs, missiles and vanilla / enemy
        /// CAS keep the game's own formula byte for byte.
        /// </summary>
        [HarmonyPatch(typeof(CASController), "GetAimPosition")]
        internal static class CasLiveAimPatch
        {
            /// <summary>Point-blank and very-long ranges are left to the vanilla formula.</summary>
            private const float MinLiveRange = 40f;

            private const float MaxLiveRange = 2200f;

            private static readonly AccessTools.FieldRef<CASController, CASAttackType> AttackTypeRef =
                AccessTools.FieldRefAccess<CASController, CASAttackType>("_finalAttackType");
            private static readonly AccessTools.FieldRef<CASController, BallisticComputer> BcRef =
                AccessTools.FieldRefAccess<CASController, BallisticComputer>("_cachedBallisticComputer");
            private static readonly AccessTools.FieldRef<CASController, bool> LastKnownRef =
                AccessTools.FieldRefAccess<CASController, bool>("_targetIsLastKnownPosition");

            private static readonly MethodInfo GetTargetVelocityMethod =
                AccessTools.Method(typeof(CASController), "GetTargetVelocity", Type.EmptyTypes);
            private static readonly Func<CASController, Vector3> GetTargetVelocity =
                CachedDelegate.Create<Func<CASController, Vector3>>(GetTargetVelocityMethod);

            private static void Postfix(CASController __instance, ref Vector3 __result)
            {
                try
                {
                    if (__instance == null)
                    {
                        return;
                    }

                    // Our own weapons only; everything else keeps the vanilla aim. Bombs are included:
                    // their release point is solved from the same fixed-range formula
                    // (ReleaseDistance - 0.7 * duration * airspeed), which is only right for a single
                    // drop at that one distance - a stick of bombs released while the aircraft keeps
                    // closing is released too early / too late for every bomb except the nominal one,
                    // which is what made bomb slots inaccurate. Re-solving from the live range aims
                    // each release at the target.
                    CASAttackType type = AttackTypeRef(__instance);
                    bool ours = __instance.GetComponent<CustomCasMarker>() != null;
                    bool ourRockets = type == CASAttackType.Rockets && ours;
                    bool ourBombs = type == CASAttackType.Bombs && ours;
                    if (type != CASAttackType.GunRun && !ourRockets && !ourBombs)
                    {
                        return;
                    }

                    if (LastKnownRef(__instance) || __instance.FinalTarget == null ||
                        __instance.FinalTarget.Center == null)
                    {
                        return; // firing at a remembered position: vanilla already points exactly there.
                    }

                    BallisticComputer bc = BcRef(__instance);
                    if (bc == null || bc.OutOfRange)
                    {
                        return; // the shared firing table is already exhausted; keep the vanilla solution.
                    }

                    Vector3 targetPos = __instance.FinalTarget.Center.position;
                    Vector3 flat = targetPos - __instance.transform.position;
                    flat.y = 0f;
                    float range = flat.magnitude;
                    if (range < MinLiveRange || range > MaxLiveRange)
                    {
                        return; // point-blank or absurdly long: keep the vanilla solution.
                    }

                    float drop = bc.GetFallOfShot(range);
                    if (bc.OutOfRange)
                    {
                        return; // this range is outside the firing table; keep the vanilla solution.
                    }

                    float flightTime = bc.GetFlightTime(range);

                    Vector3 targetVelocity = Vector3.zero;
                    try
                    {
                        if (GetTargetVelocity != null)
                        {
                            targetVelocity = GetTargetVelocity(__instance);
                        }
                        else if (GetTargetVelocityMethod != null)
                        {
                            targetVelocity = (Vector3)GetTargetVelocityMethod.Invoke(__instance, null);
                        }
                    }
                    catch (Exception)
                    {
                        // Moving-target lead is a refinement; a failure must not break the aim.
                    }

                    // Identical composition to vanilla: aim ABOVE the target by the drop of the LIVE
                    // range (so the fall brings the round onto it) plus the target's motion lead.
                    __result = targetPos + targetVelocity * flightTime + Vector3.up * drop;
                }
                catch (Exception)
                {
                }
            }
        }

        /// <summary>
        /// CAS accuracy, applied at fire time.
        ///
        /// SlotN_CasAccuracy is a radius in metres divided by five (CasPayloadFactory.AccuracyRadius):
        /// 0 = hit the target's centre, 1 = a 5 m circle, 0.5 = 2.5 m. For the gun runs, rocket pods and
        /// bombs of our sorties that is made true by the mod's own impact resolver - the point is drawn
        /// in CasImpactPointPatch and the round is taken there by CasImpactAimPatch - so this patch
        /// zeroes those hardpoints' launch deviation instead of scaling it: a steered round is not moved
        /// by the deviation anyway, and a bomb needs an exact release so its terminal correction stays
        /// small. The value is restored right after the Fire() call.
        ///
        /// Missiles use their own guidance component, so for them the knob keeps its historical
        /// meaning here: a multiplier of the hardpoint's own launch deviation (1 = natural, 0.5 = half,
        /// 0 = none).
        ///
        /// The patch also cycles the gun belt (CasPayloadFactory.AdvanceBelt) so a burst mixes the real
        /// rounds; that part applies to every one of our hardpoints.
        /// </summary>
        [HarmonyPatch(typeof(CASHardpoint), "Fire")]
        internal static class CasAccuracyPatch
        {
            private static readonly AccessTools.FieldRef<CASHardpoint, float> DeviationRef =
                AccessTools.FieldRefAccess<CASHardpoint, float>("_launchDeviation");

            private struct DeviationState
            {
                internal bool Scaled;
                internal float Original;
            }

            private static void Prefix(CASHardpoint __instance, out DeviationState __state)
            {
                __state = default(DeviationState);
                try
                {
                    if (__instance == null)
                    {
                        return;
                    }

                    // Cycle the gun belt so a burst mixes the real rounds (4x PGU-14/B API + 1x
                    // PGU-13/B HEI for NATO, 2x OFZ-30 HEI + 1x BR-30 AP for Pact). No-op for the
                    // hardpoints that have no belt.
                    CasPayloadFactory.AdvanceBelt(__instance);

                    bool ours = CasPayloadFactory.IsRuntimeHardpoint(__instance) ||
                                CasPayloadFactory.IsOurSortie(__instance);
                    if (!ours)
                    {
                        return; // vanilla / enemy CAS: leave the game's own values alone.
                    }

                    // The mod flies these rounds itself - the impact point is drawn in
                    // CasImpactPointPatch and the round is taken there by CasImpactAimPatch - so the
                    // release is made exact: any launch deviation would only give the correction more
                    // work to do (and for a bomb, a bigger terminal nudge to look at).
                    if (CasPayloadFactory.UsesImpactResolver(__instance))
                    {
                        __state.Original = DeviationRef(__instance);
                        __state.Scaled = true;
                        DeviationRef(__instance) = 0f;
                        return;
                    }

                    float accuracy = CasPayloadFactory.SlotAccuracy(__instance);
                    if (Mathf.Approximately(accuracy, 1f))
                    {
                        return; // natural spread.
                    }

                    __state.Original = DeviationRef(__instance);
                    __state.Scaled = true;
                    DeviationRef(__instance) = CustomSlotBuilder.ScaleValue(__state.Original, accuracy);
                }
                catch (Exception)
                {
                }
            }

            private static void Postfix(CASHardpoint __instance, DeviationState __state)
            {
                if (!__state.Scaled || __instance == null)
                {
                    return;
                }
                DeviationRef(__instance) = __state.Original;
            }
        }

        /// <summary>
        /// A gun run fires from ONE barrel, not from every pylon.
        ///
        /// CASHardpointManager.SetUpHardpoints instantiates the loadout's hardpoint prefab on EVERY
        /// attach point (a single-entry prefab list is reused, which is how the runtime gun is mounted),
        /// so the A-10's 11 stations each get their own gun. CASAttackMeta then cycles one hardpoint per
        /// trigger pull, which made a "burst" alternate between eleven muzzles across the wingspan - the
        /// two parallel impact rows the player sees and the reason a gun run could not hit anything.
        ///
        /// This keeps only the runtime gun whose muzzle sits closest to the aircraft's centreline (on
        /// the A-10 that is a fuselage station, i.e. outside the skin, so the rounds do not immediately
        /// hit the launching aircraft) and drops the other copies from the attack, so the whole
        /// 140-round burst leaves one muzzle.
        /// </summary>
        [HarmonyPatch(typeof(CASAttackMeta), "Init")]
        internal static class CasGunSingleHardpointPatch
        {
            private static readonly AccessTools.FieldRef<CASAttackMeta, List<CASHardpoint>> IncludedRef =
                AccessTools.FieldRefAccess<CASAttackMeta, List<CASHardpoint>>("_hardpointsIncluded");
            private static readonly AccessTools.FieldRef<CASAttackMeta, int> FiringIndexRef =
                AccessTools.FieldRefAccess<CASAttackMeta, int>("_firingIndex");

            private static void Postfix(CASAttackMeta __instance)
            {
                try
                {
                    if (__instance == null || __instance.UniqueType != CASAttackType.GunRun)
                    {
                        return;
                    }
                    List<CASHardpoint> included = IncludedRef(__instance);
                    if (included == null || included.Count <= 1)
                    {
                        return;
                    }

                    CASHardpoint chosen = null;
                    float bestLateral = float.MaxValue;
                    for (int i = 0; i < included.Count; i++)
                    {
                        CASHardpoint candidate = included[i];
                        if (!CasPayloadFactory.IsRuntimeGun(candidate))
                        {
                            continue;
                        }

                        CASController controller = candidate.GetComponentInParent<CASController>();
                        Vector3 origin = controller != null ? controller.transform.position : candidate.transform.position;
                        Vector3 right = controller != null ? controller.transform.right : Vector3.right;
                        float lateral = Mathf.Abs(Vector3.Dot(candidate.transform.position - origin, right));
                        if (lateral < bestLateral)
                        {
                            bestLateral = lateral;
                            chosen = candidate;
                        }
                    }

                    if (chosen == null)
                    {
                        return;
                    }

                    included.Clear();
                    included.Add(chosen);
                    FiringIndexRef(__instance) = 0;

                    // Align the muzzle with the aircraft's own forward axis: the pilot's ballistics
                    // assume the rounds leave along the flight vector, and a pylon attach point can be
                    // pitched a few degrees (bomb racks often are). The muzzle stays where the pylon is
                    // (outside the skin) - only its direction is squared up.
                    CASController owner = chosen.GetComponentInParent<CASController>();
                    if (owner != null)
                    {
                        chosen.transform.rotation = owner.transform.rotation;
                    }

                }
                catch (Exception)
                {
                }
            }
        }

        /// <summary>
        /// Frame-rate independent gun burst.
        ///
        /// Vanilla MultiFire does "fire one round, WaitForSeconds(interval)" per pull. WaitForSeconds
        /// cannot resolve below one frame, so a 3900 rpm burst (65 rounds/s) really fired at the frame
        /// rate: at 60 fps ~3700 rpm, but at 40 fps only ~2400 rpm, stretching the burst far past the
        /// duration CASController used to compute the aim point - the rounds then walk past the target.
        ///
        /// This replaces the coroutine for our gun runs with a time-accumulating loop: every frame it
        /// releases however many rounds the elapsed time is owed, so the burst always lasts
        /// TriggerPulls * TriggerPullInterval seconds no matter the frame rate, and several rounds can
        /// leave in one frame when the frame rate dips.
        /// </summary>
        [HarmonyPatch(typeof(CASHardpointManager), "MultiFire")]
        internal static class CasGunBurstPatch
        {
            private static readonly AccessTools.FieldRef<CASHardpointManager, bool> BusyRef =
                AccessTools.FieldRefAccess<CASHardpointManager, bool>("_busyFiring");
            private static readonly MethodInfo FireMetaMethod =
                AccessTools.Method(typeof(CASHardpointManager), "Fire", new[] { typeof(CASAttackMeta) });

            /// <summary>
            /// The same private method as an open delegate. A burst pulls the trigger once per round -
            /// 140 times for a gun run - and MethodInfo.Invoke boxes an argument array every time; on the
            /// frame the burst starts that is exactly the kind of per-round cost that shows up as a
            /// stutter while the gun fires.
            /// </summary>
            private static readonly Action<CASHardpointManager, CASAttackMeta> FireMeta =
                CachedDelegate.Create<Action<CASHardpointManager, CASAttackMeta>>(FireMetaMethod);

            private static bool Prefix(CASHardpointManager __instance, CASAttackMeta meta, ref IEnumerator __result)
            {
                // Every attack of one of our sorties, not just the gun run: the vanilla MultiFire leaves
                // _busyFiring true for good when its coroutine is interrupted (the aircraft leaving at the
                // end of a pass is enough), and every later attack on that manager is then refused - the
                // "the plane came back and dropped nothing the second time" report. This replacement
                // always clears the flag, and releases at the authored rate instead of at the frame rate.
                if (__instance == null || meta == null ||
                    !CasFireChainRepair.IsOurSortie(__instance.GetComponentInParent<CASController>()))
                {
                    return true; // vanilla / enemy aircraft: keep the game's own coroutine.
                }

                // A gun run whose attack entry lost its burst fires ONE round and is over. CASHardpointManager.Fire only reaches MultiFire when
                // TriggerPulls > 1, so a rebuilt entry carrying the default 1 means a single trigger pull.
                // The unified gun's own belt is the authority for our gun, so restore it before the burst
                // starts (this has to live here, not in the iterator: an iterator body is compiled into a
                // generated state machine, out of reach of a static call-graph check).
                if (meta.UniqueType == CASAttackType.GunRun && meta.TriggerPulls < 2)
                {
                    CasPayloadFactory.ApplyGunRateOfFire(meta);
                }

                __result = Burst(__instance, meta, CasGunAudio.AirframeNameOf(__instance));
                return false;
            }

            private static IEnumerator Burst(CASHardpointManager manager, CASAttackMeta meta, string gunAirframeName)
            {
                BusyRef(manager) = true;

                // Sustained gun sound for the whole burst.
                //
                // The mod's OWN recordings are used here (see CasGunAudio): a GAU-8 set with separate
                // close / mid / far takes and a GSh-30 loop + stop tail, played from an AudioSource with
                // an inverse-square fade. The game's FMOD emitter stays as the fallback for when the
                // sound bundle is not installed, so a missing bundle degrades to the old behaviour
                // instead of a silent strafe.
                CasGunAudio.Handle gunSound = null;
                StudioEventEmitter gunAudio = null;
                bool audioStopped = false;
                bool busyCleared = false;
                try
                {
                    try
                    {
                        // The emitter hangs off the runtime gun hardpoint, so it travels with the
                        // aircraft and its distance to the player is the distance the sound fades over.
                        StudioEventEmitter anchor = CasPayloadFactory.FindGunEmitter(meta);
                        if (anchor != null)
                        {
                            gunSound = CasGunAudio.Begin(anchor.transform, gunAirframeName);
                        }
                    }
                    catch (Exception)
                    {
                    }

                    if (gunSound == null)
                    {
                        try
                        {
                            gunAudio = CasPayloadFactory.FindGunEmitter(meta);
                            if (gunAudio != null)
                            {
                                gunAudio.Play();
                            }
                        }
                        catch (Exception)
                        {
                        }
                    }

                    int total = Mathf.Max(1, meta.TriggerPulls);
                    float interval = Mathf.Max(0.0005f, meta.TriggerPullInterval);
                    int fired = 0;
                    float elapsed = 0f;
                    float nextShotAt = 0f;

                    while (fired < total)
                    {
                        while (fired < total && elapsed + 1e-5f >= nextShotAt)
                        {
                            if (FireMeta != null)
                            {
                                FireMeta(manager, meta);
                            }
                            else if (FireMetaMethod != null)
                            {
                                FireMetaMethod.Invoke(manager, new object[] { meta });
                            }
                            fired++;
                            nextShotAt += interval;
                        }
                        // Fade with the distance and pick up a range change while the burst runs.
                        CasGunAudio.Update(gunSound);
                        yield return null;
                        elapsed += Time.deltaTime;
                    }

                    // The burst is over: end the sound and free the manager now.
                    if (gunSound != null)
                    {
                        CasGunAudio.End(gunSound);   // plays the GSh-30 falling-off tail
                        audioStopped = true;
                    }
                    else if (gunAudio != null)
                    {
                        try
                        {
                            gunAudio.Stop();
                            audioStopped = true;
                        }
                        catch (Exception)
                        {
                        }
                    }
                    BusyRef(manager) = false;
                    busyCleared = true;

                }
                finally
                {
                    if (!audioStopped && gunAudio != null)
                    {
                        try
                        {
                            gunAudio.Stop();
                        }
                        catch (Exception)
                        {
                        }
                    }
                    if (!busyCleared)
                    {
                        BusyRef(manager) = false;
                    }
                }
            }
        }

        /// <summary>
        /// Throttles the gun's per-round one-shot to every other round.
        ///
        /// 3900 rpm is 65 FMOD one-shots per second, each of which may start a speed-of-sound coroutine;
        /// that is a lot of audio churn for a sound whose event is longer than the gap. Playing it on
        /// every second round still sounds like a continuous burst and halves the cost.
        /// </summary>
        [HarmonyPatch(typeof(CASHardpoint), "LaunchSingleMunition")]
        internal static class CasGunAudioThrottlePatch
        {
            private static void Prefix(CASHardpoint __instance, out string __state)
            {
                __state = null;
                try
                {
                    if (__instance == null || !CasPayloadFactory.IsRuntimeGun(__instance))
                    {
                        return;
                    }
                    if ((__instance.TotalMunitionsLaunched & 1) == 0)
                    {
                        return; // keep the sound on even rounds
                    }
                    __state = CasPayloadFactory.GetAudioEvent(__instance);
                    if (!string.IsNullOrEmpty(__state))
                    {
                        CasPayloadFactory.SetAudioEvent(__instance, string.Empty);
                    }
                }
                catch (Exception)
                {
                }
            }

            private static void Postfix(CASHardpoint __instance, string __state)
            {
                if (__instance != null && !string.IsNullOrEmpty(__state))
                {
                    CasPayloadFactory.SetAudioEvent(__instance, __state);
                }
            }
        }

        /// <summary>
        /// Guarantees that every hit by one of the mod's own rounds produces an impact effect and the
        /// autocannon explosion sound.
        ///
        /// The vanilla path is ParticleEffectsManager.CreateImpactEffectOfType, which resolves an effect
        /// from the round's ImpactEffectDescriptor; when that lookup finds nothing it returns null and
        /// the impact is completely invisible. This postfix only fires when the vanilla effect was NOT
        /// created, then spawns the round's own detonation / terrain-impact prefab (the donor round's,
        /// i.e. the game's 30mm HE explosion) and plays the game's autocannon explosion event - so a
        /// gun run can never be silent and invisible, whatever the effect database does.
        /// </summary>
        [HarmonyPatch(typeof(LiveRound), "doImpactVFX")]
        internal static class CasImpactEffectFallbackPatch
        {
            private static readonly AccessTools.FieldRef<LiveRound, bool> TerrainVfxRef =
                AccessTools.FieldRefAccess<LiveRound, bool>("_didTerrainImpactVfx");
            private static readonly AccessTools.FieldRef<LiveRound, bool> NonTerrainVfxRef =
                AccessTools.FieldRefAccess<LiveRound, bool>("_didNonTerrainImpactVfx");
            private static readonly AccessTools.FieldRef<LiveRound, bool> RicochetRef =
                AccessTools.FieldRefAccess<LiveRound, bool>("_ricochet");

            /// <summary>Warhead state: whether the round's fuze completed (or its jet fired).</summary>
            private static readonly AccessTools.FieldRef<LiveRound, bool> FuzeRef =
                AccessTools.FieldRefAccess<LiveRound, bool>("_fuzeCompleted");

            private static readonly AccessTools.FieldRef<LiveRound, bool> JetRef =
                AccessTools.FieldRefAccess<LiveRound, bool>("_jetActive");

            private static void Postfix(LiveRound __instance, bool terrainHit)
            {
                try
                {
                    if (__instance == null || __instance.Info == null)
                    {
                        return;
                    }

                    if (CasPayloadFactory.IsOurMissile(__instance.Info))
                    {
                        NoteMissileImpact(__instance, terrainHit);
                        return;
                    }

                    if (!CasPayloadFactory.IsOurRound(__instance.Info))
                    {
                        return; // not one of our rounds (or a spall fragment of one).
                    }

                    if (RicochetRef(__instance))
                    {
                        return; // ricochets have their own (vanilla) effect.
                    }
                    if (terrainHit ? TerrainVfxRef(__instance) : NonTerrainVfxRef(__instance))
                    {
                        return; // the game's own effect was created - leave it alone.
                    }
                    CasPayloadFactory.SpawnFallbackImpact(__instance, terrainHit);
                }
                catch (Exception)
                {
                }
            }

            /// <summary>
            /// The air-to-ground missile's impact makes sure a high-explosive warhead is HEARD. The game
            /// plays a round's impact sound through
            /// ImpactSFXManager.PlaySimpleImpactAudio with "fuzed" set from this round's own warhead state;
            /// when the fuze did not complete it downgrades a bomb or missile detonation to a kinetic clang
            /// (see the switch in that method), which is exactly "an explosion with no explosion sound". The
            /// missile's explosion effect itself comes from the bomb's own effect descriptor, so only the
            /// sound needs the fallback.
            ///
            /// </summary>
            private static void NoteMissileImpact(LiveRound round, bool terrainHit)
            {
                bool fuzed = FuzeRef(round) || JetRef(round);
                if (fuzed)
                {
                    return; // the game played the warhead's own explosion sound.
                }

                CasPayloadFactory.PlayFuzedImpactAudio(round.Info, round.transform.position);
            }
        }

        /// <summary>
        /// The ground crater for the mod's own air-to-ground MISSILES and for the rounds of its own GUN
        /// RUNS - plus the ground scar under one of the mod's own missiles that detonated on a unit or an
        /// object instead of on the terrain (<see cref="StampGroundScar"/>) - and for nothing else.
        ///
        /// WHY THE GAME'S OWN STEP CAN PRODUCE NO MARK. A terrain crater is created in exactly one place:
        /// ImpactDecalsManager.CreateImpactDecalOfType, called from LiveRound.DoImpactDecal
        /// (LiveRound.cs:1406), which is reached only from LiveRound.doImpactVFX. Two gates suppress it:
        ///
        ///   1. doImpactVFX returns BEFORE DoImpactDecal when ParticleEffectsManager
        ///      .CreateImpactEffectOfType answered null (LiveRound.cs:1487-1491). The effect and the decal
        ///      are resolved from the same per-round ImpactEffectDescriptor / ImpactDecalDescriptor pair
        ///      through two separate export-time caches, and neither call's result is checked by the
        ///      caller, so this failure is completely silent.
        ///   2. Even when the call happens, CreateImpactDecalOfType returns null for a combination the
        ///      decal database has no entry for (the game's debug-only warning path needs
        ///      ImpactDecalsManager.IsDebug, which a shipped build never sets), for a round whose
        ///      descriptor asks for no decal at all (ImpactDecalsManager.cs:22-25), and - the reason a gun
        ///      run used to leave nothing - for ANY round on Dirt whose EffectSize is Bullet or
        ///      Autocannon (ImpactDecalsManager.cs:405-417). A terrain hit always arrives as Dirt
        ///      (LiveRound.cs:296), and these gun rounds ARE 30 mm autocannon clones, so that refusal is
        ///      exactly the vanilla 30 mm behaviour the player asked to change for the mod's rounds only.
        ///
        /// WHY THIS HOOKS DoImpactDecal AND NOT doImpactVFX. Because gate 1 means doImpactVFX can skip
        /// the decal altogether, and because gate 2 leaves no trace on the round, the only place that
        /// knows whether a mark was made is the decal call itself. This prefix therefore takes that call
        /// over for the mod's rounds: it runs the SAME ImpactDecalsManager method with the SAME
        /// arguments the game would have passed - so the entry and the art are the game's own - and then
        /// reports the result.
        ///
        /// DOUBLE-STAMPING. Skipping the original is what makes it impossible rather than merely
        /// unlikely: for a dirt terrain hit by one of the mod's round types this method body is the only
        /// decal call for that hit, so the game cannot stamp a second crater next to it. The body is left
        /// entirely alone for every other round (vanilla, enemy, campaign CAS - the original is run
        /// unchanged), for every air / object hit, and for every non-Dirt terrain surface (Water), so a
        /// hull / tree decal is still the game's own and no other round's behaviour changes by one byte.
        /// The ground SCAR added for a missile's object hit cannot double-stamp either: the original body
        /// is deliberately left running for that hit, so the scar refuses itself when the struck object
        /// reports Dirt (the game's own call has already left that dirt crater), and one DoImpactDecal
        /// call IS one hit - doImpactVFX records the non-terrain decal as done the moment this call
        /// returns and returns early on that flag next time (LiveRound.cs:1483-1486 and 1524).
        ///
        /// <c>IsSpall</c> and <c>_impactSkipDecal</c> are the game's own two suppression flags inside
        /// DoImpactDecal (LiveRound.cs:1401-1404): when either is set the original would have stamped
        /// nothing anyway, so this runs it exactly as the game would and stamps nothing either.
        ///
        /// THE TWO KINDS OF ROUND ARE HANDLED DIFFERENTLY, and both state their own reason:
        ///   * a MISSILE carries the bomb donor's own decal descriptor and a Bomb / MainGun effect size,
        ///     so it passes the dirt test as it stands - the game's own ammo is handed over untouched.
        ///   * a GUN ROUND must keep its own ImpactEffectDescriptor (it is what resolves the round's
        ///     explosion, and the dirt test reads the same descriptor's EffectSize). It is therefore
        ///     asked for a crater through CasPayloadFactory.CachedDecalAmmo: a cached clone that differs
        ///     from the round in that ONE field and in nothing else.
        ///
        /// THE HULL HIT KEEPS ITS OWN DECAL, AND THE GROUND BENEATH IT IS SCARRED TOO (the player's
        /// decision, option B). The mod aims its own missiles at `Unit.Center` - the unit's origin +
        /// 1.5 m (Unit.cs:141-154), i.e. a point inside the hull - so the NORMAL outcome of an AGM-65 /
        /// Kh-25 shot is a contact with the VEHICLE, and a hull contact is not a terrain contact:
        /// LiveRound sets SurfaceMaterial.Dirt only in its terrain branch (LiveRound.cs:282-298) and
        /// reports Steel / the tree's own material for everything else. The aim, the guaranteed hit, the
        /// object's own decal and the terrain branch above are all left exactly as they were; what is
        /// ADDED is that <see cref="StampGroundScar"/> marks the terrain point below the detonation,
        /// because a 250 kg-class warhead that goes off on a tank does crater the ground beneath it. A
        /// vanilla bomb that lands on a tank is untouched.
        /// </summary>
        [HarmonyPatch(typeof(LiveRound), "DoImpactDecal")]
        internal static class CasCraterPatch
        {
            private static readonly AccessTools.FieldRef<LiveRound, bool> SpallSkipRef =
                AccessTools.FieldRefAccess<LiveRound, bool>("_impactSkipDecal");

            private static readonly AccessTools.FieldRef<LiveRound, bool> RicochetRef =
                AccessTools.FieldRefAccess<LiveRound, bool>("_ricochet");

            private static readonly AccessTools.FieldRef<LiveRound, Vector3> ImpactNormalRef =
                AccessTools.FieldRefAccess<LiveRound, Vector3>("_impactNormal");

            /// <summary>
            /// How far below the detonation the ground may be for a scar to be stamped. A hull hit is
            /// about 1.5 m above the ground (`Unit.Center` is the unit's origin + 1.5 m), so anything
            /// past this is not a warhead going off against a vehicle - it is a hit on something tall or
            /// in the air, and a crater invented on the ground far below it would be exactly the "crater
            /// without ground contact" this patch refuses everywhere else.
            /// </summary>
            private const float MaxGroundScarDropMeters = 20f;

            // Harmony can bind ONLY the target method's own parameters. `LiveRound.DoImpactDecal` takes just
            // `bool terrainHit`, so everything else this patch needs has to be read from the round's fields -
            // which is exactly where vanilla's own call takes them:
            //   LiveRound.DoImpactDecal: CreateImpactDecalOfType(Info, this._penetrationLevel, this._ricochet,
            //   this._isHeat, this._fusedStatus, this._materialHit, ...)
            // Declaring them as Prefix parameters instead is what killed PatchAll with
            //   'Parameter "penetrationLevel" not found in method ...DoImpactDecal(bool terrainHit)'
            // and took the whole mod's initialisation down with it.
            private static readonly AccessTools.FieldRef<LiveRound, int> PenetrationRef =
                AccessTools.FieldRefAccess<LiveRound, int>("_penetrationLevel");

            private static readonly AccessTools.FieldRef<LiveRound, bool> HeatRef =
                AccessTools.FieldRefAccess<LiveRound, bool>("_isHeat");

            private static readonly AccessTools.FieldRef<LiveRound, ParticleEffectsManager.FusedStatus> FusedRef =
                AccessTools.FieldRefAccess<LiveRound, ParticleEffectsManager.FusedStatus>("_fusedStatus");

            private static readonly AccessTools.FieldRef<LiveRound, ParticleEffectsManager.SurfaceMaterial> MaterialRef =
                AccessTools.FieldRefAccess<LiveRound, ParticleEffectsManager.SurfaceMaterial>("_materialHit");

            private static bool Prefix(LiveRound __instance, bool terrainHit)
            {
                try
                {
                    if (__instance == null || __instance.Info == null)
                    {
                        return true; // not even a round: the game's own call.
                    }

                    AmmoType ammo = __instance.Info;

                    if (!terrainHit)
                    {
                        // The round struck something that is NOT the terrain. The game's own body (which
                        // this prefix hands the call back to) stamps that object's own decal through
                        // CreateImpactDecalOfType(..., impactObject: this._impactObject) - the hull hit
                        // keeps exactly the decal it always had, and this prefix forces nothing onto it.
                        //
                        // What is ADDED for one of the mod's own air-to-ground missiles is the ground scar
                        // beneath the detonation (StampGroundScar): the warhead went off, just not on the
                        // terrain, and a 250 kg-class charge on a tank craters the dirt under it. The
                        // terrain branch below is the other half of this and the two cannot both run for
                        // one hit: `terrainHit` has one value for one call, and the game itself makes that
                        // call once per hit (LiveRound.cs:1483-1486, 1524). Then say what was struck, once
                        // per round type, as before.
                        if (CasPayloadFactory.IsOurMissile(ammo))
                        {
                            StampGroundScar(__instance, ammo);
                        }

                        return true;
                    }

                    int penetrationLevel = PenetrationRef(__instance);
                    bool isHeat = HeatRef(__instance);
                    ParticleEffectsManager.FusedStatus fusedStatus = FusedRef(__instance);
                    ParticleEffectsManager.SurfaceMaterial surfaceMaterial = MaterialRef(__instance);

                    // The missile branch first, and it is exactly the branch that shipped before: the
                    // missile's own ammo, untouched.
                    bool missile = CasPayloadFactory.IsOurMissile(ammo);
                    if (!missile && !CasPayloadFactory.IsOurRound(ammo))
                    {
                        return true; // vanilla / campaign / enemy / bundle rocket: the game's own body.
                    }

                    // The surface material of a terrain hit is Dirt or the round is not on terrain at
                    // all (Water reaches here with SurfaceMaterial.Water): a non-Dirt surface keeps the
                    // game's own decal for every round, the missiles included.
                    if (surfaceMaterial != ParticleEffectsManager.SurfaceMaterial.Dirt)
                    {
                        return true;
                    }

                    if (__instance.IsSpall || SpallSkipRef(__instance))
                    {
                        // The game's own guards: run the original so it does exactly what it always did.
                        return true;
                    }

                    // The ammo the game's own call is made with. For a missile it is the round's; for a
                    // gun round it is the decal-only clone, because that round's own descriptor must keep
                    // its EffectSize (it resolves the round's impact EXPLOSION).
                    AmmoType decalAmmo = ammo;
                    if (!missile)
                    {
                        decalAmmo = CasPayloadFactory.CachedDecalAmmo(ammo);
                        if (decalAmmo == null)
                        {
                            // The clone could not be prepared without risking a write into the game's
                            // shared tables: hand the hit back rather than registering anything.
                            return true;
                        }
                    }

                    Transform transform = __instance.transform;

                    // The game's own call, field for field (LiveRound.cs:1406). Taking the call over is
                    // also the whole double-stamp protection: for this hit the original body is the only
                    // other place a decal is created, and it does not run.
                    GameObject decal = ImpactDecalsManager.Instance.CreateImpactDecalOfType(
                        decalAmmo, penetrationLevel, RicochetRef(__instance), isHeat, fusedStatus, surfaceMaterial,
                        transform.position - transform.forward * 0.01f, transform.forward, transform.up,
                        ImpactNormalRef(__instance), null);

                    return false;
                }
                catch (Exception)
                {
                    // Never leave an impact unhandled: hand the hit back to the game's own body, which is
                    // exactly what would have run without this patch.
                    return true;
                }
            }

            /// <summary>
            /// THE GROUND SCAR UNDER A MISSILE'S OBJECT HIT - the complementary half of the terrain
            /// crater above, and the player's decision (option B): the hull hit keeps exactly the decal
            /// the game gives it, and the ground under the exploding warhead is marked as well, because a
            /// 250 kg-class charge that detonates on a tank does crater the dirt beneath it.
            ///
            /// TRIGGER. One of the mod's own missiles (<see cref="CasPayloadFactory.IsOurMissile"/>,
            /// a reference match on the two the factory built), arriving with terrainHit false, i.e. on a
            /// unit / object / tree - the terrain half of that same call is the branch above, so the two
            /// are mutually exclusive and neither can stamp the other's decal. Its warhead must be
            /// bomb-class, read from the round's OWN descriptor (ImpactEffectDescriptor.EffectSize ==
            /// Bomb) rather than from a list of names: that is the same field the game's own Dirt gate
            /// sizes the crater from (ImpactDecalsManager.cs:284-304, 1.8-2x for a Bomb).
            ///
            /// WHERE. The game's own downward terrain ray, taken from the round's own position -
            /// CodeUtils.TerrainCollisionCheck (CodeUtils.cs:71-76), the helper CASController and
            /// HelicopterController use for their own ground contact. It fires straight down from that
            /// position against the TERRAIN layer alone, which is the layer LiveRound itself tests to
            /// decide that something is ground (LiveRound.cs:282); the mask is terrain-only, so the hull
            /// the round just hit cannot block it. Vanilla's own callers never check the miss, so this
            /// checks the hit collider itself. The hit (not just its point) is kept because the crater has
            /// to lie on the ground's OWN normal: the round's `_impactNormal` is the HULL's, and a crater
            /// stood up against the side of a tank is the artefact this avoids.
            ///
            /// GUARDS, in order (any of them leaves the game's own hull decal untouched and says why in
            /// the decision): not a spall fragment and not `_impactSkipDecal` (the game's own two suppression
            /// flags inside DoImpactDecal, LiveRound.cs:1401-1404 - where the game stamps nothing, this
            /// stamps nothing); a bomb-class warhead; the struck object must NOT report Dirt itself
            /// (then the original body, which this prefix lets run for a non-terrain hit, has already left
            /// the game's own dirt crater for this hit - a projected second one would be the double stamp
            /// this patch exists to prevent, and that is also the ground-ricochet case, which reaches here
            /// with terrainHit false because doImpactVFX passes `terrainHit &amp;&amp; !ricochet`,
            /// LiveRound.cs:1455); the ray must hit; and the ground must be between 0 and
            /// <see cref="MaxGroundScarDropMeters"/> below the detonation, so a hit on something tall or
            /// in the air does not invent a crater far below it.
            ///
            /// ONCE PER HIT. This runs only from the terrainHit false half of one DoImpactDecal call, and
            /// that call is itself once per hit: doImpactVFX marks the non-terrain decal done the moment
            /// the call returns and returns early on that flag next time (LiveRound.cs:1483-1486, 1524),
            /// exactly the once-per-round guarantee the terrain branch relies on when it returns false.
            /// A round that later reaches the terrain goes through the OTHER branch and gets the game's
            /// own crater there, as it always did.
            /// </summary>
            private static void StampGroundScar(LiveRound round, AmmoType ammo)
            {
                try
                {
                    // The game's own suppression flags for this very call: a spall fragment, or a hit on
                    // something whose armour asks for no decals at all, leaves no mark in vanilla and
                    // leaves none here.
                    if (round.IsSpall || SpallSkipRef(round))
                    {
                        return;
                    }

                    // Bomb-class only, and from the round's own descriptor - no list of round names.
                    ParticleEffectsManager.EffectSize effectSize = ammo.ImpactEffectDescriptor.EffectSize;
                    if (effectSize != ParticleEffectsManager.EffectSize.Bomb)
                    {
                        return;
                    }

                    // The struck object's own material decides whether the game's own call has already
                    // left a Dirt crater for this hit (ImpactDecalsManager.cs:405-417 requires nothing but
                    // the material to be Dirt). Adding the projected crater then would stamp the ground
                    // twice for one hit.
                    ParticleEffectsManager.SurfaceMaterial material = MaterialRef(round);
                    if (material == ParticleEffectsManager.SurfaceMaterial.Dirt)
                    {
                        return;
                    }

                    // The ground point: the game's own terrain ray, from the round's own position.
                    RaycastHit ground = GHPC.Utility.CodeUtils.TerrainCollisionCheck(round.transform);
                    if (ground.collider == null)
                    {
                        return;
                    }

                    float drop = round.transform.position.y - ground.point.y;
                    if (drop < 0f)
                    {
                        return;
                    }
                    if (drop > MaxGroundScarDropMeters)
                    {
                        return;
                    }

                    // The game's own call, field for field the argument list the terrain branch above
                    // passes (LiveRound.cs:1406) - the round's own state, the round's own ammo untouched -
                    // with three things changed on purpose: the PROJECTED GROUND POINT (nudged 1 cm back
                    // along the round's own path, exactly as the terrain branch does it, so the crater sits
                    // just clear of the surface it is projected onto), the GROUND's own normal, and
                    // SurfaceMaterial.Dirt. The missile's own ammo passes the game's Dirt gate by itself
                    // (EffectSize Bomb, HasImpactDecal 1, DecalCategory Explosion - the bomb donor's
                    // descriptors), so no clone is needed the way a gun round needs one.
                    Vector3 scarPoint = ground.point - round.transform.forward * 0.01f;
                    ImpactDecalsManager.Instance.CreateImpactDecalOfType(
                        ammo, PenetrationRef(round), RicochetRef(round), HeatRef(round), FusedRef(round),
                        ParticleEffectsManager.SurfaceMaterial.Dirt, scarPoint, round.transform.forward,
                        Vector3.up, ground.normal, null);
                }
                catch (Exception)
                {
                    // The hull hit itself is not affected in any way by this failing: the caller runs the
                    // game's own body for it either way.
                }
            }

            /// <summary>
            /// The scar report, once per round type and per outcome. The success line is the one that
            /// records the generated decal ("CAS missile ground scar: 'Kh-25' at (x, y, z) ... -&gt;
            /// decal '...'"); each guard leaves the game's own hull decal untouched, so "no scar" is never
            /// silent and never ambiguous.
            /// </summary>
        }

        /// <summary>
        /// The mod's own hit logic, step 2: put the round on the impact point CasImpactPointPatch
        /// resolved for it, instead of letting the game's ballistics, aim computer, launch deviation or
        /// guidance decide where it goes.
        ///
        /// This is a Prefix on LiveRound.DoUpdate, i.e. it runs immediately before the round's own
        /// movement for the frame. (A component Update could not be ordered against it: rounds are not
        /// updated by Unity but by LiveRoundBatchHandler.Update, which loops over every live round.)
        ///
        /// Two modes, both of which leave the impact itself (obliquity, armour, penetration, spall,
        /// effects, audio, ricochet) to the game - only the POINT is the mod's:
        ///
        ///   * AIR-TO-GROUND MISSILES of ours: this patch only asks the round's own guidance
        ///     (CasMissileGuidance) where the nose should go and then writes the flight state with the
        ///     profile's pinned speed. The shape of the flight - the launch pull-up, the arch, the
        ///     Kh-25's pop-up, the terminal dive, the laser beam that has to be held and both rounds' fail
        ///     states - belongs to that component, not here, because the two missiles are deliberately
        ///     different weapons rather than one trajectory with two sets of numbers.
        ///
        ///   * bullets and rockets (CasImpactAim.GravityAware = false): the flight state is rewritten to
        ///     "at the round's current position, at its current speed, straight at the point", so the
        ///     velocity the game integrates and the displacement it raycasts along both point at the
        ///     point. Within the last couple of metres (or once the point is behind the round) the mod
        ///     lets go and the game flies it in.
        ///
        ///   * bombs (GravityAware = true): their trajectory IS gravity, so the vertical motion is left
        ///     alone and only the horizontal velocity is corrected - predicted with the game's own
        ///     ballistic integrator, so drag and gravity match exactly, and limited to a fin-sized
        ///     acceleration so the bomb keeps its arc instead of turning into a missile. The correction
        ///     only starts inside the terminal window (a few seconds of fall), and the release deviation
        ///     of a bomb hardpoint is zeroed (CasAccuracyPatch) so the whole correction has to be small.
        ///
        /// Net effect: SlotN_CasAccuracy = 0 puts every round into the target's own centre (a hit that
        /// cannot miss), 0.5 into a 2.5 m circle, 1 into a 5 m circle - bullets, rockets and bombs alike.
        /// </summary>
        [HarmonyPatch(typeof(LiveRound), "DoUpdate")]
        internal static class CasImpactAimPatch
        {
            private static readonly AccessTools.FieldRef<LiveRound, MotionState> MotionRef =
                AccessTools.FieldRefAccess<LiveRound, MotionState>("_currentMotionState");

            /// <summary>
            /// The guidance rotation before LiveRound overwrites it with the ballistic step direction.
            /// </summary>
            private static readonly Dictionary<int, Quaternion> GuidedRotations =
                new Dictionary<int, Quaternion>();

            /// <summary>Within this distance of the point the mod stops steering and the game takes over.</summary>
            private const float HandoverDistance = 4f;

            /// <summary>
            /// A bomb is only corrected in the last few seconds of its fall: correcting from release
            /// would make it a glide bomb, and the release computer already flies it onto the target.
            /// </summary>
            private const float TerminalWindowSeconds = 6f;

            /// <summary>Integration step of the impact prediction, in seconds.</summary>
            private const float PredictionStepSeconds = 0.05f;

            /// <summary>
            /// Correction authority, in m/s². 25 m/s² over the terminal window is a few dozen m/s of
            /// horizontal authority - enough for the whole impact circle (a 5 m offset needs ~1 m/s at
            /// 6 s out) and small enough that a bomb still looks like a bomb.
            /// </summary>
            private const float MaxCorrectionAcceleration = 25f;

            private static void Prefix(LiveRound __instance, ref float dt)
            {
                try
                {
                    if (__instance == null)
                    {
                        return;
                    }

                    // Cheap reject before the component lookup: this runs for EVERY live round of the
                    // battle every frame. Only rounds fired from a hardpoint (NpcRound) can carry an
                    // impact aim, and spall fragments (the bulk of the rounds in a firefight) never do.
                    if (!__instance.NpcRound || __instance.IsSpall)
                    {
                        return;
                    }

                    CasImpactAim aim = __instance.GetComponent<CasImpactAim>();
                    if (aim == null || aim.Released)
                    {
                        return; // not one of ours, or already handed back to the game
                    }
                    if (aim.ShotId != __instance.ID)
                    {
                        // The round was recycled (LiveRoundMarshaller pools rounds and hands them to any
                        // weapon of the same visual type): this aim belongs to an earlier shot.
                        aim.Released = true;
                        return;
                    }

                    // The missile's own guidance, if this round is one of ours. It exists only on an
                    // air-to-ground missile, and it owns everything about the shape of that flight: the arch,
                    // the pop-up, the terminal dive, the laser beam and the fail states. A round without it
                    // (bullets, rockets, bombs) is flown by the shared resolver below.
                    CasMissileGuidance guidance = CasPayloadFactory.IsOurMissile(__instance.Info)
                        ? __instance.GetComponent<CasMissileGuidance>() : null;
                    float rawDt = dt;
                    if (guidance != null)
                    {
                        // The batch handler supplies an uncapped frame delta after a hitch or pause.
                        // Guidance and the game's ballistic movement must advance by the same step.
                        dt = Mathf.Min(dt, 1f / 30f);
                    }

                    Vector3 point = Vector3.zero;
                    bool havePoint = CasPayloadFactory.TryGetImpactPoint(aim, out point);
                    if (!havePoint && guidance == null)
                    {
                        aim.Released = true; // target gone (destroyed / de-spotted): fly on ballistically
                        return;
                    }

                    Vector3 position = __instance.transform.position;

                    if (guidance == null && aim.GravityAware)
                    {
                        CorrectFallingRound(__instance, position, point);
                        return;
                    }

                    Vector3 direction;
                    if (guidance != null)
                    {
                        // The guidance decides where the nose goes and sets the round's rotation itself (a
                        // lost laser round is rolling as it tumbles, which a plain `forward =` would erase).
                        // False means it is done with the round - the impact point is reached - and the game
                        // takes the last couple of metres.
                        if (!guidance.TryStep(havePoint, point, dt, rawDt, out direction))
                        {
                            aim.Released = true;
                            return;
                        }
                    }
                    else
                    {
                        Vector3 toPoint = point - position;
                        float distance = toPoint.magnitude;

                        // Hand over when the point is reached, or when it is already behind the round - the
                        // latter is what keeps a round that penetrated the hull from being turned back
                        // around inside it.
                        float collisionSpeed = Mathf.Max(1f, __instance.CurrentSpeed);
                        float collisionWindow = Mathf.Max(HandoverDistance,
                            collisionSpeed * Mathf.Min(Time.deltaTime, 0.05f) * 0.75f);
                        if (distance <= collisionWindow)
                        {
                            aim.Released = true;
                            return;
                        }

                        direction = toPoint / distance;
                        __instance.transform.forward = direction;
                    }

                    // An air-to-ground missile of ours flies its WHOLE flight at the profile's Mach 1.2
                    // (see MissileProfile.CruiseSpeedMeters): the bomb the missile's data came from is a
                    // blunt gravity body whose drag would bleed the speed away, and inheriting the
                    // aircraft's airspeed made the same missile faster or slower depending on the run, so
                    // the speed is pinned rather than floored. Bullets and rockets keep their own speed.
                    float pinned = CasPayloadFactory.CruiseSpeedFor(__instance.Info);
                    float speed = pinned > 0f ? pinned : Mathf.Max(1f, __instance.CurrentSpeed);

                    if (pinned > 0f && __instance.MaxSpeed > pinned)
                    {
                        // The game scales a round's penetration by (CurrentSpeed / MaxSpeed)^2, so a round
                        // that is pinned below its launch speed would silently lose warhead performance.
                        // Fix the basis at the speed it really arrives with.
                        __instance.MaxSpeed = pinned;
                    }
                    MotionState state = MotionRef(__instance);
                    state.position = position;
                    state.velocity = direction * speed;
                    MotionRef(__instance) = state;

                    // Remember the attitude the guidance chose for this frame. LiveRound's own update
                    // replaces the round's rotation with its ballistic displacement direction, so the
                    // postfix puts the guided attitude - roll included - back.
                    if (guidance != null)
                    {
                        GuidedRotations[__instance.GetInstanceID()] = guidance.IntendedRotation;
                    }
                }
                catch (Exception)
                {
                }
            }

            /// <summary>
            /// LiveRound replaces the guided missile's rotation with its ballistic displacement direction.
            /// Restore the complete guidance rotation, including roll, after that update. Checking for a
            /// zero transform.forward cannot detect this: Unity returns a unit vector even after assigning
            /// a zero direction to Transform.forward.
            /// </summary>
            private static void Postfix(LiveRound __instance)
            {
                int id = __instance.GetInstanceID();
                Quaternion guidedRotation;
                bool guidedThisFrame = GuidedRotations.TryGetValue(id, out guidedRotation);
                GuidedRotations.Remove(id);

                try
                {
                    if (!guidedThisFrame || __instance.Pooled)
                    {
                        return;
                    }
                    CasImpactAim aim = __instance.GetComponent<CasImpactAim>();
                    if (aim == null || aim.Released || aim.ShotId != __instance.ID)
                    {
                        return;
                    }
                    CasMissileGuidance guidance = __instance.GetComponent<CasMissileGuidance>();
                    if (guidance == null)
                    {
                        return;
                    }
                    __instance.transform.rotation = guidedRotation;
                }
                catch (Exception)
                {
                }
            }

            /// <summary>
            /// Gravity-aware terminal correction for a falling round (a bomb).
            ///
            /// 1. Predict the impact by stepping the round's OWN flight model forward with the game's
            ///    ballistic integrator (RK4 or the simple one, exactly as LiveRound does it), so gravity,
            ///    drag and air density are the same ones the round will actually fly through, and note
            ///    where it crosses the point's height and when.
            /// 2. If that is still far away, leave the bomb alone - it is not in its terminal phase.
            /// 3. Otherwise move the horizontal velocity by the amount that shifts the predicted impact
            ///    onto the point (miss / time-to-go), capped at a fin-sized acceleration this frame.
            ///
            /// Re-running this every frame is what makes it converge: each frame re-predicts from the
            /// real state, so model error (drag on the correction itself, a moving target) is corrected
            /// on the next frame instead of accumulating.
            /// </summary>
            private static void CorrectFallingRound(LiveRound round, Vector3 position, Vector3 point)
            {
                MotionState state = MotionRef(round);
                state.position = position; // predict from where the round really is

                // Cheap reject first: the drag-free fall time is a LOWER bound (drag can only make the
                // bomb fall slower), so a bomb outside the window by this estimate is certainly not in
                // its terminal phase yet and the expensive prediction is not run at all.
                float drop = position.y - point.y;
                if (drop <= 0f)
                {
                    return; // at or below the point already: gravity has nothing left to aim
                }
                float analytic = AnalyticFallTime(state.velocity.y, Physics.gravity.y, drop);
                if (analytic < 0f || analytic > TerminalWindowSeconds)
                {
                    return;
                }

                MotionState probe = state;
                Vector3 predicted = Vector3.zero;
                float timeToImpact = -1f;
                float elapsed = 0f;
                while (elapsed < TerminalWindowSeconds)
                {
                    MotionState next = Step(round, probe);

                    // Crossing the point's height on the way down: that is where this bomb will land.
                    if (probe.position.y > point.y && next.position.y <= point.y)
                    {
                        float span = probe.position.y - next.position.y;
                        float fraction = span > 0.0001f ? (probe.position.y - point.y) / span : 0f;
                        fraction = Mathf.Clamp01(fraction);
                        predicted = Vector3.Lerp(probe.position, next.position, fraction);
                        timeToImpact = elapsed + PredictionStepSeconds * fraction;
                        break;
                    }

                    probe = next;
                    elapsed += PredictionStepSeconds;
                }

                if (timeToImpact < 0f || timeToImpact > TerminalWindowSeconds)
                {
                    return; // does not reach the point's height inside the window: leave the bomb alone
                }

                Vector3 miss = point - predicted;
                miss.y = 0f;
                if (miss.sqrMagnitude < 0.0025f)
                {
                    return; // already within 5 cm: nothing to correct
                }

                Vector3 correction = miss / Mathf.Max(0.25f, timeToImpact);
                float limit = MaxCorrectionAcceleration * Time.deltaTime;
                if (correction.magnitude > limit)
                {
                    correction = correction.normalized * limit;
                }

                state.velocity = new Vector3(state.velocity.x + correction.x, state.velocity.y,
                    state.velocity.z + correction.z);
                MotionRef(round) = state;

            }

            /// <summary>One step of the round's own flight model, exactly as LiveRound integrates it.</summary>
            private static MotionState Step(LiveRound round, MotionState state)
            {
                return round.UseErrorCorrection
                    ? BallisticEvaluatorRK4.EvaluateTimestep(state, PredictionStepSeconds, round.Info)
                    : BallisticEvaluatorNonCorrected.EvaluateTimestep(state, PredictionStepSeconds, round.Info);
            }

            /// <summary>
            /// Drag-free fall time (seconds) from the current vertical speed to `drop` metres below, i.e.
            /// the positive root of 0.5*g*t² + vy*t + drop = 0. Returns -1 when the round never gets
            /// down there (moving up, or gravity disabled).
            /// </summary>
            private static float AnalyticFallTime(float verticalSpeed, float gravity, float drop)
            {
                if (gravity >= 0f)
                {
                    return -1f;
                }
                float discriminant = verticalSpeed * verticalSpeed - 2f * gravity * drop;
                if (discriminant < 0f)
                {
                    return -1f;
                }
                float root = (-verticalSpeed - Mathf.Sqrt(discriminant)) / gravity;
                return root > 0f ? root : -1f;
            }
        }

        /// <summary>
        /// Attaches CasImpactAim to the round CASHardpoint.SpawnMunition just created.
        ///
        /// SpawnMunition never returns the round, but it calls LiveRound.Init on it before returning, so
        /// this postfix claims the point CasImpactPointPatch stashed - only if the round fires exactly
        /// the ammo that hardpoint was holding, so a stash that was never claimed (SpawnMunition threw,
        /// or the round never got created) can never hijack somebody else's round.
        ///
        /// Init is also where a RECYCLED round starts its new life: LiveRoundMarshaller pools rounds by
        /// visual type and hands them to any weapon, so the prefix disarms whatever aim the previous
        /// shot left on the object. (And the steering patch additionally checks the round's shot id, so
        /// even a round reused without going through Init cannot be steered to an old target.)
        /// </summary>
        [HarmonyPatch(typeof(LiveRound), "Init")]
        internal static class CasImpactAttachPatch
        {
            [HarmonyPriority(Priority.First)]
            private static void Prefix(LiveRound __instance, LiveRound parentRound,
                out CasMissileSpawnContextPatch.LaunchContext __state)
            {
                __state = parentRound == null ? CasMissileSpawnContextPatch.Capture(__instance) : null;
                try
                {
                    if (__instance == null)
                    {
                        return;
                    }
                    CasImpactAim stale = __instance.GetComponent<CasImpactAim>();
                    if (stale == null)
                    {
                        return;
                    }
                    stale.Released = true;
                    stale.Target = null;
                    stale.DynamicTarget = null;
                    stale.Offset = Vector3.zero;
                    stale.ShotId = 0;
                    stale.GravityAware = false;
                }
                catch (Exception)
                {
                }
            }

            private static void Postfix(LiveRound __instance,
                CasMissileSpawnContextPatch.LaunchContext __state)
            {
                try
                {
                    if (__instance == null || __instance.Info == null)
                    {
                        return;
                    }

                    Transform target;
                    Vector3 offset;
                    bool gravityAware;
                    Transform carrier;
                    Unit pendingUnit;
                    bool ours = CasPayloadFactory.ConsumePendingImpactForRound(__instance, out target, out offset,
                        out gravityAware, out carrier, out pendingUnit);
                    if (__state != null && ReferenceEquals(__state.Round, __instance))
                    {
                        CasPayloadFactory.RegisterRuntimeMissile(__instance.Info);
                        // Retained on the Harmony invocation, so nested Init/launch cleanup cannot
                        // destroy the binding after Prefix captured this exact spawned missile.
                        target = __state.Target;
                        carrier = __state.Carrier;
                        pendingUnit = __state.Unit;
                        offset = Vector3.zero;
                        gravityAware = false;
                        ours = true;
                    }

                    // The air-to-ground missile's flight effects are the TOW's, composed into the bundle;
                    // the game's own effect materials are swapped in here, because this is the first moment
                    // the mission is guaranteed to be fully loaded (see CasMissileVisualRepair).
                    if (CasPayloadFactory.IsOurMissile(__instance.Info))
                    {
                        CasMissileVisualRepair.ApplyMissileVisual(__instance.gameObject);
                        // ...and the missile's motor burns for as long as its own profile says, instead of
                        // one figure for both rounds: the AGM-65 is a short boost that then glides without
                        // smoke, the Kh-25 a single long high-thrust stage.
                        CasAirframeCatalog.MissileProfile flight = CasPayloadFactory.ProfileFor(__instance.Info);
                        if (flight != null)
                        {
                            CasMissileMotorBurnout motor = __instance.GetComponent<CasMissileMotorBurnout>();
                            if (motor != null)
                            {
                                motor.SetBurnSeconds(flight.MotorBurnSeconds);
                            }
                        }

                        // AGM-65 target selection happens in CasImpactPointPatch immediately before
                        // SpawnMunition. Keep that pending target here; selecting again in Init would
                        // collapse the salvo back to the controller's shared FinalTarget. Kh-25 does not
                        // enter the AGM plan and therefore keeps the same FinalTarget for both rounds.
                    }
                    else if (CasPrewarmer.IsBundledAmmo(__instance.Info))
                    {
                        // The bundle's own rocket rounds (FFAR / S-5K / S-8K, and the bombs): their flight
                        // visual hangs off the AmmoType, two references away from the hardpoint prefab, so
                        // it is the same class of asset as the missile's and gets the same safety net.
                        CasMissileVisualRepair.ApplyRoundVisual(__instance.gameObject, __instance.Info);
                    }

                    // Our own missiles are flown by CasMissileGuidance rather than the shared impact
                    // resolver, but they still use the pending context to carry their per-round target
                    // from LaunchSingleMunition into Init.
                    bool missile = CasPayloadFactory.IsOurMissile(__instance.Info);
                    if (missile && (!ours || target == null || carrier == null))
                    {
                        return;
                    }
                    if (!ours && !missile)
                    {
                        return;
                    }

                    CasImpactAim aim = __instance.GetComponent<CasImpactAim>();
                    if (aim == null)
                    {
                        aim = __instance.gameObject.AddComponent<CasImpactAim>();
                    }
                    aim.Target = target;
                    aim.Offset = offset;
                    aim.ShotId = __instance.ID;
                    aim.GravityAware = gravityAware;
                    aim.Released = false;

                    // Keep the target transform on the round.  CasMissileGuidance samples it every
                    // frame, so a moving vehicle is not reduced to the position it had at launch.
                    aim.DynamicTarget = missile ? target : null;

                    // THE MISSILE'S OWN FLIGHT. An air-to-ground missile of ours is not flown by the shared
                    // impact resolver at all: it gets its own guidance component, which decides the shape of
                    // the flight (the arch, the pop-up, the terminal dive), the beam that keeps a laser round
                    // on course, and what the round does when that guidance is lost. CasImpactAimPatch then
                    // only has to ask it where to point and write the flight state.
                    if (CasPayloadFactory.IsOurMissile(__instance.Info))
                    {
                        CasMissileGuidance guidance = __instance.GetComponent<CasMissileGuidance>();
                        if (guidance == null)
                        {
                            guidance = __instance.gameObject.AddComponent<CasMissileGuidance>();
                        }
                        guidance.Init(CasPayloadFactory.ProfileFor(__instance.Info), carrier);
                        // The launch target the factory already resolved is handed over with the aim: it is
                        // what tells the round whether it is an ANTI-AIRCRAFT round (see
                        // CasMissileGuidance.SetTarget / CasAirTargets.IsAirUnit).
                        guidance.SetTarget(aim.DynamicTarget, pendingUnit);
                        CASController controller = carrier != null ? carrier.GetComponentInParent<CASController>() : null;
                        if (controller != null)
                        {
                            if (CasMissileAttackRun.IsAgm65(__instance.Info))
                            {
                                // Do not consume the A/B ordinal in the launch prefix. Init has
                                // completed and this round now owns its independent target.
                                CasMissileAttackRun.CommitAgmLaunch(controller, pendingUnit);
                            }
                            long token = CasMissileAttackRun.NoteMissileFired(controller,
                                CasPayloadFactory.ProfileFor(__instance.Info), pendingUnit, __instance.ID);
                            guidance.SetHoldToken(token);
                        }
                        CasTargetSpreadPatch.RegisterMissile(guidance, pendingUnit);
                    }

                    // The mod flies this round itself. LiveRound's own guided branch ignores the motion
                    // state this patch rewrites (it steers by transform.forward + GoalVector instead), so
                    // a round that arrived marked as guided would silently ignore the impact point; the
                    // hardpoints this runs for fire unguided ammo, and this just makes that explicit.
                    __instance.Guided = false;

                    // Our gun-run rounds are the mod's own clones, and they fly without tracers (see
                    // CasPayloadFactory.BuildRound). CASHardpoint.SpawnMunition honours AmmoType.UseTracer
                    // for the round's Light but never touches its DynamicTracer, so the tracer streak kept
                    // rendering; the game's own weapons set it explicitly (WeaponSystem does
                    // "tracer.Active = ammoType.UseTracer"). Same for us: no tracer on our bullets.
                    if (CasPayloadFactory.IsOurRound(__instance.Info))
                    {
                        DisableTracer(__instance);
                    }
                }
                catch (Exception)
                {
                }
            }

            /// <summary>Switches off every tracer visual of one round (the streak and its light).</summary>
            private static void DisableTracer(LiveRound round)
            {
                DynamicTracer[] tracers = round.GetComponentsInChildren<DynamicTracer>(true);
                for (int i = 0; i < tracers.Length; i++)
                {
                    if (tracers[i] != null)
                    {
                        tracers[i].Active = false;
                    }
                }

                Light[] lights = round.GetComponentsInChildren<Light>(true);
                for (int i = 0; i < lights.Length; i++)
                {
                    if (lights[i] != null)
                    {
                        lights[i].enabled = false;
                    }
                }
            }
        }

        /// <summary>
        /// Multi-plane CAS target spreading, ported from CheatMode. Vanilla lets every plane pick the
        /// same "best" target independently, so a batch of sorties all attack one vehicle. This runs
        /// after SearchForTarget has chosen FinalTarget: if that target is already claimed by another
        /// live plane, the plane is re-routed to the nearest unclaimed enemy - first from its own
        /// spotted list, then (because a plane's spotting cone is narrow) from every live enemy unit
        /// within 3 km of the called position that it actually has a weapon for. The attack parameters
        /// are recomputed by re-entering TurnTowardTarget so weapon choice / release distance follow
        /// the new target.
        ///
        /// ---------------------------------------------------------------------------------------------
        /// THE AIR TARGET HALF (the player's "can the AGM lock onto a helicopter?").
        ///
        /// A map click is a ground point (MapController.cs:1390-1401 flattens it, and
        /// CASController.SetInterestPoint does it again at :903-908), and the aircraft's spot cone is
        /// 45 degrees wide and 1000 m long (:928) - so a helicopter is only ever attacked by accident,
        /// when it happens to be inside that cone AND out-scores every ground unit in it. Vanilla's own
        /// target table already has the answer for one (TargetShortNameUs.Chopper falls through to
        /// AirToGroundMissile, CASController.cs:1193-1210), so what is missing is only ACQUISITION:
        ///
        ///   * the PREFIX (below) looks for an enemy aircraft near the clicked point, tests it against
        ///     vanilla's own visibility gate from where the aircraft is right now (CasAirTargets.
        ///     IsVisibleFrom), and writes the answer into CASController.CheatTargetUnit - the game's own
        ///     field, honoured at :959-962, so the aircraft selects it through the game's own code path;
        ///   * the POSTFIX then re-asserts that same, already-validated unit as FinalTarget, because
        ///     vanilla can still be one frame away from reading the field (its own TargetSearchTime gate
        ///     at :954) and the mod wants the air target chosen on the first pass.
        ///
        /// WHY THE CHECK IS IN THE PREFIX AND NOT ONLY AT CALL TIME. CheatTargetUnit bypasses BOTH the
        /// cone and the visibility test, so a field written once and then honoured for the rest of the
        /// sortie is an aircraft attacking through terrain, smoke and trees: the unit can fly behind a
        /// hill while the sortie is still inbound. The prefix therefore re-decides every search and, in
        /// its catch-all, CLEARS the field rather than leaving a stale bypass in place. Every step is
        /// behind CasAirTargets.IsOurMissileSortie, so vanilla aircraft, enemy aircraft, campaign CAS and
        /// every bomb / rocket / gun slot never enter this code at all.
        /// </summary>
        [HarmonyPatch(typeof(CASController), "SearchForTarget")]
        internal static class CasTargetSpreadPatch
        {
            private const float WideSearchRadius = 8000f;

            /// <summary>
            /// How near the CLICKED point (in three dimensions, from the clicked ground point to the
            /// aircraft's centre) an enemy aircraft has to be for a call to be treated as a call against
            /// it. The map click is the only thing the player supplies, so this is the player's aim: it
            /// has to cover clicking the map icon of a helicopter that is a few hundred metres up, and it
            /// must not reach an aircraft flying over the grid square by accident - which is why it is
            /// measured in 3-D rather than on the ground plan (a fast mover at 5 km altitude is not
            /// "near" a click even when it is directly above it).
            /// </summary>
            private const float AirSearchRadiusMeters = 800f;

            private static readonly AccessTools.FieldRef<CASController, List<Unit>> SpottedRef =
                AccessTools.FieldRefAccess<CASController, List<Unit>>("_spottedTargetsCurrent");
            private static readonly AccessTools.FieldRef<CASController, Vector3> InterestRef =
                AccessTools.FieldRefAccess<CASController, Vector3>("_interestPoint");
            private static readonly AccessTools.FieldRef<CASController, bool> LastKnownRef =
                AccessTools.FieldRefAccess<CASController, bool>("_targetIsLastKnownPosition");

            private static readonly MethodInfo EnterStateMethod =
                AccessTools.Method(typeof(CASController), "EnterState");
            private static readonly MethodInfo GetIdealAttackTypeMethod =
                AccessTools.Method(typeof(CASController), "GetIdealAttackType");
            private static readonly Func<CASController, Unit, CASAttackType> GetIdealAttackType =
                CachedDelegate.Create<Func<CASController, Unit, CASAttackType>>(GetIdealAttackTypeMethod);
            private static readonly object TurnTowardTarget = ResolveTurnState();

            /// <summary>Target -> the plane currently attacking it.</summary>
            private static readonly Dictionary<Unit, HashSet<CASController>> Claims =
                new Dictionary<Unit, HashSet<CASController>>();
            private static readonly List<Unit> ReleasedClaims = new List<Unit>();
            private static readonly Dictionary<CasMissileGuidance, Unit> MissileClaims =
                new Dictionary<CasMissileGuidance, Unit>();

            /// <summary>
            /// Plane -> the enemy aircraft the PREFIX validated against the visibility gate on its most
            /// recent search. The POSTFIX only ever adopts a unit from here, so an air target can never be
            /// selected without having passed the gate this frame.
            /// </summary>
            private static readonly Dictionary<int, Unit> ValidatedAirTargets = new Dictionary<int, Unit>();

            /// <summary>
            /// The planes whose CheatTargetUnit THIS MOD wrote and has not cleared again. The field belongs
            /// to the game (a mission script can set it on any aircraft), so it is only ever cleared here
            /// for a plane this mod put an air target on - and never for a target the mod did not choose.
            /// </summary>
            private static readonly HashSet<int> CheatWritten = new HashSet<int>();

            /// <summary>Scratch list for pruning the validated air targets (never re-allocated per frame).</summary>
            private static readonly List<int> PrunedAirTargets = new List<int>();

            internal static void ResetForScene()
            {
                Claims.Clear();
                ReleasedClaims.Clear();
                MissileClaims.Clear();
                ValidatedAirTargets.Clear();
                CheatWritten.Clear();
            }

            internal static void Tick()
            {
                ReleasedClaims.Clear();
                foreach (KeyValuePair<Unit, HashSet<CASController>> pair in Claims)
                {
                    if (pair.Key == null || pair.Key.Neutralized || pair.Value == null || pair.Value.Count == 0)
                    {
                        ReleasedClaims.Add(pair.Key);
                    }
                }
                for (int i = 0; i < ReleasedClaims.Count; i++) Claims.Remove(ReleasedClaims[i]);
                ReleasedClaims.Clear();

                List<CasMissileGuidance> releasedMissiles = new List<CasMissileGuidance>();
                foreach (KeyValuePair<CasMissileGuidance, Unit> pair in MissileClaims)
                {
                    if (pair.Key == null || pair.Value == null || pair.Value.Neutralized)
                    {
                        releasedMissiles.Add(pair.Key);
                    }
                }
                for (int i = 0; i < releasedMissiles.Count; i++) MissileClaims.Remove(releasedMissiles[i]);

                // Same for the validated air targets: a unit that is gone must not be left sitting in the
                // map for a plane whose next search may not run (the whole point of the map is that the
                // POSTFIX only ever adopts a unit the gate accepted). CheatWritten is deliberately NOT
                // touched here: the plane's own gate is what clears the game's field, so the mod has to
                // remember that it was the one that wrote it.
                if (ValidatedAirTargets.Count > 0)
                {
                    PrunedAirTargets.Clear();
                    foreach (KeyValuePair<int, Unit> pair in ValidatedAirTargets)
                    {
                        if (pair.Value == null || pair.Value.Neutralized)
                        {
                            PrunedAirTargets.Add(pair.Key);
                        }
                    }
                    for (int i = 0; i < PrunedAirTargets.Count; i++)
                    {
                        ValidatedAirTargets.Remove(PrunedAirTargets[i]);
                    }
                    PrunedAirTargets.Clear();
                }
            }

            internal static void RegisterMissile(CasMissileGuidance missile, Unit target)
            {
                if (missile != null && target != null)
                {
                    MissileClaims[missile] = target;
                }
            }

            /// <summary>
            /// Finds the second target for an AGM-65 salvo.  This is deliberately separate from
            /// the normal plane-to-plane spreading path: it requires a fresh visibility check at
            /// launch time and excludes the first missile's target even though claims owned by the
            /// same plane are normally allowed for multi-plane deconfliction.
            /// </summary>
            internal static Unit FindAlternativeTargetForAgm(CASController plane, Unit exclude)
            {
                if (plane == null || exclude == null || exclude.Center == null) return null;

                List<Unit>[] allUnits = SceneUnitsManager.AllLiveUnitsByFaction;
                if (allUnits == null) return null;

                Unit best = null;
                float bestDistanceSquared = float.PositiveInfinity;
                for (int f = 0; f < allUnits.Length; f++)
                {
                    Faction faction = (Faction)f;
                    if (faction == Faction.Neutral || faction == plane.unitFaction)
                    {
                        continue;
                    }

                    List<Unit> list = allUnits[f];
                    if (list == null) continue;
                    for (int i = 0; i < list.Count; i++)
                    {
                        Unit candidate = list[i];
                        if (candidate == null || candidate == exclude || candidate.Neutralized ||
                            candidate.Center == null)
                        {
                            continue;
                        }

                        // AGM-65's second round is a local pair attack: measure the radius from
                        // the first target, not from the aircraft. This prevents a distant unit
                        // near the ingress path from stealing the second missile.
                        float distanceSquared = (candidate.Center.position - exclude.Center.position).sqrMagnitude;
                        float radius = CasMissileAttackRun.SecondaryTargetRadiusMeters;
                        if (distanceSquared > radius * radius)
                        {
                            continue;
                        }
                        // Radius first: do not run expensive visibility/attack checks for the
                        // rest of the battlefield, and an out-of-range unit cannot fail B's scan.
                        if (IsClaimedByOther(candidate, plane) || !CanPlaneAttack(plane, candidate) ||
                            !CasAirTargets.IsVisibleFrom(plane, candidate)) continue;
                        if (distanceSquared < bestDistanceSquared)
                        {
                            bestDistanceSquared = distanceSquared;
                            best = candidate;
                        }
                    }
                }
                return best;
            }

            internal static void ReleaseMissile(CasMissileGuidance missile)
            {
                if (missile != null)
                {
                    MissileClaims.Remove(missile);
                }
            }

            /// <summary>
            /// Releases every target reservation owned by one aircraft when its sortie ends.
            /// The normal per-frame pruning handles destroyed targets, but a live target must also be
            /// released when the aircraft leaves the area or is removed from the scene; otherwise the
            /// next CAS call can incorrectly treat that target as occupied forever.
            /// </summary>
            internal static void RemovePlane(CASController plane)
            {
                if (plane == null)
                {
                    return;
                }

                foreach (KeyValuePair<Unit, HashSet<CASController>> pair in Claims)
                {
                    HashSet<CASController> owners = pair.Value;
                    if (owners == null)
                    {
                        continue;
                    }
                    owners.Remove(plane);
                }

                ReleasedClaims.Clear();
                foreach (KeyValuePair<Unit, HashSet<CASController>> pair in Claims)
                {
                    if (pair.Value == null || pair.Value.Count == 0)
                    {
                        ReleasedClaims.Add(pair.Key);
                    }
                }
                for (int i = 0; i < ReleasedClaims.Count; i++)
                {
                    Claims.Remove(ReleasedClaims[i]);
                }
                ReleasedClaims.Clear();

                int id = plane.GetInstanceID();
                ClearOwnCheatTarget(id, plane);

                // Do not clear MissileClaims here. A missile can remain in flight after its carrier
                // leaves the CAS state machine; its target reservation belongs to the projectile until
                // CasMissileGuidance.OnDestroy releases it.
            }

            /// <summary>
            /// THE AIR-TARGET GATE, run before vanilla's own target search on every search frame.
            ///
            /// It answers one question - "is there an enemy aircraft near the point this call was made
            /// against, that this sortie can reach and can actually see right now?" - and writes the answer
            /// into the game's own CASController.CheatTargetUnit. Vanilla then selects it at :959-962
            /// through its own code, so nothing about the selection, the attack type or the release is
            /// reimplemented here.
            ///
            /// It also OWNS THE FIELD: when there is no visible air target this frame the field is cleared
            /// again (and the validated map entry with it), because CheatTargetUnit is honoured with no
            /// cone test and no visibility test of its own - a value left behind is an aircraft that keeps
            /// attacking a target it can no longer see, through terrain, smoke or a forest.
            ///
            /// The catch-all clears rather than returns for the same reason: an exception here must fail
            /// towards the game's own (visibility-tested) selection, never towards a bypass.
            ///
            /// PRIORITY. SearchForTarget carries a second prefix in this mod (CasFlightBehaviourRepair.
            /// CasSearchAndPassPatch, which declines the search once the aircraft already has a target).
            /// This one asks to run first so the gate is evaluated on every search that can actually read
            /// the field. The choice is not load-bearing for safety: a prefix that declines the search also
            /// skips the original body, and the only code that READS CheatTargetUnit is that body - so there
            /// is no frame on which vanilla can act on the field without this gate having run first.
            /// </summary>
            [HarmonyPriority(Priority.First)]
            private static void Prefix(CASController __instance)
            {
                int id = 0;
                try
                {
                    if (__instance == null)
                    {
                        return;
                    }
                    id = __instance.GetInstanceID();

                    if (!CasAirTargets.IsOurMissileSortie(__instance))
                    {
                        // Not ours, or no air-to-ground missile left to fly at an aircraft. Only a value
                        // THIS MOD wrote is cleared - the field itself belongs to the game.
                        ClearOwnCheatTarget(id, __instance);
                        return;
                    }

                    Vector3 interest = InterestRef(__instance);
                    Unit candidate = FindNearbyEnemyAir(__instance, interest, AirSearchRadiusMeters);
                    if (candidate == null)
                    {
                        ClearOwnCheatTarget(id, __instance);
                        return;
                    }

                    if (!CasAirTargets.IsVisibleFrom(__instance, candidate))
                    {
                        ClearOwnCheatTarget(id, __instance);
                        return;
                    }

                    ValidatedAirTargets[id] = candidate;
                    if (__instance.CheatTargetUnit != candidate)
                    {
                        __instance.CheatTargetUnit = candidate;
                        CheatWritten.Add(id);
                    }
                }
                catch (Exception)
                {
                    // FAIL SAFE, NOT FAIL OPEN: clear the validated entry and the game's bypass field, so an
                    // error here can never leave an aircraft attacking an unseen target.
                    ClearOwnCheatTarget(id, __instance);
                }
            }

            /// <summary>
            /// Drops the validated air target for a plane and, if THIS MOD was the one that wrote
            /// CASController.CheatTargetUnit on it, clears that field too. A target the game itself put
            /// there (a scripted cheat CAS call) is left exactly as it was.
            /// </summary>
            private static void ClearOwnCheatTarget(int id, CASController plane)
            {
                ValidatedAirTargets.Remove(id);
                if (plane == null || !CheatWritten.Remove(id))
                {
                    return; // this mod never wrote the field on this plane: it is the game's business.
                }

                // Cleared only when it still holds the air unit this mod put there (or nothing / a
                // destroyed reference): a ground target or a scripted value is not ours to touch.
                Unit current = plane.CheatTargetUnit;
                if (current == null || CasAirTargets.IsAirUnit(current))
                {
                    plane.CheatTargetUnit = null;
                }
            }

            private static void Postfix(CASController __instance)
            {
                try
                {
                    if (__instance == null)
                    {
                        return;
                    }

                    // Claims stay reserved for the lifetime of the sortie, even while the aircraft is
                    // turning away or is temporarily outside its search state.
                    Tick();

                    // An air target the prefix just validated (visible from where the aircraft is) wins over
                    // whatever vanilla chose: that is the whole feature, and it is the only path by which an
                    // air unit is ever adopted here.
                    Unit air;
                    bool haveAir = ValidatedAirTargets.TryGetValue(__instance.GetInstanceID(), out air) &&
                                   air != null && !air.Neutralized;
                    Unit chosen = __instance.FinalTarget;

                    if (!haveAir && chosen == null)
                    {
                        return;
                    }

                    Unit result = haveAir ? air : chosen;
                    if (!haveAir && IsClaimedByOther(chosen, __instance))
                    {
                        Unit alternative = FindAlternativeTarget(__instance, chosen);
                        if (alternative != null)
                        {
                            result = alternative;
                        }
                    }

                    if (result != chosen)
                    {
                        __instance.FinalTarget = result;
                        LastKnownRef(__instance) = false;
                        RecomputeAttackParams(__instance);
                    }

                    // Preserve the native selection for the AGM first trigger pull. The controller
                    // may clear FinalTarget while transitioning into FiringWeapons; the cached unit
                    // is only updated with a live target that has already passed native search.
                    if (result != null)
                    {
                        CasMissileAttackRun.RememberPrimaryTarget(__instance, result);
                    }

                    ClaimForPlane(result, __instance);
                }
                catch (Exception)
                {
                }
            }

            private static bool IsClaimedByOther(Unit target, CASController plane)
            {
                if (target == null)
                {
                    return false;
                }
                HashSet<CASController> owners;
                if (Claims.TryGetValue(target, out owners) && owners != null)
                {
                    foreach (CASController owner in owners)
                        if (owner != null && owner != plane) return true;
                }
                foreach (KeyValuePair<CasMissileGuidance, Unit> pair in MissileClaims)
                {
                    if (pair.Key != null && pair.Value == target && pair.Key.CarrierController != plane)
                        return true;
                }
                return false;
            }

            private static void ClaimForPlane(Unit target, CASController plane)
            {
                if (target == null || plane == null) return;
                HashSet<CASController> owners;
                if (!Claims.TryGetValue(target, out owners))
                {
                    owners = new HashSet<CASController>();
                    Claims[target] = owners;
                }
                owners.Add(plane);
            }



            /// <summary>
            /// THE ENEMY AIRCRAFT NEAREST THE POINT THIS CALL WAS MADE AGAINST, or null.
            ///
            /// This is the acquisition the player's request turns on: a map click is a ground point, a
            /// helicopter is not, and none of the ground searches in this class would ever return one. The
            /// scan is over SceneUnitsManager.AllLiveUnitsByFaction - the same list vanilla's own
            /// SearchForTarget walks (:913) - and it keeps only units that
            ///
            ///   * belong to a faction hostile to this aircraft (never Neutral, never its own side),
            ///   * are alive (Unit.Neutralized is false),
            ///   * are AIRCRAFT by the game's own classification (CasAirTargets.IsAirUnit: ShortNameUs
            ///     Chopper / FastMover, or an IAircraft that reports itself a helicopter), and
            ///   * are not already another plane's or another missile's target (IsClaimedByOther, so a
            ///     pair of sorties splits a pair of helicopters instead of both shooting at one).
            ///
            /// The distance is measured in three dimensions from the CLICKED point to the unit's centre, so
            /// "near the call" means "the player clicked its map icon": a helicopter 300 m up is accepted
            /// from a click up to ~740 m away, while a fast mover at altitude is not swept up by a click on
            /// the ground underneath it. `radius` is the caller's decision because the two callers ask
            /// different questions (the search gate uses AirSearchRadiusMeters; the widening search in
            /// FindAlternativeTarget has no click to measure from and uses the point it was given).
            ///
            /// Nothing is called if nothing is found, and the caller is responsible for the visibility gate
            /// (CasAirTargets.IsVisibleFrom) before the result is used as a target.
            /// </summary>
            internal static Unit FindNearbyEnemyAir(CASController plane, Vector3 point, float radius)
            {
                if (plane == null || radius <= 0f)
                {
                    return null;
                }

                List<Unit>[] allUnits = SceneUnitsManager.AllLiveUnitsByFaction;
                if (allUnits == null)
                {
                    return null;
                }

                Unit best = null;
                float bestDistanceSquared = radius * radius;
                for (int f = 0; f < allUnits.Length; f++)
                {
                    Faction faction = (Faction)f;
                    if (faction == Faction.Neutral || faction == plane.unitFaction)
                    {
                        continue; // only enemies.
                    }
                    List<Unit> list = allUnits[f];
                    if (list == null)
                    {
                        continue;
                    }
                    for (int i = 0; i < list.Count; i++)
                    {
                        Unit candidate = list[i];
                        if (candidate == null || candidate.Neutralized || !CasAirTargets.IsAirUnit(candidate))
                        {
                            continue;
                        }
                        if (IsClaimedByOther(candidate, plane))
                        {
                            continue;
                        }
                        Transform center = candidate.Center;
                        if (center == null)
                        {
                            continue;
                        }
                        float distanceSquared = (center.position - point).sqrMagnitude;
                        if (distanceSquared < bestDistanceSquared)
                        {
                            bestDistanceSquared = distanceSquared;
                            best = candidate;
                        }
                    }
                }
                return best;
            }

            private static Unit FindAlternativeTarget(CASController plane, Unit exclude)
            {
                Vector3 interest = InterestRef(plane);

                // 0) An enemy AIRCRAFT near the called point, for the mod's own air-to-ground-missile
                // sorties. This comes first because it is the only candidate class the ground searches
                // below cannot reach at all - a plane's spotting cone is 45 deg / 1000 m and the called
                // point is on the ground, so a helicopter is otherwise only engaged by luck.
                if (CasAirTargets.IsOurMissileSortie(plane))
                {
                    Unit air = FindNearbyEnemyAir(plane, interest, AirSearchRadiusMeters);
                    if (air != null && air != exclude)
                    {
                        if (CasAirTargets.IsVisibleFrom(plane, air))
                        {
                            return air;
                        }
                    }
                }

                // 1) The plane's own spotted list (vanilla already filtered visibility + attackability).
                Unit best = FindNearest(plane, SpottedRef(plane), interest, exclude);
                if (best != null)
                {
                    return best;
                }

                // 2) Widen: every live enemy unit near the called position, if the plane can attack it.
                List<Unit>[] allUnits = SceneUnitsManager.AllLiveUnitsByFaction;
                if (allUnits == null)
                {
                    return null;
                }

                Unit wideBest = null;
                float wideBestDistanceSquared = WideSearchRadius * WideSearchRadius;
                for (int f = 0; f < allUnits.Length; f++)
                {
                    Faction faction = (Faction)f;
                    if (faction == Faction.Neutral || faction == plane.unitFaction)
                    {
                        continue; // only enemies.
                    }
                    List<Unit> list = allUnits[f];
                    if (list == null)
                    {
                        continue;
                    }
                    for (int i = 0; i < list.Count; i++)
                    {
                        Unit candidate = list[i];
                        if (candidate == null || candidate.Neutralized || candidate == exclude)
                        {
                            continue;
                        }
                        if (IsClaimedByOther(candidate, plane))
                        {
                            continue;
                        }
                        Transform center = candidate.Center;
                        if (center == null) continue;
                        // Measured from the PLANE, not from the called point: the second enemy is often far
                        // from where the player clicked but well within the aircraft's own reach, and using
                        // the called point as the centre is what left second sorties with no alternative.
                        float distanceSquared = (center.position - plane.transform.position).sqrMagnitude;
                        if (!(distanceSquared < wideBestDistanceSquared)) continue;
                        if (!CanPlaneAttack(plane, candidate))
                        {
                            continue; // no weapon for it - vanilla would end the run instead.
                        }

                        wideBestDistanceSquared = distanceSquared;
                        wideBest = candidate;
                    }
                }
                return wideBest;
            }

            private static Unit FindNearest(CASController plane, List<Unit> candidates, Vector3 interest, Unit exclude)
            {
                if (candidates == null || candidates.Count == 0)
                {
                    return null;
                }

                Unit best = null;
                float bestDistanceSquared = float.PositiveInfinity;
                for (int i = 0; i < candidates.Count; i++)
                {
                    Unit candidate = candidates[i];
                    if (candidate == null || candidate == exclude || candidate.Neutralized)
                    {
                        continue;
                    }
                    if (IsClaimedByOther(candidate, plane))
                    {
                        continue;
                    }

                    Transform center = candidate.Center;
                    if (center == null) continue;
                    float distanceSquared = (center.position - interest).sqrMagnitude;
                    if (distanceSquared < bestDistanceSquared)
                    {
                        bestDistanceSquared = distanceSquared;
                        best = candidate;
                    }
                }
                return best;
            }

            private static bool CanPlaneAttack(CASController plane, Unit unit)
            {
                if (GetIdealAttackTypeMethod == null)
                {
                    return true; // reflection failed: be permissive.
                }
                try
                {
                    if (GetIdealAttackType != null)
                        return GetIdealAttackType(plane, unit) != CASAttackType.Inert;
                    object result = GetIdealAttackTypeMethod.Invoke(plane, new object[] { unit });
                    // CASAttackType.Inert is GHPC's own "this aircraft has no weapon for that target"
                    // sentinel, so a plane that answers Inert is skipped as a candidate. It is not the
                    // removed training round - that one is kept out of the mod's payloads elsewhere
                    // (see SlotConfigParsing.IsRemovedAttackType).
                    return !CASAttackType.Inert.Equals(result);
                }
                catch
                {
                    return false;
                }
            }

            // AGM-65's per-shot plan uses the same native attack-type capability test as the
            // ordinary CAS spreading path.  Expose only this narrow wrapper so the missile plan
            // can revalidate a target immediately before its second trigger pull.
            internal static bool CanPlaneAttackForAgm(CASController plane, Unit unit)
            {
                return CanPlaneAttack(plane, unit);
            }

            private static void RecomputeAttackParams(CASController plane)
            {
                if (EnterStateMethod == null || TurnTowardTarget == null)
                {
                    return;
                }
                try
                {
                    EnterStateMethod.Invoke(plane, new object[] { TurnTowardTarget });
                }
                catch (Exception)
                {
                }
            }

            private static object ResolveTurnState()
            {
                try
                {
                    if (EnterStateMethod == null) return null;
                    ParameterInfo[] parameters = EnterStateMethod.GetParameters();
                    return parameters.Length == 1
                        ? Enum.Parse(parameters[0].ParameterType, "TurnTowardTarget") : null;
                }
                catch { return null; }
            }

        }
    }
}


