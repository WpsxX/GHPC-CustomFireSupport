using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using FMODUnity;
using GHPC;
using GHPC.Effects;
using GHPC.PhysicsHelpers;
using GHPC.UI;
using GHPC.UI.Map;
using GHPC.Vehicle;
//using GHPC.Weaponry;
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
    ///                                                target are re-routed to distinct ones.
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
                catch (Exception ex)
                {
                    Log.Error("InitControlState prefix failed: " + ex);
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
                        // The panel's selected entry is not one of ours. That used to happen silently
                        // when a re-roll replaced the airframe object the button was holding, and vanilla
                        // then picked "the first ready airframe" - i.e. the wrong aircraft, on the wrong
                        // attack type. It is a bug whenever it happens with a custom slot on the panel, so
                        // say so instead of quietly sending whatever vanilla finds.
                        if (CustomSupportRegistry.PanelHasActiveCasSupport())
                        {
                            Log.Warn("CAS call: the selected airframe is not one of the mod's slots (the panel " +
                                     "is pointing at an object the registry does not know); falling back to the " +
                                     "game's own pick, which may be a different aircraft and attack type.");
                        }
                        return true;
                    }

                    CasAirframeUnit[] array = unitFaction == Faction.Blue ? __instance.BlueCasAirframes : __instance.RedCasAirframes;
                    int index = array == null ? -1 : Array.IndexOf(array, airframe);
                    if (index < 0)
                    {
                        Log.Warn("CAS call: the selected airframe is not in the " + unitFaction +
                                 " array (it was injected before the player faction was known); " +
                                 "falling back to the game's own pick.");
                        return true;
                    }

                    casIndex = index;

                    // Bomb / rocket slots fly a different airframe with a different loadout each call
                    // (gun runs keep their designated airframe). The re-roll mutates this very airframe
                    // object, so the panel button, the manager's array and this call all stay in sync.
                    CustomSupportRegistry.TryRerollAirframeForCall(unitFaction, airframe);

                    // Vanilla instantiates the prefab and immediately does GetComponent<CASController>()
                    // on the clone. A donor whose controller is not on its own root would throw there
                    // (NullReferenceException) and abort the whole call, so refuse it with a clear log.
                    // CasDonorProvider now resolves donors to the aircraft root, which makes this a
                    // pure safety net rather than the normal path.
                    if (airframe.airframePrefab == null)
                    {
                        Log.Error("CAS call: the selected airframe has no prefab; the call was cancelled.");
                        __result = MapMissionResult.Empty;
                        return false;
                    }
                    if (airframe.airframePrefab.GetComponent<CASController>() == null)
                    {
                        CASController nested = airframe.airframePrefab.GetComponentInChildren<CASController>(true);
                        Log.Error("CAS call: airframe prefab '" + airframe.airframePrefab.name +
                                  "' has no CASController on its root" +
                                  (nested != null ? " (one exists on a child, which vanilla cannot use)" : string.Empty) +
                                  "; the call was cancelled. This donor should have been resolved to the aircraft root.");
                        __result = MapMissionResult.Empty;
                        return false;
                    }

                    Log.Verbose("CAS call routed to custom airframe index " + index + ".");
                    CustomSupportRegistry.MarkNextSpawnedSortie();
                }
                catch (Exception ex)
                {
                    Log.Error("SendCasSupport prefix failed: " + ex);
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
                catch (Exception ex)
                {
                    Log.Error("instant volley failed: " + ex.Message);
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
            /// <summary>One diagnostic line per hardpoint instance, so the log shows the resolved radius.</summary>
            private static readonly HashSet<int> Logged = new HashSet<int>();

            private static void Prefix(CASHardpoint __instance)
            {
                try
                {
                    // Never carry a stale point into this round.
                    CasPayloadFactory.ClearPendingImpact();

                    if (__instance == null || !CasPayloadFactory.UsesImpactResolver(__instance))
                    {
                        return; // vanilla / enemy / bomb / missile: the game flies it
                    }

                    CASController controller = __instance.GetComponentInParent<CASController>();
                    if (controller == null || controller.FinalTarget == null ||
                        controller.FinalTarget.Center == null)
                    {
                        return; // no locked target: nothing to resolve, the round stays ballistic
                    }

                    // The controller we just resolved is handed on, so the slot lookup does not have to
                    // walk the hierarchy a second time for every round of a 140-round burst.
                    float accuracy = CasPayloadFactory.SlotAccuracy(controller, __instance);
                    Vector3 offset = CasPayloadFactory.ImpactOffsetFor(__instance, accuracy);
                    bool guaranteed = CasPayloadFactory.IsGuaranteedHit(__instance);
                    CasPayloadFactory.SetPendingImpact(controller.FinalTarget.Center, offset, __instance.Ammo,
                        CasPayloadFactory.IsGravityAware(__instance));

                    if (Logged.Add(__instance.GetInstanceID()))
                    {
                        Log.Verbose("CAS impact resolver: '" + __instance.name + "' type=" + __instance.Type +
                                    " CasAccuracy=" + accuracy.ToString("0.###") + " -> " +
                                    (guaranteed
                                        ? "this payload always hits the locked target's centre (CasAccuracy is ignored for it)."
                                        : "every round is flown into a " +
                                          CasPayloadFactory.AccuracyRadius(accuracy).ToString("0.##") +
                                          " m circle around the locked target" +
                                          (CasPayloadFactory.AccuracyRadius(accuracy) <= 0f
                                              ? " (0 m = the target's own centre)."
                                              : (CasPayloadFactory.IsGravityAware(__instance)
                                                  ? " (gravity-aware terminal correction)."
                                                  : "."))));
                    }
                }
                catch (Exception ex)
                {
                    Log.Error("CAS impact point patch failed: " + ex);
                }
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
                Log.Verbose("slot " + slot.Config.Index + ": sortie recharge " + recharge.ToString("0.#") + "s.");
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
            private static bool _diagnosed;

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
                    if (ours && __instance.gameObject.GetComponent<CustomCasMarker>() == null)
                    {
                        __instance.gameObject.AddComponent<CustomCasMarker>();
                        Log.Verbose("CAS sortie marked for precision: '" + __instance.name + "'.");
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
                        Log.Verbose("CAS SetLoadout: activated hardpoint manager chain on '" + __instance.name + "'.");
                        return;
                    }

                    if (_diagnosed)
                    {
                        return;
                    }
                    _diagnosed = true;
                    Log.Warn("CAS controller '" + __instance.name + "' has no CASHardpointManager anywhere " +
                             "(including inactive). Hierarchy: " + DumpHierarchy(root));
                }
                catch (Exception ex)
                {
                    Log.Error("CAS SetLoadout manager patch failed: " + ex);
                }
            }

            private static string DumpHierarchy(GameObject root)
            {
                StringBuilder builder = new StringBuilder();
                AppendNode(builder, root.transform, 0);
                return builder.ToString();
            }

            private static void AppendNode(StringBuilder builder, Transform t, int depth)
            {
                for (int i = 0; i < depth; i++)
                {
                    builder.Append("  ");
                }
                builder.Append(t.name).Append(depth == 0 ? "(root)" : string.Empty)
                       .Append(" [active=").Append(t.gameObject.activeSelf)
                       .Append("] hasMgr=").Append(t.GetComponent<CASHardpointManager>() != null).Append('\n');
                for (int i = 0; i < t.childCount; i++)
                {
                    AppendNode(builder, t.GetChild(i), depth + 1);
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
                catch (Exception ex)
                {
                    Log.Error("CAS airspeed patch failed: " + ex);
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
                        if (GetTargetVelocityMethod != null)
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
                catch (Exception ex)
                {
                    Log.Error("CAS live-aim patch failed: " + ex);
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
        /// Missiles are still flown by the game's own guidance, so for them the knob keeps its historical
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

            /// <summary>
            /// True when the hardpoint belongs to an aircraft this mod summoned. Delegates to
            /// CasPayloadFactory so every patch shares exactly one definition.
            /// </summary>
            private static bool IsOurSortie(CASHardpoint hardpoint)
            {
                return CasPayloadFactory.IsOurSortie(hardpoint);
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

                    bool runtime = CasPayloadFactory.IsRuntimeHardpoint(__instance);
                    bool marker = __instance.GetComponentInParent<CustomCasMarker>() != null;
                    bool ours = runtime || IsOurSortie(__instance);
                    if (!ours)
                    {
                        LogFireOnce(__instance, "vanilla hardpoint (left untouched)", runtime, marker);
                        return; // vanilla / enemy CAS: leave the game's own values alone.
                    }

                    float accuracy = CasPayloadFactory.SlotAccuracy(__instance);

                    // The mod flies these rounds itself - the impact point is drawn in
                    // CasImpactPointPatch and the round is taken there by CasImpactAimPatch - so the
                    // release is made exact: any launch deviation would only give the correction more
                    // work to do (and for a bomb, a bigger terminal nudge to look at).
                    if (CasPayloadFactory.UsesImpactResolver(__instance))
                    {
                        __state.Original = DeviationRef(__instance);
                        __state.Scaled = true;
                        DeviationRef(__instance) = 0f;
                        LogFireOnce(__instance, "impact resolver owns the trajectory (CasAccuracy " +
                                                CasPayloadFactory.AccuracyRadius(accuracy).ToString("0.##") +
                                                " m circle, release deviation " +
                                                __state.Original.ToString("0.###") + " -> 0)", runtime, marker);
                        return;
                    }

                    if (Mathf.Approximately(accuracy, 1f))
                    {
                        LogFireOnce(__instance, "natural spread kept", runtime, marker);
                        return; // natural spread.
                    }

                    __state.Original = DeviationRef(__instance);
                    __state.Scaled = true;
                    DeviationRef(__instance) = CustomSlotBuilder.ScaleValue(__state.Original, accuracy);
                    LogFireOnce(__instance, "deviation " + __state.Original.ToString("0.###") + " -> " +
                                             DeviationRef(__instance).ToString("0.###") + " (scale " +
                                             accuracy.ToString("0.###") + ")", runtime, marker);
                }
                catch (Exception ex)
                {
                    Log.Error("CAS accuracy patch failed: " + ex);
                }
            }

            /// <summary>One line per hardpoint instance: proves what the fire-time patch actually did.</summary>
            private static void LogFireOnce(CASHardpoint hardpoint, string detail, bool runtime, bool marker)
            {
                if (hardpoint == null || !_logged.Add(hardpoint.GetInstanceID()))
                {
                    return;
                }
                Log.Verbose("CAS fire diag: '" + hardpoint.name + "' type=" + hardpoint.Type +
                            " runtime=" + runtime + " marker=" + marker + " -> " + detail + ".");
            }

            private static readonly HashSet<int> _logged = new HashSet<int>();

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

                    Log.Info("CAS gun run: firing from a single hardpoint at " +
                             bestLateral.ToString("0.0") + " m off the centreline (the other mounted " +
                             "gun copies are dropped from the attack); 140 rounds, one stream.");
                }
                catch (Exception ex)
                {
                    Log.Error("CAS single-hardpoint patch failed: " + ex);
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
            private static readonly HashSet<int> _burstLogged = new HashSet<int>();

            /// <summary>How long after the last round we keep collecting impacts for the hit report.</summary>
            private const float CollectWindowSeconds = 3f;

            /// <summary>Active per-burst impact collector, fed by CasImpactEffectFallbackPatch.</summary>
            private static HitReport _report;

            private sealed class HitReport
            {
                internal Vector3 TargetCentre;
                internal readonly HashSet<int> SeenRounds = new HashSet<int>();
                internal readonly List<float> Distances = new List<float>(160);
                internal Vector3 Centroid;

                internal void Add(LiveRound round)
                {
                    if (round == null)
                    {
                        return;
                    }
                    int id = round.GetInstanceID();
                    if (!SeenRounds.Add(id))
                    {
                        return; // one impact per round
                    }
                    Vector3 offset = round.transform.position - TargetCentre;
                    offset.y = 0f;
                    Distances.Add(offset.magnitude);
                    Centroid += offset;
                }
            }

            private static bool Prefix(CASHardpointManager __instance, CASAttackMeta meta, ref IEnumerator __result)
            {
                if (__instance == null || !CasPayloadFactory.IsOurGunMeta(meta))
                {
                    return true; // vanilla / enemy aircraft: keep the game's own coroutine.
                }
                __result = Burst(__instance, meta);
                return false;
            }

            /// <summary>Feeds the impact collector for the burst currently in the air.</summary>
            internal static void NoteImpact(LiveRound round)
            {
                HitReport report = _report;
                if (report != null)
                {
                    report.Add(round);
                }
            }

            private static IEnumerator Burst(CASHardpointManager manager, CASAttackMeta meta)
            {
                BusyRef(manager) = true;

                CASController controller = manager != null ? manager.GetComponentInParent<CASController>() : null;

                // One line per sortie: proves the launch-inherit speed the fire control uses.
                // It must read ~ the aircraft's airspeed (analytic), never 0.
                if (controller != null && _burstLogged.Add(controller.GetInstanceID()))
                {
                    float range = 0f;
                    if (controller.FinalTarget != null && controller.FinalTarget.Center != null)
                    {
                        Vector3 flat = controller.FinalTarget.Center.position - controller.transform.position;
                        flat.y = 0f;
                        range = flat.magnitude;
                    }
                    Log.Info("CAS gun burst: " + Mathf.Max(1, meta.TriggerPulls) + " rounds, " +
                             "inherit launch speed " + controller.Velocity.magnitude.ToString("0") + " m/s, " +
                             "range to target " + range.ToString("0") + " m, " +
                             "sustained audio on.");
                }

                // Arm the impact collector around the locked target (static during the burst).
                HitReport report = null;
                if (controller != null && controller.FinalTarget != null && controller.FinalTarget.Center != null)
                {
                    report = new HitReport { TargetCentre = controller.FinalTarget.Center.position };
                    _report = report;
                }

                // Sustained gun sound for the whole burst: at strafing distance the per-round one-shots
                // alone are a thin, barely audible crackle, so the burst also drives the hardpoint's
                // StudioEventEmitter (see CasPayloadFactory.AttachGunAudio).
                StudioEventEmitter gunAudio = null;
                bool audioStopped = false;
                bool busyCleared = false;
                try
                {
                    try
                    {
                        gunAudio = CasPayloadFactory.FindGunEmitter(meta);
                        if (gunAudio != null)
                        {
                            gunAudio.Play();
                        }
                    }
                    catch (Exception ex)
                    {
                        Log.Error("CAS gun audio: could not start the sustained fire event: " + ex);
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
                            if (FireMetaMethod != null)
                            {
                                FireMetaMethod.Invoke(manager, new object[] { meta });
                            }
                            fired++;
                            nextShotAt += interval;
                        }
                        yield return null;
                        elapsed += Time.deltaTime;
                    }

                    // The burst is over: stop the sound and free the manager now, then keep collecting
                    // impacts for the hit report (the last rounds are still in flight).
                    if (gunAudio != null)
                    {
                        try
                        {
                            gunAudio.Stop();
                            audioStopped = true;
                        }
                        catch (Exception ex)
                        {
                            Log.Error("CAS gun audio: could not stop the sustained fire event: " + ex);
                        }
                    }
                    BusyRef(manager) = false;
                    busyCleared = true;

                    if (report != null)
                    {
                        float collectUntil = Time.time + CollectWindowSeconds;
                        while (Time.time < collectUntil)
                        {
                            yield return null;
                        }
                        FinishReport(report);
                    }
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
                    _report = null;
                }
            }

            /// <summary>Prints one summary line per burst: how many rounds landed where vs the target.</summary>
            private static void FinishReport(HitReport report)
            {
                if (report == null || report.Distances.Count == 0)
                {
                    return;
                }
                List<float> distances = report.Distances;
                float nearest = float.MaxValue;
                float farthest = 0f;
                float sum = 0f;
                int near6 = 0;
                int near12 = 0;
                for (int i = 0; i < distances.Count; i++)
                {
                    float d = distances[i];
                    if (d < nearest)
                    {
                        nearest = d;
                    }
                    if (d > farthest)
                    {
                        farthest = d;
                    }
                    sum += d;
                    if (d <= 6f)
                    {
                        near6++;
                    }
                    if (d <= 12f)
                    {
                        near12++;
                    }
                }
                Vector3 centroid = report.Centroid / distances.Count;

                Log.Info("CAS gun hit report: " + distances.Count + " impacts; " + near6 + " inside 6 m, " +
                         near12 + " inside 12 m of the target centre; nearest " + nearest.ToString("0.0") +
                         " m, mean " + (sum / distances.Count).ToString("0.0") + " m, farthest " +
                         farthest.ToString("0") + " m; centroid offset from centre (" +
                         centroid.x.ToString("+0.0;-0.0;0.0") + ", " + centroid.z.ToString("+0.0;-0.0;0.0") + ") m.");
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
                catch (Exception ex)
                {
                    Log.Error("CAS gun audio throttle failed: " + ex);
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

                    // Hit-report telemetry: count every impact of the burst in the air (deduped per
                    // round), whatever the VFX outcome below is.
                    CasGunBurstPatch.NoteImpact(__instance);

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
                catch (Exception ex)
                {
                    Log.Error("CAS impact fallback failed: " + ex);
                }
            }

            /// <summary>
            /// The air-to-ground missile's impact: report what happened, and make sure a high-explosive
            /// warhead is HEARD. The game plays a round's impact sound through
            /// ImpactSFXManager.PlaySimpleImpactAudio with "fuzed" set from this round's own warhead state;
            /// when the fuze did not complete it downgrades a bomb or missile detonation to a kinetic clang
            /// (see the switch in that method), which is exactly "an explosion with no explosion sound". The
            /// missile's explosion effect itself comes from the bomb's own effect descriptor, so only the
            /// sound needs the fallback.
            /// </summary>
            private static void NoteMissileImpact(LiveRound round, bool terrainHit)
            {
                bool fuzed = FuzeRef(round) || JetRef(round);
                bool vfx = terrainHit ? TerrainVfxRef(round) : NonTerrainVfxRef(round);

                Log.Verbose("CAS missile impact: '" + round.Info.Name + "' at " +
                            round.transform.position.ToString("0.#") + " - fuzed=" + fuzed +
                            ", terrainHit=" + terrainHit + ", vanilla effect=" + vfx + ".");

                if (fuzed)
                {
                    return; // the game played the warhead's own explosion sound.
                }

                CasPayloadFactory.PlayFuzedImpactAudio(round.Info, round.transform.position);
                Log.Info("CAS missile impact: the round did not fuze, so the game would only have played a " +
                         "kinetic impact sound - the warhead's own detonation audio was played instead.");
            }
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

            /// <summary>Within this distance of the point the mod stops steering and the game takes over.</summary>
            private const float HandoverDistance = 2f;

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

            private static void Prefix(LiveRound __instance)
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

                    Vector3 point;
                    if (!CasPayloadFactory.TryGetImpactPoint(aim, out point))
                    {
                        aim.Released = true; // target gone (destroyed / de-spotted): fly on ballistically
                        return;
                    }

                    Vector3 position = __instance.transform.position;

                    if (aim.GravityAware)
                    {
                        CorrectFallingRound(__instance, aim, position, point);
                        return;
                    }

                    Vector3 toPoint = point - position;
                    float distance = toPoint.magnitude;
                    Vector3 forward = __instance.transform.forward;

                    // Hand over when the point is reached, or when it is already behind the round - the
                    // latter is what keeps a round that penetrated the hull from being turned back
                    // around inside it.
                    if (distance <= HandoverDistance || Vector3.Dot(toPoint, forward) <= 0f)
                    {
                        aim.Released = true;
                        return;
                    }

                    Vector3 direction = toPoint / distance;
                    __instance.transform.forward = direction;

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
                    if (pinned > 0f && !aim.SpeedLogged)
                    {
                        aim.SpeedLogged = true;
                        Log.Info("CAS missile flight speed: '" + __instance.Info.Name + "' flies at " +
                                 speed.ToString("0") + " m/s (Mach " +
                                 CasAirframeCatalog.MachOf(speed).ToString("0.0#") +
                                 ") for the whole flight, whatever speed the aircraft released it at.");
                    }

                    MotionState state = MotionRef(__instance);
                    state.position = position;
                    state.velocity = direction * speed;
                    MotionRef(__instance) = state;
                }
                catch (Exception ex)
                {
                    Log.Error("CAS impact steering failed: " + ex);
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
            private static void CorrectFallingRound(LiveRound round, CasImpactAim aim, Vector3 position, Vector3 point)
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

                if (!aim.CorrectionLogged)
                {
                    aim.CorrectionLogged = true;
                    Log.Verbose("CAS bomb terminal correction: " + Mathf.Sqrt(miss.sqrMagnitude).ToString("0.##") +
                                " m to go in " + timeToImpact.ToString("0.##") + " s -> horizontal nudge " +
                                correction.magnitude.ToString("0.##") + " m/s.");
                }
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
            private static void Prefix(LiveRound __instance)
            {
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
                    stale.Offset = Vector3.zero;
                    stale.ShotId = 0;
                    stale.GravityAware = false;
                    stale.CorrectionLogged = false;
                    stale.SpeedLogged = false;
                }
                catch (Exception ex)
                {
                    Log.Error("CAS impact detach patch failed: " + ex);
                }
            }

            private static void Postfix(LiveRound __instance)
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
                    bool ours = CasPayloadFactory.ConsumePendingImpact(__instance.Info, out target, out offset,
                        out gravityAware);

                    // The air-to-ground missile's flight effects are the TOW's, composed into the bundle;
                    // the game's own effect materials are swapped in here, because this is the first moment
                    // the mission is guaranteed to be fully loaded (see CasMissileVisualRepair).
                    if (CasPayloadFactory.IsOurMissile(__instance.Info))
                    {
                        CasMissileVisualRepair.Apply(__instance.gameObject);
                    }

                    if (!ours)
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
                    aim.CorrectionLogged = false;
                    aim.SpeedLogged = false;
                    aim.Released = false;

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
                catch (Exception ex)
                {
                    Log.Error("CAS impact attach patch failed: " + ex);
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
        /// </summary>
        [HarmonyPatch(typeof(CASController), "SearchForTarget")]
        internal static class CasTargetSpreadPatch
        {
            private const float WideSearchRadius = 3000f;

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

            /// <summary>Target -> the plane currently attacking it.</summary>
            private static readonly Dictionary<Unit, CASController> Claims = new Dictionary<Unit, CASController>();

            private static void Postfix(CASController __instance)
            {
                try
                {
                    if (__instance == null)
                    {
                        return;
                    }

                    Unit chosen = __instance.FinalTarget;
                    if (chosen == null)
                    {
                        return;
                    }

                    PruneStaleClaims();
                    ReleasePlaneClaims(__instance);

                    Unit result = chosen;
                    if (IsClaimedByOther(chosen, __instance))
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
                        Log.Info("CAS target spreading: '" + __instance.gameObject.name + "' re-routed from '" +
                                 chosen.FriendlyName + "' to '" + result.FriendlyName + "' (" + Claims.Count +
                                 " claim(s) active).");
                    }

                    Claims[result] = __instance;
                }
                catch (Exception ex)
                {
                    Log.Error("CAS target spreading patch failed: " + ex);
                }
            }

            private static bool IsClaimedByOther(Unit target, CASController plane)
            {
                if (target == null)
                {
                    return false;
                }
                CASController other;
                return Claims.TryGetValue(target, out other) && other != null && other != plane;
            }

            private static Unit FindAlternativeTarget(CASController plane, Unit exclude)
            {
                Vector3 interest = InterestRef(plane);

                // 1) The plane's own spotted list (vanilla already filtered visibility + attackability).
                Unit best = FindNearest(plane, SpottedRef(plane), interest, exclude, float.MaxValue);
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
                float wideBestDistance = WideSearchRadius;
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
                        if (!CanPlaneAttack(plane, candidate))
                        {
                            continue; // no weapon for it - vanilla would end the run instead.
                        }

                        float distance = Vector3.Distance(candidate.Center.position, interest);
                        if (distance < wideBestDistance)
                        {
                            wideBestDistance = distance;
                            wideBest = candidate;
                        }
                    }
                }
                return wideBest;
            }

            private static Unit FindNearest(CASController plane, List<Unit> candidates, Vector3 interest, Unit exclude, float maxDistance)
            {
                if (candidates == null || candidates.Count == 0)
                {
                    return null;
                }

                Unit best = null;
                float bestDistance = maxDistance;
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

                    float distance = Vector3.Distance(candidate.Center.position, interest);
                    if (distance < bestDistance)
                    {
                        bestDistance = distance;
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

            private static void RecomputeAttackParams(CASController plane)
            {
                if (EnterStateMethod == null)
                {
                    return;
                }
                try
                {
                    ParameterInfo[] parameters = EnterStateMethod.GetParameters();
                    if (parameters.Length != 1)
                    {
                        return;
                    }
                    object state = Enum.Parse(parameters[0].ParameterType, "TurnTowardTarget");
                    EnterStateMethod.Invoke(plane, new object[] { state });
                }
                catch (Exception ex)
                {
                    Log.Error("CAS target spreading: failed to recompute attack parameters: " + ex.Message);
                }
            }

            private static void ReleasePlaneClaims(CASController plane)
            {
                List<Unit> release = null;
                foreach (KeyValuePair<Unit, CASController> pair in Claims)
                {
                    if (pair.Value == plane)
                    {
                        if (release == null)
                        {
                            release = new List<Unit>();
                        }
                        release.Add(pair.Key);
                    }
                }
                if (release != null)
                {
                    for (int i = 0; i < release.Count; i++)
                    {
                        Claims.Remove(release[i]);
                    }
                }
            }

            private static void PruneStaleClaims()
            {
                List<Unit> stale = null;
                foreach (KeyValuePair<Unit, CASController> pair in Claims)
                {
                    Unit target = pair.Key;
                    CASController plane = pair.Value;
                    if (target == null || plane == null || plane.FinalTarget != target)
                    {
                        if (stale == null)
                        {
                            stale = new List<Unit>();
                        }
                        stale.Add(target);
                    }
                }
                if (stale != null)
                {
                    for (int i = 0; i < stale.Count; i++)
                    {
                        Claims.Remove(stale[i]);
                    }
                }
            }
        }
    }
}
