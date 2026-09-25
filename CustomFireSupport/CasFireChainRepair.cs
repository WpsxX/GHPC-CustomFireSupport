using System;
using System.Collections.Generic;
using System.Text;
using GHPC.Vehicle;
using GHPC.Weapons;
using HarmonyLib;
using UnityEngine;

namespace CustomFireSupport
{
    /// <summary>
    /// The game's CAS firing chain has three ways to end in "the aircraft flies its pass and drops
    /// nothing", and every one of them reports itself through <c>UnityEngine.Debug</c> - which
    /// MelonLoader does not write to the player's log. The player therefore sees a silent no-fire and
    /// nothing to report; a previous attempt at this mod "fixed" it by bypassing an unrelated gate
    /// (CasAirframeUnit.IsReady) and that made things worse.
    ///
    /// What the chain actually does, read out of the shipped assembly:
    ///
    ///   1. CASController.EnterState(TurnTowardTarget)
    ///          _finalAttackType = GetIdealAttackType(FinalTarget);
    ///          CASAttackMeta meta = _hardpointManager.GetAttackMetaByType(_finalAttackType);
    ///          _releaseDistance = meta.ReleaseDistance;      // NO null check
    ///      A type with no attack entry throws a NullReferenceException right there. The state was
    ///      already switched, so the aircraft carries on with the PREVIOUS distances.
    ///
    ///   2. CASController.EnterState(OnFinal)
    ///          _pendingAmmoType = GetAmmoType(_finalAttackType);
    ///          if (_pendingAmmoType == null) { "Attempting to fix..."; GetIdealAttackType(...); }  // result DISCARDED
    ///          if (_pendingAmmoType == null) { "Could not fix. Ending attack run..."; EndAttackRun(); }
    ///      The "fix" re-runs the same call and throws its answer away, so it can never succeed.
    ///
    ///   3. CASController.Fire (coroutine)
    ///          if (!_hardpointManager.CanDoAttackType(_finalAttackType)) { EndAttackRun(); yield break; }
    ///      No log at all.
    ///
    ///   4. CASHardpointManager.Fire(type)
    ///          if (!_configDone) / (_busyFiring) / (!CanDoAttackType(type)) -> Debug.Log + return.
    ///      _configDone is false whenever CASHardpointManager.HasCriticalConfigError() rejected the
    ///      loadout (a HardpointPrefabs list that is neither one entry nor one entry per attach point),
    ///      and _busyFiring is left true for good if the vanilla MultiFire coroutine is interrupted
    ///      (the aircraft leaving is enough) - after which EVERY later attack on that manager is
    ///      refused.
    ///
    /// So this file does two things:
    ///
    ///   * it makes the chain observable - one audit line per sortie at loadout time, and one line
    ///     whenever an attack is about to be refused, both through the mod's logger;
    ///   * it removes the failure modes themselves for the mod's own sorties: the attack type is
    ///     corrected to one the sortie can actually fire, a loadout the game would refuse is repaired
    ///     before DoConfig sees it, a stuck _busyFiring cannot survive a burst, and a gun run can no
    ///     longer end up with a single trigger pull.
    ///
    /// Nothing here touches vanilla or enemy aircraft: every entry point starts with the same
    /// ownership test the rest of the mod uses (see <see cref="IsOurSortie(CASController)"/>).
    /// </summary>
    internal static class CasFireChainRepair
    {
        /// <summary>Aircraft already audited this session, so a repeated loadout set does not repeat it.</summary>
        private static readonly HashSet<int> Audited = new HashSet<int>();

        internal static void ResetForScene()
        {
            Audited.Clear();
        }

        /// <summary>
        /// True when the controller is flying one of the mod's sorties. Delegates to the hardpoint test
        /// the rest of the mod uses, so "ours" has exactly one definition: the CustomCasMarker the
        /// SetLoadout patch puts on the aircraft, or the spawn bookkeeping of the CasAirframeUnit array.
        /// </summary>
        internal static bool IsOurSortie(CASController controller)
        {
            if (controller == null)
            {
                return false;
            }
            if (controller.GetComponent<CustomCasMarker>() != null)
            {
                return true;
            }

            CASHardpointManager manager = controller.GetComponentInChildren<CASHardpointManager>(true);
            if (manager == null)
            {
                return false;
            }
            CASHardpoint[] points = manager.GetComponentsInChildren<CASHardpoint>(true);
            for (int i = 0; i < points.Length; i++)
            {
                if (CasPayloadFactory.IsOurSortie(points[i]))
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// The two rules this repair is built on live in <see cref="CasFireChainRules"/> so they can be
        /// regression-tested without the game; this class applies them to live objects.
        /// </summary>
        private static CASHardpointManager ManagerOf(CASController controller)
        {
            return controller == null ? null : controller.GetComponentInChildren<CASHardpointManager>(true);
        }

        /// <summary>
        /// Mirrors CASHardpointManager.HasCriticalConfigError() for a loadout about to be applied, and
        /// repairs the one case the mod can repair. Returns true when the game will accept the loadout.
        ///
        /// CASHardpointManager.SetUpHardpoints instantiates HardpointPrefabs[i] for EVERY attach point
        /// (reusing entry 0 when the list has a single entry), and DoConfig() bails out - leaving
        /// _configDone false, so every Fire() is refused forever - when the list is neither a single
        /// entry nor at least one entry per attach point. The mod already guarantees this when it builds
        /// a loadout; this is the last line of defence, and it runs BEFORE the game's DoConfig because
        /// it is called from the SetLoadout prefix.
        /// </summary>
        internal static bool EnsureLoadoutConfigurable(CASHardpointManager manager, CASLoadoutScriptable loadout)
        {
            if (manager == null || loadout == null || loadout.Loadout == null)
            {
                return false;
            }

            CASLoadout body = loadout.Loadout;
            GameObject[] prefabs = body.HardpointPrefabs;
            int attachPoints = manager.HardpointAttachPoints == null ? 0 : manager.HardpointAttachPoints.Length;

            if (prefabs == null || prefabs.Length == 0)
            {
                Log.Warn("CAS fire chain: the loadout for '" + manager.gameObject.name +
                         "' carries no hardpoint prefab at all; the game refuses to configure it, so this " +
                         "sortie cannot fire. (attack entries: " + DescribeAttacks(body.Attacks) + ")");
                return false;
            }
            if (!CasFireChainRules.HardpointListFits(prefabs.Length, attachPoints))
            {
                if (attachPoints == 0)
                {
                    Log.Warn("CAS fire chain: '" + manager.gameObject.name +
                             "' reports no hardpoint attach point; the game refuses to configure it.");
                    return false;
                }

                // The game's own critical error. Only a loadout this mod built may be rewritten: a vanilla
                // asset is shared with the game's own (and the enemy's) sorties.
                GameObject first = null;
                for (int i = 0; i < prefabs.Length && first == null; i++)
                {
                    first = prefabs[i];
                }
                if (!CustomSlotBuilder.IsOurLoadout(loadout) || first == null)
                {
                    Log.Warn("CAS fire chain: loadout '" + loadout.name + "' has " + prefabs.Length +
                             " hardpoint prefab(s) for " + attachPoints + " attach point(s); the game requires " +
                             "one entry or one per attach point, so it will refuse to configure this sortie and " +
                             "the pass is flown dry. Not rewriting it: it is not a loadout this mod built.");
                    return false;
                }

                body.HardpointPrefabs = new[] { first };
                Log.Warn("CAS fire chain: loadout '" + loadout.name + "' had " + prefabs.Length +
                         " hardpoint prefab(s) for " + attachPoints + " attach point(s), which the game refuses " +
                         "(it wants one entry or one per attach point). Collapsed to the single prefab '" +
                         first.name + "', which the game reuses on every attach point, so the sortie can fire.");
            }
            return true;
        }

        /// <summary>
        /// One line per sortie: what the game will be able to fire, and the reason if it cannot fire
        /// anything. Called after SetLoadout, i.e. after DoConfig has run, so it reports the state the
        /// firing chain will actually see.
        /// </summary>
        internal static void AuditSortie(CASController controller, CASLoadoutScriptable loadout)
        {
            try
            {
                if (controller == null || !Audited.Add(controller.GetInstanceID()))
                {
                    return;
                }
                if (!IsOurSortie(controller))
                {
                    Audited.Remove(controller.GetInstanceID());
                    return;
                }

                CASHardpointManager manager = ManagerOf(controller);
                if (manager == null)
                {
                    Log.Warn("CAS fire chain: sortie '" + controller.name + "' has no CASHardpointManager, so " +
                             "the game never configures a loadout and the aircraft can never fire.");
                    return;
                }

                CASHardpoint[] points = manager.GetComponentsInChildren<CASHardpoint>(true);
                StringBuilder mounted = new StringBuilder();
                int totalRounds = 0;
                for (int i = 0; i < points.Length; i++)
                {
                    CASHardpoint point = points[i];
                    if (point == null) continue;
                    if (mounted.Length > 0) mounted.Append(", ");
                    mounted.Append(point.Type).Append('=').Append(point.TotalMunitionsRemaining)
                          .Append('/').Append(point.TotalMunitionsCapacity);
                    totalRounds += point.TotalMunitionsRemaining;
                }

                Log.Info("CAS fire chain: sortie '" + controller.name + "' flew with " +
                         (loadout != null && loadout.Loadout != null
                             ? loadout.Loadout.HardpointPrefabs != null
                                 ? loadout.Loadout.HardpointPrefabs.Length + " hardpoint prefab(s)"
                                 : "no hardpoint prefab list"
                             : "no loadout") +
                         " over " + (manager.HardpointAttachPoints == null ? 0 : manager.HardpointAttachPoints.Length) +
                         " attach point(s); mounted: " + (mounted.Length == 0 ? "(none)" : mounted.ToString()) +
                         "; attack entries: " +
                         (loadout != null && loadout.Loadout != null
                             ? DescribeAttacks(loadout.Loadout.Attacks)
                             : "(none)") + ".");

                // The two ways the firing chain goes quiet, reported the moment the sortie starts rather
                // than after the player has watched an aircraft drop nothing.
                for (int i = 0; i < CasFireChainRules.AttackTypes.Length; i++)
                {
                    bool mountedType = false;
                    for (int p = 0; p < points.Length; p++)
                    {
                        if (points[p] != null && points[p].Type == CasFireChainRules.AttackTypes[i] && points[p].TotalMunitionsRemaining > 0)
                        {
                            mountedType = true;
                            break;
                        }
                    }
                    bool metaExists = manager.GetAttackMetaByType(CasFireChainRules.AttackTypes[i]) != null;
                    if (mountedType && !metaExists)
                    {
                        Log.Warn("CAS fire chain: the sortie carries " + CasFireChainRules.AttackTypes[i] + " pylons with rounds " +
                                 "but its loadout declares no " + CasFireChainRules.AttackTypes[i] + " attack entry. If the game " +
                                 "picks that type the attack cannot fire: EnterState reads the missing entry " +
                                 "without a null check, and Fire() abandons the run without a word. The type " +
                                 "is corrected at pick time instead (CasAttackTypeGuard).");
                    }
                    else if (!mountedType && metaExists && points.Length > 0)
                    {
                        Log.Verbose("CAS fire chain: attack entry " + CasFireChainRules.AttackTypes[i] +
                                    " exists with no pylon behind it (harmless: CanDoAttackType stays false).");
                    }
                }

                if (totalRounds == 0 && points.Length > 0)
                {
                    Log.Warn("CAS fire chain: every mounted pylon of this sortie reports zero rounds left, " +
                             "so the game will refuse the attack as 'out of munitions'.");
                }
            }
            catch (Exception ex)
            {
                Log.Error("CAS fire chain audit failed: " + ex);
            }
        }

        private static string DescribeAttacks(CASAttackMeta[] attacks)
        {
            if (attacks == null || attacks.Length == 0)
            {
                return "(none - the aircraft would fly its pass without firing)";
            }
            StringBuilder builder = new StringBuilder();
            for (int i = 0; i < attacks.Length; i++)
            {
                if (attacks[i] == null)
                {
                    continue;
                }
                if (builder.Length > 0) builder.Append(", ");
                builder.Append(attacks[i].UniqueType).Append("(pulls=").Append(attacks[i].TriggerPulls)
                       .Append(", allAtOnce=").Append(attacks[i].FireAllAtOnce ? 1 : 0).Append(')');
            }
            return builder.Length == 0 ? "(none - the aircraft would fly its pass without firing)" : builder.ToString();
        }

        /// <summary>
        /// Postfix on CASController.GetIdealAttackType: never hand the firing chain a type this sortie
        /// cannot actually fire.
        ///
        /// This is the fix for the silent dry pass. The game derives the attack type from the TARGET and
        /// the mounted pylons, then reads the matching attack entry without a null check (see the class
        /// comment). When the two disagree the aircraft either throws inside EnterState or quietly ends
        /// the attack run, and nothing the player can see says why. Correcting the type here - before it
        /// is stored in _finalAttackType - prevents both, and the corrected type is always one that has
        /// BOTH a mounted pylon and an attack entry, i.e. one the chain can really fire.
        /// </summary>
        [HarmonyPatch(typeof(CASController), "GetIdealAttackType")]
        internal static class CasAttackTypeGuard
        {
            private static void Postfix(CASController __instance, ref CASAttackType __result)
            {
                try
                {
                    if (__result == CASAttackType.Inert || !IsOurSortie(__instance))
                    {
                        return; // the game already found nothing, or it is not our sortie.
                    }

                    CASHardpointManager manager = ManagerOf(__instance);
                    if (manager == null)
                    {
                        return;
                    }

                    if (CanFire(manager, __result))
                    {
                        return;
                    }

                    CASAttackType replacement = __result;
                    if (!TryPickFireableType(manager, __result, out replacement))
                    {
                        Log.Warn("CAS fire chain: the game picked " + __result + " for this sortie but the " +
                                 "loadout cannot fire it, and no other attack type is fireable either " +
                                 "(" + DescribeMounted(manager) + "). The pass will be flown dry.");
                        return;
                    }

                    Log.Warn("CAS fire chain: the game picked " + __result + " for this sortie, which the " +
                             "loadout cannot fire (that is the silent dry pass - the game reads the missing " +
                             "attack entry without a null check). Firing " + replacement + " instead; " +
                             DescribeMounted(manager) + ".");
                    __result = replacement;
                }
                catch (Exception ex)
                {
                    Log.Error("CAS fire chain type guard failed: " + ex);
                }
            }

            /// <summary>
            /// A type is fireable when the sortie has a non-empty pylon of it AND an attack entry for it:
            /// CanDoAttackType covers the first, GetAttackMetaByType the second, and the game needs both.
            /// </summary>
            private static bool[] FireableTypes(CASHardpointManager manager)
            {
                bool[] fireable = new bool[CasFireChainRules.AttackTypes.Length];
                for (int i = 0; i < CasFireChainRules.AttackTypes.Length; i++)
                {
                    fireable[i] = manager.CanDoAttackType(CasFireChainRules.AttackTypes[i]) &&
                                  manager.GetAttackMetaByType(CasFireChainRules.AttackTypes[i]) != null;
                }
                return fireable;
            }

            private static bool CanFire(CASHardpointManager manager, CASAttackType type)
            {
                return FireableTypes(manager)[CasFireChainRules.TypeIndex(type)];
            }

            /// <summary>
            /// The closest fireable type: the requested one when possible, otherwise the best of the
            /// remaining, preferring the order the game's own per-target tables use (bombs and rockets
            /// first: they are what a bomb/rocket slot exists for).
            /// </summary>
            private static bool TryPickFireableType(CASHardpointManager manager, CASAttackType wanted,
                out CASAttackType chosen)
            {
                bool[] fireable = FireableTypes(manager);
                int index;
                bool found = CasFireChainRules.ChooseFireableType(fireable, CasFireChainRules.TypeIndex(wanted), out index);
                chosen = found ? CasFireChainRules.AttackTypes[index] : wanted;
                return found;
            }

            private static string DescribeMounted(CASHardpointManager manager)
            {
                CASHardpoint[] points = manager.GetComponentsInChildren<CASHardpoint>(true);
                StringBuilder builder = new StringBuilder("mounted: ");
                bool any = false;
                for (int i = 0; i < points.Length; i++)
                {
                    if (points[i] == null) continue;
                    if (any) builder.Append(", ");
                    builder.Append(points[i].Type).Append('=').Append(points[i].TotalMunitionsRemaining);
                    any = true;
                }
                if (!any) builder.Append("(none)");
                return builder.ToString();
            }
        }

        /// <summary>
        /// Prefix on CASHardpointManager.Fire(CASAttackType): says WHY an attack is about to be refused.
        ///
        /// The game's own reasons are Debug.Log lines, which never reach the player's log, so "the plane
        /// came and dropped nothing" has been undiagnosable. This only reports; it changes no decision.
        ///
        /// The overload is named explicitly: the manager also has Fire(CASAttackMeta), and patching "Fire"
        /// by name alone would be ambiguous.
        /// </summary>
        [HarmonyPatch(typeof(CASHardpointManager), "Fire", new[] { typeof(CASAttackType) })]
        internal static class CasFireRefusalLogPatch
        {
            private static readonly AccessTools.FieldRef<CASHardpointManager, bool> BusyRef =
                AccessTools.FieldRefAccess<CASHardpointManager, bool>("_busyFiring");
            private static readonly AccessTools.FieldRef<CASHardpointManager, List<CASAttackMeta>> AttacksRef =
                AccessTools.FieldRefAccess<CASHardpointManager, List<CASAttackMeta>>("_attacks");

            /// <summary>One line per (sortie, attack type): a refusal is a state, not a per-round event.</summary>
            private static readonly HashSet<string> Reported = new HashSet<string>(StringComparer.Ordinal);

            internal static void ResetForScene()
            {
                Reported.Clear();
            }

            private static void Prefix(CASHardpointManager __instance, CASAttackType type)
            {
                try
                {
                    if (__instance == null)
                    {
                        return;
                    }
                    CASController controller = __instance.GetComponentInParent<CASController>();
                    if (!IsOurSortie(controller))
                    {
                        return;
                    }

                    string key = __instance.GetInstanceID() + "/" + type;
                    bool busy = BusyRef != null && BusyRef(__instance);
                    bool can = __instance.CanDoAttackType(type);
                    if (can && !busy)
                    {
                        Reported.Remove(key);
                        return; // this attack will fire; nothing to explain.
                    }
                    if (!Reported.Add(key))
                    {
                        return;
                    }

                    string reason = busy
                        ? "_busyFiring is still true, so the game treats this manager as mid-burst and " +
                          "refuses every further attack on it (a vanilla burst that was interrupted - the " +
                          "aircraft leaving is enough - never clears the flag)"
                        : "CanDoAttackType(" + type + ") is false: no non-empty pylon of that type is mounted";
                    Log.Warn("CAS fire chain: refusing " + type + " on '" + __instance.gameObject.name +
                             "' - " + reason + ". " + DescribePylons(__instance) + ".");

                    if (!busy)
                    {
                        Log.Warn("CAS fire chain: this sortie is carrying " + DescribeAttackEntries(__instance) +
                                 ", so the game can only fire what its pylons deliver. If the requested slot " +
                                 "type is missing here, the loadout that was mounted does not match the slot.");
                    }
                }
                catch (Exception ex)
                {
                    Log.Error("CAS fire chain refusal log failed: " + ex);
                }
            }

            private static string DescribePylons(CASHardpointManager manager)
            {
                CASHardpoint[] points = manager.GetComponentsInChildren<CASHardpoint>(true);
                StringBuilder builder = new StringBuilder("pylons: ");
                bool any = false;
                for (int i = 0; i < points.Length; i++)
                {
                    if (points[i] == null) continue;
                    if (any) builder.Append(", ");
                    builder.Append(points[i].Type).Append('=').Append(points[i].TotalMunitionsRemaining)
                           .Append('/').Append(points[i].TotalMunitionsCapacity);
                    any = true;
                }
                if (!any) builder.Append("(none instantiated)");
                return builder.ToString();
            }

            private static string DescribeAttackEntries(CASHardpointManager manager)
            {
                List<CASAttackMeta> attacks = AttacksRef == null || manager == null ? null : AttacksRef(manager);
                return "attack entries " + (attacks == null ? "(none)" : DescribeAttacks(attacks.ToArray()));
            }
        }
    }
}
