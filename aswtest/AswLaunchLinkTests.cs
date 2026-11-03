using System;
using System.Collections.Generic;

namespace VoiceBotBridge
{
    /// <summary>
    /// 0.27.8 (live 0.8.7, ASW-1 "adopt expired ... left_deck"): the launch adoption follows the LaunchTask spawned from its own task (FromKey),
    /// takes a task the game put back into the hangar as the hangar row, does not expire within a few scans under time compression,
    /// and the warnings keep one line per kind and group.
    /// </summary>
    internal static class AswLaunchLinkTests
    {
        private const string Sh3 = "usn_sh-3h";
        private static Dictionary<string, object> P(string json) { return (Dictionary<string, object>)Json.Parse(json); }

        private static AswFakeGame Game(out AswFakeDeck d)
        {
            var f = new AswFakeGame();
            d = new AswFakeDeck();
            f.RealDeck = d;
            f.Decks[77] = new AswTarget { Uid = 77, Name = d.ShipName, Lat = 35, Lon = 20 };
            return f;
        }

        private static AswController Make(AswFakeGame f) { return new AswController(f, new AswParams(), new AswConfig { RestoreOnLoad = "off" }); }
        private static void Run(AswController c, AswFakeGame f, int ticks, double dt = 1) { for (int i = 0; i < ticks; i++) { f.Advance(dt); f.Tick(c); } }
        private static bool Logged(AswFakeGame f, string sub) { foreach (var l in f.Logs) if (l.Contains(sub)) return true; return false; }
        private static string Bad(AswFakeGame f) { foreach (var l in f.Logs) if (l.Contains("tick error")) return l; return null; }

        private static string Create(AswController c, params int[] uids)
        {
            var sb = new System.Text.StringBuilder("{\"op\":\"create\",\"group\":\"ASW-1\",\"uids\":[");
            for (int i = 0; i < uids.Length; i++) { if (i > 0) sb.Append(','); sb.Append(uids[i]); }
            string r = c.Group(P(sb.Append("]}").ToString()));
            if (AswController.IsErr(r)) return r;
            r = c.SearchCourse(P("{\"tactics\":[\"dip\"]}"));
            return AswController.IsErr(r) ? "order " + r : null;
        }

        public static string Run()
        {
            string e;
            if ((e = LinkedTask()) != null) return "LinkedTask: " + e;
            if ((e = NoLink()) != null) return "NoLink: " + e;
            if ((e = Aborted()) != null) return "Aborted: " + e;
            if ((e = AbortedMerge()) != null) return "AbortedMerge: " + e;
            if ((e = Compressed()) != null) return "Compressed: " + e;
            if ((e = PendingLong()) != null) return "PendingLong: " + e;
            if ((e = WarnDedupe()) != null) return "WarnDedupe: " + e;
            if ((e = ProposeLog()) != null) return "ProposeLog: " + e;
            return null;
        }

        // a foreign task of the same type on the same deck spawns in the same frame and is listed first: the group follows the machine of its own task
        private static string LinkedTask()
        {
            AswFakeDeck d; var f = Game(out d);
            d.GapFrame = false; d.TaxiS = 60;
            string foreign = d.AddTask(Sh3, "SH-3H", 3, true);
            string ours = d.AddTask(Sh3, "SH-3H", 3, true);
            var c = Make(f);
            f.Advance(1); f.Tick(c);
            string err = Create(c, d.UidOfTask(ours));
            if (err != null) return err;
            Run(c, f, 10);   // both spawn: foreign -> 7100, ours -> 7101 (taxiing)
            if (Bad(f) != null) return Bad(f);
            var m = c.View().Groups[0].Members;
            if (m.Count != 1 || m[0] != 7101) return "members " + string.Join(",", m.ConvertAll(x => x.ToString()).ToArray()) + " | " + string.Join(" | ", f.Logs.ToArray());
            if (!Logged(f, "-> 7101 via=task")) return "no via=task log";
            Run(c, f, 120);
            m = c.View().Groups[0].Members;
            if (m.Count != 1 || m[0] != 7101 || f.HasEvent("asw_warn", "member_lost")) return "after take-off " + string.Join(",", m.ConvertAll(x => x.ToString()).ToArray());
            return null;
        }

        // the LaunchTask row does not name its task (missing member): the old rule (a launching unit row of the deck) still adopts it
        private static string NoLink()
        {
            AswFakeDeck d; var f = Game(out d);
            d.NoLink = true;
            string ka = d.AddTask(Sh3, "SH-3H", 3, true);
            var c = Make(f);
            f.Advance(1); f.Tick(c);
            string err = Create(c, d.UidOfTask(ka));
            if (err != null) return err;
            Run(c, f, 120);
            if (Bad(f) != null) return Bad(f);
            if (!Logged(f, "via=unit")) return "no via=unit";
            if (f.HasEvent("asw_warn", "member_lost")) return "member_lost";
            var m = c.View().Groups[0].Members;
            if (m.Count != 1 || m[0] != 7100) return "members " + m.Count;
            return null;
        }

        // the game takes the task back (abortLaunchTask, e.g. a failed spawn): no member_lost, the member is the hangar row again
        private static string Aborted()
        {
            AswFakeDeck d; var f = Game(out d);
            d.AddHangar(Sh3, "SH-3H", 1);
            string ka = d.AddTask(Sh3, "SH-3H", 3000, true);
            var c = Make(f);
            f.Advance(1); f.Tick(c);
            string err = Create(c, d.UidOfTask(ka));
            if (err != null) return err;
            Run(c, f, 5);
            if (!d.AbortTask(ka)) return "abort";
            Run(c, f, 10);
            if (Bad(f) != null) return Bad(f);
            if (f.HasEvent("asw_warn", "member_lost")) return "member_lost";
            if (!Logged(f, "aborted: back in the hangar")) return "no aborted log: " + string.Join(" | ", f.Logs.ToArray());
            var m = c.View().Groups[0].Members;
            // T-181 (ADR-0080): the machine is a slot of its type again (the source row of the old model is gone); the standby may order it again at once
            if (!Logged(f, "aborted: back in the hangar -> slot")) return "not a hangar slot again: " + string.Join(" | ", f.Logs.ToArray());
            if (m.Contains(d.UidOfTask(ka))) return "the task row is still a member";
            return null;
        }

        // the hangar row of the type is already in the group: the aborted task merges into it
        private static string AbortedMerge()
        {
            AswFakeDeck d; var f = Game(out d);
            d.AddHangar(Sh3, "SH-3H", 1);
            d.PrepS = 3000;
            string ka = d.AddTask(Sh3, "SH-3H", 3000, true);
            var c = Make(f);
            f.Advance(1); f.Tick(c);
            int task = d.UidOfTask(ka), hang = AswMath.HangarUid(77, Sh3);
            string err = Create(c, task, hang);
            if (err != null) return err;
            Run(c, f, 5);
            // the hangar row may have been sent out meanwhile; abort our first task only
            if (!d.AbortTask(ka)) return "abort";
            Run(c, f, 10);
            if (Bad(f) != null) return Bad(f);
            if (f.HasEvent("asw_warn", "member_lost")) return "member_lost";
            if (!Logged(f, "aborted: back in the hangar")) return "no aborted log: " + string.Join(" | ", f.Logs.ToArray());
            if (c.View().Groups[0].Members.Contains(task)) return "the task row is still a member";
            return null;
        }

        // time compression: 100 s of mission time per scan; the machine is gone without a trace. Not given up after 5 scans (500 s), but after the scan floor.
        private static string Compressed()
        {
            AswFakeDeck d; var f = Game(out d);
            string ka = d.AddTask(Sh3, "SH-3H", 30000, true);
            var c = Make(f);
            f.Advance(1); f.Tick(c);
            string err = Create(c, d.UidOfTask(ka));
            if (err != null) return err;
            Run(c, f, 3);
            d.VanishTask(ka);
            Run(c, f, 5, 100);
            if (f.HasEvent("asw_warn", "member_lost")) return "expired after 5 scans";
            if (!c.View().Groups[0].Members.Contains(d.UidOfTask(ka)) && c.View().Groups[0].Members.Count == 0) return "member removed";
            Run(c, f, AswController.AdoptGapTicks, 100);
            if (!f.HasEvent("asw_warn", "member_lost")) return "never expired";
            if (!Logged(f, "asw adopt expired")) return "no expired log";
            if (!Logged(f, "row gone: last=deck_prep")) return "no row-gone log: " + string.Join(" | ", f.Logs.ToArray());
            return null;
        }

        // live 0.27.8: an own task created from the hangar waits on the deck far longer than the adoption gap (held / long ready-up): the member
        // is followed from the handover on, but never given up while the task row is listed
        private static string PendingLong()
        {
            AswFakeDeck d; var f = Game(out d);
            d.PrepS = 100000;
            d.AddHangar(Sh3, "SH-3H", 3);
            var c = Make(f);
            f.Advance(1); f.Tick(c);
            string err = Create(c, AswMath.HangarUid(77, Sh3));
            if (err != null) return err;
            Run(c, f, 2000);
            if (Bad(f) != null) return Bad(f);
            if (!Logged(f, "asw handover")) return "no handover: " + string.Join(" | ", f.Logs.ToArray());
            if (!Logged(f, "asw adopt start")) return "no adopt start";
            if (f.HasEvent("asw_warn", "member_lost") || Logged(f, "adopt expired")) return "lost while the task is on the deck";
            if (c.View().Groups[0].Members.Count < 2) return "members " + c.View().Groups[0].Members.Count;
            return null;
        }

        // a contact that is listed only every 3rd scan is still taken (T-173: no proposal any more, the strike group attacks)
        private static string ProposeLog()
        {
            var f = new AswFakeGame();
            f.Mem.Add(AswFakeGame.Helo(1, "air", dip: true));
            var c = Make(f);
            c.SearchCourse(P("{\"tactics\":[\"dip\",\"rows\"]}"));
            Run(c, f, 5);
            var ahead = AswMath.Offset(new GeoPt(f.Tgt.Lat, f.Tgt.Lon), 90, 10);
            var sub = new AswContact { Id = 5, Name = "Sub #5", Lat = ahead.Lat, Lon = ahead.Lon, Source = "live" };
            for (int i = 0; i < 60; i++)
            {
                if (i % 3 == 0) { if (!f.Subs.Contains(sub)) f.Subs.Add(sub); } else f.Subs.Remove(sub);
                f.Advance(1); f.Tick(c);
                if (i % 3 == 0) f.Subs.Remove(sub);
            }
            if (!f.HasEvent("asw_contact")) return "a flickering contact was not taken";
            if (f.HasEvent("asw_attack_proposed")) return "a search group proposed an attack";
            if (f.Attacks.Count != 0) return "a search group attacked";
            return null;
        }

        // the same warning kind of a group again: one line, the latest text
        private static string WarnDedupe()
        {
            var f = new AswFakeGame();
            f.Mem.Add(AswFakeGame.Helo(1, "air"));
            f.Mem.Add(AswFakeGame.Helo(2, "air"));
            var c = Make(f);
            if (AswController.IsErr(c.Group(P("{\"op\":\"create\",\"group\":\"G\",\"uids\":[1,2]}")))) return "create";
            Run(c, f, 2);
            f.Mem.RemoveAll(m => m.Uid == 1);
            Run(c, f, 320);
            f.Mem.RemoveAll(m => m.Uid == 2);
            Run(c, f, 320);
            int n = 0; string last = null;
            foreach (var w in c.View().Warnings) if (w.StartsWith("member_lost (G)", StringComparison.Ordinal)) { n++; last = w; }
            if (n != 1) return "member_lost lines " + n + ": " + string.Join(" | ", c.View().Warnings.ToArray());
            if (!last.Contains("Helo 2")) return "not the latest text: " + last;
            return null;
        }
    }
}
