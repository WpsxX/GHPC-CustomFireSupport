using System;
using System.Runtime.CompilerServices;
using GHPC.PhysicsHelpers;
using GHPC.Weaponry;
using GHPC.Weapons;
using HarmonyLib;
using UnityEngine;

namespace CustomFireSupport
{
    /// <summary>Positive, monotone interpolation of the document's static-launch measurements.</summary>
    internal static class CasGunCurve
    {
        internal static readonly float[] Ranges = { 0f, 100f, 300f, 500f, 1000f, 1500f, 2000f };
        private static readonly float[] Speeds = { 1010f, 1010f, 940f, 890f, 780f, 680f, 590f };
        private static readonly float[] Penetrations = { 98f, 98f, 85f, 75f, 55f, 38f, 25f };

        private static int Segment(float range)
        {
            for (int i = 0; i < Ranges.Length - 2; i++)
                if (range < Ranges[i + 1]) return i;
            return Ranges.Length - 2;
        }

        private static float Sample(float range, float[] values)
        {
            range = Math.Max(0f, range);
            int i = Segment(range);
            double t = (range - Ranges[i]) / (Ranges[i + 1] - Ranges[i]);
            return (float)(values[i] * Math.Exp(Math.Log(values[i + 1] / values[i]) * t));
        }

        internal static float Speed(float range) { return Sample(range, Speeds); }
        internal static float Penetration(float range) { return Sample(range, Penetrations); }
        internal static float UofPenetration(float range)
        { return 7f * (float)Math.Exp(-Math.Max(0f, range - 100f) / 150f); }

        // dv/dx = -k*v, dx/dt = v. Split at table knots so large frames cannot skip a drag segment.
        internal static void Step(float range, float speed, float dt, out float distance, out float finalSpeed)
        {
            double x = Math.Max(0f, range), v = Math.Max(0f, speed), remaining = Math.Max(0f, dt);
            double start = x;
            while (remaining > 0.0000001 && v > 0.000001)
            {
                int i = Segment((float)x);
                double k = -Math.Log(Speeds[i + 1] / Speeds[i]) / (Ranges[i + 1] - Ranges[i]);
                double boundary = i == Ranges.Length - 2 ? double.PositiveInfinity : Ranges[i + 1] - x;
                double boundaryTime = k == 0.0 ? boundary / v : (Math.Exp(k * boundary) - 1.0) / (k * v);
                double t = Math.Min(remaining, boundaryTime);
                double dx = k == 0.0 ? v * t : Math.Log(1.0 + k * v * t) / k;
                x += dx;
                v = k == 0.0 ? v : v / (1.0 + k * v * t);
                remaining -= t;
                if (t == boundaryTime) x = Ranges[i + 1];
            }
            distance = (float)(x - start);
            finalSpeed = (float)v;
        }
    }

    /// <summary>Per-shot state; no shared donor ammunition or global game ballistics are changed.</summary>
    internal static class CasGunBallistics
    {
        private sealed class Shot
        {
            internal int Id;
            internal Vector3 Position;
            internal float Range;
            internal float Energy = 1f;
            internal bool Gau;
            internal bool Api;
            internal bool Uof;
            internal float FragmentRange;
        }

        private static readonly ConditionalWeakTable<LiveRound, Shot> Shots = new ConditionalWeakTable<LiveRound, Shot>();
        [ThreadStatic] private static LiveRound Updating;
        [ThreadStatic] private static AmmoType Blasting;

        private static float HeRange(AmmoType ammo)
        {
            if (!CasPayloadFactory.IsOurRound(ammo) || ammo.Category != AmmoType.AmmoCategory.Explosive) return 0f;
            return ammo.Name == "PGU-13/B HEI" ? 8f : 10f;
        }

        private static bool TryShot(LiveRound round, out Shot shot)
        {
            shot = null;
            return round != null && Shots.TryGetValue(round, out shot) && shot.Id == round.ID;
        }

        [HarmonyPatch(typeof(LiveRound), "Init")]
        private static class InitShot
        {
            private static void Postfix(LiveRound __instance, LiveRound parentRound)
            {
                Shots.Remove(__instance);
                AmmoType ammo = __instance.Info;
                bool gau = !__instance.IsSpall && CasPayloadFactory.IsOurRound(ammo) &&
                    (ammo.Name == "PGU-14/B API" || ammo.Name == "PGU-13/B HEI");
                bool uof = !__instance.IsSpall && CasPayloadFactory.IsOurRound(ammo) && ammo.Name == "3UOF8 HEI";
                float fragmentRange = __instance.IsSpall && parentRound != null ? HeRange(parentRound.Info) : 0f;
                if (!gau && !uof && fragmentRange <= 0f) return;
                Shots.Add(__instance, new Shot
                {
                    Id = __instance.ID, Position = __instance.transform.position, Gau = gau,
                    Api = ammo.Name == "PGU-14/B API", Uof = uof, FragmentRange = fragmentRange
                });
                // Init clears the override after constructing ShotInfo. Restore only our HE fragments.
                if (fragmentRange > 0f) __instance.RhaPenetrationOverride = UnityEngine.Random.Range(2f, 4f);
            }
        }

        [HarmonyPatch(typeof(LiveRound), "DoUpdateImpl")]
        private static class UpdateShot
        {
            private static bool Prefix(LiveRound __instance, out LiveRound __state)
            {
                __state = Updating;
                Updating = __instance;
                Shot shot;
                if (!TryShot(__instance, out shot) || __instance.Pooled) return true;
                if (shot.FragmentRange > 0f && shot.Range >= shot.FragmentRange - 0.001f)
                {
                    __instance.Detonate();
                    return true; // the original completes the report and returns the fragment to its pool
                }
                return true;
            }

            private static void Postfix(LiveRound __instance)
            {
                Shot shot;
                if (!TryShot(__instance, out shot)) return;
                Vector3 position = __instance.transform.position;
                shot.Range += Vector3.Distance(shot.Position, position);
                shot.Position = position;
            }

            private static Exception Finalizer(Exception __exception, LiveRound __state)
            {
                Updating = __state;
                return __exception;
            }
        }

        private static bool Evaluate(MotionState state, float deltaTime, AmmoType ammoType, ref MotionState result)
        {
            Shot shot;
            if (!TryShot(Updating, out shot) || !ReferenceEquals(Updating.Info, ammoType) || deltaTime <= 0f) return true;
            float speed = state.velocity.magnitude;
            if (shot.Gau)
            {
                float distance, finalSpeed;
                CasGunCurve.Step(shot.Range, speed, deltaTime, out distance, out finalSpeed);
                Vector3 direction = speed > 0f ? state.velocity / speed : Vector3.zero;
                result = new MotionState
                {
                    position = state.position + direction * distance + Physics.gravity * (0.5f * deltaTime * deltaTime),
                    velocity = direction * finalSpeed + Physics.gravity * deltaTime
                };
                return false;
            }
            if (shot.FragmentRange > 0f && speed > 0f)
            {
                float t = Mathf.Min(deltaTime, Mathf.Max(0f, shot.FragmentRange - shot.Range) / speed);
                result = new MotionState
                {
                    position = state.position + state.velocity * t,
                    velocity = state.velocity + Physics.gravity * t
                };
                return false;
            }
            return true;
        }

        [HarmonyPatch(typeof(BallisticEvaluatorRK4), "EvaluateTimestep")]
        private static class RkStep
        {
            private static bool Prefix(MotionState state, float deltaTime, AmmoType ammoType, ref MotionState __result)
            { return Evaluate(state, deltaTime, ammoType, ref __result); }
        }

        [HarmonyPatch(typeof(BallisticEvaluatorNonCorrected), "EvaluateTimestep")]
        private static class SimpleStep
        {
            private static bool Prefix(MotionState state, float deltaTime, AmmoType ammoType, ref MotionState __result)
            { return Evaluate(state, deltaTime, ammoType, ref __result); }
        }

        [HarmonyPatch(typeof(LiveRound), "get_CurrentPenRating")]
        private static class Penetration
        {
            private static bool Prefix(LiveRound __instance, ref float __result)
            {
                Shot shot;
                if (!TryShot(__instance, out shot) || (!shot.Gau && !shot.Uof)) return true;
                float range = shot.Range + Vector3.Distance(shot.Position, __instance.transform.position);
                __result = (shot.Uof ? CasGunCurve.UofPenetration(range) :
                    shot.Api ? CasGunCurve.Penetration(range) : 10f) * shot.Energy;
                return false;
            }
        }

        [HarmonyPatch(typeof(LiveRound), "penCheck")]
        private static class ImpactEnergy
        {
            private static void Prefix(LiveRound __instance, out float __state)
            { __state = __instance.CurrentSpeed; }
            private static void Postfix(LiveRound __instance, float __state)
            {
                Shot shot;
                if (__state <= 0f || !TryShot(__instance, out shot) || (!shot.Gau && !shot.Uof)) return;
                float ratio = Mathf.Clamp01(__instance.CurrentSpeed / __state);
                shot.Energy *= ratio * ratio; // retain vanilla energy loss after armor and ricochets
            }
        }

        // Vanilla couples frag range and strength, with a minimum range of 20 m. Scope overrides to
        // this round's synchronous blast call; other ammunition keeps all original behavior.
        [HarmonyPatch(typeof(LiveRound), "DoExplosiveBlast")]
        private static class BlastScope
        {
            private static void Prefix(LiveRound __instance, out AmmoType __state)
            { __state = Blasting; Blasting = HeRange(__instance.Info) > 0f ? __instance.Info : null; }
            private static Exception Finalizer(Exception __exception, AmmoType __state)
            { Blasting = __state; return __exception; }
        }

        [HarmonyPatch(typeof(BlastEffectManager), "GetMaxFragRange")]
        private static class FragRange
        {
            private static void Postfix(ref float __result)
            { if (Blasting != null) __result = HeRange(Blasting); }
        }

        [HarmonyPatch(typeof(BlastEffectManager), "GetMaxFragDamage")]
        private static class FragStrength
        {
            private static void Postfix(ref float __result)
            { if (Blasting != null) __result = 4f; }
        }

        [HarmonyPatch(typeof(BlastEffectManager), "GetFragDamage")]
        private static class FragFalloff
        {
            private static bool Prefix(float currentRadius, float maxFragRadius, float maxFragDamage, ref float __result)
            {
                if (Blasting == null) return true;
                // Dense 4 m core; fade to zero at the document's effective casualty radius.
                __result = maxFragDamage * Mathf.Clamp01((maxFragRadius - currentRadius) / (maxFragRadius - 4f));
                return false;
            }
        }

        [HarmonyPatch(typeof(BlastEffectManager), "GetFragChance")]
        private static class FragChance
        {
            private static bool Prefix(float currentRadius, float maxFragRadius, ref float __result)
            {
                if (Blasting == null) return true;
                float density = Mathf.Clamp01((maxFragRadius - currentRadius) / (maxFragRadius - 4f));
                __result = density * density;
                return false;
            }
        }
    }
}
