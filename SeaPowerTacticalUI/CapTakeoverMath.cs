// T-317 / ADR-0124: "player or the game's AI" for a CAP/SAR aircraft whose route, ROE or throttle differs from what the mod set.
// The air combat of the game AI (MoveToEngage, EngageAirContact, Aligning ...) and the game's own ROE / route changes make such a deviation
// without the player; a deviation inside the AI combat window of an aircraft the player did not select is the game (ai_drift). No Unity, selftest.
using System;
using System.Collections.Generic;
using System.Globalization;

namespace VoiceBotBridge
{
    internal enum TakeVerdict { None = 0, Player = 1, AiDrift = 2 }

    internal sealed class TakeProbe
    {
        public string State;          // StateMachine.CurrentStateName ("Null"/null = unknown)
        public bool Aligning;         // _alignmentTasks.Count > 0
        public int EngageTasks;       // _currentEngageTasks.Count
        public string RoeNow, RoeSet; // tight|hold|free; RoeSet null = the mod set no ROE
        public int Wps = -1;          // waypoints now; -1 = the mod placed no route
        public double DevNm = -1;     // last waypoint vs PlacedByMod; -1 = n/a
        public int ThrNow = -1, ThrSet = -1;   // DashTick only; -1 = n/a
    }

    internal static class CapTakeoverMath
    {
        public const double RouteTolNm = 0.5, AiGraceS = 30, SelLeadS = 5;

        private static readonly string[] CombatStates =
        {
            "MoveToEngage", "EngageAirContact", "EngageSurfaceContact", "EngageSubSurfaceContact", "Aligning",
            "IdentifyAirContact", "IdentifySurfaceContact", "IdentifySubSurfaceContact", "MissileEvasion", "EvadeMissile",
            "AdvancedMissileEvasion", "Intercept", "AvoidingCollision",
            "Pursuit", "Chandelle", "Immelman", "SplitS", "HighYoYo", "Scissors", "BreakTurn", "Cranking",
        };

        /// <summary>The state names of the game's air combat (ordinal compare).</summary>
        public static bool AiCombatState(string state)
        {
            if (string.IsNullOrEmpty(state)) return false;
            foreach (string s in CombatStates) if (string.Equals(s, state, StringComparison.Ordinal)) return true;
            return false;
        }

        public static bool AiActive(TakeProbe p) { return p != null && (AiCombatState(p.State) || p.Aligning || p.EngageTasks > 0); }

        /// <summary>"roe" | "route" | "throttle" | null (no deviation). Order: roe, route, throttle.</summary>
        public static string Deviation(TakeProbe p)
        {
            if (p == null) return null;
            if (p.RoeSet != null && p.RoeNow != null && !string.Equals(p.RoeSet, p.RoeNow, StringComparison.OrdinalIgnoreCase)) return "roe";
            if (p.Wps > 0 && p.DevNm > RouteTolNm) return "route";
            if (p.ThrNow >= 0 && p.ThrSet >= 0 && p.ThrNow != p.ThrSet) return "throttle";
            return null;
        }

        public static TakeVerdict Decide(string deviation, bool aiWindow, bool selected)
        {
            if (deviation == null) return TakeVerdict.None;
            return aiWindow && !selected ? TakeVerdict.AiDrift : TakeVerdict.Player;
        }

        public static string Detail(TakeProbe p)
        {
            var ci = CultureInfo.InvariantCulture;
            return "st=" + (string.IsNullOrEmpty(p.State) ? "-" : p.State) + " roe=" + (p.RoeNow ?? "-") + "/" + (p.RoeSet ?? "-")
                + " wps=" + p.Wps + " dev_nm=" + (p.DevNm < 0 ? "-" : p.DevNm.ToString("0.0", ci))
                + " thr=" + (p.ThrNow < 0 ? "-" : p.ThrNow + "/" + p.ThrSet);
        }

        /// <summary>T-323: the point "Return control" must compare with. The CAP keeps the LAST waypoint as the mod's base (Patrol, ai_drift);
        /// ControlMemory.DecideRoute compares the FIRST current waypoint. Returns the current first waypoint when the current last one is
        /// still within RouteTolNm of placedLast (the route is still ours), else null (keep the base: no waypoints -> restore, other route -> player_changed).</summary>
        public static double[] RestoreBase(IList<double[]> wpsNow, double[] placedLast, Func<double, double, double, double, double> distNm)
        {
            if (wpsNow == null || wpsNow.Count == 0 || placedLast == null) return null;
            var last = wpsNow[wpsNow.Count - 1];
            if (distNm(last[0], last[1], placedLast[0], placedLast[1]) > RouteTolNm) return null;
            return new[] { wpsNow[0][0], wpsNow[0][1] };
        }

        internal static string SelfTestRestoreBase()
        {
            Func<double, double, double, double, double> d = (a, b, c, e) => Math.Abs(a - c) * 60 + Math.Abs(b - e) * 60;
            var w = new List<double[]> { new[] { 1.0, 0 }, new[] { 2.0, 0 }, new[] { 3.0, 0 } };
            if (RestoreBase(null, new[] { 3.0, 0 }, d) != null) return "restore base: null wps";
            if (RestoreBase(new List<double[]>(), new[] { 3.0, 0 }, d) != null) return "restore base: empty wps";
            if (RestoreBase(w, null, d) != null) return "restore base: null base";
            var p = RestoreBase(w, new[] { 3.0, 0 }, d);
            if (p == null || p[0] != 1.0 || p[1] != 0) return "restore base: first point";
            if (RestoreBase(w, new[] { 3.0 + 0.4 / 60, 0 }, d) == null) return "restore base: 0.4 nm";
            if (RestoreBase(w, new[] { 3.0 + 0.6 / 60, 0 }, d) != null) return "restore base: 0.6 nm";
            p = RestoreBase(new List<double[]> { new[] { 3.0, 0 } }, new[] { 3.0, 0 }, d);
            if (p == null || p[0] != 3.0) return "restore base: single point";
            p = RestoreBase(w, new[] { 3.0, 0 }, d); p[0] = 99;
            if (w[0][0] != 1.0) return "restore base: not a copy";
            return null;
        }

        internal static string SelfTest()
        {
            foreach (string s in new[] { "MoveToEngage", "EngageAirContact", "EngageSurfaceContact", "EngageSubSurfaceContact", "Aligning", "IdentifyAirContact",
                "MissileEvasion", "EvadeMissile", "AdvancedMissileEvasion", "Intercept", "AvoidingCollision", "Pursuit" })
                if (!AiCombatState(s)) return "AiCombatState " + s;
            foreach (string s in new[] { "Default", "CAP", "ReturnToBase", "Null", "Winchester", "BingoFuel", "", null })
                if (AiCombatState(s)) return "AiCombatState not " + (s ?? "null");
            if (AiCombatState("moveToEngage")) return "AiCombatState is ordinal";

            if (AiActive(new TakeProbe { State = "CAP" })) return "AiActive calm";
            if (!AiActive(new TakeProbe { State = "CAP", Aligning = true })) return "AiActive aligning";
            if (!AiActive(new TakeProbe { State = "CAP", EngageTasks = 1 })) return "AiActive tasks";
            if (!AiActive(new TakeProbe { State = "MoveToEngage" })) return "AiActive state";

            if (Deviation(new TakeProbe { RoeNow = "free", RoeSet = "free" }) != null) return "dev roe same";
            if (Deviation(new TakeProbe { RoeNow = "tight", RoeSet = "free" }) != "roe") return "dev roe tight";
            if (Deviation(new TakeProbe { RoeNow = "FREE", RoeSet = "free" }) != null) return "dev roe case";
            if (Deviation(new TakeProbe { RoeNow = "free", RoeSet = null }) != null) return "dev roe not set";
            if (Deviation(new TakeProbe { Wps = 3, DevNm = 0.4 }) != null) return "dev route 0.4";
            if (Deviation(new TakeProbe { Wps = 3, DevNm = 0.6 }) != "route") return "dev route 0.6";
            if (Deviation(new TakeProbe { Wps = 0, DevNm = 5 }) != null) return "dev route wps 0";
            if (Deviation(new TakeProbe { Wps = -1, DevNm = 5 }) != null) return "dev route not placed";
            if (Deviation(new TakeProbe { ThrNow = 3, ThrSet = 2 }) != "throttle") return "dev throttle";
            if (Deviation(new TakeProbe { ThrNow = 2, ThrSet = 2 }) != null) return "dev throttle same";
            if (Deviation(new TakeProbe { ThrNow = 3, ThrSet = -1 }) != null) return "dev throttle n/a";
            if (Deviation(new TakeProbe { RoeNow = "tight", RoeSet = "free", Wps = 2, DevNm = 3 }) != "roe") return "dev order roe first";

            if (Decide("route", true, false) != TakeVerdict.AiDrift) return "decide drift";
            if (Decide("route", true, true) != TakeVerdict.Player) return "decide selected";
            if (Decide("route", false, false) != TakeVerdict.Player) return "decide no window";
            if (Decide(null, true, false) != TakeVerdict.None || Decide(null, false, true) != TakeVerdict.None) return "decide none";

            string d = Detail(new TakeProbe { State = "EngageAirContact", RoeNow = "free", RoeSet = "tight", Wps = 3, DevNm = 1.8 });
            if (d != "st=EngageAirContact roe=free/tight wps=3 dev_nm=1.8 thr=-") return "detail " + d;

            // tracker
            var t = new CapTakeoverTracker();
            string why;
            var calm = new TakeProbe { State = "CAP", Wps = 3, DevNm = 2 };
            var fight = new TakeProbe { State = "EngageAirContact", Wps = 3, DevNm = 2 };
            if (t.Check(1, calm, 100, out why) != TakeVerdict.Player || why != "route") return "tracker calm player";
            if (t.InWindow(1, 100)) return "tracker no window";
            if (t.Check(1, fight, 110, out why) != TakeVerdict.AiDrift) return "tracker fight drift";
            if (!t.InWindow(1, 110)) return "tracker window open";
            if (t.Check(1, calm, 130, out why) != TakeVerdict.AiDrift) return "tracker grace 20 s";
            if (t.Check(1, calm, 140, out why) != TakeVerdict.AiDrift) return "tracker grace 30 s";
            if (t.InWindow(1, 141)) return "tracker window closed after 30 s";
            if (t.Check(1, calm, 141, out why) != TakeVerdict.Player) return "tracker after grace";
            if (t.Check(1, new TakeProbe { State = "CAP" }, 142, out why) != TakeVerdict.None || why != null) return "tracker none";

            // selection: 3 s before the window opens counts, 8 s does not, a selection inside the window counts
            t = new CapTakeoverTracker();
            t.NoteSelected(2, 197);
            if (t.Check(2, fight, 200, out why) != TakeVerdict.Player) return "tracker selected 3 s before";
            if (!t.SelectedInWindow(2, 200)) return "SelectedInWindow lead";
            t = new CapTakeoverTracker();
            t.NoteSelected(2, 192);
            if (t.Check(2, fight, 200, out why) != TakeVerdict.AiDrift) return "tracker selected 8 s before";
            if (t.SelectedInWindow(2, 200)) return "SelectedInWindow too old";
            t.NoteSelected(2, 205);
            if (t.Check(2, fight, 206, out why) != TakeVerdict.Player) return "tracker selected in window";
            t.Inherit(3, 2);
            if (t.Check(3, fight, 206, out why) != TakeVerdict.Player) return "tracker inherited selection";

            // drift line once per window; a new window re-arms it
            t = new CapTakeoverTracker();
            t.Check(4, fight, 10, out why);
            if (t.DriftLogged(4)) return "drift not logged yet";
            t.NoteDriftLogged(4);
            if (!t.DriftLogged(4)) return "drift logged";
            t.Check(4, fight, 20, out why);
            if (!t.DriftLogged(4)) return "drift stays in the window";
            t.Check(4, fight, 100, out why);
            if (t.DriftLogged(4)) return "drift re-armed by a new window";

            t.Forget(4);
            if (t.InWindow(4, 100)) return "Forget";
            t.Check(5, fight, 1, out why); t.NoteSelected(5, 1);
            t.Clear();
            if (t.InWindow(5, 1) || t.SelectedInWindow(5, 1)) return "Clear";
            return null;
        }
    }

    /// <summary>Per-uid window and selection memory; mission time in seconds. Shared by CapGame, SarGame and the captest fake.</summary>
    internal sealed class CapTakeoverTracker
    {
        private sealed class E { public double AiStart = -1e9, AiLast = -1e9, SelLast = -1e9; public bool DriftLogged; }
        private readonly Dictionary<int, E> _e = new Dictionary<int, E>();

        private E Get(int uid) { E e; if (!_e.TryGetValue(uid, out e)) { e = new E(); _e[uid] = e; } return e; }

        /// <summary>Every frame for the selected unit.</summary>
        public void NoteSelected(int uid, double now) { if (uid > 0) Get(uid).SelLast = now; }

        /// <summary>The unit counts as selected when <paramref name="fromUid"/> (the leader of its formation) was: orders to a formation go through the leader.</summary>
        public void Inherit(int uid, int fromUid)
        {
            E f;
            if (uid > 0 && fromUid > 0 && fromUid != uid && _e.TryGetValue(fromUid, out f)) { E e = Get(uid); if (f.SelLast > e.SelLast) e.SelLast = f.SelLast; }
        }

        /// <summary>Updates the window (AiActive -> aiLast = now, aiStart if closed) and returns the verdict; why = deviation or null.</summary>
        public TakeVerdict Check(int uid, TakeProbe p, double now, out string why)
        {
            Touch(uid, p, now);
            why = CapTakeoverMath.Deviation(p);
            return CapTakeoverMath.Decide(why, InWindow(uid, now), SelectedInWindow(uid, now));
        }

        /// <summary>Updates the window from the current state only (no verdict): a member that is not checked for a takeover (leaving) keeps its window.</summary>
        public void Touch(int uid, TakeProbe p, double now)
        {
            if (!CapTakeoverMath.AiActive(p)) return;
            E e = Get(uid);
            if (now - e.AiLast > CapTakeoverMath.AiGraceS) { e.AiStart = now; e.DriftLogged = false; }
            e.AiLast = now;
        }

        public bool InWindow(int uid, double now)
        {
            E e; return _e.TryGetValue(uid, out e) && e.AiLast > -1e8 && now - e.AiLast <= CapTakeoverMath.AiGraceS;
        }

        public bool SelectedInWindow(int uid, double now)
        {
            E e; return _e.TryGetValue(uid, out e) && e.AiStart > -1e8 && e.SelLast >= e.AiStart - CapTakeoverMath.SelLeadS;
        }

        /// <summary>Seconds since the AI window opened (log only); -1 = none.</summary>
        public double SinceAiS(int uid, double now) { E e; return _e.TryGetValue(uid, out e) && e.AiStart > -1e8 ? now - e.AiStart : -1; }

        public bool DriftLogged(int uid) { E e; return _e.TryGetValue(uid, out e) && e.DriftLogged; }
        public void NoteDriftLogged(int uid) { Get(uid).DriftLogged = true; }
        public void Forget(int uid) { _e.Remove(uid); }
        public void Clear() { _e.Clear(); }
    }
}
