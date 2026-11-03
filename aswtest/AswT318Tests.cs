using System;
using System.Collections.Generic;

namespace VoiceBotBridge
{
    // T-318: a SAR helicopter whose air-threat retreat ended (resume, "ignore", release) or that evades a missile is not "taken by the player".
    internal static class AswT318Tests
    {
        private static Dictionary<string, object> P(string json) { return (Dictionary<string, object>)Json.Parse(json); }

        /// <summary>The service as the controller sees it: stages and covers are set by the test.</summary>
        private sealed class FakeThreats : IAirThreats
        {
            public AirGuardParams P { get; } = new AirGuardParams();
            public int Revision { get { return 0; } }
            public Dictionary<int, AirStage> Unit = new Dictionary<int, AirStage>();
            public Dictionary<int, AirCover> Cover = new Dictionary<int, AirCover>();
            public Dictionary<string, AirStage> Group = new Dictionary<string, AirStage>();
            public HashSet<string> Ign = new HashSet<string>();
            public IList<AirTrack> Tracks() { return new List<AirTrack>(); }
            public AirUnitThreat ForUnit(int uid)
            {
                AirStage s;
                if (!Unit.TryGetValue(uid, out s) || s == AirStage.None) return null;
                return new AirUnitThreat { Uid = uid, TrackId = 10, Stage = s, Nm = 30, EtaS = 200, RNm = 10, RSrc = "class", Why = "dist" };
            }
            public AirUnitThreat AtPoint(GeoPt p, bool helo) { return null; }
            public AirStage GroupStage(string window, string group) { AirStage s; return !Ign.Contains(group) && Group.TryGetValue(group, out s) ? s : AirStage.None; }
            public AirCover CoverFor(int uid) { AirCover c; AirStage s; return Unit.TryGetValue(uid, out s) && s == AirStage.Retreat && Cover.TryGetValue(uid, out c) ? c : null; }
            public bool Ignored(string window, string group) { return Ign.Contains(group); }
            public void Ignore(string window, string group, double now) { Ign.Add(group); }
        }

        /// <summary>The game side of the retreat helper over the ASW fake: the route goes through its Goto, like the real one.</summary>
        private sealed class FakeSteer : IAirSteerGame
        {
            private readonly AswFakeGame _f;
            public List<string> Calls = new List<string>();
            public FakeSteer(AswFakeGame f) { _f = f; }
            private AswMember M(int uid) { return _f.Mem.Find(m => m.Uid == uid); }
            public GeoPt? Pos(int uid) { var m = M(uid); return m == null ? (GeoPt?)null : new GeoPt(m.Lat, m.Lon); }
            public string Goto(int uid, IList<GeoPt> pts, int altFt) { Calls.Add("goto:" + uid); _f.SarWorld.Routes[uid] = new List<GeoPt>(pts); return _f.Goto(uid, pts, altFt); }
            public string Orbit(int uid, GeoPt center, double radiusNm, bool racetrack) { Calls.Add("orbit:" + uid); _f.SarWorld.Routes[uid] = new List<GeoPt> { center }; return _f.Goto(uid, new List<GeoPt> { center }, 0); }
            public int Throttle(int uid) { return 2; }
            public string SetThrottle(int uid, int step) { Calls.Add("throttle:" + uid + ":" + step); return null; }
            public IList<CapStep> ThrottleSteps(int uid) { return new List<CapStep> { new CapStep { Step = 1, Kn = 60 }, new CapStep { Step = 2, Kn = 90 }, new CapStep { Step = 3, Kn = 120 } }; }
            public string SetLowPreset(int uid) { Calls.Add("low:" + uid); return null; }
            public string Remember(int uid) { Calls.Add("remember:" + uid); return null; }
            public string RestoreSpeedAlt(int uid) { Calls.Add("restore:" + uid); return null; }
            public bool Evading(int uid) { var m = M(uid); return m != null && m.Evading; }
            public int Count(string prefix) { int n = 0; foreach (var c in Calls) if (c.StartsWith(prefix, StringComparison.Ordinal)) n++; return n; }
        }

        private static AirCover Cov(double n, double e, string mode = "cover") { return new AirCover { Mode = mode, ShipUid = 500, ShipName = "Yorktown", Point = new GeoPt(35 + n / 60.0, 20 + e / 60.0) }; }

        private sealed class World
        {
            public AswFakeGame F = new AswFakeGame();
            public SarController S;
            public AswController C;
            public FakeThreats Th = new FakeThreats();
            public FakeSteer St;
            public int Landings;

            public World()
            {
                F.Mode = "Arcade"; F.SarWorld.PlacedModel = true;   // T-318
                F.LoadoutList.Add(new LoadoutOption { Name = "Default", Display = "Default", Capacity = 20, ReadyUpMin = 15, CoolDownMin = 15, Available = true });
                S = new SarController(F.SarWorld, new SarParams(), null);
                C = new AswController(F, new AswParams(), new AswConfig { RestoreOnLoad = "off" });
                C.Sar = S;
                St = new FakeSteer(F);
                C.AirGuard = Th;
                C.AirSteer = new AirRetreatSteer(St, s => F.Logs.Add("steer " + s));
            }

            public AswMember Deck(int uid, string where = "deck_ready")
            {
                var m = AswFakeGame.Helo(uid, where);
                m.Type = "SH-3H"; m.DeckKey = "task:t" + uid; m.Loadout = "Default"; m.Capacity = 20; m.BestCapacity = 20; m.Passive = 0; m.ActiveBuoys = 0; m.Vlad = 0; m.Torpedoes = 0;
                m.DeckUid = 9000; m.ReadyInS = where == "deck_prep" ? 60 : 0; m.FuelFrac = 1;
                F.Mem.Add(m);
                return m;
            }

            public void Step()
            {
                var sw = F.SarWorld;
                foreach (var m in F.Mem)
                {
                    if (m.Where == "air" && !sw.Units.ContainsKey(m.Uid)) { var u = sw.AddUnit(m.Uid, "helo", 0, 0); u.Capacity = m.Capacity; u.Onboard = m.Onboard; }
                    if (m.Where == "deck_prep") { m.ReadyInS -= 1; if (m.ReadyInS <= 0) { m.ReadyInS = 0; m.Where = "deck_ready"; } }
                    if (m.Where == "air") m.FuelFrac -= 1.0 / m.EnduranceS;
                }
                sw.Step(1);
                F.Now = sw.Now;
                foreach (var m in F.Mem)
                {
                    SarUnit u;
                    if (m.Where != "air" || !sw.Units.TryGetValue(m.Uid, out u)) continue;
                    m.Lat = u.Lat; m.Lon = u.Lon; m.Onboard = u.Onboard;
                }
                F.Tick(C);
            }

            public void Run(int s) { for (int i = 0; i < s; i++) Step(); }
            public bool Logged(string sub) { foreach (var l in F.Logs) if (l.Contains(sub)) return true; return false; }
            public int ClaimsOf(int uid) { int n = 0; foreach (var cl in S.Registry.Claims) if (cl.Rescuer == uid) n++; return n; }
        }

        private static string SarSetup(World w, int uid = 1)
        {
            w.Deck(uid);
            w.F.SarWorld.AddRaft(11, 40, 3, 2); w.F.SarWorld.AddRaft(12, -40, 6, 2);
            string r = w.C.Group(P("{\"group\":\"SAR-1\",\"op\":\"create\",\"uids\":[" + uid + "]}"));
            if (AswController.IsErr(r)) return r;
            r = w.C.SarOrder(P("{\"group\":\"SAR-1\"}"));
            return AswController.IsErr(r) ? r : null;
        }

        private static int AirUid(World w) { foreach (var m in w.F.Mem) if (m.Where == "air") return m.Uid; return 0; }

        private static string Tail(World w) { return string.Join(" | ", w.F.Logs.GetRange(Math.Max(0, w.F.Logs.Count - 6), Math.Min(6, w.F.Logs.Count)).ToArray()); }

        private static bool Took(World w) { return w.Logged("player_took") || w.F.SarWorld.Events.Exists(e => e.Contains("player_took")); }
        private static int ForgotCount(World w, int uid) { int n = 0; foreach (var x in w.F.SarWorld.Forgot) if (x == uid) n++; return n; }

        private static string Setup(World w, out int u)
        {
            u = 0;
            w.Deck(1);
            w.F.SarWorld.AddRaft(11, 40, 3, 2); w.F.SarWorld.AddRaft(12, -40, 6, 2);
            string r = w.C.Group(P("{\"group\":\"SAR-1\",\"op\":\"create\",\"uids\":[1]}"));
            if (AswController.IsErr(r)) return r;
            r = w.C.SarOrder(P("{\"group\":\"SAR-1\"}"));
            if (AswController.IsErr(r)) return r;
            w.Run(300);
            u = AirUid(w);
            if (u == 0) return "helicopter not airborne: " + Tail(w);
            if (w.ClaimsOf(u) == 0) return "no claim before the threat: " + Tail(w);
            return null;
        }

        private static void Retreat(World w, int u) { w.Th.Unit[u] = AirStage.Retreat; w.Th.Cover[u] = Cov(8, 6); w.Th.Group["SAR-1"] = AirStage.Retreat; }
        private static void Clear(World w, int u) { w.Th.Unit[u] = AirStage.None; w.Th.Group["SAR-1"] = AirStage.None; }

        public static readonly string[] Names = { "resume", "ignore_moved", "release_all", "evading", "manual" };

        public static string Run(string only = null)
        {
            string e;
            Func<string, bool> want = n => only == null || only == n;
            if (want("resume") && (e = Resume()) != null) return "resume: " + e;
            if (want("ignore_moved") && (e = IgnoreMoved()) != null) return "ignore_moved: " + e;
            if (want("release_all") && (e = ReleaseAll()) != null) return "release_all: " + e;
            if (want("evading") && (e = Evading()) != null) return "evading: " + e;
            if (want("manual") && (e = Manual()) != null) return "manual: " + e;
            return null;
        }

        private static string Resume()
        {
            var w = new World(); int u;
            string err = Setup(w, out u); if (err != null) return err;
            Retreat(w, u); w.Run(10); Clear(w, u); w.Run(60);
            if (Took(w)) return "player_took: " + Tail(w);
            if (w.ClaimsOf(u) == 0) return "the rafts are not assigned again: " + Tail(w);
            if (ForgotCount(w, u) < 2) return "forgot " + ForgotCount(w, u);
            return null;
        }

        private static string IgnoreMoved()
        {
            var w = new World(); int u;
            string err = Setup(w, out u); if (err != null) return err;
            Retreat(w, u); w.Run(10);
            w.Th.Cover.Remove(u); w.Run(30);   // "ignore": no cover, SAR assigns a raft again
            if (w.ClaimsOf(u) == 0) return "no raft assigned while ignored: " + Tail(w);
            w.Th.Cover[u] = Cov(20, 15); w.F.SarWorld.Routes[u] = new List<GeoPt> { Cov(20, 15).Point }; w.Run(5);   // the cover returns, moved: the route is the cover point
            Clear(w, u); w.Run(60);
            if (Took(w)) return "player_took: " + Tail(w);
            if (w.ClaimsOf(u) == 0) return "the rafts are not assigned again: " + Tail(w);
            return null;
        }

        private static string ReleaseAll()
        {
            var w = new World(); int u;
            string err = Setup(w, out u); if (err != null) return err;
            Retreat(w, u); w.Run(10);
            int before = ForgotCount(w, u);
            w.Th.P.Mode = "off"; w.Run(60);
            if (Took(w)) return "player_took: " + Tail(w);
            if (ForgotCount(w, u) <= before) return "not forgotten on release_all";
            return null;
        }

        private static string Evading()
        {
            var w = new World(); int u;
            string err = Setup(w, out u); if (err != null) return err;
            var m = w.F.Mem.Find(x => x.Uid == u);
            GeoPt basePt = w.F.SarWorld.Placed[u];
            m.Evading = true;
            w.F.SarWorld.Routes[u] = new List<GeoPt> { SarFakeGame.At(3, 0) };
            w.Run(20);
            if (Took(w)) return "player_took while evading: " + Tail(w);
            m.Evading = false;
            w.F.SarWorld.Routes[u] = new List<GeoPt> { basePt };
            w.Run(20);
            if (Took(w)) return "player_took after evading: " + Tail(w);
            return null;
        }

        private static string Manual()
        {
            var w = new World(); int u;
            string err = Setup(w, out u); if (err != null) return err;
            Retreat(w, u); w.Run(10); Clear(w, u); w.Run(60);
            if (Took(w)) return "early player_took: " + Tail(w);
            if (!w.F.SarWorld.Placed.ContainsKey(u)) return "no base after the new assignment";
            var pos = w.F.SarWorld.Units[u];
            w.F.SarWorld.Routes[u] = new List<GeoPt> { new GeoPt(pos.Lat + 5 / 60.0, pos.Lon + 30 / 60.0) };
            w.Run(10);
            if (!Took(w)) return "a manual command must give player_took: " + Tail(w);
            return null;
        }
    }
}
