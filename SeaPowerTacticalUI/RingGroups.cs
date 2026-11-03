using System;
using System.Collections.Generic;
using System.Text;

namespace VoiceBotBridge
{
    // T-328 (ADR-0127): group membership from the command ring. Pure C#, no game types.

    /// One group of a window as the ring sees it. Why: null = the unit may join; else the admission code of the controller.
    internal sealed class RingGroupOption { public string Window, Name, Kind; public int Members; public string Why; }   // Window "cap"|"asw"|"strike" (the strike window, T-341); Kind "cap"|"asw"|"strike" (ASW strike order)|"asuw" (strike window)|"sar"

    /// Ring data of one aircraft. Current = its group (null = in none); Options = the other groups in window order, hidden-class groups removed.
    internal sealed class RingGroupsView { public RingGroupOption Current; public List<RingGroupOption> Options = new List<RingGroupOption>(); public int Admissible; }

    /// Adapter of one window (cap / asw). Mutations only through the existing group commands.
    internal interface IRingGroupWindow
    {
        string Window { get; }                              // "cap" | "asw" | "strike"
        bool Ready { get; }                                 // false while a save restore waits (restore_waiting)
        IList<RingGroupOption> Groups();                    // Window, Name, Kind, Members; Why not filled
        string GroupOf(int uid);                            // the group of the unit in this window, null = none
        string AdmitWhy(int uid, string group, string ignoreWindow, string ignoreGroup);   // dry run, no side effects; null = ok
        bool Add(int uid, string group);                    // runs {op:add}; true = ack ok (membership is checked by the caller)
        bool Remove(int uid, string group);                 // runs {op:remove}
    }

    internal sealed class RingMoveResult
    {
        public bool Ok, RolledBack, RollbackFailed;
        public string Error, FromWindow, FromGroup, ToWindow, ToGroup;   // Error: admission code | "unknown_group" | "remove_failed" | "add_failed" | "not_ready" | "same_group"

        public string Json()
        {
            var sb = new StringBuilder("{");
            sb.Append("\"ok\":").Append(Ok ? "true" : "false");
            sb.Append(",\"error\":").Append(Q(Error));
            sb.Append(",\"from\":").Append(Q(FromGroup == null ? null : FromWindow + ":" + FromGroup));
            sb.Append(",\"to\":").Append(Q(ToGroup == null ? null : ToWindow + ":" + ToGroup));
            sb.Append(",\"rolled_back\":").Append(RolledBack ? "true" : "false");
            sb.Append(",\"rollback_failed\":").Append(RollbackFailed ? "true" : "false");
            return sb.Append('}').ToString();
        }

        private static string Q(string s)
        {
            if (s == null) return "null";
            var sb = new StringBuilder("\"");
            foreach (char c in s)
            {
                if (c == '"' || c == '\\') sb.Append('\\').Append(c);
                else if (c < ' ') sb.Append(' ');
                else sb.Append(c);
            }
            return sb.Append('"').ToString();
        }
    }

    internal static class RingGroups
    {
        public const int PageSize = 6;

        /// Classes the ring does not show at all (the aircraft type does not belong to the window).
        public static bool HiddenWhy(string why) { return why == "hidden" || why == "aew"; }

        public static string Value(string window, string group) { return window + ":" + group; }

        /// Arc width for n visible cells: 32 deg per cell, 64..192 deg.
        public static double ArcSpan(int cells)
        {
            double s = 32.0 * cells;
            return s < 64.0 ? 64.0 : (s > 192.0 ? 192.0 : s);
        }

        public static bool ParseValue(string value, out string window, out string group)
        {
            window = null; group = null;
            if (string.IsNullOrEmpty(value)) return false;
            int i = value.IndexOf(':');
            if (i <= 0 || i >= value.Length - 1) return false;
            window = value.Substring(0, i);
            group = value.Substring(i + 1);
            return true;
        }

        private static int KindRank(string kind)
        {
            switch (kind) { case "cap": return 0; case "asw": return 1; case "strike": return 2; case "asuw": return 3; case "sar": return 4; default: return 5; }
        }

        private static IRingGroupWindow Find(IList<IRingGroupWindow> wins, string window)
        {
            if (wins == null) return null;
            foreach (var w in wins) if (w != null && w.Window == window) return w;
            return null;
        }

        private static RingGroupOption Pick(IList<RingGroupOption> list, string name)
        {
            if (list == null) return null;
            foreach (var g in list) if (g != null && g.Name == name) return g;
            return null;
        }

        /// Where the unit is now (first window that holds it). false = in none.
        private static bool Locate(int uid, IList<IRingGroupWindow> wins, out string window, out string group)
        {
            window = null; group = null;
            if (wins == null) return false;
            foreach (var w in wins)
            {
                if (w == null) continue;
                string g = w.GroupOf(uid);
                if (g != null) { window = w.Window; group = g; return true; }
            }
            return false;
        }

        /// null = no sectors (a window is not ready, or the unit has no group and no listed option).
        public static RingGroupsView View(int uid, IList<IRingGroupWindow> wins)
        {
            if (wins == null || wins.Count == 0) return null;
            foreach (var w in wins) if (w == null || !w.Ready) return null;
            string curWin, curGroup;
            bool inGroup = Locate(uid, wins, out curWin, out curGroup);
            var v = new RingGroupsView();
            // window order inside a kind; kinds in the fixed order cap -> asw -> strike -> asuw -> sar
            var all = new List<RingGroupOption>();
            foreach (var w in wins)
            {
                var gs = w.Groups();
                if (gs == null) continue;
                foreach (var g in gs)
                {
                    if (g == null) continue;
                    var o = new RingGroupOption { Window = w.Window, Name = g.Name, Kind = g.Kind, Members = g.Members };
                    if (inGroup && o.Window == curWin && o.Name == curGroup) { v.Current = o; continue; }
                    o.Why = w.AdmitWhy(uid, g.Name, curWin, curGroup);
                    if (HiddenWhy(o.Why)) continue;
                    all.Add(o);
                }
            }
            if (inGroup && v.Current == null) v.Current = new RingGroupOption { Window = curWin, Name = curGroup, Kind = curWin };
            // stable sort by kind rank (insertion order kept inside a kind)
            for (int rank = 0; rank <= 5; rank++)
                foreach (var o in all) if (KindRank(o.Kind) == rank) { v.Options.Add(o); if (o.Why == null) v.Admissible++; }
            if (v.Current == null && v.Options.Count == 0) return null;
            return v;
        }

        public static RingMoveResult Join(int uid, IList<IRingGroupWindow> wins, string toWindow, string toGroup)
        {
            var r = new RingMoveResult { ToWindow = toWindow, ToGroup = toGroup };
            var target = Find(wins, toWindow);
            if (target == null || Pick(target.Groups(), toGroup) == null) { r.Error = "unknown_group"; return r; }
            foreach (var w in wins) if (w == null || !w.Ready) { r.Error = "not_ready"; return r; }
            string curWin, curGroup;
            bool has = Locate(uid, wins, out curWin, out curGroup);
            if (has) { r.FromWindow = curWin; r.FromGroup = curGroup; }
            if (has && curWin == toWindow && curGroup == toGroup) { r.Error = "same_group"; return r; }
            string why = target.AdmitWhy(uid, toGroup, curWin, curGroup);
            if (why != null) { r.Error = why; return r; }
            IRingGroupWindow old = has ? Find(wins, curWin) : null;
            if (has)
            {
                if (old == null) { r.Error = "remove_failed"; return r; }
                old.Remove(uid, curGroup);
                if (old.GroupOf(uid) != null) { r.Error = "remove_failed"; return r; }
            }
            target.Add(uid, toGroup);
            if (target.GroupOf(uid) == toGroup) { r.Ok = true; return r; }
            r.Error = target.AdmitWhy(uid, toGroup, null, null) ?? "add_failed";
            if (has)
            {
                old.Add(uid, curGroup);
                if (old.GroupOf(uid) == curGroup) r.RolledBack = true; else r.RollbackFailed = true;
            }
            return r;
        }

        public static RingMoveResult Leave(int uid, IList<IRingGroupWindow> wins, string window, string group)
        {
            var r = new RingMoveResult { FromWindow = window, FromGroup = group };
            var w = Find(wins, window);
            if (w == null) { r.Error = "unknown_group"; return r; }
            if (!w.Ready) { r.Error = "not_ready"; return r; }
            if (w.GroupOf(uid) != group) { r.Error = "unknown_group"; return r; }
            w.Remove(uid, group);
            if (w.GroupOf(uid) != null) { r.Error = "remove_failed"; return r; }
            r.Ok = true;
            return r;
        }

        private sealed class FakeWin : IRingGroupWindow
        {
            public string Win; public bool IsReady = true;
            public List<RingGroupOption> List = new List<RingGroupOption>();
            public Dictionary<int, string> Mem = new Dictionary<int, string>();
            public Dictionary<string, string> Refuse = new Dictionary<string, string>();   // group -> admission code
            public bool AddRefuses, RemoveCalled; public int RemoveCalls, AddCalls;
            public string LastIgnoreWin, LastIgnoreGroup;
            public string Window { get { return Win; } }
            public bool Ready { get { return IsReady; } }
            public IList<RingGroupOption> Groups() { return List; }
            public string GroupOf(int uid) { string g; return Mem.TryGetValue(uid, out g) ? g : null; }
            public string AdmitWhy(int uid, string group, string iw, string ig)
            {
                LastIgnoreWin = iw; LastIgnoreGroup = ig;
                string c; return Refuse.TryGetValue(group, out c) ? c : null;
            }
            public bool Add(int uid, string group) { AddCalls++; if (AddRefuses) return true; Mem[uid] = group; return true; }
            public bool Remove(int uid, string group) { RemoveCalls++; RemoveCalled = true; Mem.Remove(uid); return true; }
        }

        private static RingGroupOption G(string win, string name, string kind, int n) { return new RingGroupOption { Window = win, Name = name, Kind = kind, Members = n }; }

        private static List<IRingGroupWindow> Rig(out FakeWin cap, out FakeWin asw)
        {
            cap = new FakeWin { Win = "cap" };
            cap.List.Add(G("cap", "C1", "cap", 2)); cap.List.Add(G("cap", "C2", "cap", 1));
            asw = new FakeWin { Win = "asw" };
            asw.List.Add(G("asw", "S1", "sar", 1)); asw.List.Add(G("asw", "A1", "asw", 3)); asw.List.Add(G("asw", "K1", "strike", 0));
            return new List<IRingGroupWindow> { cap, asw };
        }

        public static string SelfTest()
        {
            FakeWin cap, asw;
            var wins = Rig(out cap, out asw);
            // (1) View
            cap.Mem[1] = "C1";
            cap.Refuse["C2"] = "strike_loadout"; asw.Refuse["A1"] = "aew"; asw.Refuse["K1"] = "unfit";
            var v = View(1, wins);
            if (v == null || v.Current == null || v.Current.Name != "C1") return "view: current";
            if (v.Options.Count != 3) return "view: options " + v.Options.Count + " (aew hidden, current excluded)";
            if (v.Options[0].Name != "C2" || v.Options[1].Name != "K1" || v.Options[2].Name != "S1") return "view: order " + v.Options[0].Name + v.Options[1].Name + v.Options[2].Name;
            if (v.Options[0].Why != "strike_loadout" || v.Options[1].Why != "unfit") return "view: grey reasons";
            if (v.Admissible != 1 || v.Options[2].Why != null) return "view: admissible " + v.Admissible;
            cap.IsReady = false;
            if (View(1, wins) != null) return "view: not ready must be null";
            cap.IsReady = true;
            var emptyCap = new FakeWin { Win = "cap" }; var emptyAsw = new FakeWin { Win = "asw" };
            if (View(5, new List<IRingGroupWindow> { emptyCap, emptyAsw }) != null) return "view: no group no options must be null";
            var onlyHidden = new FakeWin { Win = "cap" }; onlyHidden.List.Add(G("cap", "H", "cap", 0)); onlyHidden.Refuse["H"] = "hidden";
            if (View(5, new List<IRingGroupWindow> { onlyHidden }) != null) return "view: only hidden must be null";
            // (2) Join without current group
            wins = Rig(out cap, out asw);
            var r = Join(7, wins, "cap", "C2");
            if (!r.Ok || cap.GroupOf(7) != "C2" || r.FromGroup != null) return "join: add";
            if (cap.LastIgnoreWin != null) return "join: ignore pair for a free unit";
            // (3) move cap -> asw
            r = Join(7, wins, "asw", "A1");
            if (!r.Ok || cap.GroupOf(7) != null || asw.GroupOf(7) != "A1" || r.FromGroup != "C2") return "join: move";
            if (asw.LastIgnoreWin != "cap" || asw.LastIgnoreGroup != "C2") return "join: ignore pair must be the current group";
            // (4) refusal: nothing called
            wins = Rig(out cap, out asw); cap.Mem[2] = "C1"; asw.Refuse["A1"] = "no_asw_load";
            r = Join(2, wins, "asw", "A1");
            if (r.Ok || r.Error != "no_asw_load" || cap.RemoveCalls != 0 || cap.GroupOf(2) != "C1") return "join: refusal changes nothing";
            // (5) add refused -> rollback
            wins = Rig(out cap, out asw); cap.Mem[3] = "C1"; asw.AddRefuses = true;
            r = Join(3, wins, "asw", "A1");
            if (r.Ok || !r.RolledBack || r.RollbackFailed || cap.GroupOf(3) != "C1" || r.Error != "add_failed") return "join: rollback " + r.Json();
            // (6) rollback refused
            wins = Rig(out cap, out asw); cap.Mem[3] = "C1"; asw.AddRefuses = true; cap.AddRefuses = true;
            r = Join(3, wins, "asw", "A1");
            if (r.Ok || r.RolledBack || !r.RollbackFailed || cap.GroupOf(3) != null) return "join: rollback failed " + r.Json();
            // (7) same group
            wins = Rig(out cap, out asw); cap.Mem[4] = "C2";
            r = Join(4, wins, "cap", "C2");
            if (r.Ok || r.Error != "same_group" || cap.RemoveCalls != 0) return "join: same group";
            r = Join(4, wins, "cap", "nope");
            if (r.Error != "unknown_group") return "join: unknown group";
            asw.IsReady = false;
            if (Join(4, wins, "cap", "C1").Error != "not_ready") return "join: not ready";
            asw.IsReady = true;
            // (8) Leave
            r = Leave(4, wins, "cap", "C2");
            if (!r.Ok || cap.GroupOf(4) != null) return "leave";
            if (Leave(4, wins, "cap", "C2").Ok) return "leave: not a member";
            // (9) ParseValue
            string w, g;
            if (!ParseValue("cap:ПВО:1", out w, out g) || w != "cap" || g != "ПВО:1") return "parse: split at first colon";
            if (ParseValue("nocolon", out w, out g) || ParseValue(":x", out w, out g) || ParseValue("a:", out w, out g)) return "parse: bad values";
            if (Value("cap", "ПВО-1") != "cap:ПВО-1") return "value";
            // (10) arc span
            if (ArcSpan(1) != 64 || ArcSpan(3) != 96 || ArcSpan(5) != 160 || ArcSpan(6) != 192 || ArcSpan(9) != 192) return "arc span";
            if (!HiddenWhy("hidden") || !HiddenWhy("aew") || HiddenWhy("player") || HiddenWhy(null)) return "hidden why";
            if (!r.Json().Contains("\"ok\":true")) return "json";
            return null;
        }
    }
}
