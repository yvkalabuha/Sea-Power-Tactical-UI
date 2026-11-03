using System;
using System.Collections.Generic;
using System.Linq;

namespace VoiceBotBridge
{
    // T-176 C11 (cross-spook, lost-ring): the cross outside the spook radius, the active buoy ahead, the dip abeam; the lost-contact ring.
    internal static partial class AswT176Tests
    {
        private static AswContact SubAhead(AswFakeGame f, int id, double eastNm, double courseDeg, double kn)
        {
            var p = AswMath.Offset(new GeoPt(f.Tgt.Lat, f.Tgt.Lon), 90, eastNm);
            var s = new AswContact { Id = id, Name = "Sub #" + id, Lat = p.Lat, Lon = p.Lon, Source = "live", Side = "hostile", Live = true, ClassName = "Kilo", Classified = true, MapPosture = "Enemy", CourseDeg = courseDeg, SpeedKn = kn };
            f.Subs.Add(s);
            return s;
        }

        private static string CrossSpook()
        {
            // SSQ-53 (R 2.70): the cross stands at r = 2.3 (the spook radius) around the estimate moved along the course; the active buoy ahead; the dip abeam
            var f = new AswFakeGame { DropDelayS = 0 };
            var b = AswFakeGame.Helo(1, "air", passive: 40); b.EnduranceS = 1e7; b.FuelFrac = 1; b.Vlad = 0; f.Mem.Add(b);
            var d = AswFakeGame.Helo(2, "air", dip: true, passive: 0); d.EnduranceS = 1e7; d.FuelFrac = 1; d.Vlad = 0; d.ActiveBuoys = 0; f.Mem.Add(d);
            var c = Make(f);
            c.Group(P("{\"op\":\"create\",\"group\":\"G\",\"uids\":[1,2]}"));
            c.SearchCourse(P("{\"tactics\":[\"dip\"]}"));   // the buoy aircraft has no tactic: it is a reserve and localizes
            var sub = SubAhead(f, 7, 6, 90, 6);
            Run(c, f, 12);
            var cross = f.Drops.FirstOrDefault(x => x.Uid == 1 && x.Ammo == "usn_ssq-53");
            if (cross == null || cross.Pts.Count != 4) return "no cross " + (cross == null ? 0 : cross.Pts.Count);
            var subPos = new GeoPt(sub.Lat, sub.Lon);
            double cx = cross.Pts.Average(p => AswMath.ToLocal(subPos, 0, p).X), cy = cross.Pts.Average(p => AswMath.ToLocal(subPos, 0, p).Y);
            if (cy < 0.1) return "the cross is not moved along the course: shift east " + cy;
            foreach (var p in cross.Pts)
            {
                var q = AswMath.ToLocal(subPos, 0, p);
                double rr = Math.Sqrt(Math.Pow(q.X - cx, 2) + Math.Pow(q.Y - cy, 2));
                if (Math.Abs(rr - 2.3) > 0.05) return "cross radius " + rr + " (2.3)";
            }
            // the active buoy: ahead (east) of the cross centre at r
            var act = f.Drops.FirstOrDefault(x => x.Uid == 1 && x.Ammo == "usn_ssq-62");
            if (act == null) return "no active buoy";
            var aq = AswMath.ToLocal(subPos, 0, act.Pts[0]);
            if (aq.Y < cy + 2.0 || Math.Abs(aq.X - cx) > 0.3) return "the active buoy is not ahead: " + aq.X + "," + aq.Y;
            // the dip: abeam of the course at the spook radius, the sides alternate with every dip
            var abeam = new Func<List<NmPt>>(() => f.Dips.Select(x => AswMath.ToLocal(subPos, 0, x[0])).Where(q => Math.Abs(q.X) > 1.5).ToList());   // the points of the chain on the axis have X = 0
            if (abeam().Count == 0) return "no dip abeam";
            var dq = abeam()[0];
            if (Math.Abs(Math.Abs(dq.X) - 2.3) > 0.5) return "dip not abeam: " + dq.X + "," + dq.Y;
            Run(c, f, 400);
            if (abeam().Count < 2) return "no second dip";
            var dq2 = abeam()[1];
            if (dq.X * dq2.X >= 0) return "the sides did not alternate: " + dq.X + " / " + dq2.X;

            // SSQ-41 (R ~ 1.08): the cross cannot stand outside the spook radius: r = 0.8R, said in the log
            var f2 = new AswFakeGame { DropDelayS = 0 };
            var b2 = AswFakeGame.Helo(1, "air", passive: 40); b2.EnduranceS = 1e7; b2.FuelFrac = 1; b2.Vlad = 0; b2.ActiveBuoys = 0; f2.Mem.Add(b2);
            f2.TypesOf[1] = new List<BuoyType> { new BuoyType { Ammo = "usn_ssq-41", Sensor = "SSQ-41_Sonar", PassiveKm = 4, LifeS = 3600 } };
            var c2 = Make(f2);
            c2.Group(P("{\"op\":\"create\",\"group\":\"G\",\"uids\":[1]}"));
            c2.SearchCourse(P("{\"tactics\":[\"dip\"]}"));
            SubAhead(f2, 7, 6, 90, 0);
            Run(c2, f2, 12);
            if (!Logged(f2, "cross inside the spook radius")) return "no spook line";
            var cr2 = f2.Drops.FirstOrDefault(x => x.Ammo == "usn_ssq-41");
            if (cr2 == null) return "no cross SSQ-41";

            // the cross waits while a shooter is about to attack the track, and goes after the torpedo is in the water
            var f3 = new AswFakeGame { DropDelayS = 0, ReleaseDelayS = 5, SimTorps = true };
            var b3 = AswFakeGame.Helo(1, "air", passive: 40); b3.EnduranceS = 1e7; b3.FuelFrac = 1; b3.Vlad = 0; b3.ActiveBuoys = 0; f3.Mem.Add(b3); f3.FixPos.Add(1);
            var sh = AswFakeGame.Helo(3, "air"); sh.Torpedoes = 2; f3.Mem.Add(sh); f3.FixPos.Add(3);
            var c3 = Make(f3);
            c3.Group(P("{\"op\":\"create\",\"group\":\"G\",\"uids\":[1]}"));
            c3.Group(P("{\"op\":\"create\",\"group\":\"K\",\"uids\":[3]}"));
            c3.SearchCourse(P("{\"group\":\"G\",\"tactics\":[\"dip\"]}"));
            var s3 = SubAhead(f3, 9, 6, 90, 0);
            var sp = new GeoPt(s3.Lat, s3.Lon);
            var shp = AswMath.Offset(sp, 270, 5); sh.Lat = shp.Lat; sh.Lon = shp.Lon;
            c3.StartStrikeRun("K", 9, 3, "shooter");
            Run(c3, f3, 3);
            if (!Logged(f3, "cross waits for the attack")) return "the cross did not wait";
            if (f3.Drops.Any(x => x.Uid == 1 && x.Ammo == "usn_ssq-53")) return "the cross was laid before the attack";
            return null;
        }

        private static string LostRing()
        {
            var f = new AswFakeGame { DropDelayS = 0 };
            var b = AswFakeGame.Helo(1, "air", passive: 200); b.EnduranceS = 1e7; b.FuelFrac = 1; b.Vlad = 0; b.ActiveBuoys = 0; f.Mem.Add(b);
            var d = AswFakeGame.Helo(2, "air", dip: true, passive: 0); d.EnduranceS = 1e7; d.FuelFrac = 1; d.Vlad = 0; d.ActiveBuoys = 0; f.Mem.Add(d);
            var c = Make(f);
            c.Group(P("{\"op\":\"create\",\"group\":\"G\",\"uids\":[1,2]}"));
            c.SearchCourse(P("{\"tactics\":[\"dip\"]}"));
            var sub = SubAhead(f, 7, 6, 90, 10);
            Run(c, f, 20);
            var last = new GeoPt(sub.Lat, sub.Lon);
            int before = f.Drops.Count;
            sub.Live = false; f.Subs.Remove(sub);   // the track is gone from the list
            Run(c, f, 320);   // LostAfterS = 300
            if (!f.HasEvent("asw_contact_lost")) return "no asw_contact_lost";
            if (!c.RingActive(7)) return "no ring after the loss";
            var g = c.View().Groups[0];
            var ringLine = g.Lines.FirstOrDefault(l => l.Kind == "lost_ring");
            if (ringLine == null) return "no lost_ring line";
            // R_FOC = v (lost + fly) with v = 10 kn: about 0.8 nm after 300 s; the ring is moved along the course (east) by about v t / 2
            double cxs = ringLine.Points.Average(p => AswMath.ToLocal(last, 0, p).Y);
            if (cxs < 0.2) return "the ring is not moved along the course: " + cxs;
            double r1 = ringLine.Points.Max(p => AswMath.DistNm(last, p));
            if (r1 > 8) return "the first ring is too big: " + r1;
            if (!Logged(f, "ring r=")) return "no ring line";
            // it grows every LostExpandS (600 s) until LostMaxNm (12): 10 kn * t / 3600 = 12 -> 4320 s
            Run(c, f, 650);
            double r2 = c.View().Groups[0].Lines.First(l => l.Kind == "lost_ring").Points.Max(p => AswMath.DistNm(last, p));
            if (r2 <= r1 + 0.5) return "the ring did not grow: " + r1 + " -> " + r2;
            if (f.Drops.Count <= before) return "no ring buoys after the ring grew (the first ring lies inside the cross)";
            // the dipping sonar goes over the ring points
            if (f.Dips.Count == 0) return "no dips over the ring";
            Run(c, f, 5000);
            if (c.RingActive(7)) return "the ring never ends";
            if (!f.HasEvent("asw_contact_ring_end", "max")) return "no ring end event";
            if (c.View().Groups[0].Lines.Any(l => l.Kind == "lost_ring")) return "the ring line stays";

            // the track is back: the ring ends
            var f2 = new AswFakeGame { DropDelayS = 0 };
            var b2 = AswFakeGame.Helo(1, "air", passive: 100); b2.EnduranceS = 1e7; b2.FuelFrac = 1; b2.Vlad = 0; b2.ActiveBuoys = 0; f2.Mem.Add(b2);
            var c2 = Make(f2);
            c2.Group(P("{\"op\":\"create\",\"group\":\"G\",\"uids\":[1]}"));
            c2.SearchCourse(P("{\"tactics\":[\"dip\"]}"));
            var s2 = SubAhead(f2, 7, 6, 90, 5);
            Run(c2, f2, 20);
            s2.Live = false;
            Run(c2, f2, 330);
            if (!c2.RingActive(7)) return "ring 2";
            s2.Live = true;
            Run(c2, f2, 5);
            if (c2.RingActive(7)) return "the ring goes on after the track is back";
            return null;
        }
    }
}
