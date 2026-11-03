using System;
using System.Collections.Generic;

namespace VoiceBotBridge
{
    // T-192 stream A: cover hysteresis (ship, track, same command) on the air_guard stand.
    internal static class AirGuardT192Tests
    {
        private static string _err;
        private static void True(string n, bool c) { if (!c && _err == null) _err = n; }
        private const double NmDeg = 1.0 / 60.0;

        public static string Run(string part)
        {
            _err = null;
            try
            {
                if (part == "t192_keep_ship") KeepShip();
                if (part == "t192_same_cover") SameCover();
                if (part == "t192_keep_track") KeepTrack();
                if (part == "t192_cover_for") CoverFor();
                if (part == "t195_held_not_threat") HeldNotThreat();
            }
            catch (Exception e) { return part + ": " + e.GetType().Name + ": " + e.Message + " " + e.StackTrace; }
            return _err;
        }

        private static AirTrack Mig(int id, double northNm, double eastNm, double course, double kn = 300, double rnm = 10)
        {
            return new AirTrack { Id = id, Cls = "MiG-21bis", Lat = northNm * NmDeg, Lon = eastNm * NmDeg, CourseDeg = course, SpeedKn = kn, Hostile = true, RNm = rnm, RSrc = "class" };
        }

        private static AirSam Ship(int uid, string name, double northNm, double eastNm)
        {
            return new AirSam { Uid = uid, Name = name, Lat = northNm * NmDeg, Lon = eastNm * NmDeg, RSamNm = 24 };
        }

        private static double TimeS(AirOwn o, AirCover c, double kn) { return AswMath.DistNm(new GeoPt(o.Lat, o.Lon), c.Point) / kn * 3600.0; }

        private static void KeepShip()
        {
            var p = new AirGuardParams();
            var bird = new AirOwn { Uid = 7, Lat = 0, Lon = 0, SpeedKn = 100, Helicopter = true };
            var thr = Mig(1, 200, 0, 180, 0);
            var a = Ship(10, "A", 0, 17); var b = Ship(11, "B", 0, -16);
            var both = new List<AirSam> { a, b };
            var onlyA = AirGuardMath.Cover(bird, thr, new List<AirSam> { a }, 100, p); var onlyB = AirGuardMath.Cover(bird, thr, new List<AirSam> { b }, 100, p);
            double tA = TimeS(bird, onlyA, 100), tB = TimeS(bird, onlyB, 100);
            True("setup: B faster by < the threshold", tB < tA && tA - tB < Math.Max(p.CoverKeepS, p.CoverKeepFrac * tA));
            True("no hold -> faster ship B", AirGuardMath.Cover(bird, thr, both, 100, p).ShipUid == 11);
            True("hold A: gain below the threshold -> A", AirGuardMath.Cover(bird, thr, both, 100, p, 10).ShipUid == 10);
            True("hold B stays B", AirGuardMath.Cover(bird, thr, both, 100, p, 11).ShipUid == 11);
            // a much closer B wins even against the held A
            var b2 = Ship(11, "B", 0, -8);
            True("B much faster -> B", AirGuardMath.Cover(bird, thr, new List<AirSam> { a, b2 }, 100, p, 10).ShipUid == 11);
            // safety before hysteresis: threat close to the path to A
            var thrNear = Mig(2, -10, 17, 0, 0, 12);
            var cr = AirGuardMath.Cover(bird, thrNear, both, 100, p, 10);
            True("held A risky, B safe -> B", cr.ShipUid == 11 && cr.Note == null);
            // LandOrCover passes the hold through
            True("LandOrCover keeps the ship", AirGuardMath.LandOrCover(bird, thr, both, 100, 0, p, 10).ShipUid == 10);
            True("LandOrCover without hold", AirGuardMath.LandOrCover(bird, thr, both, 100, 0, p).ShipUid == 11);
        }

        private static void SameCover()
        {
            var p = new AirGuardParams();
            AirCover Mk(string mode, int ship, double northNm, string note) { return new AirCover { Mode = mode, ShipUid = ship, ShipName = "S", Point = new GeoPt(northNm * NmDeg, 0), Note = note }; }
            var a = Mk("cover", 1, 0, null);
            True("same: 1.5 nm", AirGuardMath.SameCover(a, Mk("cover", 1, 1.5, null), p));
            True("not same: 2.5 nm", !AirGuardMath.SameCover(a, Mk("cover", 1, 2.5, null), p));
            True("not same: other ship", !AirGuardMath.SameCover(a, Mk("cover", 2, 0, null), p));
            True("not same: other mode", !AirGuardMath.SameCover(a, Mk("land", 1, 0, null), p));
            True("not same: other note", !AirGuardMath.SameCover(a, Mk("cover", 1, 0, "cover_path_risk"), p));
            True("not same: null", !AirGuardMath.SameCover(a, null, p) && !AirGuardMath.SameCover(null, a, p));
        }

        private static void KeepTrack()
        {
            var p = new AirGuardParams();
            True("keep: 30 -> 28", AirGuardMath.KeepCoverTrack(30, 28, p));
            True("switch: 30 -> 24", !AirGuardMath.KeepCoverTrack(30, 24, p));
        }

        private static void CoverFor()
        {
            var g = new AirGuardFakeGame();
            g.Sams.Add(Ship(100, "Yorktown", 0, -15)); g.Sams.Add(Ship(101, "Perry", -15, 0));
            g.Owns.Add(new AirOwn { Uid = 1, Name = "Ka-27", Lat = 0, Lon = 0, SpeedKn = 100, CourseDeg = 270, Helicopter = true, Window = "asw", Group = "ASW-2" });
            var grp = new AirGuardGroup { Window = "asw", Name = "ASW-2", Kind = "area" }; grp.Uids.Add(1);
            var c = new AirGuardController(g, new AirGuardParams());
            c.RegisterGroups("asw", () => new List<AirGuardGroup> { grp });
            int jumps = 0; AirCover prev = null; int ship0 = 0; bool shipChanged = false;
            for (int i = 0; i < 120; i++)
            {
                double t = i * 2; g.Now = t;
                double s = i % 2 == 0 ? 1 : -1;
                g.Tracks.Clear();
                g.Tracks.Add(Mig(10, 40 - s, 0, 180)); g.Tracks.Add(Mig(11, 0, 40 + s, 270));
                c.Tick(t);
                var cv = c.CoverFor(1);
                if (cv == null) continue;
                if (ship0 == 0) ship0 = cv.ShipUid; else if (cv.ShipUid != ship0) shipChanged = true;
                if (prev != null && !AirGuardMath.SameCover(prev, cv, new AirGuardParams())) jumps++;
                prev = cv;
            }
            int lines = 0; foreach (var l in g.Lines) if (l.Contains("cover uid=")) lines++;
            True("retreat reached (cover given)", prev != null);
            True("cover log lines <= 1, got " + lines, lines <= 1);
            True("ship does not change", !shipChanged);
            True("point shifts <= CoverMoveNm between calls, jumps " + jumps, jumps == 0);
            // one track gets 15 nm closer: the cover is rebuilt from it
            int before = lines;
            for (int i = 120; i < 150; i++)
            {
                double t = i * 2; g.Now = t;
                g.Tracks.Clear();
                g.Tracks.Add(Mig(10, 40, 0, 180)); g.Tracks.Add(Mig(11, 0, 25, 270));
                c.Tick(t); c.CoverFor(1);
            }
            var ut = c.ForUnit(1);
            True("closer track drives the stage", ut != null && ut.TrackId == 11);
            True("cover rebuilt from the closer track: log <= 2", CountLines(g) <= 2);
        }

        /// <summary>T-195 item 4: the held cover track that stopped being a threat (not hostile any more) must not be held.</summary>
        private static void HeldNotThreat()
        {
            foreach (var mode in new[] { "friendly", "unknown_slow" })
            {
                var hold = HeldCoverPoint(mode, true);
                var fresh = HeldCoverPoint(mode, false);
                True(mode + ": cover given", hold != null && fresh != null);
                if (hold == null || fresh == null) continue;
                double d = AswMath.DistNm(hold.Value, fresh.Value);
                True(mode + ": cover rebuilt from the real threat (same point as a fresh one), d=" + d.ToString("0.0"), d < 0.5);
            }
        }

        // Track 10 (north) is the worst and gets held; then track 11 (east) becomes the worst by a small gap while track 10 stops being a threat.
        // withHistory=false: a controller that never saw track 10 (the expected result).
        private static GeoPt? HeldCoverPoint(string mode, bool withHistory)
        {
            var g = new AirGuardFakeGame();
            g.Sams.Add(Ship(100, "Yorktown", 0, -15)); g.Sams.Add(Ship(101, "Perry", -15, 0));
            g.Owns.Add(new AirOwn { Uid = 1, Name = "Ka-27", Lat = 0, Lon = 0, SpeedKn = 100, CourseDeg = 270, Helicopter = true, Window = "asw", Group = "ASW-2" });
            var grp = new AirGuardGroup { Window = "asw", Name = "ASW-2", Kind = "area" }; grp.Uids.Add(1);
            var c = new AirGuardController(g, new AirGuardParams());
            c.RegisterGroups("asw", () => new List<AirGuardGroup> { grp });
            double t = 0;
            if (withHistory)
            {
                for (int i = 0; i < 10; i++)
                {
                    t = i * 2; g.Now = t; g.Tracks.Clear();
                    g.Tracks.Add(Mig(10, 36, 0, 180)); g.Tracks.Add(Mig(11, 0, 38, 270));
                    c.Tick(t); c.CoverFor(1);
                }
            }
            for (int i = 10; i < 20; i++)
            {
                t = i * 2; g.Now = t; g.Tracks.Clear();
                var old = Mig(10, 36, 0, 180);
                if (mode == "friendly") old.Hostile = false;
                else { old.Hostile = false; old.Unknown = true; old.SpeedKn = 100; }   // unknown and slow: not a retreat threat
                var nw = Mig(11, 0, 35.5, 270);
                if (withHistory) g.Tracks.Add(old);
                g.Tracks.Add(nw);
                c.Tick(t);
            }
            var cv = c.CoverFor(1);
            return cv == null ? (GeoPt?)null : cv.Point;
        }

        private static int CountLines(AirGuardFakeGame g) { int n = 0; foreach (var l in g.Lines) if (l.Contains("cover uid=")) n++; return n; }
    }
}
