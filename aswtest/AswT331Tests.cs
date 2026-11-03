using System;
using System.Collections.Generic;
using System.Linq;

namespace VoiceBotBridge
{
    // T-331: the waiting point of the strike attack is anchored (a circling aircraft is not a reason to order a new point: each reached point is a
    // "We're on position" message of the game), and the air retreat flies one route per move of the cover point. `aswtest t331 <name>`.
    internal static class AswT331Tests
    {
        private static readonly string[] Names = { "hold_math", "anchor_kept", "datum_moved", "drifted", "second_near", "retreat_once" };

        private static Dictionary<string, object> P(string json) { return AswT173AttackTests.P(json); }
        private static void Run(AswController c, AswFakeGame f, int s) { AswT173AttackTests.Run(c, f, s); }
        private static int Gotos(AswFakeGame f, int uid) { return f.Calls.Count(x => x == "goto:" + uid); }

        public static string Run(string only)
        {
            foreach (string n in Names)
            {
                if (only != null && only != n) continue;
                string e;
                switch (n)
                {
                    case "hold_math": e = HoldMath(); break;
                    case "anchor_kept": e = AnchorKept(); break;
                    case "datum_moved": e = DatumMoved(); break;
                    case "drifted": e = Drifted(); break;
                    case "second_near": e = SecondNear(); break;
                    default: e = AswT331RetreatTests.Run(); break;
                }
                if (e != null) return n + ": " + e;
            }
            if (only != null && Array.IndexOf(Names, only) < 0) return "unknown case " + only;
            return null;
        }

        private static string HoldMath()
        {
            var d0 = new GeoPt(35, 20);
            var air = AswMath.Offset(d0, 270, 4);
            double b = AswHoldMath.AnchorBrg(null, d0, d0, air, 0.5);
            if (Math.Abs(b - 270) > 0.5) return "fresh bearing " + b;
            var air2 = AswMath.Offset(d0, 200, 4);
            if (Math.Abs(AswHoldMath.AnchorBrg(90, d0, AswMath.Offset(d0, 0, 0.3), air2, 0.5) - 90) > 1e-9) return "kept bearing lost while the datum moved 0.3";
            if (Math.Abs(AswHoldMath.AnchorBrg(90, d0, AswMath.Offset(d0, 0, 0.6), air2, 0.5) - AswMath.BearingDeg(AswMath.Offset(d0, 0, 0.6), air2)) > 1e-6) return "no new bearing after the datum moved 0.6";
            if (!AswHoldMath.NeedRegoto(true, 0, 0, 0, 0.5, 60)) return "first";
            if (AswHoldMath.NeedRegoto(false, 600, 0.1, 1.5, 0.5, 60)) return "circling aircraft is a reason";
            if (!AswHoldMath.NeedRegoto(false, 61, 0.1, 3.0, 0.5, 60)) return "drifted aircraft after 60 s";
            if (AswHoldMath.NeedRegoto(false, 20, 0.1, 3.0, 0.5, 60)) return "drifted aircraft before 60 s";
            if (!AswHoldMath.NeedRegoto(false, 5, 0.5, 0, 0.5, 60)) return "datum moved";
            return null;
        }

        private sealed class World
        {
            public AswFakeGame F = new AswFakeGame();
            public AswController C;
            public AswContact Sub;
            public int Shooter, Second;
            public GeoPt Datum;

            public World()
            {
                var p = AswMath.Offset(new GeoPt(F.Tgt.Lat, F.Tgt.Lon), 90, 6);
                Datum = p;
                // the shooter is the nearest (1.4 nm from the datum: it flies to the drop point), the second number is 6 nm out: it waits outside the spook ring
                var far = AswMath.Offset(p, 270, 6.0); var near = AswMath.Offset(p, 270, 1.4);
                foreach (int u in new[] { 1, 2 })
                {
                    var m = AswFakeGame.Helo(u, "air"); m.Torpedoes = 2;
                    var at = u == 1 ? near : far;
                    m.Lat = at.Lat; m.Lon = at.Lon;
                    F.Mem.Add(m); F.FixPos.Add(u);
                }
                F.ShipsList.Add(new AswShipPos { Uid = 500, Name = "Yorktown", Lat = F.Tgt.Lat, Lon = F.Tgt.Lon });
                Sub = new AswContact { Id = 21, Name = "Sub #21", Lat = p.Lat, Lon = p.Lon, Source = "live", Side = "hostile", Live = true, ClassName = "Kilo", Classified = true, MapPosture = "Enemy" };
                F.Subs.Add(Sub);
                C = AswT173AttackTests.Make(F, cf => cf.Loc.ApproachMaxS = 1e9);
                C.Group(P("{\"op\":\"create\",\"group\":\"G\",\"uids\":[1,2]}"));
                C.Order(P("{\"group\":\"G\",\"order\":\"strike\",\"ref_uid\":500}"));
                Run(C, F, 90);   // the roles settle (rebalance of the better shooter)
                Shooter = C.RunShooter(21);
                Second = Shooter == 1 ? 2 : 1;
            }

            public AswMember Mem(int uid) { return F.Mem.First(m => m.Uid == uid); }

            /// <summary>The second number flies to its last ordered point and circles round it (radius nm).</summary>
            public void Circle(int uid, double radiusNm, int seconds, double phase0)
            {
                for (int i = 0; i < seconds; i++)
                {
                    List<GeoPt> r;
                    var m = Mem(uid);
                    if (F.Route.TryGetValue(uid, out r) && r.Count > 0)
                    {
                        var c = r[r.Count - 1];
                        var pos = AswMath.Offset(c, (phase0 + i * 6) % 360, radiusNm);
                        m.Lat = pos.Lat; m.Lon = pos.Lon;
                    }
                    F.Advance(1); F.Tick(C);
                }
            }
        }

        private static string Dbg(World w)
        {
            var m = w.Mem(w.Second);
            var pt = w.F.Route.ContainsKey(w.Second) && w.F.Route[w.Second].Count > 0 ? w.F.Route[w.Second].Last() : new GeoPt(0, 0);
            return "air-point " + AswMath.DistNm(new GeoPt(m.Lat, m.Lon), pt) + " air-datum " + AswMath.DistNm(new GeoPt(m.Lat, m.Lon), w.Datum) + " shooter=" + w.Shooter + " now=" + w.C.RunShooter(21) + " state=" + w.C.RunState(21) + " calls=" + string.Join(",", w.F.Calls.Where(c => c.StartsWith("goto")).ToArray()) + " second=" + w.Second + " logs: " + string.Join(" | ", w.F.Logs.Skip(Math.Max(0, w.F.Logs.Count - 8)).ToArray());
        }

        private static string Setup(out World w)
        {
            w = new World();
            if (w.Shooter == 0) return "no shooter: " + AswT173AttackTests.Dump(w.F);
            if (Gotos(w.F, w.Second) < 1) return "setup: no waiting order for the second number (calls " + string.Join(",", w.F.Calls.ToArray()) + ")";
            return null;
        }

        private static string AnchorKept()
        {
            World w; string e = Setup(out w); if (e != null) return e;
            // the helicopter is on the point and circles round it for 10 minutes: no new order
            int g0 = Gotos(w.F, w.Second);
            w.Circle(w.Second, 1.0, 600, 0);
            int added = Gotos(w.F, w.Second) - g0;
            if (added != 0) return "circling second number got " + added + " new goto orders in 10 min";
            return null;
        }

        private static string DatumMoved()
        {
            World w; string e = Setup(out w); if (e != null) return e;
            w.Circle(w.Second, 1.0, 120, 0);
            int g0 = Gotos(w.F, w.Second);
            var old = w.F.Route[w.Second].Last();
            var moved = AswMath.Offset(w.Datum, 0, 0.8);
            w.Sub.Lat = moved.Lat; w.Sub.Lon = moved.Lon;
            w.Circle(w.Second, 1.0, 20, 0);
            if (Gotos(w.F, w.Second) <= g0) return "the datum moved 0.8 nm: no new order";
            var now = w.F.Route[w.Second].Last();
            if (AswMath.DistNm(old, now) < 0.3) return "the new point did not follow the datum";
            return null;
        }

        private static string Drifted()
        {
            World w; string e = Setup(out w); if (e != null) return e;
            w.Circle(w.Second, 1.0, 30, 0);
            int g0 = Gotos(w.F, w.Second);
            var far = AswMath.Offset(w.F.Route[w.Second].Last(), 270, 4);
            var m = w.Mem(w.Second); m.Lat = far.Lat; m.Lon = far.Lon;
            // no route follow: the helicopter stays 4 nm away from the point
            Run(w.C, w.F, 70);
            if (Gotos(w.F, w.Second) <= g0) return "drifted 4 nm for 70 s: no new order";
            return null;
        }

        private static string SecondNear()
        {
            World w; string e = Setup(out w); if (e != null) return e;
            var pt = w.F.Route[w.Second].Last();
            var near = AswMath.Offset(pt, 45, 1.0);
            var m = w.Mem(w.Second); m.Lat = near.Lat; m.Lon = near.Lon;
            int g0 = Gotos(w.F, w.Second);
            Run(w.C, w.F, 300);
            if (Gotos(w.F, w.Second) != g0) return "the second number 1 nm from its point got " + (Gotos(w.F, w.Second) - g0) + " orders; " + Dbg(w);
            return null;
        }
    }
}
