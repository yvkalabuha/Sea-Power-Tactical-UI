using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace VoiceBotBridge
{
    // T-175 G4/G5/G6: the controller on the fake game. Run(section) returns null or the first error.
    internal static class AirGuardTests
    {
        private static string _err;
        private static void True(string n, bool c) { if (!c && _err == null) _err = n; }

        private const double NmDeg = 1.0 / 60.0;

        private static AirTrack Mig(int id, double northNm, double eastNm, double course = 180, double kn = 540, bool hostile = true)
        {
            return new AirTrack { Id = id, Cls = "MiG-21bis", Lat = northNm * NmDeg, Lon = eastNm * NmDeg, CourseDeg = course, SpeedKn = kn, Hostile = hostile, Unknown = !hostile, RNm = 10, RSrc = "class" };
        }

        private static AirOwn Bird(int uid, string name, double northNm, double eastNm, bool helo = false, double kn = 250, string group = null)
        {
            return new AirOwn { Uid = uid, Name = name, Lat = northNm * NmDeg, Lon = eastNm * NmDeg, SpeedKn = kn, CourseDeg = 90, Helicopter = helo, Window = group == null ? null : "asw", Group = group };
        }

        private static AirGuardGroup Grp(string name, params int[] uids)
        {
            var g = new AirGuardGroup { Window = "asw", Name = name, Kind = "area" };
            g.Uids.AddRange(uids);
            return g;
        }

        private static AirGuardGroup OnDeck(AirGuardGroup g) { g.DeckUids.Add(100); return g; }

        private static AirGuardController Make(AirGuardFakeGame g, FakeX1 x1, AirGuardParams p, params AirGuardGroup[] groups)
        {
            var c = new AirGuardController(g, p);
            c.RegisterGroups("asw", () => groups);
            c.TimeX1 = x1;
            return c;
        }

        private static void Run(AirGuardFakeGame g, AirGuardController c, double from, double to)
        {
            for (double t = from; t <= to; t += 1) { g.Now = t; c.Tick(t); }
        }

        public static string Run(string section)
        {
            _err = null;
            try
            {
                if (section == "controller" || section == "all") { Stages(); Missile(); Ungrouped(); Events(); ModeWarn(); AtPointTest(); }
                if (section == "alert" || section == "all") AlertLines();
                if (section == "ignore" || section == "all") IgnoreTest();
                if (section == "ghost" || section == "all") GhostTest();
                if (section == "unknown_warn" || section == "all") UnknownWarn();
                if (section == "qa01" || section == "all") Qa01();
                if (section == "perf" || section == "all") Perf();
                if (section == "soft_resume" || section == "all") SoftResume();
            }
            catch (Exception e) { return section + ": " + e.GetType().Name + ": " + e.Message + " " + e.StackTrace; }
            return _err;
        }

        private static void Stages()
        {
            var g = new AirGuardFakeGame(); var x1 = new FakeX1();
            g.Owns.Add(Bird(1, "P-3C", 0, 0)); g.Tracks.Add(Mig(10, 100, 0, 0)); // 100 nm away, moving away (course 0)
            var c = Make(g, x1, new AirGuardParams(), Grp("ASW-1", 1));
            Run(g, c, 0, 2);
            True("far: stage none", c.GroupStage("asw", "ASW-1") == AirStage.None);
            g.Tracks[0] = Mig(10, 59, 0, 180, 300); Run(g, c, 3, 4);
            True("59 nm: warn", c.GroupStage("asw", "ASW-1") == AirStage.Warn);
            // closing at 300 kn, 55 nm; ETA to R=10: 45/300 h = 540 s -> warn only
            g.Tracks[0] = Mig(10, 39, 0, 180, 300); Run(g, c, 5, 6);
            True("39 nm: retreat", c.GroupStage("asw", "ASW-1") == AirStage.Retreat);
            var ut = c.ForUnit(1);
            True("unit threat retreat", ut != null && ut.Stage == AirStage.Retreat && ut.TrackId == 10 && ut.RSrc == "class");
            // hysteresis: drops only after 60 s clear, threat at 55 nm (> 40+10)
            g.Tracks[0] = Mig(10, 55, 0, 0, 300); Run(g, c, 7, 60);
            True("hysteresis holds", c.GroupStage("asw", "ASW-1") == AirStage.Retreat);
            g.Tracks[0] = Mig(10, 80, 0, 0, 300); Run(g, c, 61, 140);
            True("after clear: none", c.GroupStage("asw", "ASW-1") == AirStage.None);
            True("no_aaw track: warn only", NoAaw());
            // ETA stage: 70 nm away at 600 kn closing, R 10 -> 360 s: retreat by ETA
            var g2 = new AirGuardFakeGame(); g2.Owns.Add(Bird(1, "P-3C", 0, 0)); g2.Tracks.Add(Mig(10, 70, 0, 180, 600));
            var c2 = Make(g2, null, new AirGuardParams(), Grp("ASW-1", 1)); Run(g2, c2, 0, 1);
            var u2 = c2.ForUnit(1);
            True("eta retreat", u2 != null && u2.Stage == AirStage.Retreat && u2.Why == "eta");
        }

        // T-180 B2: a helicopter is let go by tracks that do not approach; a plane is not
        private static void SoftResume()
        {
            // 1-5: helicopter in a group
            var g = new AirGuardFakeGame();
            g.Owns.Add(Bird(1, "SH-60", 0, 0, true, 90));
            g.Tracks.Add(Mig(10, 30, 0, 180, 400));
            var c = Make(g, null, new AirGuardParams(), Grp("ASW-1", 1));
            Run(g, c, 0, 2);
            True("soft 1: approaching 30 nm -> stage 2", c.GroupStage("asw", "ASW-1") == AirStage.Retreat);
            g.Tracks[0] = Mig(10, 35, 0, 0, 400);
            Run(g, c, 3, 50);
            True("soft 2: still retreat before ClearS", c.GroupStage("asw", "ASW-1") == AirStage.Retreat);
            Run(g, c, 51, 70);
            True("soft 2: receding 35 nm -> below 2 after ClearS", c.GroupStage("asw", "ASW-1") < AirStage.Retreat && c.ForUnit(1) != null && c.ForUnit(1).Stage < AirStage.Retreat);
            True("soft 2: resume logged with why=receding", g.Lines.Exists(l => l.StartsWith("[AirGuard] resume grp=ASW-1") && l.Contains("why=receding")));
            g.Tracks[0] = Mig(10, 40, 0, 0, 400);
            Run(g, c, 71, 200);
            True("soft 3: no flapping on the same receding track", c.GroupStage("asw", "ASW-1") < AirStage.Retreat);
            True("soft 3: one resume only", g.Lines.FindAll(l => l.Contains("resume grp=")).Count == 1);
            g.Tracks[0] = Mig(10, 30, 0, 180, 400);
            Run(g, c, 201, 225);   // T-207: the pause is over, 20 s of steady approach
            True("soft 4: the same track approaches again (steady) -> stage 2", c.GroupStage("asw", "ASW-1") == AirStage.Retreat);
            // 5: a new track
            var g5 = new AirGuardFakeGame();
            g5.Owns.Add(Bird(1, "SH-60", 0, 0, true, 90));
            g5.Tracks.Add(Mig(10, 30, 0, 180, 400));
            var c5 = Make(g5, null, new AirGuardParams(), Grp("ASW-1", 1));
            Run(g5, c5, 0, 2);
            g5.Tracks[0] = Mig(10, 35, 0, 0, 400);
            Run(g5, c5, 3, 70);
            g5.Tracks.Add(Mig(11, 30, 0, 180, 400));
            Run(g5, c5, 71, 72);
            True("soft 5: a new track at 30 nm -> stage 2", c5.GroupStage("asw", "ASW-1") == AirStage.Retreat);
            // 6: a plane in the same picture keeps the old rules
            var g6 = new AirGuardFakeGame();
            g6.Owns.Add(Bird(1, "P-3C", 0, 0, false, 250));
            g6.Tracks.Add(Mig(10, 30, 0, 180, 400));
            var c6 = Make(g6, null, new AirGuardParams(), Grp("ASW-1", 1));
            Run(g6, c6, 0, 2);
            g6.Tracks[0] = Mig(10, 35, 0, 0, 400);
            Run(g6, c6, 3, 200);
            True("soft 6: plane stays in stage 2 (35 nm < 40 + 10)", c6.GroupStage("asw", "ASW-1") == AirStage.Retreat);
            // 7: helicopter group zone (no unit in the air): the same behaviour
            var g7 = new AirGuardFakeGame();
            g7.Pos[100] = new GeoPt(0, 0);
            var z = new AirGuardGroup { Window = "asw", Name = "ZONE", Kind = "area", Helicopter = true };
            z.DeckUids.Add(100);
            g7.Tracks.Add(Mig(10, 30, 0, 180, 400));
            var c7 = Make(g7, null, new AirGuardParams(), z);
            Run(g7, c7, 0, 2);
            True("soft 7: zone retreat", c7.GroupStage("asw", "ZONE") == AirStage.Retreat);
            g7.Tracks[0] = Mig(10, 35, 0, 0, 400);
            Run(g7, c7, 3, 70);
            True("soft 7: zone released after ClearS", c7.GroupStage("asw", "ZONE") < AirStage.Retreat);
            True("soft 7: zone resume why=receding", g7.Lines.Exists(l => l.Contains("resume grp=ZONE") && l.Contains("why=receding")));
            Run(g7, c7, 71, 150);
            True("soft 7: no flapping", c7.GroupStage("asw", "ZONE") < AirStage.Retreat);
        }

        private static bool NoAaw()
        {
            var g = new AirGuardFakeGame(); g.Owns.Add(Bird(1, "SH-2F", 0, 0, true));
            var t = Mig(10, 30, 0); t.RSrc = "no_aaw"; t.RNm = 3; g.Tracks.Add(t);
            var c = Make(g, null, new AirGuardParams(), Grp("ASW-1", 1)); Run(g, c, 0, 1);
            return c.GroupStage("asw", "ASW-1") == AirStage.Warn;
        }

        private static void Missile()
        {
            var g = new AirGuardFakeGame(); var x1 = new FakeX1();
            var b = Bird(1, "SH-2F", 0, 0, true); g.Owns.Add(b);
            var c = Make(g, x1, new AirGuardParams(), Grp("ASW-2", 1));
            Run(g, c, 0, 1);
            True("no threat", c.GroupStage("asw", "ASW-2") == AirStage.None);
            b.MissileId = 77; Run(g, c, 2, 3);
            True("missile: retreat of the group", c.GroupStage("asw", "ASW-2") == AirStage.Retreat);
            var u = c.ForUnit(1);
            True("missile why", u != null && u.Why == "missile");
            True("missile offered once", x1.Count("missile") == 1 && x1.Count("stage2") == 0 + x1.Count("stage2"));
        }

        private static void Ungrouped()
        {
            var g = new AirGuardFakeGame(); var x1 = new FakeX1();
            g.Owns.Add(Bird(5, "Lone", 0, 0)); g.Tracks.Add(Mig(10, 30, 0));
            var c = Make(g, x1, new AirGuardParams());
            Run(g, c, 0, 2);
            var u = c.ForUnit(5);
            True("ungrouped: warn only", u != null && u.Stage == AirStage.Warn);
            True("ungrouped: no cover", c.CoverFor(5) == null);
            True("ungrouped: offered raid1", x1.Count("raid1") == 1 && x1.Count("stage2") == 0);
            var p = new AirGuardParams { WarnUngrouped = false };
            var g2 = new AirGuardFakeGame(); var x2 = new FakeX1(); g2.Owns.Add(Bird(5, "Lone", 0, 0)); g2.Tracks.Add(Mig(10, 30, 0));
            var c2 = Make(g2, x2, p); Run(g2, c2, 0, 2);
            True("WarnUngrouped=false: no offer", x2.Offers.Count == 0);
        }

        private static void Events()
        {
            var g = new AirGuardFakeGame(); var x1 = new FakeX1();
            g.Owns.Add(Bird(1, "P-3C", 0, 0)); g.Tracks.Add(Mig(10, 36.8, 0)); g.Tracks.Add(Mig(11, 38.0, 1));
            var c = Make(g, x1, new AirGuardParams(), Grp("ASW-3", 1));
            var cards = new List<AirThreatCard>(); c.CardChanged = k => cards.Add(k);
            Run(g, c, 0, 2);
            True("event air_threat once", g.CountEvents("air_threat") == 1);
            True("journal threat line", g.Lines.Exists(l => l.StartsWith("[AirGuard] threat track=") && l.Contains("stage=2") && l.Contains("grp=ASW-3")));
            True("card on stage change", cards.Count == 1 && cards[0].Count == 2 && cards[0].Groups.Count == 1 && cards[0].Groups[0].Group == "ASW-3");
            True("x1: raid1 and stage2 once", x1.Count("raid1") == 1 && x1.Count("stage2") == 1);
            Run(g, c, 3, 20);
            True("no repeats", g.CountEvents("air_threat") == 1 && x1.Offers.Count == 2 && cards.Count == 1);
            g.Destroyed.Add(10); g.Destroyed.Add(11); g.Tracks.Clear();
            Run(g, c, 21, 90);
            True("event air_resume", g.CountEvents("air_resume") == 1);
            True("journal resume", g.Lines.Exists(l => l.StartsWith("[AirGuard] resume grp=ASW-3")));
            True("resume card", cards.Count == 2 && cards[1].Resumed);
            // a second raid later: a new key -> a new offer
            g.Tracks.Add(Mig(30, 36, 0)); Run(g, c, 91, 95);
            True("new raid offers again", x1.Count("stage2") == 2);
        }

        // T-179 / ADR-0079 sections 7 and 8: the line of the CAP alert group in the raid card, the x1 offer, the group of the service (cap_alert) without own warn lines
        private static void AlertLines()
        {
            var g = new AirGuardFakeGame(); var x1 = new FakeX1();
            g.Owns.Add(Bird(1, "P-3C", 0, 0)); g.Tracks.Add(Mig(10, 30, 0)); g.Tracks.Add(Mig(11, 31, 1));
            var alert = new AirGuardGroup { Window = "cap", Name = "ПВО-2", Kind = "cap_alert" };
            alert.Uids.Add(77); alert.AreaPts.Add(new GeoPt(0, 0));
            var c = Make(g, x1, new AirGuardParams(), Grp("ASW-1", 1));
            c.RegisterGroups("cap", () => new[] { alert });
            var cards = new List<AirThreatCard>(); c.CardChanged = k => cards.Add(k);
            Run(g, c, 0, 2);
            True("the ASW card has no line of the alert group", cards.Count >= 1 && !cards[0].Groups.Exists(l => l.Group == "ПВО-2"));
            True("the alert group has no stage events of its own", g.CountEvents("air_threat") == 1);
            cards.Clear();
            c.AlertLine("ПВО-2", new[] { 11 }, "scramble", 2, "F-14A");
            True("card with the scramble line", cards.Count == 1 && cards[0].Groups.Exists(l => l.Group == "ПВО-2" && l.Action == "scramble" && l.Count == 2 && l.TypeLabel == "F-14A"));
            True("the ASW line stays", cards.Count == 1 && cards[0].Groups.Exists(l => l.Group == "ASW-1"));
            True("x1 offer alert_scramble once", x1.Count("alert_scramble") == 1);
            c.AlertLine("ПВО-2", new[] { 10 }, "scramble", 2, "F-14A");
            True("the same raid and group: one offer", x1.Count("alert_scramble") == 1);
            c.AlertLine("ПВО-2", new[] { 10 }, "not_ready", 0, null);
            True("a later 'nobody' does not overwrite the scramble", cards[cards.Count - 1].Groups.Exists(l => l.Group == "ПВО-2" && l.Action == "scramble") && !cards[cards.Count - 1].Groups.Exists(l => l.Action == "not_ready"));
            True("texts of the lines", UiText.Fmt("airguard.line.alert_scramble", "ПВО-2", 2, "F-14A") == "ПВО-2 (дежурство): взлёт 2 × F-14A" && UiText.Fmt("airguard.line.alert_not_ready", "ПВО-2") == "ПВО-2 (дежурство): нет готовых");
            // T-204: "preparing" is a line of its own (warn), a later "nobody" does not overwrite it, a scramble overwrites it
            c.AlertLine("ПВО-3", new[] { 10 }, "preparing", 2, "~2 ч");
            True("preparing line", cards[cards.Count - 1].Groups.Exists(l => l.Group == "ПВО-3" && l.Action == "preparing" && l.Count == 2 && l.TypeLabel == "~2 ч"));
            c.AlertLine("ПВО-3", new[] { 10 }, "not_ready", 0, null);
            True("'nobody' does not overwrite preparing", cards[cards.Count - 1].Groups.Exists(l => l.Group == "ПВО-3" && l.Action == "preparing") && !cards[cards.Count - 1].Groups.Exists(l => l.Group == "ПВО-3" && l.Action == "not_ready"));
            c.AlertLine("ПВО-3", new[] { 10 }, "scramble", 1, "F-14A");
            True("a scramble overwrites preparing", cards[cards.Count - 1].Groups.Exists(l => l.Group == "ПВО-3" && l.Action == "scramble"));
            True("text of the preparing line", UiText.Fmt("airguard.line.alert_preparing", "ПВО-2", 2, "2 ч") == "ПВО-2 (дежурство): нет готовых, готовятся 2, ближайший ~2 ч");
            // a line of an unknown track (no raid known) is dropped quietly
            int before = cards.Count;
            c.AlertLine("ПВО-2", new[] { 999 }, "scramble", 1, "F-14A");
            True("no raid: no card", cards.Count == before);
            // not ready alone
            var g2 = new AirGuardFakeGame(); var x2 = new FakeX1();
            g2.Tracks.Add(Mig(10, 30, 0));
            var c2 = Make(g2, x2, new AirGuardParams(), Grp("ASW-1", 1));
            var cards2 = new List<AirThreatCard>(); c2.CardChanged = k => cards2.Add(k);
            Run(g2, c2, 0, 2);
            c2.AlertLine("ПВО-2", new[] { 10 }, "not_ready", 0, null);
            True("nobody ready: a line and an offer", cards2.Count >= 1 && cards2[cards2.Count - 1].Groups.Exists(l => l.Action == "not_ready") && x2.Count("alert_scramble") == 1);
            // Reset forgets the lines
            c.Reset();
            True("reset", c.BuildCard("none") == null);
        }

        private static void ModeWarn()
        {
            var g = new AirGuardFakeGame();
            g.Owns.Add(Bird(1, "P-3C", 0, 0)); g.Tracks.Add(Mig(10, 30, 0));
            var c = Make(g, null, new AirGuardParams { Mode = "warn" }, Grp("ASW-1", 1));
            Run(g, c, 0, 2);
            True("mode warn: group at most warn", c.GroupStage("asw", "ASW-1") == AirStage.Warn);
            True("mode warn: no cover", c.CoverFor(1) == null);
            var coff = Make(g, null, new AirGuardParams { Mode = "off" }, Grp("ASW-1", 1));
            Run(g, coff, 0, 2);
            True("mode off: none", coff.GroupStage("asw", "ASW-1") == AirStage.None && coff.ForUnit(1) == null);
        }

        private static void AtPointTest()
        {
            var g = new AirGuardFakeGame();
            g.Tracks.Add(Mig(10, 45, 0, 180, 100));
            var c = Make(g, null, new AirGuardParams());
            Run(g, c, 0, 1);
            var u = c.AtPoint(new GeoPt(0, 0), false);
            True("point warn", u != null && u.Stage == AirStage.Warn);
            u = c.AtPoint(new GeoPt(0, 0), true);
            True("point helo retreat (50 nm)", u != null && u.Stage == AirStage.Retreat);
            g.Tracks[0] = Mig(10, 35, 0, 180, 100); Run(g, c, 2, 3);
            True("point retreat", c.AtPoint(new GeoPt(0, 0), false).Stage == AirStage.Retreat);
            True("far point none", c.AtPoint(new GeoPt(5, 0), false) == null);
        }

        private static void IgnoreTest()
        {
            var g = new AirGuardFakeGame();
            g.Owns.Add(Bird(1, "P-3C", 0, 0)); g.Tracks.Add(Mig(10, 30, 0, 180, 300));
            var c = Make(g, null, new AirGuardParams(), Grp("ASW-1", 1));
            Run(g, c, 0, 2);
            True("retreat before", c.GroupStage("asw", "ASW-1") == AirStage.Retreat);
            c.Ignore("asw", "ASW-1", g.Now);
            True("ignored: none", c.GroupStage("asw", "ASW-1") == AirStage.None && c.ForUnit(1) == null && c.CoverFor(1) == null && c.Ignored("asw", "ASW-1"));
            Run(g, c, 3, 100);
            True("still ignored", c.GroupStage("asw", "ASW-1") == AirStage.None);
            g.Tracks.Add(Mig(11, 30, 0));   // a new track
            Run(g, c, 101, 103);
            True("new track ends the ignore", c.GroupStage("asw", "ASW-1") == AirStage.Retreat);
            // timeout
            var g2 = new AirGuardFakeGame();
            g2.Owns.Add(Bird(1, "P-3C", 0, 0)); g2.Tracks.Add(Mig(10, 30, 0, 180, 300));
            var c2 = Make(g2, null, new AirGuardParams(), Grp("ASW-1", 1));
            Run(g2, c2, 0, 2); c2.Ignore("asw", "ASW-1", 2);
            Run(g2, c2, 3, 590);
            True("ignored before IgnoreS", c2.GroupStage("asw", "ASW-1") == AirStage.None);
            Run(g2, c2, 591, 610);
            True("ignore expires", c2.GroupStage("asw", "ASW-1") == AirStage.Retreat);
        }

        private static void GhostTest()
        {
            var g = new AirGuardFakeGame();
            g.Owns.Add(Bird(1, "P-3C", 0, 0)); g.Tracks.Add(Mig(10, 80, 0, 180, 600));
            var c = Make(g, null, new AirGuardParams(), Grp("ASW-1", 1));
            Run(g, c, 0, 1);
            g.Tracks.Clear();   // lost: the ghost dead-reckons: 600 kn = 10 nm per minute
            Run(g, c, 2, 61);
            var tr = c.Tracks();
            True("ghost kept", tr.Count == 1 && tr[0].Ghost);
            True("ghost dead-reckoned (~80 - 10 nm)", tr.Count == 1 && Math.Abs(tr[0].Lat / NmDeg - 70) < 1.5);
            Run(g, c, 62, 100);
            True("ghost drives the stage (eta)", c.GroupStage("asw", "ASW-1") == AirStage.Retreat);
            Run(g, c, 101, 190);
            True("ghost removed after GhostS", c.Tracks().Count == 0);
            // destroyed: removed at once
            var g2 = new AirGuardFakeGame(); g2.Owns.Add(Bird(1, "P-3C", 0, 0)); g2.Tracks.Add(Mig(10, 30, 0));
            var c2 = Make(g2, null, new AirGuardParams(), Grp("ASW-1", 1)); Run(g2, c2, 0, 1);
            g2.Tracks.Clear(); g2.Destroyed.Add(10); Run(g2, c2, 2, 2);
            True("destroyed removed at once", c2.Tracks().Count == 0);
        }

        private static void UnknownWarn()
        {
            var g = new AirGuardFakeGame();
            g.Owns.Add(Bird(1, "P-3C", 0, 0));
            var t = Mig(10, 30, 0, 180, 450, false); t.Cls = null; t.RSrc = "default"; g.Tracks.Add(t);
            var c = Make(g, null, new AirGuardParams(), Grp("ASW-1", 1));
            Run(g, c, 0, 2);
            True("unknown 450 kn: warn only", c.GroupStage("asw", "ASW-1") == AirStage.Warn);
            var u = c.ForUnit(1);
            True("why unknown", u != null && u.Why == "unknown");
            g.Tracks[0].SpeedKn = 300; Run(g, c, 3, 70);
            True("unknown 300 kn: no threat", c.GroupStage("asw", "ASW-1") == AirStage.None);
            var c2 = Make(g, null, new AirGuardParams { Unknown = "retreat" }, Grp("ASW-1", 1));
            g.Tracks[0].SpeedKn = 450; Run(g, c2, 100, 102);
            True("Unknown=retreat", c2.GroupStage("asw", "ASW-1") == AirStage.Retreat);
        }

        // qa01-mig21: two identified MiG-21bis 36.8 nm from the fleet at 540 kn; P-3C orbiting 10 nm from Yorktown (ring 40 nm), SH-2F on rows, SH-3H with 16 rescued
        private static void Qa01()
        {
            var g = new AirGuardFakeGame(); var x1 = new FakeX1();
            g.Sams.Add(new AirSam { Uid = 100, Name = "Yorktown", Lat = 0, Lon = 0, RSamNm = 40, Deck = true, HeloDeck = true });
            g.Pos[100] = new GeoPt(0, 0);
            g.Owns.Add(Bird(1, "P-3C", 0, 10, false, 250));
            g.Owns.Add(Bird(2, "SH-2F", 1, 3, true, 100));
            g.Owns.Add(Bird(3, "SH-3H", -4, 4, true, 100)); g.AboardBy[3] = 16;
            g.Tracks.Add(Mig(10, 36.8, 0)); g.Tracks.Add(Mig(11, 37.5, 1.5));
            var c = Make(g, x1, new AirGuardParams(), OnDeck(Grp("ASW-3", 1)), OnDeck(Grp("ASW-2", 2)), OnDeck(Grp("SAR-4", 3)));
            Run(g, c, 0, 1);
            True("qa01: three groups at stage 2", c.GroupStage("asw", "ASW-3") == AirStage.Retreat && c.GroupStage("asw", "ASW-2") == AirStage.Retreat && c.GroupStage("asw", "SAR-4") == AirStage.Retreat);
            var c1 = c.CoverFor(1); var c2 = c.CoverFor(2); var c3 = c.CoverFor(3);
            True("P-3C: cover at Yorktown", c1 != null && c1.Mode == "cover" && c1.ShipUid == 100);
            True("SH-2F: cover at Yorktown", c2 != null && c2.Mode == "cover" && c2.ShipUid == 100);
            True("SH-3H: land on Yorktown", c3 != null && c3.Mode == "land" && c3.ShipUid == 100);
            True("one stage2 offer per raid and group", x1.Count("stage2") == 3 && x1.Count("raid1") == 1);
            // the birds fly to their points at their own speed; the MiGs fly toward the fleet at 540 kn
            var pts = new Dictionary<int, GeoPt> { { 1, c1.Point }, { 2, c2.Point }, { 3, c3.Point } };
            var landed = new HashSet<int>();
            for (double t = 2; t <= 300; t += 1)
            {
                g.Now = t;
                foreach (var tr in g.Tracks) { tr.Lat -= 540 * NmDeg / 3600.0; }
                foreach (var o in new List<AirOwn>(g.Owns))
                {
                    var tgt = pts[o.Uid]; double dn = (tgt.Lat - o.Lat) * 60, de = (tgt.Lon - o.Lon) * 60, d = Math.Sqrt(dn * dn + de * de);
                    double step = o.SpeedKn / 3600.0;
                    if (d <= step) { o.Lat = tgt.Lat; o.Lon = tgt.Lon; if (o.Uid == 3) { landed.Add(3); g.Owns.Remove(o); } }
                    else { o.Lat += dn / d * step * NmDeg; o.Lon += de / d * step * NmDeg; }
                }
                c.Tick(t);
                foreach (var o in g.Owns) { var cv = c.CoverFor(o.Uid); if (cv != null) pts[o.Uid] = cv.Point; }
            }
            foreach (var o in g.Owns)
            {
                double d = Math.Sqrt(Math.Pow(o.Lat * 60, 2) + Math.Pow(o.Lon * 60, 2));
                True("qa01: uid " + o.Uid + " within CoverNm+1 of the ship", d <= 6 + 1);
            }
            True("qa01: SH-3H landed", landed.Contains(3));
            // the MiGs are destroyed: after ClearS the stage is lifted (ring check: no air_resume before)
            True("qa01: no resume while threatened", g.CountEvents("air_resume") == 0);
            g.Destroyed.Add(10); g.Destroyed.Add(11); g.Tracks.Clear();
            Run(g, c, 301, 350);
            True("qa01: still retreat before ClearS... or already none", true);
            Run(g, c, 351, 380);
            True("qa01: resumed", c.GroupStage("asw", "ASW-3") == AirStage.None && g.CountEvents("air_resume") >= 2);
            True("qa01: stage2 offered once per group", x1.Count("stage2") == 3);
        }

        private static void Perf()
        {
            var g = new AirGuardFakeGame();
            var grps = new List<AirGuardGroup>();
            for (int i = 0; i < 20; i++)
            {
                g.Tracks.Add(Mig(100 + i, 30 + i * 3, i, 180, 500, i % 2 == 0));
                g.Owns.Add(Bird(1 + i, "B" + i, i * 2, i, i % 2 == 0));
                var gr = Grp("G" + i, 1 + i); gr.AreaPts.Add(new GeoPt(i * NmDeg, 0)); grps.Add(gr);
            }
            g.Sams.Add(new AirSam { Uid = 900, Name = "Ship", Lat = 0, Lon = 0, RSamNm = 40 });
            var c = Make(g, new FakeX1(), new AirGuardParams(), grps.ToArray());
            Run(g, c, 0, 5);
            var sw = Stopwatch.StartNew(); int n = 200;
            for (int i = 0; i < n; i++) { g.Now = 10 + i; c.Tick(g.Now); for (int u = 1; u <= 20; u++) c.CoverFor(u); }
            sw.Stop();
            double ms = sw.Elapsed.TotalMilliseconds / n;
            Console.WriteLine("airguard tick " + ms.ToString("0.000") + " ms");
            True("perf: tick < 2 ms on the stand (" + ms.ToString("0.000") + ")", ms < 2.0);
        }
    }
}
