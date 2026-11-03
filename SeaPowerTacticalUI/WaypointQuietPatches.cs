using System;
using VoiceBotBridge.Ui;

namespace VoiceBotBridge
{
    /// <summary>
    /// T-331: bodies of the "We're on position" guard (targets: WaypointQuietSpecs, installed by HarmonyFeature). OnFinished of a waypoint task opens a scope
    /// (the unit + whether to stay quiet); isLastWaypoint() of that very unit inside the scope answers false when quiet, which drops both the text and the voice.
    /// Outside the scope isLastWaypoint costs one field read. Bodies never throw into the game.
    /// </summary>
    internal static class WaypointQuietPatches
    {
        internal static volatile bool On;                      // [Messages] QuietModWaypoints
        internal static readonly WaypointQuietBook Book = new WaypointQuietBook(300);
        internal static Func<int, string> OwnerOf;             // AirOwnership.Owner (group of CAP/ASW/SAR), set by AirTickFeature

        [ThreadStatic] private static object _finishing;       // the unit of the task being finished
        [ThreadStatic] private static bool _quiet;
        [ThreadStatic] private static int _uid;
        [ThreadStatic] private static string _owner;
        private static bool _loggedFail;

        private static void Fail(string where, Exception e)
        {
            if (_loggedFail) return;
            _loggedFail = true;
            try { if (Patches.Api != null) Patches.Api.Log("waypoint quiet " + where + " failed: " + e.GetType().Name + ": " + e.Message); } catch { }
        }

        private static void Clear() { _finishing = null; _quiet = false; _owner = null; _uid = -1; }

        // Prefix of both OnFinished: remember the unit and decide.
        internal static void FinishPrefix(object __instance)
        {
            Clear();
            try
            {
                var api = Patches.Api;
                var of = OwnerOf;
                if (api == null || of == null) return;
                var unit = api.WpqUnit(__instance);
                if (unit == null) return;
                int uid = api.WpqUid(unit);
                if (uid < 0) return;
                string owner = of(uid);
                if (owner == null) return;   // a unit of the player: the game as it is
                _finishing = unit; _uid = uid; _owner = owner;
                _quiet = Book.Quiet(uid, owner, api.Frame.MissionTimeS ?? 0, On);
            }
            catch (Exception e) { Clear(); Fail("finish", e); }
        }

        internal static void FinishPostfix() { }

        // Finalizer: the scope is closed also when the original throws; the exception is returned unchanged.
        internal static Exception FinishFinalizer(Exception __exception) { Clear(); return __exception; }

        // Prefix of ObjectBase.isLastWaypoint(): inside the scope and quiet -> false (no message, no voice).
        internal static bool IsLastPrefix(object __instance, ref bool __result)
        {
            var f = _finishing;
            if (f == null || !_quiet || !ReferenceEquals(f, __instance)) return true;
            try
            {
                __result = false;
                Book.Suppressed(_uid);
                var api = Patches.Api;
                if (api != null) api.Log(WaypointQuietMath.LogLine(_uid, _owner, api.WpqPlacer(_uid), false, 0));
            }
            catch (Exception e) { Fail("is_last", e); return true; }
            return false;
        }

        // Postfix: a message of a unit of the mod that was let through.
        internal static void IsLastPostfix(object __instance, bool __result)
        {
            var f = _finishing;
            if (f == null || !__result || _quiet || !ReferenceEquals(f, __instance)) return;
            try
            {
                var api = Patches.Api;
                if (api == null) return;
                int q = Book.Shown(_uid, api.Frame.MissionTimeS ?? 0);
                api.Log(WaypointQuietMath.LogLine(_uid, _owner, api.WpqPlacer(_uid), true, q));
            }
            catch (Exception e) { Fail("is_last", e); }
        }
    }
}
