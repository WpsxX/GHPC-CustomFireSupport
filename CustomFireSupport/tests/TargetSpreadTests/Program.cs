using System;
using System.Collections.Generic;
using System.Reflection;

namespace CustomFireSupport
{
    // Minimal scene model for running the production target patch outside Unity.
    internal enum Faction { Neutral, Blue, Red }
    internal enum CASAttackType { Inert, Rockets }
    internal struct Vector3
    {
        internal float x, y, z;
        internal float sqrMagnitude { get { return x * x + y * y + z * z; } }
        public static Vector3 operator -(Vector3 a, Vector3 b)
        { return new Vector3 { x = a.x - b.x, y = a.y - b.y, z = a.z - b.z }; }
    }
    internal sealed class Transform { internal Vector3 position; }
    internal sealed class GameObject { internal string name = "plane"; }
    internal sealed class Unit
    {
        internal string FriendlyName;
        internal bool Neutralized;
        internal bool Attackable = true;
        internal Transform Center;
    }
    internal sealed class CASController
    {
        internal enum FlightState { TurnTowardTarget }
        internal Unit FinalTarget;
        internal Faction unitFaction = Faction.Blue;
        internal GameObject gameObject = new GameObject();
        internal List<Unit> Spotted = new List<Unit>();
        internal Vector3 Interest;
        internal bool LastKnown;
        internal int AttackChecks, Turns;
        private void EnterState(FlightState state) { Turns++; }
        private CASAttackType GetIdealAttackType(Unit unit)
        { AttackChecks++; return unit.Attackable ? CASAttackType.Rockets : CASAttackType.Inert; }
    }
    internal static class SceneUnitsManager { internal static List<Unit>[] AllLiveUnitsByFaction; }
    internal static class Log
    {
        internal static void Info(string message) { }
        internal static void Error(string message) { throw new Exception(message); }
    }
    internal sealed class HarmonyPatch : Attribute { public HarmonyPatch(Type t, string name) { } }
    internal static class AccessTools
    {
        internal delegate ref F FieldRef<T, F>(T obj);
        private static ref List<Unit> Spotted(CASController p) { return ref p.Spotted; }
        private static ref Vector3 Interest(CASController p) { return ref p.Interest; }
        private static ref bool LastKnown(CASController p) { return ref p.LastKnown; }
        internal static FieldRef<T, F> FieldRefAccess<T, F>(string name)
        {
            object value = name == "_spottedTargetsCurrent" ? (object)(FieldRef<CASController, List<Unit>>)Spotted :
                name == "_interestPoint" ? (object)(FieldRef<CASController, Vector3>)Interest :
                (FieldRef<CASController, bool>)LastKnown;
            return (FieldRef<T, F>)value;
        }
        internal static MethodInfo Method(Type type, string name)
        { return type.GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic); }
    }
    internal static class Program
    {
        private static int passed;
        private static readonly MethodInfo Postfix = typeof(CasTargetSpreadPatch).GetMethod("Postfix", BindingFlags.NonPublic | BindingFlags.Static);
        private static Unit Target(string name, float x, bool attackable = true)
        { return new Unit { FriendlyName = name, Attackable = attackable, Center = new Transform { position = new Vector3 { x = x } } }; }
        private static void Check(bool ok, string message)
        { if (!ok) throw new Exception(message); passed++; Console.WriteLine("PASS " + message); }
        private static void Search(CASController plane) { Postfix.Invoke(null, new object[] { plane }); }
        private static void Reset(params Unit[] red)
        {
            CasTargetSpreadPatch.ResetForScene();
            SceneUnitsManager.AllLiveUnitsByFaction = new[] { new List<Unit>(), new List<Unit>(), new List<Unit>(red) };
        }
        private static CASController Contender(Unit chosen)
        {
            Search(new CASController { FinalTarget = chosen });
            return new CASController { FinalTarget = chosen };
        }
        private static int Main()
        {
            Unit chosen = Target("chosen", 0), near = Target("near", 100), far = Target("far", 200), outside = Target("outside", 3001);
            Reset(chosen, outside, near, far);
            CASController plane = Contender(chosen);
            Search(plane);
            Check(plane.FinalTarget == near, "nearest unclaimed enemy selected");
            Check(plane.AttackChecks == 1, "outside radius and worse candidates skip weapon checks");
            Check(plane.Turns == 1, "replacement recomputes attack state");
            Reset(chosen, near, far);
            plane = Contender(chosen);
            plane.Spotted.Add(far);
            Search(plane);
            Check(plane.FinalTarget == far && plane.AttackChecks == 0, "spotted targets retain priority");
            Unit inert = Target("inert", 50, false);
            Reset(chosen, inert, far, near);
            plane = Contender(chosen);
            Search(plane);
            Check(plane.FinalTarget == near && plane.AttackChecks == 3, "unattackable nearest does not shrink search bound");
            Unit edge = Target("edge", 3000);
            Reset(chosen, edge, outside);
            plane = Contender(chosen);
            Search(plane);
            Check(plane.FinalTarget == chosen && plane.AttackChecks == 0, "radius boundary stays exclusive and fallback keeps chosen target");
            Unit tie = Target("tie", 100);
            Reset(chosen, near, tie);
            plane = Contender(chosen);
            Search(plane);
            Check(plane.FinalTarget == near, "equal-distance tie retains first candidate");
            near.Neutralized = true;
            Unit noCenter = new Unit { FriendlyName = "missing center" };
            Reset(chosen, near, noCenter, far);
            SceneUnitsManager.AllLiveUnitsByFaction[0].Add(Target("neutral", 1));
            SceneUnitsManager.AllLiveUnitsByFaction[1].Add(Target("friendly", 1));
            plane = Contender(chosen);
            Search(plane);
            Check(plane.FinalTarget == far, "destroyed, invalid, neutral and friendly candidates excluded");
            near.Neutralized = false;
            Reset(chosen, near);
            CASController owner = new CASController { FinalTarget = near };
            Search(owner);
            owner.FinalTarget = null;
            Search(owner);
            var claims = (Dictionary<Unit, CASController>)typeof(CasTargetSpreadPatch)
                .GetField("Claims", BindingFlags.Static | BindingFlags.NonPublic).GetValue(null);
            Check(claims.Count == 0, "plane losing its target releases old claim");
            Search(new CASController { FinalTarget = chosen });
            CasTargetSpreadPatch.ResetForScene();
            Check(claims.Count == 0, "scene reset releases all target references");
            MethodInfo method = AccessTools.Method(typeof(CASController), "GetIdealAttackType");
            var call = CachedDelegate.Create<Func<CASController, Unit, CASAttackType>>(method);
            Check(call != null && call(new CASController(), near) == CASAttackType.Rockets, "private open-instance delegate binds and runs");
            Check(CachedDelegate.Create<Action>(method) == null && CachedDelegate.Create<Action>(null) == null,
                "incompatible or missing delegate retains reflection fallback");
            Console.WriteLine("passed: " + passed + ", failed: 0");
            return 0;
        }
    }
}
