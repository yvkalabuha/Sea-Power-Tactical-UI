using System;
using System.Collections.Generic;
using System.Linq;

namespace VoiceBotBridge
{
    // T-180 stream D1: the barrier plan shows real rows / lines; the retreat of a group in the view model and in link_state ("air_retreat").
    internal static class AswT180Retreat
    {
        private static Dictionary<string, object> P(string json) { return (Dictionary<string, object>)Json.Parse(json); }

        /// <summary>The service as the controller sees it: stages and covers are set by the test.</summary>
        internal sealed class FakeThreats : IAirThreats
        {
            public AirGuardParams P { get; } = new AirGuardParams();
            public int Revision { get { return 0; } }
            public Dictionary<int, AirStage> Unit = new Dictionary<int, AirStage>();
            public Dictionary<int, AirCover> Cover = new Dictionary<int, AirCover>();
            public Dictionary<string, AirStage> Group = new Dictionary<string, AirStage>();
            public HashSet<string> Ign = new HashSet<string>();
            public bool Receding; public int Track = 10;   // T-207
            public IList<AirTrack> Tracks() { return new List<AirTrack>(); }
            public AirUnitThreat ForUnit(int uid)
            {
                AirStage s;
                if (!Unit.TryGetValue(uid, out s) || s == AirStage.None) return null;
                return new AirUnitThreat { Uid = uid, TrackId = Track, Receding = Receding, Stage = s, Nm = 30, EtaS = 200, RNm = 10, RSrc = "class", Why = "dist" };
            }
            public AirUnitThreat AtPoint(GeoPt p, bool helo) { return null; }
            public AirStage GroupStage(string window, string group) { AirStage s; return !Ign.Contains(group) && Group.TryGetValue(group, out s) ? s : AirStage.None; }
            public AirCover CoverFor(int uid) { AirCover c; AirStage s; return Unit.TryGetValue(uid, out s) && s == AirStage.Retreat && Cover.TryGetValue(uid, out c) ? c : null; }
            public bool Ignored(string window, string group) { return Ign.Contains(group); }
            public void Ignore(string window, string group, double now) { Ign.Add(group); }
        }

        /// <summary>The game side of the retreat helper over the ASW fake: the route goes through its Goto, like the real one.</summary>
        internal sealed class FakeSteer : IAirSteerGame
        {
            private readonly AswFakeGame _f;
            public List<string> Calls = new List<string>();
            public FakeSteer(AswFakeGame f) { _f = f; }
            private AswMember M(int uid) { return _f.Mem.Find(m => m.Uid == uid); }
            public GeoPt? Pos(int uid) { var m = M(uid); return m == null ? (GeoPt?)null : new GeoPt(m.Lat, m.Lon); }
            public string Goto(int uid, IList<GeoPt> pts, int altFt) { Calls.Add("goto:" + uid); return _f.Goto(uid, pts, altFt); }
            public string Orbit(int uid, GeoPt center, double radiusNm, bool racetrack) { Calls.Add("orbit:" + uid); return _f.Goto(uid, new List<GeoPt> { center }, 0); }
            public int Throttle(int uid) { return 2; }
            public string SetThrottle(int uid, int step) { Calls.Add("throttle:" + uid + ":" + step); return null; }
            public IList<CapStep> ThrottleSteps(int uid) { return new List<CapStep> { new CapStep { Step = 1, Kn = 60 }, new CapStep { Step = 2, Kn = 90 }, new CapStep { Step = 3, Kn = 120 } }; }
            public string SetLowPreset(int uid) { Calls.Add("low:" + uid); return null; }
            public string Remember(int uid) { Calls.Add("remember:" + uid); return null; }
            public string RestoreSpeedAlt(int uid) { Calls.Add("restore:" + uid); return null; }
            public bool Evading(int uid) { var m = M(uid); return m != null && m.Evading; }
            public int Count(string prefix) { int n = 0; foreach (var c in Calls) if (c.StartsWith(prefix, StringComparison.Ordinal)) n++; return n; }
        }

        internal static AirCover Cov(double n, double e, string mode = "cover") { return new AirCover { Mode = mode, ShipUid = 500, ShipName = "Yorktown", Point = new GeoPt(35 + n / 60.0, 20 + e / 60.0) }; }

        private static AswController Make(AswFakeGame f, FakeThreats th, out FakeSteer st)
        {
            var c = new AswController(f, new AswParams(), new AswConfig { RestoreOnLoad = "off" });
            st = new FakeSteer(f);
            c.AirGuard = th;
            c.AirSteer = new AirRetreatSteer(st, s => f.Logs.Add("steer " + s));
            return c;
        }

        private static void Run(AswController c, AswFakeGame f, int s) { for (int i = 0; i < s; i++) { f.Advance(1); f.Tick(c); } }

        public static string Run(string section)
        {
            string e;
            if ((section == null || section == "plan_barrier") && (e = PlanBarrier()) != null) return "plan_barrier: " + e;
            if ((section == null || section == "view_retreat") && (e = ViewRetreat()) != null) return "view_retreat: " + e;
            if ((section == null || section == "view_blocked") && (e = ViewBlocked()) != null) return "view_blocked: " + e;
            if ((section == null || section == "t207_why") && (e = T207Why()) != null) return "t207_why: " + e;
            if ((section == null || section == "view_resume") && (e = ViewResume()) != null) return "view_resume: " + e;
            return null;
        }

        private static string PlanBarrier()
        {
            var f = new AswFakeGame { DropDelayS = 0 };
            var h = AswFakeGame.Helo(1, "air", passive: 400); h.EnduranceS = 1e7; h.FuelFrac = 1; h.Vlad = 0; h.ActiveBuoys = 0; f.Mem.Add(h);
            var c = new AswController(f, new AswParams(), new AswConfig { RestoreOnLoad = "off" });
            c.Group(P("{\"op\":\"create\",\"group\":\"G\",\"uids\":[1]}"));
            if (AswController.IsErr(c.SearchCourse(P("{\"tactics\":[\"barrier\"]}")))) return "order";
            Run(c, f, 5);
            var pv = c.View().Groups[0].Plan;
            if (pv.RowsMax <= 0) return "rows=" + pv.RowsMax;
            if (pv.Lines < 1 || pv.LinesLaid < 1 || pv.LinesLaid > pv.Lines) return "lines " + pv.Lines + "/" + pv.LinesLaid;
            if (pv.NodesTotal <= 0) return "nodes_total " + pv.NodesTotal;
            string js = c.StateJson();
            if (!js.Contains("\"lines\":" + pv.Lines) || !js.Contains("\"lines_laid\":" + pv.LinesLaid)) return "link_state lacks lines: " + js;
            if (js.Contains("\"rows\":0,")) return "link_state rows=0";
            return null;
        }

        internal static AswFakeGame Helos(int n)
        {
            var f = new AswFakeGame { DropDelayS = 60, DropEveryS = 30, SimBuoys = true };
            for (int u = 1; u <= n; u++) { var h = AswFakeGame.Helo(u, "air", passive: 40); h.EnduranceS = 1e7; h.FuelFrac = 1; f.Mem.Add(h); }
            return f;
        }

        internal static AswController Start(AswFakeGame f, FakeThreats th, out FakeSteer st)
        {
            var c = new AswController(f, new AswParams(), new AswConfig { RestoreOnLoad = "off" });
            st = new FakeSteer(f);
            c.AirGuard = th;
            c.AirSteer = new AirRetreatSteer(st, s => f.Logs.Add("steer " + s));
            c.Group(P("{\"op\":\"create\",\"group\":\"G\",\"uids\":[1,2]}"));
            c.SearchCourse(P("{\"tactics\":[\"rows\",\"dip\"]}"));
            return c;
        }

        private static string ViewRetreat()
        {
            var f = Helos(2); var th = new FakeThreats(); FakeSteer st;
            var c = Start(f, th, out st);
            Run(c, f, 100);
            if (c.View().Groups[0].Retreat != null) return "retreat view without a threat";
            th.Unit[1] = AirStage.Retreat; th.Cover[1] = Cov(8, 6); th.Group["G"] = AirStage.Retreat;
            Run(c, f, 3);
            var gv = c.View().Groups[0];
            if (gv.Retreat == null) return "no Retreat in the view";
            if (gv.Retreat.Members != 1 || gv.Retreat.Cover != "Yorktown" || gv.Retreat.ResumeWhen != "recede" || Math.Abs(gv.Retreat.Nm - 30) > 1e-9 || gv.Retreat.Why != "dist")
                return "retreat view " + gv.Retreat.Members + "/" + gv.Retreat.Cover + "/" + gv.Retreat.ResumeWhen + "/" + gv.Retreat.Nm + "/" + gv.Retreat.Why;
            var r1 = gv.MemberRows.Find(r => r.Uid == 1); var r2 = gv.MemberRows.Find(r => r.Uid == 2);
            if (r1 == null || r1.RetreatTo != "Yorktown") return "row 1 RetreatTo=" + (r1 == null ? "-" : r1.RetreatTo);
            if (r2 == null || r2.RetreatTo != null) return "row 2 must not retreat";
            if (gv.RetreatArrows.Count != 1) return "arrows " + gv.RetreatArrows.Count;
            var a = gv.RetreatArrows[0];
            if (a.Uid != 1 || a.Away || Math.Abs(a.ToLat - th.Cover[1].Point.Lat) > 1e-9 || Math.Abs(a.ToLon - th.Cover[1].Point.Lon) > 1e-9) return "arrow " + a.Uid + " away=" + a.Away;
            string js = c.StateJson();
            if (!js.Contains("\"air_retreat\":{\"members\":1,\"launch_blocked\":true,\"nm\":30,\"cover\":\"Yorktown\",\"why\":\"dist\",\"resume_when\":\"recede\"")) return "ui_state: " + js;
            // no cover: mode away
            th.Cover[1] = new AirCover { Mode = "away", Point = new GeoPt(36, 21) };
            Run(c, f, 3);
            gv = c.View().Groups[0];
            r1 = gv.MemberRows.Find(r => r.Uid == 1);
            if (r1.RetreatTo != "" || gv.RetreatArrows.Count != 1 || !gv.RetreatArrows[0].Away || gv.Retreat.Cover != null) return "away: " + r1.RetreatTo + "/" + gv.RetreatArrows.Count;
            return null;
        }

        private static string ViewBlocked()
        {
            var f = Helos(2); var th = new FakeThreats(); FakeSteer st;
            var c = Start(f, th, out st);
            Run(c, f, 100);
            th.Group["G"] = AirStage.Retreat;   // the group is held, no machine retreats
            Run(c, f, 3);
            var gv = c.View().Groups[0];
            if (gv.Retreat == null || !gv.Retreat.LaunchBlocked || gv.Retreat.Members != 0) return "blocked view " + (gv.Retreat == null ? "null" : gv.Retreat.LaunchBlocked + "/" + gv.Retreat.Members);
            if (gv.RetreatArrows.Count != 0) return "arrows without retreating machines";
            if (!c.StateJson().Contains("\"launch_blocked\":true")) return "ui_state";
            return null;
        }

        // T-207: the member lines carry track= and why=receding|clear
        private static string T207Why()
        {
            var f = Helos(2); var th = new FakeThreats(); FakeSteer st;
            var c = Start(f, th, out st);
            Run(c, f, 100);
            th.Unit[1] = AirStage.Retreat; th.Unit[2] = AirStage.Retreat; th.Cover[1] = Cov(8, 6); th.Cover[2] = Cov(9, 6); th.Group["G"] = AirStage.Retreat;
            Run(c, f, 5);
            if (!f.Logs.Exists(l => l.Contains("[AirGuard] retreat grp=G uid=1") && l.Contains(" track=10 why="))) return "retreat line lacks track=: " + string.Join(" | ", f.Logs.FindAll(l => l.Contains("retreat grp=")));
            th.Unit[1] = AirStage.Warn; th.Receding = true; th.Unit[2] = AirStage.None; th.Group["G"] = AirStage.None;
            Run(c, f, 5);
            if (!f.Logs.Exists(l => l.Contains("resume grp=G uid=1 ") && l.Contains(" why=receding track=10 restore -> ok"))) return "member 1: " + string.Join(" | ", f.Logs.FindAll(l => l.Contains("resume grp=")));
            if (!f.Logs.Exists(l => l.Contains("resume grp=G uid=2 ") && l.Contains(" why=clear track=- restore -> ok"))) return "member 2: " + string.Join(" | ", f.Logs.FindAll(l => l.Contains("resume grp=")));
            return null;
        }

        private static string ViewResume()
        {
            var f = Helos(2); var th = new FakeThreats(); FakeSteer st;
            var c = Start(f, th, out st);
            Run(c, f, 100);
            th.Unit[1] = AirStage.Retreat; th.Cover[1] = Cov(8, 6); th.Group["G"] = AirStage.Retreat;
            Run(c, f, 5);
            if (c.View().Groups[0].Retreat == null) return "no retreat";
            th.Unit[1] = AirStage.None; th.Group["G"] = AirStage.None;
            Run(c, f, 5);
            var gv = c.View().Groups[0];
            if (gv.Retreat != null || gv.RetreatArrows.Count != 0) return "retreat view stays after the resume";
            if (gv.MemberRows.Exists(r => r.RetreatTo != null)) return "row RetreatTo stays";
            if (!c.StateJson().Contains("\"air_retreat\":null")) return "ui_state air_retreat must be null";
            return null;
        }
    }
}
