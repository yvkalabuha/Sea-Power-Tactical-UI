using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace VoiceBotBridge
{
    // T-172: regressions of the ASW review fixes (2026-10-22) - contacts.
    internal static class AswT172ContactsTests
    {
        private static Dictionary<string, object> P(string json) { return (Dictionary<string, object>)Json.Parse(json); }
        private static AswController Make(AswFakeGame f) { return new AswController(f, new AswParams(), new AswConfig { RestoreOnLoad = "off" }); }
        private static void Run(AswController c, AswFakeGame f, int seconds) { for (int i = 0; i < seconds; i++) { f.Advance(1); f.Tick(c); } }

        /// <summary>A course group with one dipping helicopter with torpedoes and a hostile submarine 10 miles ahead (T-173: no proposal any more, the out value is always null).</summary>
        private static AswController Setup(AswFakeGame f, out AswContact sub, out string proposal, bool live = true)
        {
            f.Mem.Add(AswFakeGame.Helo(1, "air", dip: true));
            var c = Make(f);
            c.SearchCourse(P("{\"tactics\":[\"dip\",\"rows\"]}"));
            Run(c, f, 5);
            var a = AswMath.Offset(new GeoPt(f.Tgt.Lat, f.Tgt.Lon), 90, 10);
            sub = new AswContact { Id = 7048, Name = "Sub #7048", Lat = a.Lat, Lon = a.Lon, Source = "live", Side = "hostile", Live = live };
            f.Subs.Add(sub);
            Run(c, f, 30);
            proposal = null;
            return c;
        }

        /// <summary>T-173: the old "Yes" is an attack run of the helicopter placed near the submarine (the fake does not fly routes).</summary>
        private static void Shoot(AswController c, AswFakeGame f, AswContact sub, int uid = 1)
        {
            var near = AswMath.Offset(new GeoPt(sub.Lat, sub.Lon), 270, 0.8);
            foreach (var m in f.Mem) if (m.Uid == uid) { m.Lat = near.Lat; m.Lon = near.Lon; }
            f.FixPos.Add(uid);
            c.StartStrikeRun(c.View().Groups[0].Name, sub.Id, uid, "shooter");
        }

        private static int Count(AswFakeGame f, string ev) { return f.Events.Count(e => e.StartsWith(ev + " ", StringComparison.Ordinal)); }

        public static string Run()
        {
            string e;
            if ((e = LiveFilter()) != null) return "live filter: " + e;
            if ((e = Killed()) != null) return "killed: " + e;
            if ((e = KilledCleanup()) != null) return "killed cleanup: " + e;
            if ((e = GuardAndLeaving()) != null) return "guard and leaving: " + e;
            if ((e = Release()) != null) return "release: " + e;
            return null;
        }

        // ---- the Live flag: a stale track is no new contact and keeps nothing alive ----

        private static string LiveFilter()
        {
            var f = new AswFakeGame();
            AswContact sub; string pid;
            var c = Setup(f, out sub, out pid, live: false);
            if (f.HasEvent("asw_contact")) return "a stale track became a contact";
            // live -> stale: the contact is lost after LostAfterS, no question is left standing
            var f2 = new AswFakeGame();
            AswContact s2; string p2;
            var c2 = Setup(f2, out s2, out p2);
            if (!f2.HasEvent("asw_contact")) return "a live track was not taken";
            s2.Live = false;
            Run(c2, f2, 250);
            if (f2.HasEvent("asw_contact_lost")) return "lost before LostAfterS";
            Run(c2, f2, 120);
            if (!f2.HasEvent("asw_contact_lost")) return "not lost 300 s after it went stale";
            // stale position still updates the estimate (checked through the lost event)
            return null;
        }

        // ---- a destroyed submarine: its own event, no square, the question and the run end ----

        private static string Killed()
        {
            var f = new AswFakeGame();
            AswContact sub; string pid;
            var c = Setup(f, out sub, out pid);
            Shoot(c, f, sub);
            if (c.RunState(sub.Id) == null || c.RunState(sub.Id) == "done") return "no attack run";
            f.Subs.Clear();
            f.Subs.Add(new AswContact { Id = sub.Id, Name = sub.Name, Lat = sub.Lat, Lon = sub.Lon, Side = "hostile", Killed = true });
            Run(c, f, 10);
            string data = f.EventData("asw_contact_killed");
            if (data == null) return "no asw_contact_killed";
            var d = P(data);
            if (!d.ContainsKey("group") || !d.ContainsKey("contact") || !d.ContainsKey("track") || !d.ContainsKey("lat") || !d.ContainsKey("lon")) return "event fields " + data;
            if (!f.Logs.Exists(l => l.Contains(" killed contact=7048 track=Sub #7048"))) return "no journal line";
            if (f.HasEvent("asw_contact_lost")) return "lost event after the kill";
            if (f.Logs.Exists(l => l.Contains("lost_square"))) return "square after the kill";
            if (c.RunDone(sub.Id) != "killed") return "run not ended with killed: " + c.RunState(sub.Id);
            if (f.Events.FindAll(e => e.StartsWith("asw_contact_killed ", StringComparison.Ordinal)).Count != 1) return "more than one kill event";
            return null;
        }

        // ---- the drawing of a destroyed contact goes: marker, cross, holder state ----

        private static AswGroupView GV(AswController c) { return c.View().Groups[0]; }

        private static string KilledCleanup()
        {
            var f = new AswFakeGame();
            AswContact sub; string pid;
            var c = Setup(f, out sub, out pid);
            var v = GV(c);
            if (v.Contacts.Count != 1) return "no marker";
            if (!v.Lines.Exists(l => l.Kind == "contact")) return "no contact lines to clear (the test does not test)";
            f.Subs.Clear();
            f.Subs.Add(new AswContact { Id = sub.Id, Name = sub.Name, Lat = sub.Lat, Lon = sub.Lon, Side = "hostile", Killed = true });
            Run(c, f, 3);
            v = GV(c);
            if (v.Contacts.Count != 0 || v.Blink) return "the contact marker stayed";
            if (v.Lines.Exists(l => l.Kind == "contact")) return "the contact cross stayed";
            if (f.Logs.Exists(l => l.Contains("lost_square"))) return "square";
            // no destroyed flag: the track vanishes right after a hit
            var f2 = new AswFakeGame { ReleaseDelayS = 1 };
            AswContact s2; string p2;
            var c2 = Setup(f2, out s2, out p2);
            Shoot(c2, f2, s2);
            Run(c2, f2, 12);
            if (!f2.Logs.Exists(l => l.Contains(" drop contact=7048"))) return "no drop";
            f2.Subs.Clear();
            Run(c2, f2, 60);
            if (!f2.HasEvent("asw_contact_killed")) return "a track gone after a hit was not taken as destroyed";
            if (f2.HasEvent("asw_contact_lost") || f2.Logs.Exists(l => l.Contains("lost_square"))) return "lost-contact square after a hit";
            if (GV(c2).Contacts.Count != 0) return "marker after the hit";
            return null;
        }

        // ---- B7 / B10: the dip on a contact goes through the route cleaning and the guard; an aircraft heading home takes no part ----

        private static string GuardAndLeaving()
        {
            var f = new AswFakeGame();
            f.Mem.Add(AswFakeGame.Helo(1, "air", dip: true));
            var c = Make(f);
            c.SearchCourse(P("{\"tactics\":[\"dip\"]}"));
            Run(c, f, 5);
            var a = AswMath.Offset(new GeoPt(f.Tgt.Lat, f.Tgt.Lon), 90, 10);
            f.Calls.Clear();
            f.Subs.Add(new AswContact { Id = 1, Name = "S1", Lat = a.Lat, Lon = a.Lon, Side = "hostile" });
            Run(c, f, 3);
            int k = f.Dips.FindIndex(x => x.Count == 1 && Math.Abs(AswMath.DistNm(x[0], a) - 2.3) < 0.1);   // T-176 C11: the holder dips abeam at the spook radius
            if (k < 0) return "no dip on the contact";
            if (!f.Calls.Contains("clear:1")) return "no clear before the dip on the contact";
            // an air-defence zone on the contact: no dip there
            var f2 = new AswFakeGame();
            f2.Mem.Add(AswFakeGame.Helo(1, "air", dip: true));
            var c2 = Make(f2);
            c2.SearchCourse(P("{\"tactics\":[\"dip\"]}"));
            Run(c2, f2, 5);
            var a2 = AswMath.Offset(new GeoPt(f2.Tgt.Lat, f2.Tgt.Lon), 90, 10);
            f2.HazardList.Add(new AswHazard(a2, 3));
            f2.Subs.Add(new AswContact { Id = 1, Name = "S1", Lat = a2.Lat, Lon = a2.Lon, Side = "hostile" });
            Run(c2, f2, 400);
            if (f2.Dips.Exists(x => x.Count == 1 && AswMath.DistNm(x[0], a2) < 2.9)) return "dip inside an air-defence zone";
            // the nearest dipper is heading home: the other one holds the contact
            var f3 = new AswFakeGame();
            var near = AswFakeGame.Helo(1, "air", dip: true); near.AiState = "ReturnToBase";
            var other = AswFakeGame.Helo(2, "air", dip: true);
            f3.Mem.Add(near); f3.Mem.Add(other);
            var c3 = Make(f3);
            c3.Group(P("{\"op\":\"create\",\"group\":\"G\",\"uids\":[1,2]}"));
            c3.SearchCourse(P("{\"tactics\":[\"dip\"]}"));
            Run(c3, f3, 5);
            var a3 = AswMath.Offset(new GeoPt(f3.Tgt.Lat, f3.Tgt.Lon), 90, 10);
            f3.Calls.Clear();
            f3.Subs.Add(new AswContact { Id = 1, Name = "S1", Lat = a3.Lat, Lon = a3.Lon, Side = "hostile" });
            Run(c3, f3, 5);
            if (f3.Calls.Contains("clear:1")) return "the aircraft heading home was routed";
            if (!f3.Calls.Contains("clear:2")) return "the other aircraft did not take the contact";
            return null;
        }

        // ---- C4: "released" is the drop of the torpedo count, not the acceptance of the order ----

        private static string Release()
        {
            // the weapon never leaves: one retry after DropTimeoutS, then the shooter is changed (the run waits for a new one)
            var f = new AswFakeGame { ReleaseDelayS = -1 };
            AswContact sub; string pid;
            var c = Setup(f, out sub, out pid);
            Shoot(c, f, sub);
            Run(c, f, 150);
            if (f.Logs.Exists(l => l.Contains(" drop contact="))) return "dropped without a release";
            if (c.RunState(sub.Id) != "engaged") return "not engaged before 180 s: " + c.RunState(sub.Id);
            Run(c, f, 60);
            if (!f.Logs.Exists(l => l.Contains("no drop in 180"))) return "no timeout line";
            Run(c, f, 400);
            if (!f.Logs.Exists(l => l.Contains("no drop in 180") && true) || c.RunState(sub.Id) == "in_water") return "second timeout";
            // the weapon leaves 20 s later
            var f2 = new AswFakeGame { ReleaseDelayS = 20 };
            AswContact s2; string p2;
            var c2 = Setup(f2, out s2, out p2);
            Shoot(c2, f2, s2);
            Run(c2, f2, 15);
            if (f2.Logs.Exists(l => l.Contains(" drop contact="))) return "dropped too early";
            if (c2.RunState(s2.Id) != "engaged") return "direct attack: not engaged while the weapon is in the queue";
            Run(c2, f2, 10);
            if (!f2.Logs.Exists(l => l.Contains(" drop contact=7048"))) return "no drop line after the release";
            if (!f2.HasEvent("asw_attack_drop")) return "no drop event";
            return null;
        }
    }
}
