using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Reflection.Emit;
using GHPC;
using GHPC.Vehicle;
using GHPC.Weapons;
using HarmonyLib;
using UnityEngine;

namespace CustomFireSupport
{
    /// <summary>Generate native firing tables incrementally on the main thread and reuse them per mission.</summary>
    internal static class CasBallisticsCache
    {
        private sealed class Entry
        {
            internal AmmoType Ammo;
            internal float Speed;
            internal float Elevation;
            internal BallisticComputer Computer;
            internal float NextAngle;
            internal bool Ready;
        }

        private static readonly List<Entry> Entries = new List<Entry>();
        private static readonly Queue<Entry> Pending = new Queue<Entry>();
        private static readonly BallisticComputer GuidedPlaceholder = new BallisticComputer();
        [ThreadStatic] private static bool DeferSimulation;
        private static readonly Func<BallisticComputer, float, BallisticComputer.BallisticResults> AtElevation =
            CachedDelegate.Create<Func<BallisticComputer, float, BallisticComputer.BallisticResults>>(
                AccessTools.Method(typeof(BallisticComputer), "ResultsAtElevation"));
        private static readonly MethodInfo AtElevationMethod = AccessTools.Method(typeof(BallisticComputer), "ResultsAtElevation");
        private static readonly AccessTools.FieldRef<CASController, CASAttackType> Attack =
            AccessTools.FieldRefAccess<CASController, CASAttackType>("_finalAttackType");

        internal static void ResetForScene() { Entries.Clear(); Pending.Clear(); }

        private static bool NeedsTable(AmmoType ammo)
        {
            return ammo != null && !CasPayloadFactory.IsOurMissile(ammo) &&
                ammo.Guidance == AmmoType.GuidanceType.Unguided;
        }

        private static float RoundedSpeed(float speed) { return (int)((speed + 5f) / 10f) * 10f; }

        private static Entry Request(AmmoType ammo, float speed, float elevation)
        {
            for (int i = 0; i < Entries.Count; i++)
            {
                Entry cached = Entries[i];
                if (ReferenceEquals(cached.Ammo, ammo) && cached.Speed == speed && cached.Elevation == elevation)
                    return cached;
            }
            BallisticComputer bc = new BallisticComputer();
            // SetAmmoType still initializes the native gravity/drag/velocity fields. Only its full
            // synchronous elevation sweep is deferred; every sample uses the unmodified native solver.
            DeferSimulation = true;
            try { bc.SetAmmoType(ammo, speed, elevation); }
            finally { DeferSimulation = false; }
            bc.AllSimPoints.Add(default(BallisticComputer.BallisticResults));
            Entry entry = new Entry { Ammo = ammo, Speed = speed, Elevation = elevation,
                Computer = bc, NextAngle = Mathf.Min(bc.ElevationStep, bc.FineElevationStep) };
            Entries.Add(entry);
            Pending.Enqueue(entry);
            return entry;
        }

        internal static void Pump()
        {
            if (Pending.Count == 0) return;
            Stopwatch timer = Stopwatch.StartNew();
            while (Pending.Count > 0 && timer.Elapsed.TotalMilliseconds < 2.0)
            {
                Entry entry = Pending.Peek();
                BallisticComputer bc = entry.Computer;
                if (entry.NextAngle > bc.MaxElevation)
                {
                    entry.Ready = true;
                    Pending.Dequeue();
                    continue;
                }
                bc.AllSimPoints.Add(AtElevation != null ? AtElevation(bc, entry.NextAngle) :
                    (BallisticComputer.BallisticResults)AtElevationMethod.Invoke(bc, new object[] { entry.NextAngle }));
                entry.NextAngle += entry.NextAngle >= bc.MinFineElevation && entry.NextAngle <= bc.MaxFineElevation
                    ? bc.FineElevationStep : bc.ElevationStep;
            }
        }

        internal static bool ReadyForFinal(CASController plane)
        {
            CASHardpointManager manager = plane.GetComponentInChildren<CASHardpointManager>(true);
            if (manager == null) return true; // Native code reports a missing manager.
            AmmoType ammo = manager.GetAmmoType(Attack(plane));
            if (!NeedsTable(ammo)) return true;
            bool inherit = manager.GetInheritVelocity(Attack(plane));
            float speed = inherit ? RoundedSpeed(ammo.MuzzleVelocity + plane.TrueAirSpeed) : -1f;
            return Request(ammo, speed, inherit ? 45f : -1f).Ready;
        }

        private static BallisticComputer CustomComputer(BallisticComputerRepository repo, AmmoType ammo,
            float speed, float density, float elevation, CASController plane)
        {
            if (!CasFireChainRepair.IsOurSortie(plane)) return repo.GetCustomComputer(ammo, speed, density, elevation);
            if (!NeedsTable(ammo)) return GuidedPlaceholder; // Guided aim never reads a firing table.
            Entry entry = Request(ammo, speed, elevation);
            return entry.Computer; // OnFinal is gated until every native sample is ready.
        }

        private static BallisticComputer StandardComputer(BallisticComputerRepository repo, AmmoType ammo, CASController plane)
        {
            if (!CasFireChainRepair.IsOurSortie(plane)) return repo.GetOrRegister(ammo);
            if (!NeedsTable(ammo)) return GuidedPlaceholder;
            Entry entry = Request(ammo, -1f, -1f);
            return entry.Computer;
        }

        [HarmonyPatch(typeof(CASHardpointManager), "DoBallisticsCache")]
        private static class WarmupPatch
        {
            private static bool Prefix(CASHardpointManager __instance, float airspeed, float elevationOverride)
            {
                if (!CasFireChainRepair.IsOurSortie(__instance.GetComponentInParent<CASController>())) return true;
                foreach (CASHardpoint point in __instance.GetComponentsInChildren<CASHardpoint>(true))
                {
                    AmmoType ammo = point.Ammo;
                    if (point.Type == CASAttackType.AirToGroundMissile && CasPayloadFactory.IsRuntimeHardpoint(point))
                        CasPayloadFactory.RegisterRuntimeMissile(ammo);
                    if (!NeedsTable(ammo)) continue;
                    Request(ammo, point.InheritVelocity ? RoundedSpeed(airspeed + ammo.MuzzleVelocity) : -1f,
                        point.InheritVelocity ? elevationOverride : -1f);
                }
                return false;
            }
        }

        [HarmonyPatch(typeof(BallisticComputer), "runFullSim")]
        private static class DeferSweepPatch
        {
            private static bool Prefix() { return !DeferSimulation; }
        }

        [HarmonyPatch(typeof(CASController), "EnterState")]
        private static class FinalComputerPatch
        {
            private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
            {
                MethodInfo custom = AccessTools.Method(typeof(BallisticComputerRepository), "GetCustomComputer");
                MethodInfo standard = AccessTools.Method(typeof(BallisticComputerRepository), "GetOrRegister");
                foreach (CodeInstruction instruction in instructions)
                {
                    MethodInfo helper = instruction.Calls(custom)
                        ? AccessTools.Method(typeof(CasBallisticsCache), "CustomComputer")
                        : instruction.Calls(standard) ? AccessTools.Method(typeof(CasBallisticsCache), "StandardComputer") : null;
                    if (helper != null)
                    {
                        CodeInstruction controller = new CodeInstruction(OpCodes.Ldarg_0);
                        controller.labels.AddRange(instruction.labels);
                        controller.blocks.AddRange(instruction.blocks);
                        instruction.labels.Clear();
                        instruction.blocks.Clear();
                        yield return controller;
                        instruction.opcode = OpCodes.Call;
                        instruction.operand = helper;
                    }
                    yield return instruction;
                }
            }
        }
    }
}
