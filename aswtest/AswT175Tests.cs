using System;
using System.Collections.Generic;

namespace VoiceBotBridge
{
    // T-175 / ADR-0075 stream A: the ASW / SAR controller applies the stages of the air threat service (retreat, no launches, resume, lost).
    internal static class AswT175Tests
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

        private static AirCover Cov(double n, double e, string mode = "cover") { return new AirCover { Mode = mode, ShipUid = 500, ShipName = "Yorktown", Point = new GeoPt(35 + n / 60.0, 20 + e / 60.0) }; }

        private static AswController Make(AswFakeGame f, FakeThreats th, out FakeSteer st)
        {
            var c = new AswController(f, new AswParams(), new AswConfig { RestoreOnLoad = "off" });
            st = new FakeSteer(f);
            c.AirGuard = th;
            c.AirSteer = new AirRetreatSteer(st, s => f.Logs.Add("steer " + s));
            return c;
        }

        private static void Run(AswController c, AswFakeGame f, int s) { for (int i = 0; i < s; i++) { f.Advance(1); f.Tick(c); } }
        private static bool Logged(AswFakeGame f, string sub) { foreach (var l in f.Logs) if (l.Contains(sub)) return true; return false; }
        private static int Drops(AswFakeGame f, int uid) { int n = 0; foreach (var d in f.Drops) if (d.Uid == uid) n++; return n; }

        private static AswFakeGame TwoHelos()
        {
            var f = new AswFakeGame { DropDelayS = 60, DropEveryS = 30, SimBuoys = true };
            for (int u = 1; u <= 2; u++) { var h = AswFakeGame.Helo(u, "air", passive: 40); h.EnduranceS = 1e7; h.FuelFrac = 1; f.Mem.Add(h); }
            return f;
        }

        private static string Group(AswController c)
        {
            string r = c.Group(P("{\"op\":\"create\",\"group\":\"G\",\"uids\":[1,2]}"));
            if (AswController.IsErr(r)) return r;
            r = c.SearchCourse(P("{\"tactics\":[\"rows\",\"dip\"]}"));
            return AswController.IsErr(r) ? r : null;
        }

        public static readonly string[] Names = { "asw_retreat", "evasion_first", "player_took", "ignore", "fuel_first", "no_launch", "loss", "sar_cover", "sar_land", "sar_no_launch", "sar_resume" };

        /// <summary>One scenario by name (the pytest cases), or all of them (null).</summary>
        public static string Run(string only = null)
        {
            string e;
            if (only == null && (e = AirRetreatSteer.SelfTest()) != null) return "steer: " + e;
            Func<string, bool> want = n => only == null || only == n;
            if (want("asw_retreat") && (e = Retreat()) != null) return "asw_retreat: " + e;
            if (want("evasion_first") && (e = EvasionFirst()) != null) return "evasion_first: " + e;
            if (want("player_took") && (e = PlayerTook()) != null) return "player_took: " + e;
            if (want("ignore") && (e = IgnoreTest()) != null) return "ignore: " + e;
            if (want("fuel_first") && (e = FuelFirst()) != null) return "fuel_first: " + e;
            if (want("no_launch") && (e = NoLaunch()) != null) return "no_launch: " + e;
            if (want("loss") && (e = Loss()) != null) return "loss: " + e;
            if (want("sar_cover") && (e = SarCover()) != null) return "sar_cover: " + e;
            if (want("sar_land") && (e = SarLand()) != null) return "sar_land: " + e;
            if (want("sar_no_launch") && (e = SarNoLaunch()) != null) return "sar_no_launch: " + e;
            if (want("sar_resume") && (e = SarResume()) != null) return "sar_resume: " + e;
            return null;
        }

        // stage 2 for one aircraft: it flies to the cover point and the tactic stops (no more buoys), the other one works on; lifted: the speed returns, the tactic goes on
        private static string Retreat()
        {
            var f = TwoHelos(); var th = new FakeThreats(); FakeSteer st;
            var c = Make(f, th, out st);
            string err = Group(c); if (err != null) return err;
            Run(c, f, 200);
            int before1 = Drops(f, 1), before2 = Drops(f, 2);
            if (before1 == 0 && before2 == 0) return "no drops before the threat";
            th.Unit[1] = AirStage.Retreat; th.Cover[1] = Cov(8, 6);
            Run(c, f, 3);
            List<GeoPt> r;
            if (!f.Route.TryGetValue(1, out r) || r.Count != 1 || Math.Abs(r[0].Lat - th.Cover[1].Point.Lat) > 1e-9) return "no route to the cover point";
            if (st.Count("remember:1") != 1) return "speed and height not remembered";
            if (c.AirRetreating("G") != 1) return "retreating " + c.AirRetreating("G");
            if (!Logged(f, "[AirGuard] retreat grp=G uid=1 mode=cover cover=Yorktown")) return "no retreat line";
            if (!f.HasEvent("air_retreat", "\"on\":true")) return "no air_retreat event";
            int d1 = Drops(f, 1);
            Run(c, f, 600);
            if (Drops(f, 1) != d1) return "the retreating aircraft still drops buoys";
            if (st.Count("goto:2") != 0 || st.Count("remember:2") != 0) return "the other aircraft was steered too";
            if (st.Count("goto:1") != 1) return "route repeated " + st.Count("goto:1");
            // the threat is gone
            th.Unit[1] = AirStage.None;
            Run(c, f, 400);
            if (st.Count("restore:1") != 1) return "speed and height not restored";
            if (!Logged(f, "[AirGuard] resume grp=G uid=1")) return "no resume line";
            if (c.AirRetreating("G") != 0) return "still retreating";
            if (Drops(f, 1) <= d1) return "the tactic did not go on after the resume";
            return null;
        }

        private static string EvasionFirst()
        {
            var f = TwoHelos(); var th = new FakeThreats(); FakeSteer st;
            var c = Make(f, th, out st);
            string err = Group(c); if (err != null) return err;
            Run(c, f, 100);
            f.Mem[0].Evading = true;
            th.Unit[1] = AirStage.Retreat; th.Cover[1] = Cov(8, 6);
            Run(c, f, 20);
            if (st.Count("goto:1") != 0 || st.Count("remember:1") != 0) return "retreat route while evading";
            f.Mem[0].Evading = false;
            Run(c, f, 3);
            if (st.Count("goto:1") != 1) return "no retreat after the manoeuvre: " + st.Count("goto:1");
            return null;
        }

        private static string PlayerTook()
        {
            var f = TwoHelos(); var th = new FakeThreats(); FakeSteer st;
            var c = Make(f, th, out st);
            string err = Group(c); if (err != null) return err;
            Run(c, f, 100);
            th.Unit[1] = AirStage.Retreat; th.Cover[1] = Cov(8, 6);
            Run(c, f, 3);
            if (c.AirRetreating("G") != 1) return "not retreating";
            f.Changed.Add(1);
            Run(c, f, 2);
            if (!f.HasEvent("asw_warn", "player_took")) return "no player_took warning";
            if (c.AirRetreating("G") != 0) return "retreat record kept";
            var g = c.View().Groups[0];
            if (g.Members.Contains(1)) return "still a member";
            int calls = st.Count("goto:1");
            Run(c, f, 30);
            if (st.Count("goto:1") != calls) return "the aircraft of the player is steered";
            return null;
        }

        private static string IgnoreTest()
        {
            var f = TwoHelos(); var th = new FakeThreats(); FakeSteer st;
            var c = Make(f, th, out st);
            string err = Group(c); if (err != null) return err;
            th.Unit[1] = AirStage.Retreat; th.Cover[1] = Cov(8, 6);
            th.Ign.Add("G");   // "Continue tasks": the service reports nothing for the group
            Unit(th, true);
            Run(c, f, 300);
            if (st.Count("goto:1") != 0) return "retreat although ignored";
            if (Drops(f, 1) == 0) return "the tactic did not go on";
            return null;
        }

        private static void Unit(FakeThreats th, bool ignored)
        {
            // ForUnit of the real service returns null for an ignored group; the fake does it through Ign
            if (ignored) { th.Unit.Clear(); }
        }

        // fuel / the game's own return beat the retreat
        private static string FuelFirst()
        {
            var f = TwoHelos(); var th = new FakeThreats(); FakeSteer st;
            var c = Make(f, th, out st);
            string err = Group(c); if (err != null) return err;
            Run(c, f, 50);
            f.Mem[0].AiState = "BingoFuel";
            th.Unit[1] = AirStage.Retreat; th.Cover[1] = Cov(8, 6);
            Run(c, f, 20);
            if (st.Count("goto:1") != 0) return "retreat route for an aircraft that goes home";
            return null;
        }

        // stage 2 of the group: the plan "launch" is carried out as "prepare" (no allow); after the threat the allow goes out at once
        private static string NoLaunch()
        {
            var f = new AswFakeGame(); var d = new AswFakeDeck();
            f.RealDeck = d; f.Decks[77] = new AswTarget { Uid = 77, Name = d.ShipName, Lat = 35, Lon = 20 };
            d.AddHangar("usn_sh-3h", "SH-3H", 2);
            var th = new FakeThreats(); FakeSteer st;
            var c = Make(f, th, out st);
            f.Advance(1); f.Tick(c);
            string r = c.Group(P("{\"op\":\"create\",\"group\":\"G\",\"uids\":[" + AswMath.HangarUid(77, "usn_sh-3h") + "]}"));
            if (AswController.IsErr(r)) return r;
            if (AswController.IsErr(c.SearchCourse(P("{\"tactics\":[\"dip\"]}")))) return "order";
            th.Group["G"] = AirStage.Retreat;
            Run(c, f, 1500);
            if (f.Allows.Count != 0) return "allow while the group is on stage 2: " + string.Join(",", f.Allows.ToArray());
            foreach (var o in d.Orders) if (o.EndsWith(" allow", StringComparison.Ordinal)) return "an order with allowLaunch: " + o;
            if (d.Orders.Count == 0) return "the preparation did not go on";
            th.Group["G"] = AirStage.None;
            Run(c, f, 60);
            bool flying = false; foreach (var m in f.RealDeck.Air) flying = true;
            bool allowed = false; foreach (var t in d.Tasks) if (t.Allow) allowed = true;
            if (!flying && !allowed && d.Units.Count == 0 && f.Allows.Count == 0) return "no launch after the threat";
            return null;
        }

        // the aircraft is lost: it leaves the group with lost:<cause>, not as "landed"
        private static string Loss()
        {
            var f = TwoHelos(); var th = new FakeThreats(); FakeSteer st;
            var c = Make(f, th, out st);
            string err = Group(c); if (err != null) return err;
            Run(c, f, 100);
            f.Mem.RemoveAll(m => m.Uid == 1);
            c.AirLost(1, "shot_down");
            Run(c, f, 5);
            if (!Logged(f, "reason=lost:shot_down")) return "no lost line";
            if (Logged(f, "asw adopt") && Logged(f, "uid=1 land")) return "taken for a landing";
            if (c.View().Groups[0].Members.Contains(1)) return "still a member";
            if (!f.HasEvent("asw_warn", "member_lost")) return "no member_lost warning";
            return null;
        }

        // ---- SAR ----

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
                F.Mode = "Arcade";
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

        // an empty helicopter: to the cover point, the rafts are released
        private static string SarCover()
        {
            var w = new World();
            string err = SarSetup(w); if (err != null) return err;
            w.Run(300);
            int u = AirUid(w);
            if (u == 0) return "helicopter not airborne: " + Tail(w);
            if (w.ClaimsOf(u) == 0) return "no claim before the threat: " + Tail(w);
            w.Th.Unit[u] = AirStage.Retreat; w.Th.Cover[u] = Cov(8, 6);
            w.Th.Group["SAR-1"] = AirStage.Retreat;
            w.Run(5);
            if (w.St.Count("goto:" + u) != 1 || w.St.Count("low:" + u) != 1) return "no cover route / low height: " + string.Join(",", w.St.Calls.ToArray());
            if (w.ClaimsOf(u) != 0) return "the rafts are not released";
            if (w.F.Calls.Contains("rtb:" + u)) return "empty helicopter sent home";
            return null;
        }

        // 16 rescued on board and a deck in the ring: it lands (the game flies it home, like the "full" leave)
        private static string SarLand()
        {
            var w = new World();
            string err = SarSetup(w); if (err != null) return err;
            w.Run(300);
            int u = AirUid(w);
            if (u == 0) return "helicopter not airborne";
            w.F.SarWorld.Units[u].Onboard = 16;
            w.Th.Unit[u] = AirStage.Retreat; w.Th.Cover[u] = Cov(2, 2, "land"); w.Th.Group["SAR-1"] = AirStage.Retreat;
            w.Run(5);
            if (!w.F.Calls.Contains("rtb:" + u)) return "no landing order: " + string.Join(",", w.F.Calls.ToArray());
            if (!w.Logged("[Sar] leave SAR-1") || !w.Logged("reason=air_threat")) return "no leave line";
            if (w.St.Count("goto:" + u) != 0) return "a cover route for a landing helicopter";
            return null;
        }

        // the group is on stage 2: the deck helicopter is prepared but not allowed
        private static string SarNoLaunch()
        {
            var w = new World();
            string err = SarSetup(w); if (err != null) return err;
            w.Th.Group["SAR-1"] = AirStage.Retreat;
            w.Run(600);
            if (w.F.Allows.Count != 0) return "allow on stage 2";
            foreach (var o in w.F.OrderLog) if (o.EndsWith(":True", StringComparison.Ordinal)) return "an order with allowLaunch: " + o;
            foreach (var m in w.F.Mem) if (m.Where == "air") return "a helicopter took off";
            w.Th.Group["SAR-1"] = AirStage.None;
            w.Run(120);
            bool air = false; foreach (var m in w.F.Mem) if (m.Where == "air") air = true;
            if (!air && w.F.Allows.Count == 0) return "no launch after the threat";
            return null;
        }

        // after the threat the rafts are assigned again
        private static string SarResume()
        {
            var w = new World();
            string err = SarSetup(w); if (err != null) return err;
            w.Run(300);
            int u = AirUid(w);
            if (u == 0) return "helicopter not airborne";
            w.Th.Unit[u] = AirStage.Retreat; w.Th.Cover[u] = Cov(8, 6); w.Th.Group["SAR-1"] = AirStage.Retreat;
            w.Run(10);
            w.Th.Unit[u] = AirStage.None; w.Th.Group["SAR-1"] = AirStage.None;
            w.Run(60);
            if (w.St.Count("restore:" + u) != 1) return "no restore";
            if (w.ClaimsOf(u) == 0) return "the rafts are not assigned again: " + Tail(w);
            if (!w.Logged("[AirGuard] resume grp=SAR-1")) return "no resume line";
            return null;
        }
    }
}
