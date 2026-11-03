using System;
using System.Collections.Generic;
using System.Linq;

namespace VoiceBotBridge
{
    // T-298: a reader of the state (dev "state" asw/sar, link_state) gets fresh rows with the ASW window closed. `aswtest t298 <name>`.
    internal static class AswT298Tests
    {
        public static readonly string[] Names = { "closed_fills", "rate", "no_reads_no_scan", "fresh_noop", "no_gone" };

        private static AswController Make(AswFakeGame f) { return new AswController(f, new AswParams(), new AswConfig { RestoreOnLoad = "off" }); }

        private static void Ticks(AswFakeGame f, AswController c, int n, int reads = 0)
        {
            for (int i = 0; i < n; i++) { f.Advance(1); f.Tick(c); for (int r = 0; r < reads; r++) c.FreshView(); }
        }

        public static string Run(string only = null)
        {
            bool known = only == null;
            foreach (string n in Names)
            {
                if (only != null && only != n) continue;
                known = true;
                string e = n == "closed_fills" ? ClosedFills() : n == "rate" ? Rate() : n == "no_reads_no_scan" ? NoReads() : n == "fresh_noop" ? FreshNoop() : NoGone();
                if (e != null) return n + ": " + e;
            }
            return known ? null : "unknown scenario " + only;
        }

        private static string ClosedFills()
        {
            var f = new AswFakeGame();
            f.Mem.Add(AswFakeGame.Helo(1, "air"));
            var c = Make(f);
            c.ViewWanted = false;
            Ticks(f, c, 5);
            if (f.MembersCalls != 0) return "scan while idle " + f.MembersCalls;
            if (c.View().Available.Count != 0) return "view not empty before the read";
            if (!c.FreshView()) return "FreshView returned false";
            if (f.MembersCalls != 1) return "Members calls " + f.MembersCalls;
            if (!c.View().Available.Any(r => r.Uid == 1)) return "uid 1 not available";
            string s = c.StateJson();
            if (s == null || !s.Contains("\"uid\":1")) return "state json without uid 1: " + s;
            bool sar = c.View().AvailableSar.Any(r => r.Uid == 1) || c.View().ExcludedSar.Any(r => r.Row.Uid == 1);
            if (!sar) return "uid 1 in neither available_sar nor excluded_sar";
            return null;
        }

        private static string Rate()
        {
            var f = new AswFakeGame(); f.Mem.Add(AswFakeGame.Helo(1, "air"));
            var c = Make(f); c.ViewWanted = false;
            Ticks(f, c, 10, 5);
            var f2 = new AswFakeGame(); f2.Mem.Add(AswFakeGame.Helo(1, "air"));
            var c2 = Make(f2); c2.ViewWanted = true;
            Ticks(f2, c2, 10);
            if (f.MembersCalls > 10) return "more than one scan per tick: " + f.MembersCalls;
            if (f.MembersCalls > f2.MembersCalls) return "closed " + f.MembersCalls + " > open " + f2.MembersCalls;
            return null;
        }

        private static string NoReads()
        {
            var f = new AswFakeGame(); f.Mem.Add(AswFakeGame.Helo(1, "air"));
            var c = Make(f); c.ViewWanted = false;
            Ticks(f, c, 20);
            if (f.MembersCalls != 0) return "Members calls " + f.MembersCalls;
            if (c.SkippedTicks != 20) return "skipped " + c.SkippedTicks;
            return null;
        }

        private static string FreshNoop()
        {
            var f = new AswFakeGame(); f.Mem.Add(AswFakeGame.Helo(1, "air"));
            var c = Make(f); c.ViewWanted = false;
            Ticks(f, c, 2);
            if (!c.FreshView()) return "first read false";
            int calls = f.MembersCalls;
            if (c.FreshView()) return "second read true";
            if (f.MembersCalls != calls) return "second read scanned";
            c.ViewWanted = true;
            Ticks(f, c, 1);
            calls = f.MembersCalls;
            if (c.FreshView()) return "open window: FreshView true";
            if (f.MembersCalls != calls) return "open window: scanned";
            return null;
        }

        private static string NoGone()
        {
            var f = new AswFakeGame(); f.Mem.Add(AswFakeGame.Helo(1, "air"));
            var c = Make(f); c.ViewWanted = false;
            Ticks(f, c, 1); c.FreshView();
            f.Mem.Clear();
            Ticks(f, c, 3, 1);
            if (f.HasEvent("asw_warn") || f.HasEvent("member_lost")) return "gone event: " + string.Join(" / ", f.Events.ToArray());
            if (c.View().Excluded.Any(x => x.Reason == "lost" || x.Reason == "landed")) return "gone reason in Excluded";
            return null;
        }
    }
}
