using System;
using System.Collections.Generic;
using System.Linq;

namespace VoiceBotBridge
{
    // T-176 C9 (v3-relief, multi-tactic): lanes, hand-over of a lane, the air threat service.
    internal static partial class AswT176Tests
    {
        private static string TacticOfUid(AswController c, int uid)
        {
            foreach (var g in c.View().Groups) foreach (var r in g.MemberRows) if (r.Uid == uid) return r.Tactic;
            return null;
        }

        private static string Relief()
        {
            // corridor X = 20, L = 40, K = 2 with 12 buoys each: two columns... the left one leaves, the other continues both sides from the cursor
            var f = CorridorGame(12, 40);
            var c = StartCourse(f, "{\"tactics\":[\"corridor\"],\"standoff\":{\"corridor\":40}}");
            Run(c, f, 3);
            var first = Local(f, f.Drops.First(d => d.Uid == 1));
            if (first.Count != 12 || first.Any(q => q.Y >= 0)) return "the left aircraft did not take the left side: " + first.Count;
            double xMaxLeft = first.Max(q => q.X);
            // aircraft 1 is sent home: its lane goes at once (Leaving), the remaining aircraft takes over both sides
            f.Mem[0].AiState = "ReturnToBase";
            int before = f.Drops.Count;
            Run(c, f, 300);
            if (TacticOfUid(c, 1) != "reserve") return "the leaving aircraft keeps its lane: " + TacticOfUid(c, 1);
            var later = f.Drops.Skip(before).Where(d => d.Uid == 2).SelectMany(d => Local(f, d)).ToList();
            var allLeft = Local(f, f.Drops.First(d => d.Uid == 1)).Concat(later.Where(q => q.Y < 0)).ToList();
            if (!later.Any(q => q.Y < 0)) return "nobody continued the left side";
            // the left side goes on from the cursor: no hole between the columns (<= one step)
            var colsL = allLeft.Select(q => Math.Round(q.X, 1)).Distinct().OrderBy(x => x).ToList();
            for (int i = 1; i < colsL.Count; i++) if (colsL[i] - colsL[i - 1] > 4.4) return "a hole on the left side: " + colsL[i - 1] + " -> " + colsL[i];
            if (!Logged(f, "asw lanes G corridor k=")) return "no lanes line";

            // the air threat: the retreating aircraft keeps its lane (paused), the other one is not moved onto it
            var f2 = CorridorGame(40, 40);
            var th = new AirStub(); th.P.Mode = "retreat";
            var cov = new AirCover { Mode = "cover", ShipUid = 500, ShipName = "Yorktown", Point = new GeoPt(35, 20) };
            var c2 = Make(f2);
            var steer = new SteerStub(f2);
            c2.AirGuard = th; c2.AirSteer = new AirRetreatSteer(steer, s => f2.Logs.Add("steer " + s));
            c2.Group(P("{\"op\":\"create\",\"group\":\"G\",\"uids\":[1,2]}"));
            c2.SearchCourse(P("{\"tactics\":[\"corridor\"]}"));
            Run(c2, f2, 3);
            th.Unit[1] = AirStage.Retreat; th.Cov[1] = cov;
            Run(c2, f2, 30);
            if (!c2.AirRetreatOf(1)) return "no retreat";
            if (TacticOfUid(c2, 1) != "corridor") return "the lane of the retreating aircraft was handed over: " + TacticOfUid(c2, 1);
            string lane2 = TacticOfUid(c2, 2);
            th.Unit[1] = AirStage.None;
            Run(c2, f2, 30);
            if (c2.AirRetreatOf(1)) return "still retreating";
            if (TacticOfUid(c2, 1) != "corridor" || TacticOfUid(c2, 2) != lane2) return "the lanes changed after the resume";
            return null;
        }

        private sealed class SteerStub : IAirSteerGame
        {
            private readonly AswFakeGame _f;
            public SteerStub(AswFakeGame f) { _f = f; }
            public GeoPt? Pos(int uid) { var m = _f.Mem.Find(x => x.Uid == uid); return m == null ? (GeoPt?)null : new GeoPt(m.Lat, m.Lon); }
            public string Goto(int uid, IList<GeoPt> pts, int altFt) { return _f.Goto(uid, pts, altFt); }
            public string Orbit(int uid, GeoPt center, double radiusNm, bool racetrack) { return _f.Goto(uid, new List<GeoPt> { center }, 0); }
            public int Throttle(int uid) { return 2; }
            public string SetThrottle(int uid, int step) { return null; }
            public IList<CapStep> ThrottleSteps(int uid) { return new List<CapStep> { new CapStep { Step = 1, Kn = 60 }, new CapStep { Step = 2, Kn = 90 }, new CapStep { Step = 3, Kn = 120 } }; }
            public string SetLowPreset(int uid) { return null; }
            public string Remember(int uid) { return null; }
            public string RestoreSpeedAlt(int uid) { return null; }
            public bool Evading(int uid) { return false; }
        }

        private static string MultiTactic()
        {
            // dip + corridor (or barrier): the dipping helicopter flies the dip, the buoy aircraft the buoys, one each first
            var f = new AswFakeGame { DropDelayS = 0 };
            var d = AswFakeGame.Helo(1, "air", dip: true, passive: 0); d.EnduranceS = 1e7; d.FuelFrac = 1; d.ActiveBuoys = 0; d.Vlad = 0; f.Mem.Add(d);
            for (int u = 2; u <= 4; u++) { var h = AswFakeGame.Helo(u, "air", passive: 60); h.EnduranceS = 1e7; h.FuelFrac = 1; h.ActiveBuoys = 0; h.Vlad = 0; f.Mem.Add(h); }
            var c = Make(f);
            c.Group(P("{\"op\":\"create\",\"group\":\"G\",\"uids\":[1,2,3,4]}"));
            // T-334 / ADR-0130: corridor and barrier exclude each other: dip + corridor, then dip + barrier (and both together are refused)
            if (!AswController.IsErr(c.SearchCourse(P("{\"tactics\":[\"barrier\",\"corridor\",\"dip\"]}")))) return "corridor + barrier was not refused";
            c.SearchCourse(P("{\"tactics\":[\"corridor\",\"dip\"]}"));
            if (TacticOfUid(c, 1) != "dip") return "the dipping helicopter flies " + TacticOfUid(c, 1);
            var tacs = new[] { 2, 3, 4 }.Select(u => TacticOfUid(c, u)).ToList();
            if (!tacs.All(t => t == "corridor")) return "buoy aircraft: " + string.Join(",", tacs);
            c.SearchCourse(P("{\"tactics\":[\"barrier\",\"dip\"]}"));
            if (TacticOfUid(c, 1) != "dip") return "the dipping helicopter flies " + TacticOfUid(c, 1) + " (barrier)";
            tacs = new[] { 2, 3, 4 }.Select(u => TacticOfUid(c, u)).ToList();
            if (!tacs.All(t => t == "barrier")) return "buoy aircraft (barrier): " + string.Join(",", tacs);
            // an aircraft without buoys gets no corridor
            var f2 = new AswFakeGame { DropDelayS = 0 };
            var a = AswFakeGame.Helo(1, "air", dip: true, passive: 0); a.ActiveBuoys = 0; a.Vlad = 0; f2.Mem.Add(a);
            var c2 = Make(f2);
            c2.Group(P("{\"op\":\"create\",\"group\":\"G\",\"uids\":[1]}"));
            c2.SearchCourse(P("{\"tactics\":[\"corridor\",\"dip\"]}"));
            if (TacticOfUid(c2, 1) != "dip") return "no buoys: " + TacticOfUid(c2, 1);
            // one aircraft only (dip + buoys): by its fitness, the order dip > corridor > barrier
            var f3 = new AswFakeGame { DropDelayS = 0 };
            var b = AswFakeGame.Helo(1, "air", dip: true, passive: 60); b.EnduranceS = 1e7; b.FuelFrac = 1; f3.Mem.Add(b);
            var c3 = Make(f3);
            c3.Group(P("{\"op\":\"create\",\"group\":\"G\",\"uids\":[1]}"));
            c3.SearchCourse(P("{\"tactics\":[\"corridor\",\"dip\"]}"));
            if (TacticOfUid(c3, 1) != "dip") return "one aircraft: " + TacticOfUid(c3, 1);
            // single lane mode: the old one-aircraft-per-tactic rule
            var f4 = CorridorGame(40, 40, 40);
            var c4 = new AswController(f4, new AswParams(), new AswConfig { RestoreOnLoad = "off", Loc = new AswLocParams { Lanes = "single" } });
            c4.Group(P("{\"op\":\"create\",\"group\":\"G\",\"uids\":[1,2,3]}"));
            c4.SearchCourse(P("{\"tactics\":[\"corridor\"]}"));
            int holders = new[] { 1, 2, 3 }.Count(u => TacticOfUid(c4, u) == "corridor");
            if (holders != 1) return "Lanes=single: " + holders + " holders";
            return null;
        }
    }
}
