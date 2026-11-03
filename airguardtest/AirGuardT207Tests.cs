using System;
using System.Collections.Generic;

namespace VoiceBotBridge
{
    // T-207: no flapping of the helicopter retreat (hysteresis of the re-hold), the tc watch, on the air_guard stand.
    internal static class AirGuardT207Tests
    {
        private static string _err;
        private static void True(string n, bool c) { if (!c && _err == null) _err = n; }
        private const double NmDeg = 1.0 / 60.0;

        public static readonly string[] Parts = { "t207_flap", "t207_resume_needs_steady", "t207_new_track", "t207_inside_radius", "t207_zone", "t207_plane", "t207_watch" };

        public static string Run(string part)
        {
            _err = null;
            try
            {
                if (part == "t207_flap") Flap();
                if (part == "t207_resume_needs_steady") NeedsSteady();
                if (part == "t207_new_track") NewTrack();
                if (part == "t207_inside_radius") InsideRadius();
                if (part == "t207_zone") Zone();
                if (part == "t207_plane") Plane();
                if (part == "t207_watch") Watch();
            }
            catch (Exception e) { return part + ": " + e.GetType().Name + ": " + e.Message + " " + e.StackTrace; }
            return _err;
        }

        // course 180 = towards the bird at the origin (the track is north of it), 0 = away
        private static AirTrack Mig(int id, double northNm, double course, double kn = 580)
        {
            return new AirTrack { Id = id, Cls = "MiG-21bis", Lat = northNm * NmDeg, Lon = 0, CourseDeg = course, SpeedKn = kn, Hostile = true, RNm = 10, RSrc = "class" };
        }

        private static AirOwn Helo(int uid) { return new AirOwn { Uid = uid, Name = "SH-60", Lat = 0, Lon = 0, SpeedKn = 90, CourseDeg = 90, Helicopter = true, Window = "asw", Group = "ASW-1" }; }

        private static AirGuardGroup Grp(params int[] uids)
        {
            var g = new AirGuardGroup { Window = "asw", Name = "ASW-1", Kind = "area" };
            g.Uids.AddRange(uids);
            return g;
        }

        private static AirGuardController Make(AirGuardFakeGame g, params AirGuardGroup[] groups)
        {
            var c = new AirGuardController(g, new AirGuardParams());
            c.RegisterGroups("asw", () => groups);
            return c;
        }

        private static void Run(AirGuardFakeGame g, AirGuardController c, double from, double to)
        {
            for (double t = from; t <= to; t += 1) { g.Now = t; c.Tick(t); }
        }

        private static bool Retreat(AirGuardController c) { return c.GroupStage("asw", "ASW-1") == AirStage.Retreat; }

        private static int Resumes(AirGuardFakeGame g) { return g.Lines.FindAll(l => l.Contains("resume grp=")).Count; }

        /// <summary>Common start: the helicopter, a track on 62 nm approaching at 580 kn (stage 2 by ETA); at t=3 it turns away and the helicopter is let go at t=63.</summary>
        private static AirGuardController Start(AirGuardFakeGame g)
        {
            g.Owns.Add(Helo(1));
            g.Tracks.Add(Mig(10, 62, 180));
            var c = Make(g, Grp(1));
            Run(g, c, 0, 2);
            True("start: stage 2 by eta", Retreat(c));
            var u = c.ForUnit(1);
            True("start: why=eta", u != null && u.Why == "eta" && !u.Receding);
            g.Tracks[0] = Mig(10, 62, 0);
            Run(g, c, 3, 70);
            True("start: let go after ClearS", !Retreat(c));
            var u2 = c.ForUnit(1);
            True("start: ForUnit.Receding", u2 != null && u2.Receding);
            True("start: resume why=receding", g.Lines.Exists(l => l.Contains("resume grp=ASW-1") && l.Contains("why=receding")));
            return c;
        }

        // the live case: the same track comes back 10 s after the resume
        private static void Flap()
        {
            var g = new AirGuardFakeGame(); var c = Start(g);
            g.Tracks[0] = Mig(10, 60, 180);   // t=73: approaching again
            Run(g, c, 71, 100);
            True("flap: stays below 2 during the pause", !Retreat(c));
            Run(g, c, 101, 142);
            True("flap: still below 2 before softAt + 60 + 20", !Retreat(c));
            Run(g, c, 143, 146);
            True("flap: back in the retreat after the steady approach", Retreat(c));
            var u = c.ForUnit(1);
            True("flap: Receding false after the climb", u != null && !u.Receding);
            // the orbit: 90 s approaching / 90 s receding
            var g2 = new AirGuardFakeGame(); g2.Owns.Add(Helo(1)); g2.Tracks.Add(Mig(10, 62, 180));
            var c2 = Make(g2, Grp(1));
            bool was = false; double lastResume = -1e9; double minGap = 1e9;
            for (double t = 0; t <= 600; t++)
            {
                bool appr = ((int)(t / 90)) % 2 == 0;
                g2.Tracks[0] = Mig(10, 62, appr ? 180 : 0);
                g2.Now = t; c2.Tick(t);
                bool now = Retreat(c2);
                if (was && !now) lastResume = t;
                if (!was && now && lastResume > 0) minGap = Math.Min(minGap, t - lastResume);
                was = now;
            }
            True("flap: orbit - at least 60 s between a resume and the next retreat (" + minGap + ")", minGap >= 60);
            True("flap: orbit - few resumes (" + Resumes(g2) + ")", Resumes(g2) <= 4 && Resumes(g2) >= 1);
        }

        // in the retreat the release needs ClearS in a row: a track that alternates every 20 s never lets go
        private static void NeedsSteady()
        {
            var g = new AirGuardFakeGame(); g.Owns.Add(Helo(1)); g.Tracks.Add(Mig(10, 62, 180));
            var c = Make(g, Grp(1));
            for (double t = 0; t <= 300; t++)
            {
                g.Tracks[0] = Mig(10, 62, ((int)(t / 20)) % 2 == 0 ? 180 : 0);
                g.Now = t; c.Tick(t);
            }
            True("steady: still in the retreat", Retreat(c));
            True("steady: no resume", Resumes(g) == 0);
        }

        // a new track is a new threat: no pause
        private static void NewTrack()
        {
            var g = new AirGuardFakeGame(); var c = Start(g);
            Run(g, c, 71, 72);
            g.Tracks.Add(Mig(11, 30, 180));
            Run(g, c, 73, 75);
            True("new track: stage 2 at once", Retreat(c));
        }

        // a released track inside R_t + PathMarginNm holds at once
        private static void InsideRadius()
        {
            var g = new AirGuardFakeGame(); var c = Start(g);
            g.Tracks[0] = Mig(10, 14, 0);   // receding, but inside 10 + 5
            Run(g, c, 71, 75);
            True("inside radius: stage 2 at once", Retreat(c));
        }

        // the zone of a helicopter group (no machine in the air): the same hysteresis
        private static void Zone()
        {
            var g = new AirGuardFakeGame(); g.Pos[100] = new GeoPt(0, 0);
            var z = new AirGuardGroup { Window = "asw", Name = "ASW-1", Kind = "area", Helicopter = true };
            z.DeckUids.Add(100);
            g.Tracks.Add(Mig(10, 62, 180));
            var c = Make(g, z);
            Run(g, c, 0, 2);
            True("zone: retreat", Retreat(c));
            g.Tracks[0] = Mig(10, 62, 0);
            Run(g, c, 3, 70);
            True("zone: let go", !Retreat(c));
            g.Tracks[0] = Mig(10, 60, 180);
            Run(g, c, 71, 130);
            True("zone: no flapping in the pause", !Retreat(c));
            Run(g, c, 131, 160);
            True("zone: retreat after the steady approach", Retreat(c));
        }

        // a plane keeps the old rules: the range holds it, not the new hysteresis
        private static void Plane()
        {
            var g = new AirGuardFakeGame();
            g.Owns.Add(new AirOwn { Uid = 1, Name = "P-3C", SpeedKn = 250, CourseDeg = 90, Window = "asw", Group = "ASW-1" });
            g.Tracks.Add(Mig(10, 30, 180, 400));
            var c = Make(g, Grp(1));
            Run(g, c, 0, 2);
            g.Tracks[0] = Mig(10, 35, 0, 400);
            Run(g, c, 3, 200);
            True("plane: stays in stage 2 (35 nm < 40 + 10)", Retreat(c));
        }

        // the tc watch runs in the tick
        private static void Watch()
        {
            var g = new AirGuardFakeGame();
            double? tc = 8;
            var c = Make(g);
            c.TimeX1 = new AirTimeX1(() => tc, () => false, () => { tc = 1; return null; }, () => g.Now, new AirGuardParams(), (n, j) => { }, s => g.Lines.Add("[AirGuard] " + s), s => { });
            Run(g, c, 0, 2);
            True("watch: no line at the start", !g.Lines.Exists(l => l.Contains("tc seen")));
            tc = 1; Run(g, c, 3, 5);
            True("watch: the change is logged once", g.Lines.FindAll(l => l.StartsWith("[AirGuard] tc seen from=8 to=1 by=other")).Count == 1);
        }
    }
}
